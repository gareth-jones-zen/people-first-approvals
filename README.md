# people-first-approvals

Checks the claims [People First](https://www.mhrglobal.com/uk/en/people-first) (MHR's HR system) asks you to approve
(long service awards, expenses, overtime) and raises each one with a recommendation. **It never approves or rejects
anything by itself:** `approve` and `reject` show what they would do, and do it only when run again with `--yes`.

```
people-first-approvals check [NOTIFICATION ...]   check these notifications' claims and raise each for review;
                                                  with none, re-read the claims still waiting (--dry-run)
people-first-approvals show NOTIFICATION          check one and print it: raises and remembers nothing
people-first-approvals approve ID [--yes]         show what approving a claim (its notification or claim id)
                                                  would do; --yes approves it
people-first-approvals reject ID [--reason TEXT] [--yes]
                                                  show what rejecting one would do; --yes rejects it (People
                                                  First takes no reason: tell them yourself)
people-first-approvals digest                     People First's daily email as JSON on standard input (a
                                                  mail-triage handler): its claims checked, the rest passed on
people-first-approvals tidy                       delete documents older than keep_days
people-first-approvals sign-in                    sign in to People First in a window
people-first-approvals config                     create the settings file (if missing) and say where it is
```

Install: unzip `people-first-approvals-<version>-win-x64.zip` from
[Releases](https://github.com/gareth-jones-zen/people-first-approvals/releases) to a folder that will stay put, such as
`%LOCALAPPDATA%\Programs\people-first-approvals`. It needs no scheduled task: something hands it People First's daily
email (a [mail-triage](https://github.com/gareth-jones-zen/mail-triage) handler), or you run `check` by hand.

## What a check does

A People First notification ("Long Service Awards claim for … requires your approval") links to the claim, and the
claim links to everything else, so a check follows the links, reading only (GETs):

| Read | For |
|---|---|
| The notification | Its type (only `ClaimApprovalRequest` is a claim) and the person |
| The claim, and its typed record (`claims/cash/…`) | The payslip item, the amount, the date earned, the approval state, the reason given, and the approve and reject links |
| Its attachments | The receipt or invoice, downloaded (asking for the file's own type: asked for JSON, People First sends the attachment's details again) |
| The person's employment periods | The reckonable service date (what service counts from) |
| The pay run | The processing date: approve by then to be paid in this run |
| The pay run's other claims | Another claim for the same item (People First lists claims one pay run at a time, so earlier pay runs aren't checked) |

**The receipt is read by a command you set up** (`[document_reader]`), such as a
[claude-headless](https://github.com/gareth-jones-zen/claude-headless) job: receipts vary too much to parse in code. It
gets a JSON file naming the document (`{input}`) and writes its reply to `{out}`: a JSON object with `total`,
`currency`, `payee`, `bill_to`, `date`, `invoice_number`, `items` and `paid`. A reply already there is used (a job that
met a usage limit writes it later), so `check` again picks it up.

**Then the checks**, each ok, a note, needing you, or a reason to reject; the recommendation is **Reject** if any fails,
**Ask** if any needs you, otherwise **Approve**:

- **Every claim:** an amount; the document's total against the claim, who it's made out to, whether it shows as paid
  (overtime expects no document); another claim for the same item in the pay run; the deadline; anything that couldn't
  be read.
- **Long service:** completed years from the reckonable service date to the date earned, against the
  `[[long_service]]` bands: under the first band fails; more than the band allows needs you.
- **A `[[claim_type]]` with a `note`** (overtime, expenses) adds it as a check only you can answer ("Was it agreed?").
  An item no type matches needs you: there are no rules for it yet.

**Each claim becomes one review item** (`[review]`, background-tools-lib's ReviewQueue; type `claim`): the title
("Long Service Awards claim: Alex Example, £180.00: Approve"), the recommendation and why, every check, the approve-by
date, and the commands (`approve` and `reject` without `--yes`, so running one shows what it would do). Checked again, its item is replaced. **Items carry names, the claim's kind, amounts and dates:
never an address or bank details**, though the receipt has them.

## Approving and rejecting

**Without `--yes`, `approve ID` and `reject ID` only show what they would do**, and exit 0: the claim (who, its kind,
the amount, when it was earned, the reason given), what the checks find about it now (as `show` does; a receipt already
read isn't read again), the request that would be sent, for `reject` that People First takes no reason (so your
`--reason` is yours to pass on), and then "Nothing approved. Run again with --yes to approve it." They write nothing:
not to People First, the state file or the review queue. `--dry-run` is another name for this (`--yes` with it is an
error).

```
Would approve this claim:
  Long Service Awards claim for Alex Example: £247.52, earned 19 Nov 2026 (awaiting approval)
    reason given: Long service award
    ok: Document: invoice 42, from Made Up Supplies Ltd, 2026-11-16: total £247.52, the same as the claim
    ok: Service: 10 years 5 months since 13 Jun 2016: the 10-year award, up to £250.00 (the next, 20 years, on 13 Jun 2036)
    ok: Deadline: approve by Fri 27 Nov 2026 to be paid Mon 30 Nov 2026
    recommendation: Approve (every check passed)
It would send People First the claim's own fields, once: POST /payroll/personOnPayroll/…/approved, If-Match the record's ETag.
Nothing approved. Run again with --yes to approve it.
```

**With `--yes`** it reads the claim first (already decided: nothing is sent), then does what People First's web app
does: reads the claim again for its ETag, then POSTs the claim's fields (under its type, with its id; no links) to its
`approve` (or `reject`) link with `annotate=t` and **`If-Match` set to that ETag**, once. Without If-Match People First
refuses the write with 412 (fixed in 0.1.1). **A write that may have reached People First
is never sent again:** "sending" is recorded before the POST; a timeout or a 5xx is read back, and if the claim doesn't
read as decided it's recorded as sent but not confirmed (a `claim-unconfirmed` item), and a second `approve` refuses. A
refusal (4xx) or a request that never left records nothing, so it can be tried again.

## The digest: `digest`

People First's daily "Today's notifications" email, as a mail-triage handler's input on standard input. The
notification ids come from its links (Outlook's Safe Links included). Each claim is checked and raised; **what's left**
(leave requests, likes, anything else) goes to `[digest] next`, with the claims' lines taken out and the unread count
lowered by as many, and **its answer is the answer**: so another handler (such as team-leave's `digest`) can decide on
the rest, and the email goes only when both are done.

- **Exit 0:** everything in it is dealt with (only claims, or the next handler said 0). **75:** People First couldn't
  be read, or the next handler said try again. **Anything else:** raise the email (a sign-in needed, no next handler for
  the rest, or the next handler's own answer).

## Documents, state and the log

- **Documents** (`documents`, a folder per claim) are deleted once the claim is decided (here or in People First:
  `check` re-reads the claims still waiting), and anything older than `keep_days` (30) whatever its state. Keep the
  folder out of any backup: receipts carry addresses.
- **The state file** holds People First's ids and paths and what happened when: no names, amounts or dates of the
  claims. Decided claims are forgotten after 90 days. **The log** holds ids and recommendations only.

## Settings

```toml
base = "base-config.toml"            # optional: settings shared with other tools
log_file = "%LOCALAPPDATA%/people-first-approvals/people-first-approvals.log"
state_file = "%LOCALAPPDATA%/people-first-approvals/state.json"
documents = "%LOCALAPPDATA%/people-first-approvals/documents"
keep_days = 30

[people_first]
url = "https://yourorg.people-first.com"
edge_profile = "%LOCALAPPDATA%/people-first-approvals/edge-profile"   # shared with other People First tools: they take turns
# account = "you@example.com"        # default: login_hint
# tenant_code = "yourorg"            # default: from the url

[document_reader]
command = ['claude-headless', '--config', 'claude-headless.toml', 'job', 'claim-document', '--input', '{input}', '--out', '{out}']
# cwd = "."                          # default: this file's folder
timeout_seconds = 600

[digest]
next = ['team-leave', '--config', 'team-leave.toml', 'digest']        # optional: what decides on the rest
# timeout_seconds = 120

[review]
file = "%LOCALAPPDATA%/my-tools/review.json"
source = "people-first-approvals"

[[long_service]]
years = 10
amount = 250
also = "a celebration meal"          # optional: shown in the check

[[claim_type]]                       # first match on the payslip item's name; default: long service, overtime, expenses
match = 'overtime'
kind = "overtime"                    # long-service | expenses | overtime | other
note = "Was the overtime agreed beforehand? Only you know."
```

## Before sharing it

The token is taken from People First's web app through your own sign-in (background-tools-lib's PeopleFirstClient), and
the tool isn't a registered API client. That's fine for reading your own permitted data on your own machine;
distributing it to other people needs clearing with the HR system's owners and information security first.

## Building

.NET 11, Native AOT. The shared libraries come from a checkout of
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib) beside this repository.
`dotnet test` runs the tests (a fake People First; everyone in them is made up); a tag `vX.Y.Z` releases.
