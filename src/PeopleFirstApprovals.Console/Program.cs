// people-first-approvals.exe. Output is Unicode to a console and UTF-8 when redirected (see ConsoleText); the digest
// command reads the email as JSON on standard input.
using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
return PeopleFirstApprovals.Cli.Run(args, new PeopleFirstApprovals.CliHost
{
    In = input,
    Out = WinBackground.ConsoleText.Out,
    Err = WinBackground.ConsoleText.Error,
});
