using System.Text.Json;
using System.Text.Json.Nodes;
using PeopleFirstClient;

namespace PeopleFirstApprovals.Tests;

public class ConfigTests
{
    [Fact]
    public void The_template_loads()
    {
        using var box = new Sandbox();
        var path = Path.Combine(box.Dir, "new", "config.toml");
        Config.CreateDefault(path, _ => null);
        var config = Config.Load(path, _ => null);
        Assert.Equal("https://yourorg.people-first.com", config.PeopleFirst.Url);
        Assert.Equal(250m, Assert.Single(config.LongService).Amount);
        Assert.Null(config.ReaderCommand);
        Assert.Equal(30, config.KeepDays);
    }

    [Fact]
    public void Settings_are_read_strictly()
    {
        using var box = new Sandbox("surprise = 1");
        var error = Assert.Throws<UsageException>(() => box.Config);
        Assert.Contains("[long_service] has an unknown setting 'surprise'", error.Message);
    }

    [Fact]
    public void Bands_and_claim_types()
    {
        using var box = new Sandbox("""

            [[claim_type]]
            match = 'gift'
            kind = "expenses"
            note = "Agreed?"
            """);
        var config = box.Config;
        Assert.Equal([10, 20], config.LongService.Select(b => b.Years));
        Assert.Equal(ClaimKind.Expenses, config.TypeOf("Team gift").Kind);
        Assert.Equal(ClaimKind.Other, config.TypeOf("Long Service Awards").Kind);   // the settings' types replace the defaults
        Assert.Equal(ClaimKind.LongService, Config.DefaultClaimTypes[0].Kind);
    }

    [Fact]
    public void The_reader_command_needs_its_placeholders()
    {
        using var box = new Sandbox();
        File.WriteAllText(box.Settings, File.ReadAllText(box.Settings).Replace("'--out', '{out}'", "'--out', 'x'"));
        Assert.Contains("{out}", Assert.Throws<UsageException>(() => box.Config).Message);
    }
}

public class CheckTests
{
    private static Assessment Assess(Sandbox box, FakePeopleFirst pf, DocumentFacts? doc, string? problem = null, string today = "2026-11-20") =>
        Checks.Assess(box.Config, Claims.Read(pf, Fixture.Notification), doc, problem, DateOnly.Parse(today));

    private static DocumentFacts Doc(decimal total, string billTo = "Mr Alex Example") =>
        new(total, "GBP", "Made Up Supplies Ltd", billTo, "2026-11-16", "42", ["A video doorbell"], "yes");

    [Fact]
    public void A_matching_receipt_and_enough_service_is_approve()
    {
        using var box = new Sandbox();
        var a = Assess(box, Fixture.LongService(), Doc(247.52m));
        Assert.Equal(Assessment.Approve, a.Recommendation);
        Assert.Contains(a.Checks, c => c.Name == "Service" && c.Mark == Mark.Ok && c.Text.StartsWith("10 years 5 months", StringComparison.Ordinal));
        Assert.Contains(a.Checks, c => c.Name == "Deadline" && c.Text.Contains("Fri 27 Nov 2026", StringComparison.Ordinal));
    }

    [Fact]
    public void Short_of_the_first_band_is_reject()
    {
        using var box = new Sandbox();
        var a = Assess(box, Fixture.LongService(started: "2017-01-01"), Doc(247.52m));
        Assert.Equal(Assessment.Reject, a.Recommendation);
        Assert.Contains("the first award is at 10 years", a.Why);
    }

    [Fact]
    public void More_than_the_band_allows_or_a_different_total_is_ask()
    {
        using var box = new Sandbox();
        Assert.Contains("up to £250.00", Assess(box, Fixture.LongService(amount: 300m), Doc(300m)).Why);
        var differs = Assess(box, Fixture.LongService(), Doc(200m));
        Assert.Equal(Assessment.Ask, differs.Recommendation);
        Assert.Contains("total £200.00, but the claim is £247.52", differs.Why);
    }

    [Fact]
    public void Made_out_to_someone_else_is_ask()
    {
        using var box = new Sandbox();
        var a = Assess(box, Fixture.LongService(), Doc(247.52m, billTo: "Sam Placeholder"));
        Assert.Equal(Assessment.Ask, a.Recommendation);
        Assert.Contains("not the claimant", a.Why);
    }

    [Fact]
    public void No_document_or_one_unread_is_ask()
    {
        using var box = new Sandbox();
        Assert.Contains("nothing is attached", Assess(box, Fixture.LongService(attachment: false), null, "nothing is attached").Why);
        Assert.Contains("not read: the reader failed", Assess(box, Fixture.LongService(), null, "the reader failed").Why);
    }

    [Fact]
    public void Overtime_needs_you_and_expects_no_document()
    {
        using var box = new Sandbox();
        var a = Assess(box, Fixture.LongService(item: "Overtime", attachment: false), null, "nothing is attached");
        Assert.Equal(Assessment.Ask, a.Recommendation);
        Assert.Contains(a.Checks, c => c.Name == "Document" && c.Mark == Mark.Note);
        Assert.Contains("agreed beforehand", a.Why);
    }

    [Fact]
    public void Another_claim_for_the_same_item_in_the_pay_run_is_ask()
    {
        using var box = new Sandbox();
        var pf = Fixture.LongService(otherClaims: """, {"id": "other", "payslipItemName": "Long Service Awards", "approvalState": 2}""");
        var a = Assess(box, pf, Doc(247.52m));
        Assert.Equal(Assessment.Ask, a.Recommendation);
        Assert.Contains("1 other Long Service Awards claim(s) in this pay run (approved)", a.Why);
    }

    [Fact]
    public void Past_the_processing_date_is_only_a_note()
    {
        using var box = new Sandbox();
        var a = Assess(box, Fixture.LongService(), Doc(247.52m), today: "2026-11-28");
        Assert.Equal(Assessment.Approve, a.Recommendation);
        Assert.Contains(a.Checks, c => c.Name == "Deadline" && c.Mark == Mark.Note);
    }

    [Theory]
    [InlineData("2016-06-13", "2026-11-19", 10, 5)]
    [InlineData("2016-06-13", "2026-06-12", 9, 11)]
    [InlineData("2016-06-13", "2026-06-13", 10, 0)]
    [InlineData("2026-01-01", "2025-01-01", 0, 0)]
    public void Service_in_completed_years_and_months(string from, string to, int years, int months) =>
        Assert.Equal((years, months), Checks.Service(DateOnly.Parse(from), DateOnly.Parse(to)));
}

public class DocumentTests
{
    [Fact]
    public void A_reply_is_read_from_its_json()
    {
        var (facts, problem) = Documents.Parse("""
            JSON:
            {"total": "£1,247.50", "payee": "Made Up Ltd", "bill_to": "A Example", "date": "2026-11-16", "invoice_number": "9",
             "items": ["One thing", ""], "paid": "yes"}
            """);
        Assert.Null(problem);
        Assert.Equal(1247.50m, facts!.Total);
        Assert.Equal(["One thing"], facts.Items);
        Assert.Contains("no JSON", Documents.Parse("nothing here").Problem);
    }

    [Fact]
    public void File_kinds_from_their_first_bytes()
    {
        Assert.Equal("pdf", Documents.Kind("%PDF-1.7"u8.ToArray()));
        Assert.Equal("png", Documents.Kind([0x89, (byte)'P', (byte)'N', (byte)'G']));
        Assert.Equal("jpg", Documents.Kind([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(Documents.Kind("{\"data\""u8.ToArray()));
    }

    [Fact]
    public void Documents_older_than_keep_days_go()
    {
        using var box = new Sandbox();
        var config = box.Config;
        var old = Directory.CreateDirectory(Path.Combine(config.Documents, "old")).FullName;
        Directory.SetCreationTime(old, box.Now.AddDays(-31));
        Directory.CreateDirectory(Path.Combine(config.Documents, "new"));
        Assert.Equal(1, Documents.Expire(config, box.Now));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(Path.Combine(config.Documents, "new")));
    }
}

public class RunTests
{
    [Fact]
    public void Check_raises_one_item_reads_the_document_once_and_keeps_only_ids()
    {
        using var box = new Sandbox();
        box.PeopleFirst = Fixture.LongService();
        box.ReaderReplies();
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "check", Fixture.Notification], box.Host()));
        Assert.Contains("recommendation: Approve", box.Out.ToString());
        var item = Assert.Single(box.ReviewItems())!.AsObject();
        Assert.Equal("claim", item["type"]!.ToString());
        Assert.Equal("Long Service Awards claim: Alex Example, £247.52: Approve", item["title"]!.ToString());
        Assert.Equal("2026-11-27", item["approve_by"]!.ToString());
        var reader = Assert.Single(box.Ran);
        Assert.EndsWith("reader-input.json", reader.Command[2]);

        // Checked again: the reply already there is used, and the item is replaced, not added to.
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "check", Fixture.Notification], box.Host()));
        Assert.Single(box.Ran);
        Assert.Single(box.ReviewItems());

        var state = box.State().ToJsonString();
        Assert.Contains(Fixture.ClaimId, state);
        Assert.DoesNotContain("Example", state);
        Assert.DoesNotContain("247", state);
        Assert.DoesNotContain("Example", File.ReadAllText(Path.Combine(box.Dir, "log.txt")));
    }

    [Fact]
    public void Show_raises_and_keeps_nothing()
    {
        using var box = new Sandbox();
        box.PeopleFirst = Fixture.LongService();
        box.ReaderReplies();
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "show", Fixture.Notification], box.Host()));
        Assert.Contains("ok: Service", box.Out.ToString());
        Assert.Empty(box.ReviewItems());
        Assert.False(File.Exists(Path.Combine(box.Dir, "state.json")));
        Assert.Empty(Directory.GetDirectories(box.Config.Documents));
    }

    [Fact]
    public void Approve_posts_the_claim_once_reads_it_back_and_tidies_up()
    {
        using var box = new Sandbox();
        var pf = Fixture.LongService();
        box.PeopleFirst = pf;
        box.ReaderReplies();
        Cli.Run(["--config", box.Settings, "check", Fixture.Notification], box.Host());
        Assert.NotEmpty(Directory.GetDirectories(box.Config.Documents));
        pf.OnPost = (_, _) =>
        {
            pf.SetState(Claims.Approved);
            return JsonDocument.Parse("{}").RootElement.Clone();
        };
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "approve", Fixture.Notification], box.Host()));
        var (path, body) = Assert.Single(pf.Posts);
        Assert.Equal($"{Fixture.CashPath}/approved", path);
        Assert.Equal(247.52m, body["data"]!["cashClaim"]!["cash"]!.GetValue<decimal>());
        Assert.Contains("Approved: the Long Service Awards claim for Alex Example, £247.52.", box.Out.ToString());
        Assert.Empty(box.ReviewItems());
        Assert.Empty(Directory.GetDirectories(box.Config.Documents));
        Assert.Equal("approved here", box.State()["claims"]![Fixture.ClaimId]!["outcome"]!.ToString());

        // Asked again: already approved, nothing sent.
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "approve", Fixture.ClaimId], box.Host()));
        Assert.Single(pf.Posts);
    }

    [Fact]
    public void A_write_that_may_have_happened_is_never_sent_again()
    {
        using var box = new Sandbox();
        var pf = Fixture.LongService();
        box.PeopleFirst = pf;
        pf.OnPost = (_, _) => throw new PeopleFirstDeliveryUnknownException("timed out");
        Assert.Equal(1, Cli.Run(["--config", box.Settings, "reject", Fixture.Notification, "--reason", "not yet"], box.Host()));
        Assert.Contains("Sent, but not confirmed", box.Out.ToString());
        Assert.Contains(box.ReviewItems(), i => i!["type"]!.ToString() == "claim-unconfirmed");
        Assert.Equal(1, Cli.Run(["--config", box.Settings, "reject", Fixture.Notification], box.Host()));
        Assert.Single(pf.Posts);
        Assert.Contains("never sent twice", box.Out.ToString());
    }

    [Fact]
    public void A_refusal_records_nothing_so_it_can_be_tried_again()
    {
        using var box = new Sandbox();
        var pf = Fixture.LongService();
        box.PeopleFirst = pf;
        pf.OnPost = (_, _) => throw new PeopleFirstException("People First answered 400") { Status = 400 };
        Assert.Equal(1, Cli.Run(["--config", box.Settings, "approve", Fixture.Notification], box.Host()));
        Assert.Null(box.State()["claims"]![Fixture.ClaimId]!["sending"]);
        Assert.Equal(1, Cli.Run(["--config", box.Settings, "approve", Fixture.Notification], box.Host()));
        Assert.Equal(2, pf.Posts.Count);
    }

    [Fact]
    public void A_dry_run_sends_nothing()
    {
        using var box = new Sandbox();
        box.PeopleFirst = Fixture.LongService();
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "approve", Fixture.Notification, "--dry-run"], box.Host()));
        Assert.Contains("Would approve", box.Out.ToString());
        Assert.Empty(box.PeopleFirst.Posts);
    }

    [Fact]
    public void A_claim_decided_in_People_First_is_tidied_away_by_the_next_check()
    {
        using var box = new Sandbox();
        var pf = Fixture.LongService();
        box.PeopleFirst = pf;
        box.ReaderReplies();
        Cli.Run(["--config", box.Settings, "check", Fixture.Notification], box.Host());
        pf.SetState(Claims.Rejected);
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "check"], box.Host()));
        Assert.Contains("0 claim(s) waiting; 1 decided since last time.", box.Out.ToString());
        Assert.Empty(box.ReviewItems());
        Assert.Empty(Directory.GetDirectories(box.Config.Documents));
    }
}

public class DigestTests
{
    private static string Email(string body) => new JsonObject { ["subject"] = "Today's notifications", ["body"] = body }.ToJsonString();

    private const string Like = "99999999-8888-7777-6666-555555555555";

    private static string Body(bool like) =>
        "Today's notifications\n\n" + (like ? "2 unread notifications\n" : "1 unread notification\n") + "\n" +
        (like ? $"Sam Placeholder liked: \"Well done\" <https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.people-first.com%2Fnotifications%2F{Like}&data=x>\n" : "") +
        "Others\n" +
        $"Long Service Awards claim for Alex Example requires your approval <https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.people-first.com%2Fnotifications%2F{Fixture.Notification}&data=y>\n";

    [Fact]
    public void Ids_come_from_the_links_even_when_encoded()
    {
        Assert.Equal([Like, Fixture.Notification], Runner.NotificationIds(Body(like: true)));
        Assert.Equal(["abcdefab-1234-1234-1234-123456789012"], Runner.NotificationIds("x/notifications/ABCDEFAB-1234-1234-1234-123456789012 y"));
    }

    [Fact]
    public void Only_claims_the_email_can_go_without_the_next_handler()
    {
        using var box = new Sandbox("""

            [digest]
            next = ['team-leave', 'digest']
            """);
        box.PeopleFirst = Fixture.LongService();
        box.ReaderReplies();
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "digest"], box.Host(Email(Body(like: false)))));
        Assert.Contains("1 claim(s) checked (Approve): items raised", box.Out.ToString());
        Assert.DoesNotContain(box.Ran, r => r.Command[0] == "team-leave");
        Assert.Single(box.ReviewItems());
    }

    [Fact]
    public void The_rest_goes_on_with_the_claims_line_out_and_the_count_lowered()
    {
        using var box = new Sandbox("""

            [digest]
            next = ['team-leave', 'digest']
            """);
        var pf = Fixture.LongService();
        Fixture.AddLike(pf, Like);
        box.PeopleFirst = pf;
        box.ReaderReplies();
        var reader = box.Answer;
        box.Answer = (command, input) => command[0] == "team-leave" ? new CommandResult(0, "only likes (1)\n", "") : reader(command, input);
        Assert.Equal(0, Cli.Run(["--config", box.Settings, "digest"], box.Host(Email(Body(like: true)))));
        var (_, forwarded) = box.Ran.Single(r => r.Command[0] == "team-leave");
        var body = JsonNode.Parse(forwarded!)!["body"]!.ToString();
        Assert.Contains("1 unread notification", body);
        Assert.DoesNotContain("Long Service", body);
        Assert.Contains("liked:", body);
        Assert.Contains("1 claim(s) checked (Approve); only likes (1)", box.Out.ToString());
    }

    [Fact]
    public void The_next_handlers_answer_is_the_answer()
    {
        using var box = new Sandbox("""

            [digest]
            next = ['team-leave', 'digest']
            """);
        var pf = Fixture.LongService();
        Fixture.AddLike(pf, Like);
        box.PeopleFirst = pf;
        box.Answer = (command, _) => command[0] == "team-leave" ? new CommandResult(75, "not yet seen", "") : new CommandResult(1, "", "no reader");
        Assert.Equal(75, Cli.Run(["--config", box.Settings, "digest"], box.Host(Email(Body(like: true)))));
    }

    [Fact]
    public void Without_a_next_handler_anything_else_is_raised()
    {
        using var box = new Sandbox();
        var pf = Fixture.LongService();
        Fixture.AddLike(pf, Like);
        box.PeopleFirst = pf;
        box.ReaderReplies();
        Assert.Equal(1, Cli.Run(["--config", box.Settings, "digest"], box.Host(Email(Body(like: true)))));
        Assert.Contains("the rest raised as usual", box.Out.ToString());
    }

    [Fact]
    public void People_First_failing_is_try_again_and_a_sign_in_is_raised()
    {
        using var box = new Sandbox();
        var host = box.Host(Email(Body(like: false)));
        var failing = new CliHost
        {
            In = host.In, Out = host.Out, Err = host.Err, Env = host.Env, Now = host.Now, Lock = false, Run = host.Run,
            OpenPeopleFirst = (_, _) => throw new HttpRequestException("no network"),
        };
        Assert.Equal(75, Cli.Run(["--config", box.Settings, "digest"], failing));
        var signIn = new CliHost
        {
            In = new StringReader(Email(Body(like: false))), Out = host.Out, Err = host.Err, Env = host.Env, Now = host.Now, Lock = false, Run = host.Run,
            OpenPeopleFirst = (_, _) => throw new SignInNeededException("sign in"),
        };
        Assert.Equal(1, Cli.Run(["--config", box.Settings, "digest"], signIn));
    }

    [Fact]
    public void Not_json_is_raised()
    {
        using var box = new Sandbox();
        Assert.Equal(1, Cli.Run(["--config", box.Settings, "digest"], box.Host("not json")));
    }
}
