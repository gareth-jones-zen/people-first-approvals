using System.Globalization;
using System.Text.Json.Nodes;

namespace PeopleFirstApprovals;

/// <summary>How a check came out: fine; worth knowing; needs you; or a reason to reject.</summary>
public enum Mark { Ok, Note, Ask, Fail }

public sealed record Check(string Name, Mark Mark, string Text)
{
    public string Line => $"{Mark switch { Mark.Ok => "ok", Mark.Note => "note", Mark.Ask => "check", _ => "no" }}: {Name}: {Text}";
}

/// <summary>A claim's checks and the recommendation they add up to: Reject if any check fails, Ask if any needs you,
/// otherwise Approve.</summary>
public sealed record Assessment(string Recommendation, string Why, IReadOnlyList<Check> Checks, ClaimKind Kind)
{
    public const string Approve = "Approve";
    public const string Ask = "Ask";
    public const string Reject = "Reject";
}

public static class Checks
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    internal static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", En);

    /// <summary>Completed years and months from one date to another.</summary>
    public static (int Years, int Months) Service(DateOnly from, DateOnly to)
    {
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month - (to.Day < from.Day ? 1 : 0);
        return months < 0 ? (0, 0) : (months / 12, months % 12);
    }

    public static Assessment Assess(Config config, Claim claim, DocumentFacts? document, string? documentProblem, DateOnly today)
    {
        var type = config.TypeOf(claim.Item);
        var checks = new List<Check>();
        var amount = claim.Amount;
        if (amount is not { } a || a <= 0)
            checks.Add(new("Amount", Mark.Ask, "the claim has no amount People First could show"));

        // The document: expected for everything but overtime.
        var expected = type.Kind != ClaimKind.Overtime;
        if (document is { } doc)
        {
            var what = string.Join(", ", new[] { doc.Number.Length > 0 ? $"invoice {doc.Number}" : "", doc.Payee.Length > 0 ? $"from {doc.Payee}" : "",
                doc.Date }.Where(s => s.Length > 0));
            if (doc.Total is not { } total)
                checks.Add(new("Document", Mark.Ask, $"{what}: its total couldn't be read"));
            else if (amount is { } claimed && total == claimed)
                checks.Add(new("Document", Mark.Ok, $"{what}: total {Config.Money(total)}, the same as the claim"));
            else
                checks.Add(new("Document", Mark.Ask, $"{what}: total {Config.Money(total)}, but the claim is {(amount is { } c ? Config.Money(c) : "for no amount")}"));
            if (doc.BillTo.Length > 0 && claim.LastName.Length > 0 && !doc.BillTo.Contains(claim.LastName, StringComparison.OrdinalIgnoreCase))
                checks.Add(new("Made out to", Mark.Ask, $"{doc.BillTo}, not the claimant"));
            if (doc.Items.Count > 0)
                checks.Add(new("Bought", Mark.Note, string.Join("; ", doc.Items)));
            if (doc.Paid.Length > 0 && !doc.Paid.StartsWith("y", StringComparison.OrdinalIgnoreCase) && !doc.Paid.Equals("true", StringComparison.OrdinalIgnoreCase))
                checks.Add(new("Paid", Mark.Ask, $"the document doesn't show it as paid ({doc.Paid})"));
        }
        else if (claim.Attachments.Count == 0)
            checks.Add(new("Document", expected ? Mark.Ask : Mark.Note, expected ? "nothing is attached" : "nothing attached (usual for this kind)"));
        else
            checks.Add(new("Document", Mark.Ask, $"{string.Join(", ", claim.Attachments.Select(x => x.Name))} attached, not read: {documentProblem ?? "unknown"}"));

        switch (type.Kind)
        {
            case ClaimKind.LongService:
                checks.Add(LongService(config, claim));
                break;
            case ClaimKind.Other:
                checks.Add(new("Kind", Mark.Ask, $"no rules for {claim.Item} claims yet{(type.Note.Length > 0 ? ": " + type.Note : "")}"));
                break;
        }
        if (type.Kind != ClaimKind.Other && type.Note.Length > 0)
            checks.Add(new("Agreed", Mark.Ask, type.Note));

        if (claim.SameItem is { } same)
        {
            var live = same.Where(o => o.State != Claims.Rejected).ToList();
            checks.Add(live.Count > 0
                ? new("Earlier claims", Mark.Ask, $"{live.Count} other {claim.Item} claim(s) in this pay run ({string.Join(", ", live.Select(o => Claims.StateName(o.State)))})")
                : new("Earlier claims", Mark.Note, $"none in this pay run (People First lists claims one pay run at a time, so earlier ones weren't checked)"));
        }

        if (claim.ApproveBy is { } by)
            checks.Add(today <= by
                ? new("Deadline", Mark.Ok, $"approve by {Day(by)}{(claim.PaidOn is { } paid ? $" to be paid {Day(paid)}" : "")}")
                : new("Deadline", Mark.Note, $"the pay run's processing date ({Day(by)}) has passed: it may go in a later pay run"));
        foreach (var part in claim.Unread)
            checks.Add(new("Unread", Mark.Ask, $"couldn't read {part}"));

        var fail = checks.FirstOrDefault(c => c.Mark == Mark.Fail);
        var ask = checks.FirstOrDefault(c => c.Mark == Mark.Ask);
        var (recommendation, why) = fail is not null ? (Assessment.Reject, $"{fail.Name}: {fail.Text}")
            : ask is not null ? (Assessment.Ask, $"{ask.Name}: {ask.Text}")
            : (Assessment.Approve, "every check passed");
        return new Assessment(recommendation, why, checks, type.Kind);
    }

    /// <summary>Long service: completed years from the reckonable service date to the date the claim was earned,
    /// against the [[long_service]] bands. Under the first band fails; more than the band allows needs you.</summary>
    private static Check LongService(Config config, Claim claim)
    {
        if (config.LongService.Count == 0)
            return new("Service", Mark.Ask, "no [[long_service]] bands are set, so the entitlement wasn't checked");
        if (claim.ServiceFrom is not { } from)
            return new("Service", Mark.Ask, "the start of service couldn't be read");
        var at = claim.Earned ?? DateOnly.FromDateTime(DateTime.Today);
        var (years, months) = Service(from, at);
        var length = $"{years} years {months} months since {from.ToString("d MMM yyyy", En)}";
        var band = config.LongService.LastOrDefault(b => b.Years <= years);
        if (band is null)
            return new("Service", Mark.Fail, $"{length}: the first award is at {config.LongService[0].Years} years");
        var next = config.LongService.FirstOrDefault(b => b.Years > band.Years);
        var nextText = next is null ? "" : $" (the next, {next.Years} years, on {from.AddYears(next.Years).ToString("d MMM yyyy", En)})";
        var also = band.Also.Length > 0 ? $", plus {band.Also}" : "";
        if (claim.Amount is { } amount && amount > band.Amount)
            return new("Service", Mark.Ask, $"{length}: the {band.Years}-year award is up to {Config.Money(band.Amount)}, and this claims {Config.Money(amount)}");
        return new("Service", Mark.Ok, $"{length}: the {band.Years}-year award, up to {Config.Money(band.Amount)}{also}{nextText}");
    }

    /// <summary>The review item: what it is, the recommendation and why, every check, and how to act on it. Names,
    /// the claim type, amounts and dates only: never an address or bank details.</summary>
    public static JsonObject Item(Claim claim, Assessment assessment, string prog)
    {
        var amount = claim.Amount is { } a ? Config.Money(a) : "no amount";
        var fields = new JsonObject
        {
            ["title"] = $"{claim.Item} claim: {(claim.Person.Length > 0 ? claim.Person : "someone")}, {amount}: {assessment.Recommendation}",
            ["recommendation"] = assessment.Recommendation,
            ["why"] = assessment.Why,
            ["checks"] = new JsonArray(assessment.Checks.Select(c => (JsonNode)JsonValue.Create(c.Line)!).ToArray()),
            ["notification"] = claim.NotificationId,
            ["claim"] = claim.ClaimId,
            ["approve"] = $"{prog} approve {claim.NotificationId}",
            ["reject"] = $"{prog} reject {claim.NotificationId}",
        };
        if (claim.ApproveBy is { } by)
            fields["approve_by"] = by.ToString("yyyy-MM-dd", En);
        if (claim.Reason.Length > 0)
            fields["reason_given"] = claim.Reason;
        return fields;
    }

    /// <summary>The item as text, for the command line.</summary>
    public static IEnumerable<string> Describe(Claim claim, Assessment assessment)
    {
        yield return $"{claim.Item} claim for {(claim.Person.Length > 0 ? claim.Person : "someone")}: {(claim.Amount is { } a ? Config.Money(a) : "no amount")}"
                     + $"{(claim.Earned is { } e ? $", earned {e.ToString("d MMM yyyy", En)}" : "")} ({Claims.StateName(claim.State)})";
        if (claim.Reason.Length > 0)
            yield return $"  reason given: {claim.Reason}";
        foreach (var check in assessment.Checks)
            yield return "  " + check.Line;
        yield return $"  recommendation: {assessment.Recommendation} ({assessment.Why})";
    }
}
