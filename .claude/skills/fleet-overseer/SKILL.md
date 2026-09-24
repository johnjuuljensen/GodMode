---
name: fleet-overseer
description: Run a set of interdependent GitHub issues to completion across parallel Claude sessions in ac-gwt worktrees — schedule by dependency and by project, dispatch workers, review their pull requests, integrate on an epic branch, and escalate only the decisions a human owns. Use when asked to oversee issues, run an epic, or coordinate several issues at once.
---

# Overseeing a fleet

A worker is an ordinary interactive Claude session in its own worktree, in its own terminal tab,
launched by `ac-gwt`. It is not a subagent. The person at the keyboard can interrupt it, take it
over and resume it, and that must stay true.

## You never edit code

Not one line. The moment you do, you have a working tree, a merge conflict and a context window
full of somebody else's file — and you stop being able to see the fleet. Everything you want changed
is dispatched, including a failing gate on your own epic branch.

Reviewing, merging, reading and writing GitHub are yours. Editing is not.

## One protocol, either tier

**An epic gets its own overseer when it gets an integration branch, and only then.** That is the
criterion, not a depth limit: an epic that is merely a grouping of issues in one lane needs no branch
and no overseer, and giving it either adds a merge tier nobody can reason about. Sub-issues nest, so
you will meet epics inside epics; most of them are groupings.

**So you never run an epic that has its own branch — you launch its overseer and step back.** Its
issues are not yours to dispatch, its workers are not yours to message, its pull requests are not
yours to review. Do not ask whether it is yours; launching the overseer is the answer. You see that
epic again when its own pull request reaches your report address.

**An overseer launched on an epic owns the issues under that epic and nothing else** - however
tempting an adjacent issue looks, and however idle you are. Reaching outside your epic collides with
whoever does own it. Say what you noticed; do not take it.

You may be overseeing epics, or overseeing one epic's issues, or both. The protocol is the same;
only the address differs. Your brief names who you report to. With no brief, that is the user.

Never message a worker that reports to another overseer — its owner's picture of the world goes
stale and two people start steering one session. One session, one address.

## State is on GitHub, never in your context

Draft pull request means working. Ready for review means the worker says it is done. A review means
changes were asked for. The epic issue holds the graph.

**Name work with both numbers, paired: `IS#43/PR#56`.** Your own state lives in pull requests, so
that is what you reach for; the user tracks the project by issue. Two vocabularies means every status
report has to be translated before it can be read. Where only one exists — an issue not yet worked, a
pull request with no issue — give the one that exists rather than inventing a pairing. Put it in
every brief you write, or the sessions you dispatch inherit the drift.

Write every non-trivial instruction to the issue or pull request as well as sending it. A worker the
user took over and resumed has lost your message but can still read GitHub. If you die, your
replacement reconstructs everything from `gh` — so keep GitHub true and keep nothing important only
in your own head.

## Take stock before you touch anything

You arrive into work already in progress — the first time, and again after every restart. Build the
picture yourself. Anything you were told is a hint to check, not a finding.

1. **Sessions.** `ListAgents`. It is scoped to this `CLAUDE_CONFIG_DIR`, so every row is yours. A
   name is the worktree folder truncated; `git -C <worktree> rev-parse --abbrev-ref HEAD` gives its
   branch when the truncation hides it. Workers are launched with the `claudeCommand` in
   `..\.worktree.json` (`claude-mega`), so you must be running under that same config dir or the
   fleet is invisible to you.
2. **Branches to issues to pull requests.** The issue number is in the branch name; confirm against
   `gh pr list --state all --json number,headRefName,baseRefName,isDraft,state`.
3. **Finished or live.** A session whose pull request has merged is finished, not adoptable — stop
   tracking it, but leave its worktree alone. A non-draft pull request means its worker believes
   it is done. An **issue** can be finished with no session at all: search `--state all` for a merged
   pull request naming it, and read what actually landed before dispatching a worker to build it
   again. An issue stays open through carelessness as often as through unfinished work.
4. **The graph**, for each epic you were given: sub-issues and `blocked_by`, recursively, plus the
   epic body for lane order and for decisions it names as unsettled.
5. **Who owns which session.** Intersect 2 with 4. A running session on an issue under an epic
   belongs to that epic's overseer; the rest are yours.
6. **Divergence, and whether it is already being fixed.** There is no `dev` here: `master` is both
   the default and the integration branch. For each epic branch, `git rev-list --count
   origin/<epic>..origin/master` and the reverse. `master` ahead of an epic branch is work the epic
   does not have, and it becomes a conflict later rather than never. Before raising it, look for a
   pull request or a session already resolving it. A branch that already merged into `master` is
   shared history, not a conflict, and is nobody's problem to raise.
7. **Who else is in a worktree.** A worktree has one owner. The `master` worktree (`..\master`) is
   the user's own checkout: read it, never write in it. An epic branch's worktree belongs to its
   overseer, and another session can be mid-merge in it. `git status` there before you write
   anything, and never commit into a tree someone else has left conflicted.
8. **What you may dispatch.** The `autoclaude` label, and only that. An issue without it is
   documented backlog, whatever its other labels say and however ready it reads - being open, or
   unblocked, or obviously next is not permission. An epic without it is not yours to run. If nothing
   carries the label, say so and stop; that is the answer, not an obstacle.

Then report the survey and wait. A picture the user corrects in one message is cheaper than a wrong
dispatch, and it is the only moment where correcting you is cheap.

## Read the graph from the API, the order from the prose

```bash
gh api repos/{owner}/{repo}/issues/N/sub_issues --jq '.[] | "\(.number)\t\(.state)\t\(.title)"'
gh api repos/{owner}/{repo}/issues/N/dependencies/blocked_by --jq '[.[].number] | join(",")'
```

Sub-issues nest — an epic can hold an epic — so recurse. A leaf is runnable when it has no
`blocked_by`, is covered by `autoclaude`, and does not carry `unclear`. All three, every time.

**An epic's `autoclaude` covers its children.** The label is put on the epic and not on each of its
sub-issues — that is how this repository uses it, uniformly, and the alternative reading makes a
labelled epic un-runnable, since dispatching its children is the only thing running an epic means. So
a leaf under an epic you own is dispatchable on the epic's label; a leaf with no labelled ancestor
needs its own. Do not label the children yourself: the coverage already exists and dozens of
redundant labels obscure which issues a human actually released.

Then read the epic body. It carries what the API cannot: couplings that are not blocks, where two
issues must agree and whichever arrives first settles it for both. That is lane order. Re-read the
epic before each dispatch decision; it is edited while you run.

## An issue does end-to-end work, or it cannot be verified

**Every issue must reach a result somebody can see** — a behaviour a user can observe in the running
server or client, or a test that exercises it end to end. A layer that only its future consumer will
ever call cannot be verified by anyone, so it cannot be reviewed either. What is yours is the moment
this applies: an issue that arrives already shaped as a
layer — *"add the field"*, *"add the step"*, *"then wire it up"* — is rewritten **before** dispatch,
not after a worker has built it. That is the same rewrite as any other underspecced issue, and it
carries `autoclaude` across.

The dispatch you are most tempted to make is the one that keeps two workers off the same file. Take
the merge instead.

## Schedule by project, not only by dependency

Two workers in one project manufacture a conflict that no single pull request can see on its own.
Group the runnable issues into lanes that do not share files, run **one worker per lane**, and stack
within a lane rather than widening it. **Lanes are about who edits what at the same time — never a
licence to split one result across several issues.**

The natural lanes here are the server (`src/GodMode.Server`, with `GodMode.ProjectFiles` and
`src/GodMode.McpBridge`), the React client (`src/GodMode.Client.React`), and the MAUI shell
(`src/GodMode.Maui`, `GodMode.ClientBase`, `SignalR.Proxy`). **The hub contract is the one seam they
all share**: `IProjectHub` / `IProjectHubClient` and the models in `src/GodMode.Shared` are mirrored
by hand in the client's `signalr/types.ts` and `signalr/hub.ts`. A change to the contract edits both sides, so give it
to one worker who owns both files for that issue, rather than to two lanes that must agree.

**A session's slot frees when its work is reviewed and clear for merge, not when it merges.** Never
hold dispatch waiting on a merge — a reviewed pull request can sit for a day and the fleet must not
sit with it. Idle, reviewed, clear: the slot is free, so launch the next issue.

**A freed slot is capacity, never a session to re-brief.** What frees is your budget to run one more
worker, not the finished session itself. Launch a new one; leave the old one where it is.

**The gate is not serialised and there is no lane to hand out.** Each worktree has its own `bin/`,
`obj/` and `node_modules`, so gates in parallel worktrees do not touch each other. Workers run the
gate and their scoped runs when they want the answer, and so do you. Do not publish a chain, do not
place anyone, do not ask a worker to wait for another.

A suite that fails in a way that looks like resource exhaustion is re-run alone before it is believed.
That is the whole of it.

## Evaluate before you dispatch

Evaluating exists to keep work moving, not to gate it. Nothing reaches `master` without the user
merging it, so a wrong dispatch is recoverable — a pull request gets reviewed and closed — while a
wrong block is not, because it just sits there looking like nothing happened. Prefer action at every
step, and never read a document so literally that it stops work it was not written about.

**First, check the work still exists.** Before anything else, before reading the issue for whether
it is well specified: has it already landed? An issue is often filed from a worktree cut before the
fix merged, so the report is true of that tree and false of `master`.

```bash
git fetch origin
git log --oneline origin/master -- <the file the issue names>
git grep -n '<the symptom>' origin/master -- <path>
gh pr list --state all --search '<issue number>'
```

Taking stock at the start of a session does not cover this. Issues enter your scope hours later, the
default branch moves under you, and the check is cheap while the failure is not: you spend a session
and a worktree to be told the diff would be empty. Run it at the moment you dispatch, on every issue,
however recently you surveyed.

Two things then make you stop and think, and neither is a verdict:

- **The issue is underspecced** — a worker would have to design before it could build. Not about
  size: an issue may add a project, a service, a field on a shared model, and that is ordinary work. The
  question is whether the issue says *what the structure is*. From it and the documents it names, can
  a worker tell what the new thing is called, where it lives and who owns it? If yes, dispatch it
  however large. If several structural questions have no answer anywhere, those answers are the work.
- **It cuts against a decision about its own subject.** Quote the line or you have nothing. **A fence
  binds what it names**: `docs/UNIFIED-ARCHITECTURE.md` §5.1 "No Shadow Config Stores" forbids
  "in-memory caches that outlive a request" — of *configuration*, so that the files under a root stay
  the one source of truth. It does not decide whether the process manager may hold a running
  project's live state in memory; that is not config. Different subjects; neither settles the other.
  If the connection takes an argument to make, it is an opinion, and an opinion goes in a comment
  with no label.

Three outcomes, in order of preference.

**Dispatch it.** The default, including when you are torn. Name the doubt in the brief so the worker
meets it in the first hour and parks it with evidence, instead of you guessing now.

**Rewrite it.** When one of the two above is real, the question is not whether the issue is allowed.
It is **whether the same outcome can be reached by a route the codebase already supports** — and
usually it can. Give that to a subagent: here is the issue, here are the documents it touches, here
is the code. It returns either the issue rewritten to reach the same outcome a specified way, or a
statement of what genuinely has to be designed first.

If it comes back rewritten: open the new issue, reference the old one and say what changed and why,
move `autoclaude` from the old to the new, close the old as superseded, and dispatch the new one.
Moving the label is not overriding the user's decision to dispatch — it carries that decision to the
version that can be built. That supersession is the **only** reason to clear `autoclaude`.

**Label it `unclear`.** Only once a rewrite has been tried and failed: no formulation reaches the
outcome without designing something no document specifies. Comment what was tried and what is
missing, so the label reads "we looked" rather than "we declined". This should be rare. Labelling
more than the occasional issue means your bar is wrong, not that the backlog is.

An epic is evaluated the same way before you launch an overseer for it.

## Dispatching

**One issue, one `ac-gwt` session, one worktree, one branch — no exceptions.** Never re-brief a
session onto a second issue, however idle it looks and however cheap the next task is. It is not a
tidiness rule:

- The worktree's folder name is how a person finds the work. Point it at a second branch and someone
  opens the IS#19 directory to find IS#24 inside it.
- That tree's transcript becomes two unrelated issues interleaved, so `--resume` replays a
  conversation about a different branch — which destroys the one property this whole arrangement rests
  on, that a human can resume a session and take it over.
- The branch left behind still has a pull request, reviews and possibly a fault to come back to. The
  session that wrote it is where that context lives.

A finished session is a record, not a resource. Stop tracking it and leave it alone.

Write a brief per worker from `brief-worker.md`, then, from PowerShell (`ac-gwt-issue` is a `.ps1`
in `~\.autoclaude`, on `PATH`):

```powershell
ac-gwt-issue <n> -BaseBranch <ref> -PromptFile <brief> -NonInteractive -Json
```

From bash, `pwsh -File` cannot pass an array argument, so wrap it:
`pwsh -NoProfile -Command "& ac-gwt-issue.ps1 <n> -BaseBranch <ref> -PromptFile <brief> -NonInteractive -Json"`.

Branch prefix and default base come from the issue's labels via `branchTypes` in `..\.worktree.json`:
`epic` → `epic/<n>-<slug>`, `bug` → `bug/<n>-<slug>`, anything else → `feature/<n>-<slug>`, all
cut from `origin/master` unless `-BaseBranch` says otherwise.

`-BaseBranch` is the epic branch, or a sibling's branch when stacking. Workers reason about code
and get the default model; add `-ClaudeArgs '--model','sonnet'` when you launch an overseer, which
mostly reads pull request state. Take the worktree path from
the JSON line — the one starting `{` — rather than deriving the branch slug yourself.

`wt` returns before the session exists. Do not guess its name: the brief tells the worker to
announce itself, and you learn the address from the `from` on its message. To hear when a worker
next goes idle, `SendMessage` with `notify_when_idle: true` rather than polling.

## Adopting a session you did not launch

Sessions are already running when you arrive. `ListAgents` shows every one under this
`CLAUDE_CONFIG_DIR` and nothing outside it, so the list is exactly your fleet — other profiles on
this machine are invisible and unreachable, and you never have to filter them out.

A running session has a worktree, probably a branch and a pull request, and none of your protocol.
Adopt it in this order:

1. **Read the pull request first.** It tells you more than the session will, and it costs the
   session nothing. `git -C <worktree> rev-parse --abbrev-ref HEAD` maps a session name to its
   branch when the name is truncated.
2. **Decide whether it is alive work.** A session whose pull request already merged is finished, not
   adoptable — stop tracking it and leave it where it is.
3. **Message it once** with what a brief would have carried: load the `fleet-worker` skill, its base
   branch, the projects and paths it owns, and your address. Say what you believe its state is, so it can
   correct you rather than guess what you know.
4. **Ask it to write the working agreement into its pull request body now.** It started without one,
   so nothing durable records what it agreed to.

Never interrupt a busy session to adopt it, and never adopt one the user is typing in — wait for it
to go idle, with `notify_when_idle: true` rather than polling. An adopted session that was
mid-thought when you arrived will finish that thought first; let it.

## The epic branch

`ac-gwt-issue <n>` on an issue labelled `epic` creates `epic/<n>-<slug>`, branched from
`origin/master`, and gives it a worktree; that is where you live. Every
sub-issue's pull request targets it. You merge those. The single pull request from the epic branch
to `master` is the user's to merge, and it is the only merge that is.

That split is why you can run unattended: nothing you merge is outward-facing, and nothing you merge
is hard to reverse. `master` is the opposite on both counts — every push to it that touches `src/`,
`tests/` or the slnx builds and publishes a Docker image (`.github/workflows/build-and-push.yml`).

- Merge with a merge commit, not a squash. The subject is the only permanent record of which branch
  the commits came from.
- A branch stacked on a sibling has its pull request retargeted to the epic branch automatically
  when the parent merges and its branch is deleted. Rely on it; do not pre-empt it.
- Merge `origin/master` into the epic branch on a schedule. An epic that runs for days while other
  work lands on `master` otherwise ends as one enormous conflict, presented at the worst moment.
- **A clean textual merge is not a green gate.** `mergeable: MERGEABLE` means git found no
  overlapping lines; it says nothing about two independently-correct branches that change what the
  same downstream test or snapshot observes. That failure is invisible in either pull request, and
  the worker's stated gate was true of the tree they ran it on and false of yours the moment you
  merged something else. So: **trial-merge locally and run the suite on the actual merged tree
  before you trust a stated gate against a moving epic branch**, and re-run it on the epic branch
  *after* each merge, not only before. Catching this is most of why the epic branch exists — on a
  direct-to-`master` flow both halves land green and the break appears in someone else's work.

## Reviewing

**No CI runs on a pull request here.** The only workflow, `build-and-push.yml`, fires after a push
to `master` and publishes an image. So `gh pr checks` reports nothing, and the gate is what a worker
runs in its own worktree as part of writing the code:

```powershell
cd src/GodMode.Client.React; npm ci; cd ../..   # the server build runs `npm run build` but never installs
dotnet build GodMode.slnx                         # also builds the React client into the server's wwwroot
dotnet test GodMode.slnx --no-build
cd src/GodMode.Client.React; npm run lint; npm test
```

**The base is not green on this gate**, so "green" means **no new failures against the base**. On
2026-09-24 the epic #168 branch (`41d60c4`) built with 0 errors, passed all 278 `GodMode.Server.Tests`,
and had 7 errors and 2 warnings from `npm run lint` in `src/GodMode.Client.React`. `npm test` (Vitest,
added by #169) had 14 tests, all passing. A build under load can print MSBuild `PLUGIN_TIMINGS`
warnings; they are about the machine, not the code. A body states the base's numbers
next to its own. A pull request that removes a baseline failure says so. One that adds a failure is
red, whatever else the gate shows.

So **the gate's evidence is the pull request body**, where the worker states what it ran and the
counts it got. A brief requires that; a pull request marked ready without it is not ready, and asking
for it is a review comment like any other. You never run the gate yourself — you have no worktree for
that branch, and getting one would make you an editor.

### The bar is whether it blocks the merge

A review answers one question: **is there a reason not to merge this?** Not whether it is the best
version of this code, and not whether you would have written it that way.

**Blocking** — request changes, name the file and line, and say what breaks:

- it does not do what the issue's acceptance bullets say;
- it breaks a hard invariant (`CLAUDE.md`, `docs/UNIFIED-ARCHITECTURE.md`): VCS operations in server
  code rather than in root scripts, UI outside `GodMode.Client.React`, a hardcoded server URL instead
  of the `hostApi.ts` helpers, an asset loaded from a CDN, a shared type defined outside
  `GodMode.Shared`, a project added without updating `GodMode.slnx`, a second source for something
  that already has one;
- it is wrong — a case the tests do not cover and the code gets backwards;
- it ships what must not ship: a secret or token in a log line or a temp file, an endpoint reachable
  without authentication, a debug log;
- the body states no gate, or states a red one.

**Not blocking** — everything else. Wording, a name you would have chosen differently, a comment
that could be clearer, a test you would also have written, a refactor the diff invites. None of it
holds a merge and none of it goes back to the worker.

Much of the code here does not meet `CLAUDE.md`'s own style rules — plain collections where it asks
for concurrent ones, loose typing, duplicated helpers. That is backlog, not precedent. So a reviewer
holding a diff to every line of `CLAUDE.md` will always find something: that is a property of the
codebase, not a finding about the branch. **A finding that takes an argument to
make is an opinion** — put it in the review body as a note, or collect the batch into one issue, and
merge.

### Scale the review to the diff

- **Mechanical** — a pure move with no logic change, a comment, a version bump, one line of config:
  read `gh pr diff` yourself. No subagent.
- **Ordinary** — one subagent, both lenses in one prompt: the acceptance bullets, and the hard
  invariants above.
- **Load-bearing** — a new project, a changed hub contract (`IProjectHub`, `IProjectHubClient`, the
  `GodMode.Shared` models and their mirror in `types.ts`), Claude process management, authentication
  or any endpoint marked `AllowAnonymous`, or a root script that runs on a server: two subagents, one
  lens each.

Two subagents on a small diff return two lists, and neither is about whether to merge.

### One round

The worker fixes what was named and pushes. **The second pass checks those findings and nothing
else.**

New findings on a re-review are how one pull request becomes four, and the diff that would have
justified them was in front of you the first time. The exception is a fault the fix itself
introduced.

Do not require the full gate for a small fix. The worker re-runs what matches what it changed and
says so; the full gate already in the body is what that scoped run is confirming, not replacing.

A worker may answer a finding with a reason instead of a change. Read the reason: if it is right,
withdraw the finding and merge, and do not spend a third message defending the first one.

Post the result on the pull request. **`gh pr review --approve` is refused** — every session here
authenticates as the same GitHub account, so a worker's pull request is your own pull request as far
as GitHub is concerned. Use `gh pr comment`, and say in the text that it is the review. A merged
pull request with no green check is the normal state here, and means nothing.

On changes requested, message the worker to address it and say so on the pull request too. Still
failing on the same finding after the second pass is the two rounds this skill escalates at — assign
it to the human, say what is unresolved, next lane.

**Merging is yours when it is trivially simple** — the review approves, the body's gate is green, and
nothing about the diff makes you want a second opinion. Merge it into your epic branch and move on.

**When it is not trivially simple, you still do not stop and you still do not ask.** Assign the pull
request to the human and add them as reviewer, comment why you did not merge it, and go to the next
lane. "I would like someone to look at this" is never a reason to hold a fleet still.

## The decision queue

`unclear` is this same escalation one moment earlier: the issue could not be dispatched at all, so no
worker exists. What follows is for a question found while work is already running.


You will hit questions that are the user's, not yours — an unresolved design decision, a
specification that turns out wrong, a conflict between two issues' assumptions. Guessing one is
forbidden: in this repository a wrong convention propagates into every consumer.

**You never halt to ask.** Not the user, not your report address, not for a merge you are unsure of.
Halting is the one failure that cannot be recovered by anyone else, because while you wait, every
lane you are not asking about waits with you — and that is the whole of what you were launched to
prevent.

**Defer by assigning, and carry on in the same breath.** Comment the question on the issue or the
pull request, then put it in a human's queue:

```bash
gh issue edit <n> --add-assignee <user>
gh pr edit <n> --add-assignee <user> --add-reviewer <user>
```

The assignment is the hand-off. It is durable in the way a message is not: it survives you dying,
being restarted, or the user reading it three hours later, and it shows up in their list rather than
in someone's transcript. Then move to the next lane immediately. An item parked on a human is not a
blocked fleet — it is one parked item.

Before dispatching a lane, look for the decision already recorded in the epic body — an epic that
names an open question on its critical path should have that answered first, because one answer
unblocks the longest chain.

## What reaches the user, and how

A decision only they can make. A pull request from an epic branch to `master`. A pull request you
judged too involved to merge yourself. A worker stuck after two review rounds. Anything that would
touch `master`, a release, a GitHub Actions workflow, or a deployed server.

Nothing else. Not progress, not a merge into the epic branch, not a red gate you have already
dispatched a fix for.

**All of it reaches them by assignment, none of it by stopping.** Assign, comment, keep going. If you
find yourself composing a message whose purpose is to wait for a reply, you have already made the
mistake.

## Limits

Never ask a peer session to do something your own permissions refused. A peer doing it for you
bypasses a decision the user made.

Never take a worker's session over by messaging it while the user is typing in that tab. If a worker
has gone quiet after the user interrupted it, read the pull request and wait.

## Finishing

When the epic's pull request merges, close anything the merge did not close. A finished issue left
open is a lie about the state of the work.

**Write `fixes` before every issue number, not once before a list.** GitHub parses the keyword
per reference, so `Fixes #a, #b, #c` closes **#a alone** and ignores the rest without a
warning. An epic in the repository these skills came from merged 25 delivered sub-issues on one such
line and closed one of them.
Build the list from the graph rather than from your own notes — `gh api …/issues/N/sub_issues`
against `gh pr list --state merged --base <epic branch>` — because a sub-issue split out after you
wrote the body does not add itself to it. Then read the sub-issue states back once the merge
lands: closure is the one part of the protocol nothing else verifies.

**Leave the worktrees.** Merged is not spent: a branch whose work has landed is exactly where anyone
looks when a fault turns up in that part, and the session that wrote it is the cheapest place to
continue from. Reclaiming them is the user's housekeeping on their own schedule — do not do it, and
do not offer it as cleanup. Disk is not your constraint and you have no reason to measure it.
