using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PeopleFirstClient;

namespace PeopleFirstApprovals;

/// <summary>People First, as this tool uses it: GETs, a file, and the one write (approve or reject), never resent.</summary>
public interface IPeopleFirst : IDisposable
{
    JsonElement Get(string path);
    /// <summary>A GET, with the record's ETag (null if none): a write to it must send it back as If-Match.</summary>
    (JsonElement Body, string? ETag) GetWithEtag(string path);
    byte[] GetFile(string path, string accept);
    JsonElement Post(string path, JsonNode body, IReadOnlyDictionary<string, string>? headers);
}

/// <summary>PeopleFirstClient's Api, signed in.</summary>
public sealed class PeopleFirstApi(Api api) : IPeopleFirst
{
    public JsonElement Get(string path) => api.GetAsync(Claims.Rooted(path)).GetAwaiter().GetResult();
    public byte[] GetFile(string path, string accept) => api.GetBytesAsync(Claims.Rooted(path), accept).GetAwaiter().GetResult();
    public (JsonElement Body, string? ETag) GetWithEtag(string path) => api.GetWithEtagAsync(Claims.Rooted(path)).GetAwaiter().GetResult();
    public JsonElement Post(string path, JsonNode body, IReadOnlyDictionary<string, string>? headers) =>
        api.PostAsync(Claims.Rooted(path), body, headers).GetAwaiter().GetResult();
    public void Dispose() => api.Dispose();
}

/// <summary>A file attached to a claim: its name and where to download it.</summary>
public sealed record Attachment(string Name, string Download);

/// <summary>Another claim for the same payslip item in the same pay run.</summary>
public sealed record OtherClaim(string Id, DateOnly? Earned, int State);

/// <summary>A notification, and (for a claim) everything the checks need, read from People First. Nothing here is
/// written to state or the log except the ids.</summary>
public sealed class Claim
{
    public required string NotificationId { get; init; }
    public required string NotificationType { get; init; }
    public string Text { get; init; } = "";
    public bool IsClaim { get; init; }
    public string ClaimId { get; init; } = "";
    public string ClaimPath { get; init; } = "";
    public string TypedPath { get; init; } = "";
    public string PayrollPersonId { get; init; } = "";
    public string Item { get; init; } = "";
    public DateOnly? Earned { get; init; }
    public int State { get; init; }
    public decimal? Amount { get; init; }
    public string Reason { get; init; } = "";
    public string PersonId { get; init; } = "";
    public string Person { get; init; } = "";
    public string LastName { get; init; } = "";
    public DateOnly? ServiceFrom { get; init; }
    public DateOnly? LeftOn { get; init; }
    public DateOnly? ApproveBy { get; init; }
    public DateOnly? PaidOn { get; init; }
    public string PayRunId { get; init; } = "";
    public string PayRunState { get; init; } = "";
    public IReadOnlyList<Attachment> Attachments { get; init; } = [];
    public IReadOnlyList<OtherClaim>? SameItem { get; init; }
    public string? ApproveLink { get; init; }
    public string? RejectLink { get; init; }
    /// <summary>The typed claim's data, as People First gave it: what approving or rejecting sends back.</summary>
    public JsonNode? TypedData { get; init; }
    /// <summary>Parts that couldn't be read (attachments, the person, the pay run): each a short reason.</summary>
    public IReadOnlyList<string> Unread { get; init; } = [];

    public bool Awaiting => State == Claims.AwaitingApproval;
}

/// <summary>Reading a notification and its claim. People First's API is hypermedia: each record links to the next, so
/// the paths come from the records themselves.</summary>
public static class Claims
{
    // A claim's approvalState, as People First's web app names them.
    public const int AwaitingApproval = 1;
    public const int Approved = 2;
    public const int AutoApproved = 3;
    public const int Rejected = 4;

    /// <summary>The notification types that ask you to approve a claim.</summary>
    public static readonly string[] ClaimTypes = ["ClaimApprovalRequest", "ClaimApprovalRequestToReportingManager"];

    /// <summary>What People First is asked for when downloading an attached file: its own type, never JSON (which
    /// gets the attachment's details back).</summary>
    public const string FileAccept = "application/pdf, image/png, image/jpeg, image/*;q=0.8";

    public static string StateName(int state) => state switch
    {
        AwaitingApproval => "awaiting approval",
        Approved => "approved",
        AutoApproved => "approved automatically",
        Rejected => "rejected",
        _ => $"state {state}",
    };

    /// <summary>A link from a record (some start with "/", some don't), as a path under /api/v1.</summary>
    public static string Rooted(string path) => path.StartsWith('/') ? path : "/" + path;

    internal static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    internal static string Str(JsonElement e, string name) => Prop(e, name) is { } v
        ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    internal static string? Link(JsonElement e, string name) =>
        Prop(e, "_links") is { } links && Prop(links, name) is { } link && Str(link, "href") is { Length: > 0 } href ? href : null;

    internal static string? MetaLink(JsonElement root, string name) =>
        Prop(root, "meta") is { } meta && Prop(meta, "links") is { } links && Prop(links, name) is { } link
            && Str(link, "href") is { Length: > 0 } href ? href : null;

    /// <summary>The single record under "data" (People First wraps each in its type's name).</summary>
    internal static JsonElement Record(JsonElement root, string? name = null)
    {
        var data = Prop(root, "data") ?? throw new PeopleFirstException("People First's reply has no data.");
        if (name is not null && Prop(data, name) is { } named)
            return named;
        foreach (var p in data.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object)
                return p.Value;
        throw new PeopleFirstException("People First's reply has no record in its data.");
    }

    internal static DateOnly? Date(string text) =>
        text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    internal static decimal? Number(JsonElement e, string name) => Prop(e, name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDecimal(),
        { ValueKind: JsonValueKind.String } s when decimal.TryParse(s.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    internal static int Whole(JsonElement e, string name) => Number(e, name) is { } n ? (int)n : 0;

    /// <summary>The id after a segment of a path: ("…/personOnPayroll/ABC/claims/…", "personOnPayroll") is ABC.</summary>
    internal static string After(string path, string segment)
    {
        var parts = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals(segment, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : "";
    }

    /// <summary>A notification and, if it asks you to approve a claim, the claim with everything around it. The
    /// notification, the claim and the typed claim must be readable; anything else that can't be read is noted in
    /// <see cref="Claim.Unread"/>, so the checks say what they couldn't do.</summary>
    public static Claim Read(IPeopleFirst pf, string notificationId)
    {
        var notification = Record(pf.Get($"/notifications/{notificationId}"), "notification");
        var type = Str(notification, "type");
        var text = Str(notification, "text");
        if (!ClaimTypes.Contains(type) || Link(notification, "claim") is not { } claimPath)
            return new Claim { NotificationId = notificationId, NotificationType = type, Text = text };
        var personId = Str(notification, "entity") is "Person" or "" ? Str(notification, "entityKey") : "";
        return ReadClaim(pf, claimPath, notificationId, type, text, personId);
    }

    /// <summary>A claim by its path (from a notification, or remembered in the state file).</summary>
    public static Claim ReadClaim(IPeopleFirst pf, string claimPath, string notificationId, string type, string text, string personId)
    {
        var unread = new List<string>();
        var claim = Record(pf.Get(claimPath), "claims");
        var typedPath = Link(claim, "claim") ?? claimPath;
        var typedRoot = pf.Get(typedPath);
        var typed = Record(typedRoot);
        var typedData = JsonNode.Parse(Prop(typedRoot, "data")!.Value.GetRawText());
        var amount = Number(typed, "cash") ?? Number(typed, "fixedValue") ?? Number(claim, "value");

        var attachments = new List<Attachment>();
        if (Link(typed, "attachments") is { } attachmentsPath)
        {
            try
            {
                var data = Prop(pf.Get(attachmentsPath), "data");
                if (data is { } d && Prop(d, "attachments") is { ValueKind: JsonValueKind.Array } list)
                    foreach (var a in list.EnumerateArray())
                        if (Link(a, "download") is { } download)
                            attachments.Add(new Attachment(Str(a, "documentName") is { Length: > 0 } n ? n : Str(a, "filename"), download));
            }
            catch (PeopleFirstException error)
            {
                unread.Add($"its attachments ({error.Status?.ToString(CultureInfo.InvariantCulture) ?? error.Message})");
            }
        }

        var (person, lastName) = ("", "");
        DateOnly? serviceFrom = null, leftOn = null;
        if (personId.Length > 0)
        {
            try
            {
                var record = Record(pf.Get($"/hrm/people/{personId}"));
                var holder = FindWith(record, "lastName") ?? record;
                var first = Str(holder, "knownAs") is { Length: > 0 } known ? known : Str(holder, "firstName");
                lastName = Str(holder, "lastName");
                person = $"{first} {lastName}".Trim();
            }
            catch (PeopleFirstException error)
            {
                unread.Add($"the person ({error.Status?.ToString(CultureInfo.InvariantCulture) ?? error.Message})");
            }
            try
            {
                var data = Prop(pf.Get($"/hrm/people/{personId}/employmentperiods"), "data");
                var periods = data is { } d && Prop(d, "employmentperiods") is { ValueKind: JsonValueKind.Array } list
                    ? list.EnumerateArray().ToList() : [];
                var current = periods.OrderByDescending(p => Str(p, "startDate"), StringComparer.Ordinal)
                    .FirstOrDefault(p => Str(p, "leavingDate").Length == 0);
                if (current.ValueKind == JsonValueKind.Object)
                {
                    serviceFrom = Date(Str(current, "reckonableServiceDate")) ?? Date(Str(current, "startDate"));
                    leftOn = Date(Str(current, "leavingDate"));
                }
                else
                    unread.Add("a current employment period");
            }
            catch (PeopleFirstException error)
            {
                unread.Add($"the employment periods ({error.Status?.ToString(CultureInfo.InvariantCulture) ?? error.Message})");
            }
        }
        else
            unread.Add("who the claim is for (the notification names no person)");

        DateOnly? approveBy = null, paidOn = null;
        var payRunState = "";
        var payRunPath = Link(claim, "payRun");
        var payRunId = payRunPath is null ? "" : After(payRunPath, "payRuns");
        if (payRunPath is not null)
        {
            try
            {
                var run = Record(pf.Get(payRunPath), "payRun");
                approveBy = Date(Str(run, "paymentProcessingDate"));
                paidOn = Date(Str(run, "paymentDate"));
                payRunState = Str(run, "stateName");
            }
            catch (PeopleFirstException error)
            {
                unread.Add($"the pay run ({error.Status?.ToString(CultureInfo.InvariantCulture) ?? error.Message})");
            }
        }

        var payrollPerson = After(claimPath, "personOnPayroll");
        List<OtherClaim>? sameItem = null;
        if (payrollPerson.Length > 0 && payRunId.Length > 0)
        {
            try
            {
                var data = Prop(pf.Get($"/payroll/personOnPayroll/{payrollPerson}/claims?payRunId={payRunId}"), "data");
                sameItem = [];
                if (data is { } d && Prop(d, "claims") is { ValueKind: JsonValueKind.Array } list)
                    foreach (var other in list.EnumerateArray())
                        if (Str(other, "id") is var id && id != Str(claim, "id")
                            && string.Equals(Str(other, "payslipItemName"), Str(claim, "payslipItemName"), StringComparison.OrdinalIgnoreCase))
                            sameItem.Add(new OtherClaim(id, Date(Str(other, "dateEarned")), Whole(other, "approvalState")));
            }
            catch (PeopleFirstException error)
            {
                unread.Add($"the pay run's other claims ({error.Status?.ToString(CultureInfo.InvariantCulture) ?? error.Message})");
            }
        }

        return new Claim
        {
            NotificationId = notificationId,
            NotificationType = type,
            Text = text,
            IsClaim = true,
            ClaimId = Str(claim, "id"),
            ClaimPath = claimPath,
            TypedPath = typedPath,
            PayrollPersonId = payrollPerson,
            Item = Str(claim, "payslipItemName"),
            Earned = Date(Str(claim, "dateEarned")),
            State = Whole(typed, "approvalState") is var s && s > 0 ? s : Whole(claim, "approvalState"),
            Amount = amount,
            Reason = Str(typed, "reason"),
            PersonId = personId,
            Person = person,
            LastName = lastName,
            ServiceFrom = serviceFrom,
            LeftOn = leftOn,
            ApproveBy = approveBy,
            PaidOn = paidOn,
            PayRunId = payRunId,
            PayRunState = payRunState,
            Attachments = attachments,
            SameItem = sameItem,
            ApproveLink = MetaLink(typedRoot, "approve"),
            RejectLink = MetaLink(typedRoot, "reject"),
            TypedData = typedData,
            Unread = unread,
        };
    }

    /// <summary>The first object at or below <paramref name="e"/> with a property named <paramref name="name"/>.</summary>
    private static JsonElement? FindWith(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty(name, out _))
                return e;
            foreach (var p in e.EnumerateObject())
                if (p.Name != "_links" && FindWith(p.Value, name) is { } found)
                    return found;
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                if (FindWith(item, name) is { } found)
                    return found;
        return null;
    }

    /// <summary>What approving or rejecting sends, as People First's web app builds it for a record in this (older)
    /// shape: the record's own fields (its links left out) and its id, under the record's type, with no "data" around
    /// it; the URL gets annotate=t; and If-Match carries the ETag of the GET it was read from (without it, 412).</summary>
    public static (string Url, JsonObject Body, Dictionary<string, string> Headers) Write(string link, JsonElement typedRoot, string? etag, string claimId)
    {
        var data = Prop(typedRoot, "data") ?? throw new PeopleFirstException("People First's reply has no data.");
        var (type, record) = data.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => (p.Name, p.Value)).FirstOrDefault();
        if (type is null)
            throw new PeopleFirstException("People First's reply has no record in its data.");
        var fields = JsonNode.Parse(record.GetRawText())!.AsObject();
        fields.Remove("_links");
        fields.Remove("links");
        fields.Remove("meta");
        fields["id"] = claimId;
        var url = link.Contains("annotate=", StringComparison.Ordinal) ? link : link + (link.Contains('?') ? "&" : "?") + "annotate=t";
        var headers = new Dictionary<string, string>();
        if (etag is not null)
            headers["If-Match"] = etag;
        return (url, new JsonObject { [type] = fields }, headers);
    }

    /// <summary>The claim's current approval state, read again (after a write, or to see it's been decided).</summary>
    public static int CurrentState(IPeopleFirst pf, string typedPath) => Whole(Record(pf.Get(typedPath)), "approvalState");
}
