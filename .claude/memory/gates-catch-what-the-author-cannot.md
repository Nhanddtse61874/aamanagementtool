---
scope: global
type: plan-misfire
tags: [plan-check, review, subagent, step-6, step-7, self-review]
date: 2026-07-20
---

# The plan author is the worst reviewer of their own plan — run the gates

M12 was a small, well-researched plan. Every structural claim it made about the
codebase checked out exactly — line numbers, helper signatures, tab labels, CSS
variable names. Reviewers said so explicitly, and it is rare.

**It still carried four defects, and the author caught none of them.**

| # | Defect | Caught by |
|---|---|---|
| 1 | The DOM test had **no `fakeAsync`/`tick()` at all** — `[ngModel]` writes model→view on a microtask, so it could not have passed | Plan Checker |
| 2 | With `tick()` added it sat **in the wrong position**; correct order is `detectChanges() → tick() → detectChanges()` | implementer |
| 3 | A spy returning `Observable` was never stubbed, so the code threw on `undefined.pipe()`, the framework swallowed it, and **4 errors printed while the suite reported green** | implementer |
| 4 | An assertion was **structurally vacuous** — see [[an-assertion-can-be-vacuous-because-its-baseline-is-empty]] | final review |

Note the layering: **each gate caught what the previous one missed.** Plan Checker
found that `tick()` was absent but not that its position would also be wrong. The
implementer found the ordering and the unstubbed spy but classified the vacuous
assertion as fine. The final review found the vacuity. No single reviewer would
have produced this list.

**How to apply.**

- **Never skip a configured gate to save time.** M9.2 skipped Plan Checker and
  escaped on luck (see [[dropping-a-gate-for-speed-needs-agreement]]); M12 ran it
  and it immediately caught a test that could not have passed, plus a styling bug
  that would have rendered every button invisible.
- **Confidence in a plan is not evidence about it.** "Every fact I asserted was
  correct" and "the plan was correct" are different claims. M12 was the first and
  not the second.
- **Give reviewers distinct lenses.** Spec-compliance, code-quality and a
  whole-change final pass found different defects precisely because they were
  asked different questions. Running the same review three times would have found
  the same things three times.
- **Prize implementers who volunteer deviations.** Two of the four here surfaced
  because a subagent reported something rather than quietly working around it —
  including one that reported its own careless `git checkout --`. That behaviour
  is worth more than a clean-looking report, and briefs should invite it
  explicitly: *"if this contradicts the code you find, STOP AND REPORT."*
