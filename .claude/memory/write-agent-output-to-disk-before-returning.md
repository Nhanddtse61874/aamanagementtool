---
scope: global
type: gotcha
tags: [fan-out, subagent, artifact-persistence, workflow, step-4]
date: 2026-07-19
---

# Fan-out agents must write to disk before returning, not hand back a value

The first M10 coverage-audit run completed normally: 410 agents, 0 errors,
~72 minutes, 796 triaged behaviours. **Nothing was lost.** But the completion
notification had not arrived when the next session ran `validate-state`, so from
that session's view there was no running agent and no `M10*` file on disk —
indistinguishable from a crashed run. The audit was re-run from scratch.

**The results were unreachable at the moment a decision needed them, which cost
exactly as much as losing them.** "Unreachable" and "lost" are the same defect
from the consumer's side.

**The rule that would have prevented it was already written** in CLAUDE.md
STEP 0 — *"Artifact persistence: write to disk immediately, never hold in
memory."* It had been applied to plans and specs and never to agent fan-out
output. A rule scoped narrowly by habit is a rule with a hole in it.

**How to apply:** every fan-out agent writes its own file (`.planning/<run>/<KEY>.md`)
**before returning**, and the synthesizer reads those files off disk rather than
from return values. A crash then costs only the incomplete sections. This fix
was carried into the re-run and worked.

**A related judgement call worth keeping:** the re-run's memo was treated as
authoritative and the first run was *not* given equal weight — its brief framed
the losable surface as "ViewModels, XAML, code-behind," which under-weighted
`App.xaml.cs` startup orchestration and would have hidden an entire blocker. Two
runs are not two opinions when their briefs differ.

Related: [[a-rule-in-a-brief-is-not-a-rule-an-agent-follows]]
