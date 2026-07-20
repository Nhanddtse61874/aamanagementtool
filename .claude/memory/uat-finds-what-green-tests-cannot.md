---
scope: global
type: gotcha
tags: [uat, step-8, dom, test-blindspot, batching]
date: 2026-07-20
---

# 806 green tests were blind to three defects a person found in twenty minutes

The 2026-07-20 UAT session was the first time anyone clicked the web app. It
found three defects while the whole suite was green:

- every dropdown on Task List and Reports rendered **blank** whatever the data
  said (`d36075a`) — a property binding applied before the `@for` options
  existed;
- a CSS animation was **erasing the transform that centred things** (`2b7635d`),
  so a modal's controls fell off the bottom of the viewport — and the same
  defect had been live in every toast in the app since it was written;
- a Log Work day total that disagrees with what the user sees (still open).

**All three lived where nothing was looking:** two in computed CSS, one in the
gap between the model and the DOM. The suite asserts through component APIs and
outgoing request bodies. **No test touches the DOM.** That is not a gap in
coverage percentage — it is a gap in *kind*.

**A second-order lesson from the dropdown bug:** the user reported it as two
defects — "values don't show" and "editing doesn't save." It was one. The edit
*did* save; the re-render showed blank, which is indistinguishable from a failed
write. **Treat a user's bug count as a symptom count, not a defect count.**

**How to apply:** a high green number is an argument for shipping only in the
dimensions the tests actually probe. When a milestone changes something a person
looks at, the click-through is the gate — do not let the number substitute for
it. This project batches UAT to the end by user decision; the cost is that trunk
carries unconfirmed behaviour, and that cost is real.

Related: [[trust-the-user-over-the-instrument]], [[the-gate-lies-in-both-directions]]
