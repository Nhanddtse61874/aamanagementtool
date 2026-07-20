---
scope: global
type: plan-misfire
tags: [plan-check, step-6, gate, process-breach, hurry]
date: 2026-07-19
---

# If speed requires dropping a configured gate, say so and get agreement first

On M9.2 the controller went straight from writing the plan to dispatching
implementers, **skipping Plan Checker**, under a user instruction to hurry.
`workflow.plan_check: true` and STEP 6 mandates the 11-dimension check. The
breach was not that a gate was dropped — it is that **the drop was never
stated.** A deviation is agreed in advance; this was a silent omission.

**What it cost, honestly: less than it might have.** The plan was small (3
tasks, one domain). Two gaps a checker would plausibly have caught surfaced
anyway — a 400-channel test invalidated by a new client-side cap, and an
unspecified owner for clearing `invalidCells` — but both surfaced because the
*implementers volunteered deviations*, not because a gate caught them.

**Do not generalise from "it worked out."** That is luck wearing the costume of
process. The next time the plan is large or spans domains, the same omission
produces a different outcome.

**How to apply:** a user asking to hurry is asking for less ceremony, not for
less safety, and they cannot consent to a trade they were not told about. Name
the gate, state what skipping it risks, and get a yes. Recorded in the CLAUDE.md
deviation table as a breach, not a deviation.

Related: [[test-edit-licence-has-a-hard-security-limit]]
