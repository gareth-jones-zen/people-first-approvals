using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PeopleFirstClient;

namespace PeopleFirstApprovals.Tests;

/// <summary>People First with no People First: GETs answered from a dictionary of paths (query included), files from
/// another, POSTs recorded and answered by a function. Everyone in it is made up.</summary>
internal sealed class FakePeopleFirst : IPeopleFirst
{
    public Dictionary<string, string> Json { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<(string Path, JsonNode Body)> Posts { get; } = [];
    public Func<string, JsonNode, JsonElement>? OnPost { get; set; }
    public List<string> Gets { get; } = [];

    public JsonElement Get(string path)
    {
        path = Claims.Rooted(path);
        Gets.Add(path);
        if (!Json.TryGetValue(path, out var body))
            throw new PeopleFirstException($"People First answered 404 to {path}") { Status = 404 };
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    public byte[] GetFile(string path, string accept) =>
        Files.TryGetValue(Claims.Rooted(path), out var bytes) ? bytes : throw new PeopleFirstException("404") { Status = 404 };

    public JsonElement Post(string path, JsonNode body)
    {
        Posts.Add((Claims.Rooted(path), body.DeepClone()));
        if (OnPost is null)
            throw new InvalidOperationException("no POST expected");
        return OnPost(path, body);
    }

    public void Dispose()
    {
    }

    public void SetState(int state)
    {
        foreach (var key in Json.Keys.Where(k => k.Contains("/claims/cash/", StringComparison.Ordinal)).ToList())
            Json[key] = Json[key].Replace("\"approvalState\": 1", $"\"approvalState\": {state}");
    }
}

/// <summary>A claim as People First shapes it: the notification, the claim, the typed (cash) claim with its links,
/// its attachments and file, the person, their employment period, the pay run and the pay run's claims.</summary>
internal static class Fixture
{
    public const string Notification = "11111111-2222-3333-4444-555555555555";
    public const string Person = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string Payroll = "bbbbbbbb-0000-0000-0000-000000000002";
    public const string ClaimId = "cccccccc-0000-0000-0000-000000000003";
    public const string PayRun = "dddddddd-0000-0000-0000-000000000004";
    public const string Attachment = "eeeeeeee-0000-0000-0000-000000000005";

    public static string ClaimPath => $"/payroll/personOnPayroll/{Payroll}/claims/{ClaimId}";
    public static string CashPath => $"/payroll/personOnPayroll/{Payroll}/claims/cash/{ClaimId}";
    public static string AttachmentsPath => $"/payroll/personOnPayrolls/{Payroll}/claims/{ClaimId}/attachments";
    public static string FilePath => $"{AttachmentsPath}/{Attachment}";

    public static FakePeopleFirst LongService(decimal amount = 247.52m, string started = "2016-06-13", string item = "Long Service Awards",
        bool attachment = true, string otherClaims = "")
    {
        var pf = new FakePeopleFirst();
        pf.Json[$"/notifications/{Notification}"] = """
            {"data": {"notification": {"_links": {"claim": {"href": "@0@"}}, "id": "@1@",
              "type": "ClaimApprovalRequest", "title": "Claim request",
              "text": "@2@ claim for Alex Example requires your approval", "entityKey": "@3@", "entity": "Person"}}}
            """;
        pf.Json[ClaimPath] = """
            {"data": {"claims": {"id": "@4@", "payslipItemName": "@2@", "dateEarned": "2026-11-19", "approvalState": 1,
              "value": null, "hasAttachment": false,
              "_links": {"claim": {"href": "@5@"}, "person": {"href": "/payroll/people/@3@"},
                         "payRun": {"href": "/payroll/payRuns/@6@"}}}}}
            """;
        pf.Json[CashPath] = """
            {"data": {"cashClaim": {"personOnPayrollId": "@7@", "approvalState": 1, "dateEarned": "2026-11-19",
              "cash": @8@, "reason": "Long service award",
              "_links": {"self": {"href": "@5@"}, "attachments": {"href": "@9@"}}}},
             "meta": {"links": {"approve": {"href": "@5@/approved"}, "reject": {"href": "@5@/rejected"}}}}
            """;
        pf.Json[AttachmentsPath] = attachment ? """
            {"data": {"attachments": [{"_links": {"download": {"href": "@10@"}}, "documentName": "INVOICE-42.pdf",
              "filename": "INVOICE-42.pdf", "authorName": "Alex Example", "fileData": ""}]}}
            """ : """{"data": {"attachments": []}}""";
        pf.Files[FilePath] = Encoding.ASCII.GetBytes("%PDF-1.3 made up");
        pf.Json[$"/hrm/people/{Person}"] = """
            {"data": {"person": {"personalDetails": {"firstName": "Alexander", "knownAs": "Alex", "lastName": "Example"}}}}
            """;
        pf.Json[$"/hrm/people/{Person}/employmentperiods"] = """
            {"data": {"employmentperiods": [{"employmentStatus": "Active", "leavingDate": "", "startDate": "@11@",
              "reckonableServiceDate": "@11@"}]}}
            """;
        pf.Json[$"/payroll/payRuns/{PayRun}"] = """
            {"data": {"payRun": {"paymentDate": "2026-11-30", "paymentProcessingDate": "2026-11-27", "stateName": "Open"}}}
            """;
        pf.Json[$"/payroll/personOnPayroll/{Payroll}/claims?payRunId={PayRun}"] = """
            {"data": {"claims": [{"id": "@4@", "payslipItemName": "@2@", "dateEarned": "2026-11-19", "approvalState": 1}@12@]}}
            """;
        var tokens = new Dictionary<string, string>
        {
            ["@0@"] = ClaimPath, ["@1@"] = Notification, ["@2@"] = item, ["@3@"] = Person, ["@4@"] = ClaimId, ["@5@"] = CashPath,
            ["@6@"] = PayRun, ["@7@"] = Payroll, ["@8@"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["@9@"] = AttachmentsPath, ["@10@"] = FilePath, ["@11@"] = started, ["@12@"] = otherClaims,
        };
        foreach (var key in pf.Json.Keys.ToList())
            foreach (var (token, value) in tokens)
                pf.Json[key] = pf.Json[key].Replace(token, value, StringComparison.Ordinal);
        return pf;
    }

    /// <summary>A notification that isn't a claim (a like).</summary>
    public static void AddLike(FakePeopleFirst pf, string id) =>
        pf.Json[$"/notifications/{id}"] = """{"data": {"notification": {"id": "@13@", "type": "SocialLike", "text": "Sam Placeholder liked: \"Well done\""}}}""".Replace("@13@", id, StringComparison.Ordinal);
}

/// <summary>A settings file in a temp folder, and a host that records what it ran.</summary>
internal sealed class Sandbox : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("pfa-test-").FullName;
    public string Settings => Path.Combine(Dir, "config.toml");
    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();
    public List<(IReadOnlyList<string> Command, string? Input)> Ran { get; } = [];
    public Func<IReadOnlyList<string>, string?, CommandResult> Answer { get; set; } = (_, _) => new CommandResult(0, "", "");
    public FakePeopleFirst? PeopleFirst { get; set; }
    public DateTime Now { get; set; } = new(2026, 11, 20, 10, 0, 0);

    public Sandbox(string extra = "")
    {
        var d = Dir.Replace('\\', '/');
        File.WriteAllText(Settings, $"""
            log_file = "{d}/log.txt"
            state_file = "{d}/state.json"
            documents = "{d}/documents"
            keep_days = 30

            [people_first]
            url = "https://example.people-first.com"
            edge_profile = "{d}/edge"
            account = "you@example.com"

            [document_reader]
            command = ['reader', '--input', '{"{input}"}', '--out', '{"{out}"}']

            [review]
            file = "{d}/review.json"
            source = "people-first-approvals"

            [[long_service]]
            years = 10
            amount = 250

            [[long_service]]
            years = 20
            amount = 1000
            {extra}
            """);
    }

    public Config Config => PeopleFirstApprovals.Config.Load(Settings, _ => null);

    public CliHost Host(string input = "") => new()
    {
        In = new StringReader(input),
        Out = Out,
        Err = Err,
        Env = _ => null,
        Now = () => Now,
        Lock = false,
        OpenPeopleFirst = (_, _) => PeopleFirst ?? throw new InvalidOperationException("no People First"),
        Run = (command, _, input, _) =>
        {
            Ran.Add((command, input));
            return Answer(command, input);
        },
    };

    /// <summary>A reader that writes a reply with this total and payee into {out}.</summary>
    public void ReaderReplies(string total = "247.52", string billTo = "Mr Alex Example")
    {
        Answer = (command, _) =>
        {
            var output = command[command.ToList().IndexOf("--out") + 1];
            File.WriteAllText(output, $$"""
                JSON:
                {"total": {{total}}, "currency": "GBP", "payee": "Made Up Supplies Ltd", "bill_to": "{{billTo}}", "date": "2026-11-16",
                 "invoice_number": "42", "items": ["A video doorbell"], "paid": "yes"}
                """);
            return new CommandResult(0, "", "");
        };
    }

    public JsonArray ReviewItems() =>
        File.Exists(Path.Combine(Dir, "review.json"))
            ? JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "review.json")))!["items"]!.AsArray()
            : [];

    public JsonObject State() => JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "state.json")))!.AsObject();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
