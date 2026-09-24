---
name: fleet-worker
description: How to work one issue while an overseer coordinates several in parallel — the draft pull request as your status, the base branch that is not the repository default, the projects and paths you may touch, and parking a design question instead of guessing it. Use when a brief says you are working under an overseer.
---

# Working under an overseer

`CLAUDE.md` already governs how you write code here, including its autonomous-mode recipe. This is
only what is different when somebody is coordinating you, and where the two disagree this wins: your
base is `AC_GWT_BASE_BRANCH` rather than `master`, a draft pull request means *working* rather than
*needs attention*, and a question for a human is parked on the issue rather than signalled with
`!!Attention needed!!`.

## Your pull request is your status

Nobody watches your terminal. The pull request is how the fleet knows what you are doing.

Name work with both numbers, paired — `IS#35/PR#44` — in the body, in comments, and in anything
you send your overseer. You will reach for the pull request number because that is where your own
state lives; the user tracks by issue.

1. Push a first commit early and open the pull request **as a draft**, body `Fixes #N`.
2. Write your contract into the body, under a `## Working agreement` heading: your base branch, the
   projects and paths you own, and who you report to. A skill lasts one turn; the pull request body
   lasts. When this session is resumed tomorrow, that heading is how it learns what it agreed to.
3. Commit at every sensible boundary and push each one. On a branch a commit is a save point.
   **Stage explicit paths; never `git commit -a` or `git add -A`.** The gate itself dirties the tree:
   `npm ci` and `npm install` can rewrite `package-lock.json`, and tooling leaves scratch files
   behind. `-a` sweeps that into your commit, in projects you do not own, and it will read as a real
   edit to whoever reviews it.
4. Mark it ready for review only when the gate is green:

   ```powershell
   cd src/GodMode.Client.React; npm ci; cd ../..   # the server build runs `npm run build` but never installs
   dotnet build GodMode.slnx                         # also builds the React client into the server's wwwroot
   dotnet test GodMode.slnx --no-build
   cd src/GodMode.Client.React; npm run lint; npm test
   ```

   Ready means done, and it is the only signal that says so. **Green means no new failures against
   your base.** The base is not clean: on 2026-09-24 the epic #168 branch (`41d60c4`) built with 0
   errors, passed all 278 `GodMode.Server.Tests`, and had 7 errors and 2 warnings from `npm run lint`
   in the React client. It had no `npm test`: Vitest arrived with #169, whose merge brought 14 tests,
   all passing. A build under
   load can print MSBuild `PLUGIN_TIMINGS` warnings; they are about the machine, not the code.
   Your brief gives your base's numbers; if it does not, measure them once in a detached checkout
   (`git worktree add --detach <scratch dir> $env:AC_GWT_BASE_BRANCH`, then `git worktree remove` it)
   rather than by stashing — the stash is shared by every worktree. Do not add to them.
5. **State that gate in the body, with its numbers** — build errors, tests passed and failed per test
   project, lint errors — and the base's numbers beside them. No CI runs on pull requests here, so
   your worktree is the only place the gate is ever executed and your body is the only place its
   result is ever recorded. A reviewer has no worktree of yours to re-run it in.

## Predict which tests a mutation reddens, before you run it

A mutation is only evidence if it is the mutation you described. So write down which tests should
fail and why, then run it — and **if the red set is wider than you predicted, the mutation is wrong,
not the code.**

The trap is that a wider red set feels like stronger evidence. It is weaker: it proves something
broke, not that the thing you named is what the test sees. The signal to look for is **"all mine"** —
the red set is exactly the tests whose subject is the thing you broke, and everything else is green.
A mutation that reddens files your branch never touched has escaped its scope.

This is not hypothetical. In the repository these skills came from, a worker mutating `roofBodies`
wrote `if (subject !== undefined) continue;` inside a loop where `subject` was shadowed by an
always-defined local, so the guard read as *always continue* and suppressed cutting for every caller instead of for subject-bearing ones. It reddened
seven tests, some of them pre-existing failures for an unrelated reason, and that run was quoted in
the pull request body as proof of a narrow claim. **A shadowed name silently rewrote the mutation.**
The review caught it by noticing that one reddened test takes no subject argument at all, so the
narrow mutation could not have reached it.

Two habits fall out. Name the mutation precisely enough in the body that a reader can check it
against the failures you quote — *"expected 2 to be 4, in my three tests, 598 others green"* is
checkable and *"7 failures"* is not. And prefer mutating a value over deleting a guard: a changed
constant cannot be widened by a binding you forgot was there.

**Commit before you mutate, and revert with git rather than by hand.** A mutation is a deliberate
break you intend to undo, so the undo has to be exact — and `git checkout -- <file>` reverts the
*whole file*, taking any uncommitted work in it with the mutation. A worker there lost a round of
review edits that way and redid them from the review text; its own earlier mutation scripts committed
first, and it dropped the habit when it moved to a one-off command line. The shortcut looks like the
same operation and is not. On a branch a commit is a save point, so this costs one commit.

## Run what you need, when you need it

Each worktree has its own `bin/`, `obj/` and `node_modules`, so your gate does not touch anyone
else's. Run the gate and the scoped runs when you want their answer. **No lane, no lock, no
claiming, no waiting.**

One thing survives, and it is about reading a result rather than taking a turn: **a suite that fails
in a way that looks like resource exhaustion — an unrelated file timing out, a worker dying — is
re-run alone before you report it.** Green under load owes no re-run; only red is suspect.

## Your base is not `master`

It is `AC_GWT_BASE_BRANCH`, and it is usually an epic branch or a sibling's branch. Open the pull
request against it:

```bash
gh pr create --draft --base "$AC_GWT_BASE_BRANCH" --title … --body …
```

There is no upstream on a branch created this way, so your first push is `git push -u origin HEAD`.

## Stay inside your projects

Your brief names them. Another session is working the next project over, and two sessions editing
one file is a merge conflict neither of you can see coming.

Something you need is outside them: say so, do not reach for it. That is a message to your overseer
and a comment on the issue — not a quiet edit.

## Park a design question, never guess it

You will find questions the issue does not answer — a decision the specification leaves open, two
issues that assume different things, a convention that turns out wrong. A guess here propagates into
every consumer, and `CLAUDE.md` makes `docs/UNIFIED-ARCHITECTURE.md` binding: work that guesses
against it is work that gets redone.

Comment the question on the issue, message your overseer, and carry on with everything that does not
depend on the answer. Blocking the whole issue on it is the last resort.

## Announce yourself, then report

Message your overseer once when you start, so it learns your address — session names are not
predictable and it cannot guess yours. Message it again when the pull request goes ready, and when a
review comes back and you have pushed the fix.

Between those, silence is correct. It reads the pull request.

## Answering a review

Fix what the review named. Do not fix what it did not — a review is not an invitation to improve the
branch, and every unasked change is one more thing the next pass has to read.

**Re-run the gate that matches your fix, not the gate that matched the pull request.** The full gate
in your body already passed on this branch. A scoped run is what says your fix did not invalidate
it.

| the fix changed | run |
|---|---|
| only `*.md` | nothing |
| code inside `src/GodMode.Client.React` only | `npm run build`, `npm run lint` and `npm test` there |
| code inside one .NET project, no change to its public types | `dotnet build` that project, then `dotnet test tests/GodMode.Server.Tests` (or the test project that covers it) |
| `GodMode.Shared`, a hub contract, a `.csproj`, `Directory.*.props`, `GodMode.slnx`, or more than one project | the full gate again |

Say which you ran and why it covers the change. On a machine carrying several worktrees at once the
full gate is a cost the whole fleet pays.

**You may disagree once.** A finding you believe is wrong gets one reply on the pull request saying
why, and no change — that is cheaper than making a change nobody wanted and reverting it later. If
your overseer holds after that, make the change.

## You never merge

Not your own pull request, not anyone's, not into the epic branch. Your overseer merges after
review; the epic branch reaches `master` only through the user. If you think your work is ready to
land, say so — do not land it.
