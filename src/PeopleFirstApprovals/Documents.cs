using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PeopleFirstApprovals;

/// <summary>What a receipt or invoice says, as the document reader returned it.</summary>
public sealed record DocumentFacts(decimal? Total, string Currency, string Payee, string BillTo, string Date, string Number,
    IReadOnlyList<string> Items, string Paid);

/// <summary>The result of running a command: its exit code (null if it timed out or wouldn't start) and its output.</summary>
public sealed record CommandResult(int? Exit, string Out, string Err)
{
    public string FirstLine => (Out.Trim().Length > 0 ? Out : Err).Trim().Split('\n')[0].Trim();
}

/// <summary>Runs a command with optional standard input and a timeout. A timeout kills the command and everything it
/// started.</summary>
public static class Commands
{
    public static CommandResult Run(IReadOnlyList<string> command, string folder, string? input, int timeoutSeconds)
    {
        var start = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in command.Skip(1))
            start.ArgumentList.Add(arg);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("it didn't start");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(null, "", $"couldn't start {command[0]}: {error.Message}");
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                    stdin.Write(input ?? "");
            }
            catch (IOException)
            {
                // It didn't read its input; its exit code says what it made of that.
            }
            if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                return new CommandResult(null, "", $"{System.IO.Path.GetFileName(command[0])} timed out after {timeoutSeconds}s");
            }
            process.WaitForExit();
            return new CommandResult(process.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
        }
    }
}

/// <summary>A claim's documents: downloaded into a folder of their own under [documents], read by the configured
/// reader (a claude-headless job, say), and deleted once the claim is decided or keep_days have passed.</summary>
public static partial class Documents
{
    public const string InputFile = "reader-input.json";
    public const string OutputFile = "reader-output.md";

    public static string Folder(Config config, string claimId) => System.IO.Path.Combine(config.Documents, Safe(claimId));

    [GeneratedRegex(@"[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Unsafe();

    internal static string Safe(string name)
    {
        var safe = Unsafe().Replace(name, "-").Trim('-', '.');
        return safe.Length == 0 ? "document" : safe.Length > 80 ? safe[..80] : safe;
    }

    /// <summary>The file's kind from its first bytes: pdf, png, jpg, or null (it isn't a file: People First answered
    /// with something else, such as the attachment's details as JSON).</summary>
    public static string? Kind(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' ? "pdf"
        : bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G' ? "png"
        : bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF ? "jpg"
        : null;

    /// <summary>Download the first attachment that is a document (a PDF or an image) into the claim's folder; returns
    /// its path, or null and why not.</summary>
    public static (string? Path, string? Problem) Download(Config config, IPeopleFirst pf, Claim claim)
    {
        if (claim.Attachments.Count == 0)
            return (null, "nothing is attached");
        var folder = Folder(config, claim.ClaimId);
        foreach (var attachment in claim.Attachments)
        {
            var name = Safe(attachment.Name);
            var existing = System.IO.Path.Combine(folder, name);
            if (File.Exists(existing))
                return (existing, null);
            var bytes = pf.GetFile(attachment.Download, Claims.FileAccept);
            if (Kind(bytes) is not { } kind)
                continue;
            Directory.CreateDirectory(folder);
            if (!name.EndsWith("." + kind, StringComparison.OrdinalIgnoreCase) && !(kind == "jpg" && name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
                name += "." + kind;
            var path = System.IO.Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            return (path, null);
        }
        return (null, $"{claim.Attachments.Count} attachment(s), none of them a PDF or an image");
    }

    /// <summary>Read a downloaded document with the configured reader: the reader's reply from an earlier run is used
    /// if it's there (a job queued at a usage limit writes it later); otherwise the reader is run now. Returns the
    /// facts, or null and why not.</summary>
    public static (DocumentFacts? Facts, string? Problem) Read(Config config, string documentPath, string claimId,
        Func<IReadOnlyList<string>, string, string?, int, CommandResult> run)
    {
        var folder = System.IO.Path.GetDirectoryName(documentPath)!;
        var output = System.IO.Path.Combine(folder, OutputFile);
        if (File.Exists(output))
            return Parse(File.ReadAllText(output));
        if (config.ReaderCommand is not { } command)
            return (null, "no [document_reader] is set up, so it wasn't read");
        var input = System.IO.Path.Combine(folder, InputFile);
        var details = new JsonObject
        {
            ["document"] = documentPath,
            ["file_name"] = System.IO.Path.GetFileName(documentPath),
            ["claim"] = claimId,
            ["received"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(input, details.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        var filled = command.Select(a => a.Replace("{input}", input, StringComparison.Ordinal).Replace("{out}", output, StringComparison.Ordinal)).ToList();
        var result = run(filled, config.ReaderFolder, null, config.ReaderTimeoutSeconds);
        if (File.Exists(output))
            return Parse(File.ReadAllText(output));
        return (null, result.Exit == 0
            ? $"the reader gave no reply yet ({result.FirstLine}): run check again later"
            : $"the reader failed ({(result.Exit is { } e ? $"exit {e}" : "no exit")}: {result.FirstLine})");
    }

    /// <summary>The reader's reply: a JSON object (anywhere in the text) with total, currency, payee, bill_to, date,
    /// invoice_number, items and paid.</summary>
    public static (DocumentFacts? Facts, string? Problem) Parse(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
            return (null, "the reader's reply has no JSON in it");
        JsonObject json;
        try
        {
            json = JsonNode.Parse(reply[start..(end + 1)]) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException error)
        {
            return (null, $"the reader's reply isn't valid JSON ({error.Message.Split('\n')[0]})");
        }
        string Text(string key) => json[key] is JsonValue v ? v.ToString() : "";
        decimal? total = json["total"] switch
        {
            JsonValue v when v.TryGetValue<decimal>(out var d) => d,
            JsonValue v when decimal.TryParse(v.ToString().Replace("£", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
        var items = json["items"] is JsonArray a ? a.Select(i => i?.ToString() ?? "").Where(i => i.Length > 0).ToList() : [];
        return (new DocumentFacts(total, Text("currency"), Text("payee"), Text("bill_to"), Text("date"), Text("invoice_number"), items, Text("paid")), null);
    }

    /// <summary>Delete a claim's folder of documents (the receipt carries the claimant's address).</summary>
    public static bool Delete(Config config, string claimId)
    {
        var folder = Folder(config, claimId);
        if (!Directory.Exists(folder))
            return false;
        Directory.Delete(folder, recursive: true);
        return true;
    }

    /// <summary>Folders under [documents] older than keep_days: deleted whatever their claim's state. Returns how many.</summary>
    public static int Expire(Config config, DateTime now)
    {
        if (!Directory.Exists(config.Documents))
            return 0;
        var gone = 0;
        foreach (var folder in Directory.GetDirectories(config.Documents))
            if (Directory.GetCreationTime(folder) < now.AddDays(-config.KeepDays))
            {
                Directory.Delete(folder, recursive: true);
                gone++;
            }
        return gone;
    }
}
