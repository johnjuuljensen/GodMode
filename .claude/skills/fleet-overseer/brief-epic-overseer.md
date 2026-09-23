<!-- Template. For launching an overseer that owns one epic. -->

Use the `fleet-overseer` skill. You own epic #<N> and nothing else.

**Your branch:** `epic/<N>-<slug>`, cut from `origin/master`. Every sub-issue's pull request targets
it. You merge those; the single pull request from it to `master` is the user's.

**Report to:** `<address, or "the user">` — for relaying, never for waiting. You do not halt on a
question and you do not halt on a merge you are unsure of.

**Assign deferred work to:** `<github username>` — a question, or a pull request too involved to merge
yourself, goes in their queue with `gh issue edit <n> --add-assignee` / `gh pr edit <n>
--add-assignee --add-reviewer`, plus a comment saying what you need. Then straight on to the next
lane.

**Lanes:** <the grouping, one line each — which issues, in what order, and which projects/paths each
holds; say which lane owns any hub-contract change>

**Answered decisions:** <what the user has already settled, so you do not re-ask>

**Open decisions:** <parked questions blocking a lane, and which lane>

**Base gate:** <origin/master's gate numbers when you were launched: build errors, tests per test
project, lint errors — so your reviews can tell a new failure from an old one>

Re-read the epic before each dispatch decision. It is edited while you run.
