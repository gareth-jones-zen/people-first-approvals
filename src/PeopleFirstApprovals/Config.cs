using System.Globalization;
using System.Text.RegularExpressions;
using PyCompat;
using ReviewQueue;
using Tomlyn;
using Tomlyn.Model;

namespace PeopleFirstApprovals;

/// <summary>Settings that can't be used, or a request that can't be met: the command line reports the message and
/// exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>What kind of claim a payslip item is, which decides the checks beyond the common ones.</summary>
public enum ClaimKind { LongService, Expenses, Overtime, Other }

/// <summary>A [[claim_type]]: payslip items whose name matches are this kind, with a note on what to look at.</summary>
public sealed record ClaimType(Regex Match, ClaimKind Kind, string Note);

/// <summary>A [[long_service]] band: from this many years' service, an award of up to this amount.</summary>
public sealed record LongServiceBand(int Years, decimal Amount, string Also);

/// <summary>The settings file. See the README and <see cref="Template"/>. Read strictly: an unknown key, or a value of
/// the wrong type, is an error that names it.</summary>
public sealed class Config
{
    public const string EnvConfig = "PEOPLE_FIRST_APPROVALS_CONFIG";
    public const string SignInHint = "Run `people-first-approvals sign-in` at a console.";

    // Explicit "\n" (a raw literal would take the .cs file's line endings), written with CRLF.
    public const string Template =
        "# people-first-approvals settings. See the README.\n" +
        "# base = \"base-config.toml\"        # settings shared with other tools\n" +
        "# login_hint = \"you@example.com\"   # the People First account (or [people_first] account)\n" +
        "\n" +
        "keep_days = 30                     # a claim's documents are deleted once it's decided, or after this\n" +
        "# documents = \"%LOCALAPPDATA%/people-first-approvals/documents\"\n" +
        "\n" +
        "[people_first]\n" +
        "url = \"https://yourorg.people-first.com\"\n" +
        "# edge_profile = \"%LOCALAPPDATA%/people-first-approvals/edge-profile\"\n" +
        "\n" +
        "# [document_reader]                # reads a claim's receipt: {input} and {out} are filled in\n" +
        "# command = ['claude-headless', '--config', 'claude-headless.toml', 'job', 'claim-document', '--input', '{input}', '--out', '{out}']\n" +
        "\n" +
        "# [review]                         # items for you to decide\n" +
        "# file = \"%LOCALAPPDATA%/my-tools/review.json\"\n" +
        "# source = \"people-first-approvals\"\n" +
        "\n" +
        "[[long_service]]\n" +
        "years = 10\n" +
        "amount = 250\n";

    public static readonly string[] KnownKeys =
    [
        "login_hint", "log_file", "state_file", "documents", "keep_days", "people_first", "document_reader", "digest", "review",
        "long_service", "claim_type",
    ];

    /// <summary>Used when the settings name no [[claim_type]]: first match wins.</summary>
    public static IReadOnlyList<ClaimType> DefaultClaimTypes { get; } =
    [
        new(Pattern(@"long\s*service"), ClaimKind.LongService, ""),
        new(Pattern(@"overtime"), ClaimKind.Overtime, "Was the overtime agreed beforehand? Only you know."),
        new(Pattern(@"expense|conference|travel|subsistence|mileage|hotel"), ClaimKind.Expenses,
            "Was it for something you agreed (a conference, a trip)? Only you know."),
    ];

    public required string Path { get; init; }
    public required PeopleFirstClient.Settings PeopleFirst { get; init; }
    public required string LogFile { get; init; }
    public required string StateFile { get; init; }
    public required string Documents { get; init; }
    public int KeepDays { get; init; } = 30;
    /// <summary>[document_reader] command, with {input} and {out}; null: documents aren't read.</summary>
    public IReadOnlyList<string>? ReaderCommand { get; init; }
    public string ReaderFolder { get; init; } = "";
    public int ReaderTimeoutSeconds { get; init; } = 600;
    /// <summary>[digest] next: the command given what's left of a digest; null: anything left is raised.</summary>
    public IReadOnlyList<string>? DigestNext { get; init; }
    public string DigestFolder { get; init; } = "";
    public int DigestTimeoutSeconds { get; init; } = 120;
    public ReviewSettings? Review { get; init; }
    public IReadOnlyList<LongServiceBand> LongService { get; init; } = [];
    public IReadOnlyList<ClaimType> ClaimTypes { get; init; } = DefaultClaimTypes;

    public ClaimType TypeOf(string payslipItem) =>
        ClaimTypes.FirstOrDefault(t => t.Match.IsMatch(payslipItem)) ?? new ClaimType(Pattern("."), ClaimKind.Other, "");

    private static Regex Pattern(string text) =>
        new(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    public static string Local(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "people-first-approvals"));
    }

    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "people-first-approvals", "config.toml"));
    }

    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new UsageException($"No settings file at {full}. Run `people-first-approvals config` to create one.");
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                text = text[1..];
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            return Parse(read.Raw, full, read.Env);
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message.TrimEnd());
        }
    }

    public static Config Parse(TomlTable raw, string full, Func<string, string?> env)
    {
        foreach (var key in raw.Keys)
            if (!KnownKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", KnownKeys)}).");
        var folder = System.IO.Path.GetDirectoryName(full)!;
        string PathSetting(string value)
        {
            var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, env), env);
            return PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(folder, expanded);
        }
        UsageException Bad(string message) => new($"{full}: {message}");
        TomlTable Section(string name)
        {
            if (!raw.TryGetValue(name, out var value))
                return [];
            return value as TomlTable ?? throw Bad($"{name} should be a [{name}] section.");
        }
        void Only(TomlTable table, string name, params string[] known)
        {
            foreach (var key in table.Keys)
                if (!known.Contains(key))
                    throw Bad($"[{name}] has an unknown setting '{key}' (known: {string.Join(", ", known)}).");
        }
        string? Text(TomlTable table, string key, string where) =>
            table.TryGetValue(key, out var v) ? v as string is { Length: > 0 } s ? s : throw Bad($"{where} should be text, and not empty.") : null;
        int Whole(TomlTable table, string key, string where, int fallback, int min, int max) =>
            table.TryGetValue(key, out var v) ? v is long n && n >= min && n <= max ? (int)n : throw Bad($"{where} should be a whole number from {min} to {max}.") : fallback;
        IReadOnlyList<string>? Command(TomlTable table, string key, string where)
        {
            if (!table.TryGetValue(key, out var v))
                return null;
            if (v is not TomlArray array || array.Count == 0 || array.Any(x => x is not string { Length: > 0 }))
                throw Bad($"{where} should be a command, as a list of text: ['program', 'argument', ...].");
            return array.Select(x => (string)x!).ToList();
        }

        var pf = Section("people_first");
        Only(pf, "people_first", "url", "tenant_code", "account", "edge_profile");
        var url = (Text(pf, "url", "people_first.url") ?? throw Bad("[people_first] needs url, as \"https://yourorg.people-first.com\".")).TrimEnd('/');
        if (!url.StartsWith("https://", StringComparison.Ordinal))
            throw Bad("people_first.url should start https://.");
        var tenantCode = Text(pf, "tenant_code", "people_first.tenant_code") ?? PeopleFirstClient.Settings.DefaultTenantCode(url);
        if (tenantCode.Length == 0)
            throw Bad("[people_first] needs tenant_code: it couldn't be worked out from the url.");
        var account = Text(pf, "account", "people_first.account") ?? Text(raw, "login_hint", "login_hint") ?? "";
        var local = Local(env);
        var peopleFirst = new PeopleFirstClient.Settings(url, tenantCode, account,
            Text(pf, "edge_profile", "people_first.edge_profile") is { } profile ? PathSetting(profile) : PyPath.Pure(System.IO.Path.Combine(local, "edge-profile")),
            SignInHint);

        var reader = Section("document_reader");
        Only(reader, "document_reader", "command", "cwd", "timeout_seconds");
        var readerCommand = Command(reader, "command", "document_reader.command");
        if (readerCommand is not null && (!readerCommand.Any(a => a.Contains("{input}", StringComparison.Ordinal))
                                          || !readerCommand.Any(a => a.Contains("{out}", StringComparison.Ordinal))))
            throw Bad("document_reader.command needs {input} (the document's details) and {out} (where the reply goes).");

        var digest = Section("digest");
        Only(digest, "digest", "next", "cwd", "timeout_seconds");

        ReviewSettings? review = null;
        if (raw.TryGetValue("review", out var reviewSection))
            review = ReviewSettings.Read(reviewSection, full, PathSetting);

        var bands = new List<LongServiceBand>();
        if (raw.TryGetValue("long_service", out var ls))
        {
            if (ls is not TomlTableArray lsa)
                throw Bad("long_service should be [[long_service]] tables, each with years and amount.");
            foreach (var band in lsa)
            {
                Only(band, "long_service", "years", "amount", "also");
                var years = Whole(band, "years", "long_service.years", -1, 1, 80);
                if (years < 0)
                    throw Bad("each [[long_service]] needs years.");
                var amount = band.TryGetValue("amount", out var a) ? a switch
                {
                    long n when n >= 0 => (decimal)n,
                    double d when d >= 0 => (decimal)d,
                    _ => throw Bad("long_service.amount should be a number of pounds, as 250."),
                } : throw Bad("each [[long_service]] needs amount.");
                bands.Add(new LongServiceBand(years, amount, Text(band, "also", "long_service.also") ?? ""));
            }
            if (bands.Select(b => b.Years).Distinct().Count() != bands.Count)
                throw Bad("two [[long_service]] bands have the same years.");
        }

        var types = new List<ClaimType>();
        if (raw.TryGetValue("claim_type", out var ct))
        {
            if (ct is not TomlTableArray cta)
                throw Bad("claim_type should be [[claim_type]] tables, each with match and kind.");
            foreach (var type in cta)
            {
                Only(type, "claim_type", "match", "kind", "note");
                var match = Text(type, "match", "claim_type.match") ?? throw Bad("each [[claim_type]] needs match, a pattern for the payslip item's name.");
                Regex pattern;
                try
                {
                    pattern = Pattern(match);
                }
                catch (ArgumentException error)
                {
                    throw Bad($"claim_type.match '{match}' isn't a valid pattern: {error.Message}");
                }
                var kind = (Text(type, "kind", "claim_type.kind") ?? "other") switch
                {
                    "long-service" => ClaimKind.LongService,
                    "expenses" => ClaimKind.Expenses,
                    "overtime" => ClaimKind.Overtime,
                    "other" => ClaimKind.Other,
                    var k => throw Bad($"claim_type.kind '{k}' should be long-service, expenses, overtime or other."),
                };
                types.Add(new ClaimType(pattern, kind, Text(type, "note", "claim_type.note") ?? ""));
            }
        }

        return new Config
        {
            Path = full,
            PeopleFirst = peopleFirst,
            LogFile = Text(raw, "log_file", "log_file") is { } log ? PathSetting(log) : PyPath.Pure(System.IO.Path.Combine(local, "people-first-approvals.log")),
            StateFile = Text(raw, "state_file", "state_file") is { } state ? PathSetting(state) : PyPath.Pure(System.IO.Path.Combine(local, "state.json")),
            Documents = Text(raw, "documents", "documents") is { } docs ? PathSetting(docs) : PyPath.Pure(System.IO.Path.Combine(local, "documents")),
            KeepDays = Whole(raw, "keep_days", "keep_days", 30, 1, 365),
            ReaderCommand = readerCommand,
            ReaderFolder = Text(reader, "cwd", "document_reader.cwd") is { } rc ? PathSetting(rc) : folder,
            ReaderTimeoutSeconds = Whole(reader, "timeout_seconds", "document_reader.timeout_seconds", 600, 10, 3600),
            DigestNext = Command(digest, "next", "digest.next"),
            DigestFolder = Text(digest, "cwd", "digest.cwd") is { } dc ? PathSetting(dc) : folder,
            DigestTimeoutSeconds = Whole(digest, "timeout_seconds", "digest.timeout_seconds", 120, 5, 3600),
            Review = review,
            LongService = bands.OrderBy(b => b.Years).ToList(),
            ClaimTypes = types.Count > 0 ? types : DefaultClaimTypes,
        };
    }

    public static string CreateDefault(string? path, Func<string, string?>? env = null)
    {
        var full = PyPath.Resolve(!string.IsNullOrEmpty(path) ? path : DefaultPath(env));
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Template.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
        }
        return full;
    }

    internal static string Money(decimal amount) => "£" + amount.ToString("#,0.00", CultureInfo.InvariantCulture);
}
