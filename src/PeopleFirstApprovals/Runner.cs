using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PeopleFirstClient;
using ReviewQueue;

namespace PeopleFirstApprovals;

/// <summary>The state file: per claim, its ids and paths (People First's ids only: no names, amounts or dates beyond
/// when this tool did something), and what's happened to it.</summary>
public static class State
{
    public static JsonObject Load(Config config)
    {
        if (!File.Exists(config.StateFile))
            return new JsonObject { ["claims"] = new JsonObject() };
        try
        {
            var state = JsonNode.Parse(File.ReadAllText(config.StateFile)) as JsonObject ?? [];
            state["claims"] ??= new JsonObject();
            return state;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"{config.StateFile} isn't valid JSON ({error.Message.Split('\n')[0]}): fix or remove it.");
        }
    }

    public static void Save(Config config, JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(config.StateFile)!);
        var temp = config.StateFile + ".tmp";
        File.WriteAllText(temp, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        File.Move(temp, config.StateFile, overwrite: true);
    }

    public static JsonObject Claims(JsonObject state) => state["claims"] as JsonObject ?? (JsonObject)(state["claims"] = new JsonObject());

    /// <summary>A claim's entry, found by its claim id or the notification that raised it.</summary>
    public static (string? ClaimId, JsonObject? Entry) Find(JsonObject state, string id)
    {
        var claims = Claims(state);
        if (claims[id] is JsonObject direct)
            return (id, direct);
        foreach (var (claimId, node) in claims)
            if (node is JsonObject entry && entry["notification"]?.ToString() == id)
                return (claimId, entry);
        return (null, null);
    }

    public static string Now(DateTime now) => now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>What the command line does: check a claim and raise it, approve or reject one, tidy up, and the digest.</summary>
public static partial class Runner
{
    public const int TryAgain = 75;
    public const string ItemType = "claim";
    public const string UnconfirmedType = "claim-unconfirmed";

    public sealed record Checked(Claim Claim, Assessment? Assessment, bool Raised);

    /// <summary>Check one notification: read it and its claim, download and read the document, assess, and (unless a
    /// dry run) raise or refresh the review item and remember the claim. A notification that isn't a claim, or a claim
    /// already decided, is returned with no assessment.</summary>
    public static Checked Check(Config config, IPeopleFirst pf, string notificationId, CliHost host, JsonObject state, bool dryRun, Action<string> log)
    {
        var claim = Claims.Read(pf, notificationId);
        if (!claim.IsClaim)
        {
            log($"{notificationId}: a {claim.NotificationType} notification, not a claim: left alone");
            return new Checked(claim, null, false);
        }
        if (!claim.Awaiting)
        {
            log($"claim {claim.ClaimId}: already {Claims.StateName(claim.State)}");
            if (!dryRun)
                Decided(config, state, claim.ClaimId, $"{Claims.StateName(claim.State)} in People First", host, log);
            return new Checked(claim, null, false);
        }
        var (path, problem) = Documents.Download(config, pf, claim);
        DocumentFacts? facts = null;
        if (path is not null)
            (facts, problem) = Documents.Read(config, path, claim.ClaimId, host.Run);
        var assessment = Checks.Assess(config, claim, facts, problem, DateOnly.FromDateTime(host.Now()));
        log($"claim {claim.ClaimId}: {assessment.Recommendation}{(path is null ? "" : facts is null ? " (document not read)" : " (document read)")}");
        if (dryRun)
        {
            if (path is not null && !Remembered(state, claim.ClaimId))
                Documents.Delete(config, claim.ClaimId);
            return new Checked(claim, assessment, false);
        }
        var entry = State.Claims(state)[claim.ClaimId] as JsonObject ?? new JsonObject();
        entry["notification"] = notificationId;
        entry["claim_path"] = claim.ClaimPath;
        entry["typed_path"] = claim.TypedPath;
        entry["person"] = claim.PersonId;
        entry["checked"] = State.Now(host.Now());
        entry["recommendation"] = assessment.Recommendation;
        entry["raised"] ??= State.Now(host.Now());
        State.Claims(state)[claim.ClaimId] = entry;
        State.Save(config, state);
        if (config.Review is { } review)
        {
            var queue = review.Open();
            var old = queue.Items(review.Source).Where(i => i["claim"]?.ToString() == claim.ClaimId).Select(i => i["id"]!.ToString()).ToList();
            if (old.Count > 0)
                queue.Done(old);
            queue.Add(review.Source, ItemType, Checks.Item(claim, assessment, Cli.Prog));
        }
        return new Checked(claim, assessment, true);
    }

    private static bool Remembered(JsonObject state, string claimId) => State.Claims(state)[claimId] is JsonObject;

    /// <summary>A claim that's been decided (here or in People First): its documents deleted, its review items gone,
    /// and the state says so.</summary>
    public static void Decided(Config config, JsonObject state, string claimId, string outcome, CliHost host, Action<string> log)
    {
        var deleted = Documents.Delete(config, claimId);
        if (config.Review is { } review)
        {
            var queue = review.Open();
            var ids = queue.Items(review.Source).Where(i => i["claim"]?.ToString() == claimId).Select(i => i["id"]!.ToString()).ToList();
            if (ids.Count > 0)
                queue.Done(ids);
        }
        if (State.Claims(state)[claimId] is JsonObject entry)
        {
            entry["decided"] = State.Now(host.Now());
            entry["outcome"] = outcome;
            entry.Remove("sending");
            State.Save(config, state);
        }
        log($"claim {claimId}: decided ({outcome}){(deleted ? "; documents deleted" : "")}");
    }

    /// <summary>Every remembered claim not yet decided, read again: one decided in People First is tidied away.
    /// Then documents older than keep_days go, and decided claims are forgotten after 90 days.</summary>
    public static int Refresh(Config config, IPeopleFirst? pf, JsonObject state, CliHost host, Action<string> log)
    {
        var decided = 0;
        if (pf is not null)
            foreach (var (claimId, node) in State.Claims(state).ToList())
            {
                if (node is not JsonObject entry || entry["decided"] is not null || entry["typed_path"]?.ToString() is not { Length: > 0 } typed)
                    continue;
                try
                {
                    var current = Claims.CurrentState(pf, typed);
                    if (current != Claims.AwaitingApproval)
                    {
                        Decided(config, state, claimId, $"{Claims.StateName(current)} in People First", host, log);
                        decided++;
                    }
                }
                catch (PeopleFirstException error) when (error.Status == 404)
                {
                    Decided(config, state, claimId, "gone from People First", host, log);
                    decided++;
                }
            }
        var expired = Documents.Expire(config, host.Now());
        if (expired > 0)
            log($"documents: {expired} folder(s) older than {config.KeepDays} days deleted");
        var cutoff = State.Now(host.Now().AddDays(-90));
        var forgotten = State.Claims(state).Where(p => p.Value is JsonObject e && e["decided"]?.ToString() is { } d && string.CompareOrdinal(d, cutoff) < 0)
            .Select(p => p.Key).ToList();
        foreach (var key in forgotten)
            State.Claims(state).Remove(key);
        if (forgotten.Count > 0)
            State.Save(config, state);
        return decided;
    }

    /// <summary>Approve or reject a claim, once. The claim is read first (already decided: nothing sent). A preview
    /// (the default: anything but --yes) stops there: it prints the claim, what the checks find about it now, and what
    /// would be sent, and writes nothing, here or in People First. Otherwise "sending" is recorded before the POST, so
    /// a run that dies mid-write never sends it again; then it's read back. People First's web app approves by POSTing
    /// the claim itself to its approve (or reject) link, as this does.</summary>
    public static int Decide(Config config, IPeopleFirst pf, string id, bool approve, string? reason, bool preview, CliHost host, JsonObject state, Action<string> log)
    {
        var verb = approve ? "approve" : "reject";
        var (knownClaim, entry) = State.Find(state, id);
        if (entry?["sending"] is not null || entry?["sent_unconfirmed"] is not null)
        {
            host.Out.WriteLine($"This claim was already sent to People First ({entry["sending"] ?? entry["sent_unconfirmed"]}) and not confirmed: "
                               + "check it in People First. It's never sent twice.");
            return 1;
        }
        Claim claim;
        if (entry is not null && entry["claim_path"]?.ToString() is { Length: > 0 } claimPath)
            claim = Claims.ReadClaim(pf, claimPath, entry["notification"]?.ToString() ?? "", "ClaimApprovalRequest", "", entry["person"]?.ToString() ?? "");
        else
            claim = Claims.Read(pf, id);
        if (!claim.IsClaim)
        {
            host.Out.WriteLine($"{id} isn't a claim waiting for you (a {claim.NotificationType} notification).");
            return 1;
        }
        var who = $"{claim.Item} claim for {(claim.Person.Length > 0 ? claim.Person : "someone")}, {(claim.Amount is { } a ? Config.Money(a) : "no amount")}";
        if (!claim.Awaiting)
        {
            host.Out.WriteLine($"The {who} is already {Claims.StateName(claim.State)}: nothing sent.");
            if (!preview)
            {
                State.Claims(state)[claim.ClaimId] ??= new JsonObject { ["notification"] = claim.NotificationId, ["typed_path"] = claim.TypedPath };
                Decided(config, state, claim.ClaimId, $"{Claims.StateName(claim.State)} in People First", host, log);
            }
            return 0;
        }
        var link = approve ? claim.ApproveLink : claim.RejectLink;
        if (link is null || claim.TypedData is null)
        {
            host.Out.WriteLine($"People First doesn't offer you {verb} on the {who}.");
            return 1;
        }
        // Read the record again just before writing: People First wants the ETag of this read (If-Match).
        var (typedRoot, etag) = pf.GetWithEtag(claim.TypedPath);
        if (Claims.Whole(Claims.Record(typedRoot), "approvalState") is var latest && latest != Claims.AwaitingApproval)
        {
            host.Out.WriteLine($"The {who} has just become {Claims.StateName(latest)}: nothing sent.");
            if (!preview)
                Decided(config, state, claim.ClaimId, $"{Claims.StateName(latest)} in People First", host, log);
            return 0;
        }
        var (url, body, headers) = Claims.Write(Claims.MetaLink(typedRoot, approve ? "approve" : "reject") ?? link, typedRoot, etag, claim.ClaimId);
        if (preview)
        {
            Preview(config, pf, claim, approve, reason, url, etag, host, state, log);
            return 0;
        }
        var stateEntry = State.Claims(state)[claim.ClaimId] as JsonObject ?? new JsonObject();
        stateEntry["notification"] = claim.NotificationId;
        stateEntry["claim_path"] = claim.ClaimPath;
        stateEntry["typed_path"] = claim.TypedPath;
        stateEntry["person"] = claim.PersonId;
        stateEntry["sending"] = $"{verb} {State.Now(host.Now())}";
        State.Claims(state)[claim.ClaimId] = stateEntry;
        State.Save(config, state);
        string? problem = null;
        try
        {
            pf.Post(url, body, headers);
        }
        catch (PeopleFirstDeliveryUnknownException error)
        {
            problem = error.Message;
        }
        catch (PeopleFirstException error) when (error.NeverSent || error.Status is >= 400 and < 500)
        {
            stateEntry.Remove("sending");
            State.Save(config, state);
            host.Out.WriteLine($"People First didn't {verb} the {who} (nothing happened; it can be tried again): {error.Message}");
            log($"claim {claim.ClaimId}: {verb} refused or never sent");
            return 1;
        }
        int now;
        try
        {
            now = Claims.CurrentState(pf, claim.TypedPath);
        }
        catch (PeopleFirstException error)
        {
            now = -1;
            problem ??= $"it couldn't be read back ({error.Message})";
        }
        var wanted = approve ? now is Claims.Approved or Claims.AutoApproved : now == Claims.Rejected;
        if (wanted)
        {
            host.Out.WriteLine($"{(approve ? "Approved" : "Rejected")}: the {who}.{(reason is { Length: > 0 } ? $" (People First takes no reason: tell them yourself: {reason})" : "")}");
            Decided(config, state, claim.ClaimId, approve ? "approved here" : "rejected here", host, log);
            return 0;
        }
        stateEntry.Remove("sending");
        stateEntry["sent_unconfirmed"] = $"{verb} {State.Now(host.Now())}";
        State.Save(config, state);
        host.Out.WriteLine($"Sent, but not confirmed: the {who} reads as {(now < 0 ? "unknown" : Claims.StateName(now))}"
                           + $"{(problem is null ? "" : $" ({problem})")}. Check People First; it won't be sent again.");
        log($"claim {claim.ClaimId}: {verb} sent, not confirmed");
        if (config.Review is { } review)
            review.Open().Add(review.Source, UnconfirmedType, new JsonObject
            {
                ["title"] = $"{who}: {verb} sent, not confirmed: check People First",
                ["claim"] = claim.ClaimId,
                ["notification"] = claim.NotificationId,
            });
        return 1;
    }

    /// <summary>What approve or reject would do, without doing it: the claim (who, the kind, the amount and dates), what
    /// the checks find about it now (as show does: a receipt already read isn't read again), what would be sent, and
    /// how to do it. Nothing is written to People First, the state file or the review queue.</summary>
    private static void Preview(Config config, IPeopleFirst pf, Claim claim, bool approve, string? reason, string url, string? etag,
        CliHost host, JsonObject state, Action<string> log)
    {
        var (verb, done) = approve ? ("approve", "approved") : ("reject", "rejected");
        var (path, problem) = Documents.Download(config, pf, claim);
        DocumentFacts? facts = null;
        if (path is not null)
            (facts, problem) = Documents.Read(config, path, claim.ClaimId, host.Run);
        var assessment = Checks.Assess(config, claim, facts, problem, DateOnly.FromDateTime(host.Now()));
        if (path is not null && !Remembered(state, claim.ClaimId))
            Documents.Delete(config, claim.ClaimId);
        host.Out.WriteLine($"Would {verb} this claim:");
        foreach (var line in Checks.Describe(claim, assessment))
            host.Out.WriteLine("  " + line);
        host.Out.WriteLine($"It would send People First the claim's own fields, once: POST {url.Split('?')[0]}"
                           + $"{(etag is null ? " (no ETag)" : ", If-Match the record's ETag")}.");
        if (!approve)
        {
            var person = claim.Person.Length > 0 ? claim.Person : "the claimant";
            host.Out.WriteLine(reason is { Length: > 0 }
                ? $"People First takes no reason, so none is sent with it: tell {person} yourself: {reason}"
                : $"People First takes no reason, so none is sent with it: tell {person} yourself.");
        }
        host.Out.WriteLine($"Nothing {done}. Run again with --yes to {verb} it.");
        log($"claim {claim.ClaimId}: {verb} previewed ({assessment.Recommendation}); nothing sent");
    }

    // ------------------------------------------------------------------------------------------------ the digest

    [GeneratedRegex(@"notifications(?:/|%2F)([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.CultureInvariant)]
    private static partial Regex NotificationLink();

    [GeneratedRegex(@"\[[^\]]*\]|<[^>]*>|https?://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(\s*)(\d+)(\s+unread notifications?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Unread();

    /// <summary>The notification ids a digest's links carry, in order, each once.</summary>
    public static IReadOnlyList<string> NotificationIds(string body) =>
        NotificationLink().Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

    private static string Plain(string line) => Spaces().Replace(Link().Replace(line, " "), " ").Trim();

    /// <summary>The digest's text without the lines of the notifications handled here, and its unread count lowered by
    /// as many as were taken out; and how many were.</summary>
    public static (string Body, int Removed) Remainder(string body, IEnumerable<string> handledTexts)
    {
        var texts = handledTexts.Select(Plain).Where(t => t.Length > 0).ToList();
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var removed = 0;
        foreach (var text in texts)
        {
            var i = lines.FindIndex(l => Plain(l).Contains(text, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                continue;
            lines.RemoveAt(i);
            removed++;
        }
        var rest = string.Join("\n", lines);
        if (removed > 0)
            rest = Unread().Replace(rest, m =>
            {
                var left = Math.Max(0, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - removed);
                return $"{m.Groups[1].Value}{left}{m.Groups[3].Value}";
            }, 1);
        return (rest, removed);
    }

    /// <summary>The digest: People First's daily email, as JSON on standard input (a mail-triage handler's input).
    /// Each notification it links to is read; each claim waiting for you is checked and raised. What's left (leave
    /// requests, likes, anything else) goes to [digest] next, with the claims' lines taken out and the count lowered,
    /// and its answer is the answer. Exit 0: everything's been dealt with, so the email can go; 75: try again later;
    /// anything else: raise the email.</summary>
    public static (int Code, string Note) Digest(Config config, string email, Func<IPeopleFirst> open, CliHost host, Action<string> log)
    {
        JsonObject input;
        try
        {
            input = JsonNode.Parse(email) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException error)
        {
            return (1, $"the input isn't the email as JSON: {error.Message.Split('\n')[0]}");
        }
        var body = input["body"]?.ToString() ?? "";
        var ids = NotificationIds(body);
        var handled = new List<Checked>();
        var others = 0;
        if (ids.Count > 0)
        {
            try
            {
                using var pf = open();
                var state = State.Load(config);
                foreach (var id in ids)
                {
                    var result = Check(config, pf, id, host, state, dryRun: false, log);
                    if (result.Claim.IsClaim)
                        handled.Add(result);
                    else
                        others++;
                }
                Refresh(config, pf, state, host, log);
            }
            catch (SignInNeededException)
            {
                return (1, "couldn't sign in to People First to check the claims in it: raised as usual");
            }
            catch (Exception error) when (error is PeopleFirstException or HttpRequestException or IOException or TimeoutException)
            {
                log($"digest: People First failed ({error.GetType().Name}): will try again");
                return (TryAgain, "People First couldn't be read: will try again");
            }
        }
        var claimsNote = handled.Count switch
        {
            0 => "",
            _ => $"{handled.Count} claim(s) checked ({string.Join(", ", handled.Select(h => h.Assessment?.Recommendation ?? "already decided"))}); ",
        };
        var (rest, removed) = Remainder(body, handled.Select(h => h.Claim.Text));
        var restCount = Unread().Match(rest) is { Success: true } u ? int.Parse(u.Groups[2].Value, CultureInfo.InvariantCulture) : (int?)null;
        if (handled.Count > 0 && removed == handled.Count && others == 0 && (restCount ?? 0) == 0)
            return (0, $"{claimsNote.TrimEnd(' ', ';')}: items raised");
        if (config.DigestNext is not { } next)
            return (1, $"{claimsNote}the rest raised as usual");
        var forward = (JsonObject)input.DeepClone();
        forward["body"] = rest;
        var answer = host.Run(next, config.DigestFolder, forward.ToJsonString(), config.DigestTimeoutSeconds);
        var code = answer.Exit ?? 1;
        var theirs = answer.FirstLine.Length > 0 ? answer.FirstLine : $"exit {code}";
        return (code, claimsNote + theirs);
    }
}
