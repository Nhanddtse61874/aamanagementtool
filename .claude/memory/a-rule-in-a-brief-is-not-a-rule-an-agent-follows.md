---
scope: project
type: gotcha
tags: [db-safety, brief, invariant, positive-control, dispatch, verification]
date: 2026-07-19
---

# A safety rule stated in a brief is not a safety rule an agent follows

Every implementer brief since M8.4 carried the instruction *"pin all three
seams — `DbPath`, `ConfigPath`, `KeyRingPath`."* It was repeated faithfully for
three milestones and it was **insufficient the entire time**: `DbPath` is only a
default, so an agent that pinned all three seams *and* aimed `ConfigPath` at the
production config would have passed the stated check and opened the live company
database.

**Two process lessons, and the second matters more:**

1. **A rule phrased as a list of things to set is weaker than a rule phrased as
   an invariant.** "Pin three seams" is a checklist an agent can satisfy while
   still being wrong. "`ConfigPath` must point at a path that does not exist"
   is an invariant a fresh `mktemp -d` guarantees mechanically.

2. **What actually saved this project was never the rule — it was the proof.**
   Grepping the startup log for the substring `no users yet, nothing to
   bootstrap` fires only on an empty database. A stated rule is a hope; a
   positive control is evidence. The same pattern settled F3 in M11 (proving
   `UseSetting` beats `appsettings.json` by running it with a positive control
   rather than by reasoning) and settled whether `File.Delete` throws on an open
   handle (by scratch experiment, after two documentation passes contradicted
   each other).

**How to apply:** when a brief contains a safety instruction, ask whether an
agent could satisfy its literal text and still cause the harm. If yes, restate
it as an invariant, and pair it with an observable that proves compliance after
the fact. Never accept reasoning where a run is available.

A recorded corollary from M8.2: **a decision in STATE.md is not a decision an
agent knows.** Cross-cutting rules must be restated verbatim in every brief.

Related: [[the-gate-lies-in-both-directions]], [[write-agent-output-to-disk-before-returning]]
