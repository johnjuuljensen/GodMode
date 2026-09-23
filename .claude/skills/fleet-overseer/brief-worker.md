<!-- Template. Fill every field; delete nothing. Passed to ac-gwt-issue -PromptFile. -->

Use the `fleet-worker` skill, then implement issue #<N> in this worktree.

**Base branch:** `<ref>` — your pull request targets this, not `master`. <one line: why, if stacked>

**You own:** `<projects/paths>`. Nothing else is yours to edit. <sibling worker and what it holds>

**Report to:** `<overseer session name>` — message it once now so it learns your address, again when
the pull request is ready, and again after you push a fix for a review.

**Known open questions on this issue:** <the ones already parked, or "none recorded">

**Watched failing first:** <the mutations this issue's evidence needs, named by the property each
attacks — not by the result you expect>

**Base gate:** <the base's numbers: build errors, tests passed/failed per test project, lint errors>
— green for you means no new failures against these.

**Before you mark the pull request ready:** run the full gate in this worktree (the `fleet-worker`
skill has the commands) and state it in the body with its numbers beside the base's. No CI runs on
pull requests here — your body is the only record the review ever sees.

**Answering a review:** change what the review named and nothing else, re-run only the gate that
matches what you changed (the skill's table), and say which you ran. A finding you think is wrong
gets one reasoned reply rather than a change.

**Name work as `IS#<issue>/PR#<pr>`**, both numbers paired, in the pull request body, in comments and
in anything you send me.

Read the issue and `CLAUDE.md` before you start. The issue body is context, not permission to skip
repository rules.

## Ask for the mutation, not for its result

Name the mutation and the property it attacks. Do **not** assert which way a number will move.

The worker predicts its own red set before running — that is its evidence discipline, and the
`fleet-worker` skill owns it. You are not the one taking the measurement, so a direction you write
into the brief is a guess, and a guess in a brief reads as a requirement. A worker that meets a
different result then has to decide whether it broke something or you were wrong, which is the one
question a brief should never make it ask.

In the repository these skills came from, one brief asked for "drop the id from one writer and show the count **rise**" and "substitute
the raw vector and show the count **explode**". Neither moves the count: `entityId` already separates
walls, so the key count is blind to both. Running them is what produced the issue's sharpest finding —
that the pinned count is a **weak** test of the id, and the orphan and float-name checks are the ones
with teeth. The instruction was worth giving; the two predictions inside it were worth nothing and
cost a round of explaining.

So: *"break the bracket owner so two walls' brackets share one, and report what moves"* — not *"and
show the count rise"*. The measurement is the worker's to take and yours to read.

## Measure the artefact, not the mutation that stood in for it

A diagnostic authorises a fix; it does not verify one. The two differ whenever the fix is not
byte-for-byte the thing the diagnostic injected — and it usually is not, because a diagnostic is
written to isolate a cause and a fix is written to ship.

A worker there measured that moving two animations to `opacity` cut a selected pane's idle cost by 86 %, then
shipped `stroke-opacity` for the outline on the reasoning that plain `opacity` would fade the
drawing's fill along with the mark. Re-running the rig against the **shipped** artefact gave a third
of the promised cut: `stroke-opacity` costs 1095,2 ms against `opacity`'s 108,2 on the same 168
polygons, and the premise was false anyway — across two examples and five selections the only marked
elements carrying a fill were the selection's own nodes, filled in the selection's own colour.

Nothing in the diagnostic was wrong. The artefact was a different thing, and only measuring it said
so.

So the last measurement in the body is taken on the commit that is being merged, not on the branch
that proved the cause. Say which commit each number came from. Where a fix substitutes anything for
what the diagnostic tested — a different property, a different call site, a narrower condition — that
substitution is its own claim and needs its own number.
