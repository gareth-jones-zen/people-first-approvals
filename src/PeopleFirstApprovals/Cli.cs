using System.Reflection;
using PeopleFirstClient;
using WinBackground;

namespace PeopleFirstApprovals;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextReader In { get; init; } = Console.In;
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public Func<string, string?> Env { get; init; } = Config.Environment;
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    /// <summary>Runs a command (the document reader, the digest's next handler): command, folder, input, timeout.</summary>
    public Func<IReadOnlyList<string>, string, string?, int, CommandResult> Run { get; init; } = Commands.Run;
    public Func<Config, Action<string>, IPeopleFirst> OpenPeopleFirst { get; init; } = (config, log) =>
        new PeopleFirstApi(Session.OpenAsync(config.PeopleFirst, new SessionOptions { Log = log, LockWaitSeconds = 300 }).GetAwaiter().GetResult());
    public Action<Config> SignIn { get; init; } = config => SignInWindow.RunAsync(config.PeopleFirst).GetAwaiter().GetResult();
    public bool Lock { get; init; } = true;
}

/// <summary>people-first-approvals: checks what People First asks you to approve (claims), raises each with a
/// recommendation, and approves or rejects one on your say-so: approve and reject show what they would do, and do it
/// only with --yes. Exit codes: 0 done (or shown); 1 failed (or, for digest, raise the email); 2 settings, arguments or
/// sign-in; 75 (digest) try again later.</summary>
public static class Cli
{
    public const string Prog = "people-first-approvals";

    private const string Usage = "usage: people-first-approvals [-h] [--version] [--config CONFIG] {check,show,approve,reject,digest,tidy,sign-in,config} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "people-first-approvals: checks the claims People First asks you to approve (long service awards, expenses,\n" +
        "overtime), with a recommendation for each. It never approves or rejects anything by itself: approve and\n" +
        "reject show what they would do, and do it only when you run them again with --yes.\n\n" +
        "    people-first-approvals check [NOTIFICATION ...]   check these notifications' claims and raise each for you;\n" +
        "                                                      with none, re-read the claims still waiting (--dry-run)\n" +
        "    people-first-approvals show NOTIFICATION          check one and print it; raises and remembers nothing\n" +
        "    people-first-approvals approve ID [--yes]         show what approving a claim (its notification or claim id)\n" +
        "                                                      would do; --yes approves it\n" +
        "    people-first-approvals reject ID [--reason TEXT] [--yes]\n" +
        "                                                      show what rejecting one would do; --yes rejects it (People\n" +
        "                                                      First takes no reason: tell them yourself)\n" +
        "    people-first-approvals digest                     People First's daily email as JSON on standard input (a\n" +
        "                                                      mail-triage handler): its claims checked, the rest passed on\n" +
        "    people-first-approvals tidy                       delete documents older than keep_days\n" +
        "    people-first-approvals sign-in                    sign in to People First in a window\n" +
        "    people-first-approvals config                     create the settings file (if missing) and say where it is\n\n" +
        "options:\n" +
        "  -h, --help       show this help message and exit\n" +
        "  --version        show program's version number and exit\n" +
        "  --config CONFIG  settings file (default %APPDATA%\\people-first-approvals\\config.toml)\n" +
        "  --yes            approve, reject: do it (without it they show what they would do and change nothing)\n" +
        "  --dry-run        check: change nothing; approve, reject: the same as leaving out --yes\n";

    private static readonly string[] CommandNames = ["check", "show", "approve", "reject", "digest", "tidy", "sign-in", "config"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal sealed class Options
    {
        public string? Config;
        public string Command = "";
        public List<string> Args = [];
        public bool DryRun;
        public bool Yes;
        public string? Reason;
    }

    private sealed class ArgumentsException(string message) : Exception(message);

    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        Options options;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;
            options = parsed;
        }
        catch (ArgumentsException error)
        {
            host.Err.WriteLine(Usage);
            host.Err.WriteLine($"{Prog}: error: {error.Message}");
            return 2;
        }

        try
        {
            if (options.Command == "config")
            {
                host.Out.WriteLine(PeopleFirstApprovals.Config.CreateDefault(options.Config ?? host.Env(PeopleFirstApprovals.Config.EnvConfig), host.Env));
                return 0;
            }
            var config = PeopleFirstApprovals.Config.Load(options.Config, host.Env);
            if (options.Command == "sign-in")
            {
                host.Out.WriteLine("A window opens for People First: sign in there; it closes by itself.");
                host.SignIn(config);
                host.Out.WriteLine("Signed in to People First.");
                return 0;
            }
            var log = Logger.Create(config.LogFile, null, clock: host.Now);
            using var held = host.Lock ? Locks.Exclusive("people-first-approvals-" + Locks.Key(config.Path), 600) : null;
            switch (options.Command)
            {
                case "digest":
                {
                    var (code, note) = Runner.Digest(config, host.In.ReadToEnd(), () => host.OpenPeopleFirst(config, log), host, log);
                    log($"digest: exit {code}");
                    host.Out.WriteLine(note);
                    return code;
                }
                case "tidy":
                {
                    var state = State.Load(config);
                    Runner.Refresh(config, null, state, host, log);
                    host.Out.WriteLine("Tidied.");
                    return 0;
                }
                case "check":
                case "show":
                    return CheckCommand(config, options, host, log);
                default:
                {
                    var state = State.Load(config);
                    using var pf = host.OpenPeopleFirst(config, log);
                    // Only --yes acts; without it (or with --dry-run, kept as another name for the preview) it's shown.
                    return Runner.Decide(config, pf, options.Args[0], options.Command == "approve", options.Reason, preview: !options.Yes, host, state, log);
                }
            }
        }
        catch (Exception error) when (error is UsageException or SignInNeededException)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            host.Err.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int CheckCommand(Config config, Options options, CliHost host, Action<string> log)
    {
        var dryRun = options.DryRun || options.Command == "show";
        var state = State.Load(config);
        using var pf = host.OpenPeopleFirst(config, log);
        foreach (var id in options.Args)
        {
            var result = Runner.Check(config, pf, id, host, state, dryRun, log);
            if (!result.Claim.IsClaim)
                host.Out.WriteLine($"{id}: a {result.Claim.NotificationType} notification, not a claim waiting for you.");
            else if (result.Assessment is null)
                host.Out.WriteLine($"{result.Claim.Item} claim {result.Claim.ClaimId}: already {Claims.StateName(result.Claim.State)}.");
            else
            {
                foreach (var line in Checks.Describe(result.Claim, result.Assessment))
                    host.Out.WriteLine(line);
                if (result.Raised)
                    host.Out.WriteLine("  raised for review.");
            }
        }
        if (!dryRun)
        {
            var decided = Runner.Refresh(config, pf, state, host, log);
            if (options.Args.Count == 0)
            {
                var waiting = State.Claims(state).Count(p => p.Value is System.Text.Json.Nodes.JsonObject e && e["decided"] is null);
                host.Out.WriteLine($"{waiting} claim(s) waiting; {decided} decided since last time.");
            }
        }
        return 0;
    }

    private static Options? Parse(IReadOnlyList<string> argv, CliHost host)
    {
        var options = new Options();
        var i = 0;
        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return null;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine($"{Prog} {Version}");
                return null;
            }
            if (arg == "--config")
            {
                if (++i >= argv.Count)
                    throw new ArgumentsException("argument --config: expected one argument");
                options.Config = argv[i];
                continue;
            }
            if (arg.StartsWith("--config=", StringComparison.Ordinal))
            {
                options.Config = arg["--config=".Length..];
                continue;
            }
            if (arg.StartsWith('-'))
                throw new ArgumentsException($"unrecognized arguments: {arg}");
            break;
        }
        if (i >= argv.Count)
            throw new ArgumentsException($"the following arguments are required: command (one of {string.Join(", ", CommandNames)})");
        options.Command = argv[i++];
        if (!CommandNames.Contains(options.Command))
            throw new ArgumentsException($"argument command: invalid choice: '{options.Command}' (choose from {string.Join(", ", CommandNames)})");
        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return null;
            }
            if (arg == "--dry-run" && options.Command is "check" or "approve" or "reject")
                options.DryRun = true;
            else if (arg == "--yes" && options.Command is "approve" or "reject")
                options.Yes = true;
            else if (arg == "--reason" && options.Command == "reject")
            {
                if (++i >= argv.Count)
                    throw new ArgumentsException("argument --reason: expected one argument");
                options.Reason = argv[i];
            }
            else if (arg.StartsWith('-'))
                throw new ArgumentsException($"unrecognized arguments: {arg}");
            else
                options.Args.Add(arg);
        }
        if (options.Yes && options.DryRun)
            throw new ArgumentsException("argument --yes: not allowed with argument --dry-run");
        var (min, max) = options.Command switch
        {
            "check" => (0, 50),
            "show" or "approve" or "reject" => (1, 1),
            _ => (0, 0),
        };
        if (options.Args.Count < min)
            throw new ArgumentsException($"{options.Command} needs {(min == 1 ? "an id" : "ids")}");
        if (options.Args.Count > max)
            throw new ArgumentsException($"unrecognized arguments: {string.Join(" ", options.Args.Skip(max))}");
        return options;
    }
}
