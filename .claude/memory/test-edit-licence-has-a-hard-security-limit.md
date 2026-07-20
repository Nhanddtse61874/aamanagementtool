---
scope: global
type: gotcha
tags: [test-gate, contract-change, security, stop-and-report, step-7]
date: 2026-07-19
---

# "Don't edit a test to reconcile a gate" has an exception — and a hard limit

**The exception is real.** The rule exists to stop an agent deleting a test that
caught a regression. It does **not** apply when the contract the test pins was
deliberately changed by the milestone. This controller wrote the gate as a fixed
number five times and was wrong five times; a deliberately changed contract
**moves the gate, and that is expected, not a regression.**

**The limit is harder than the exception.** M9's plan, as first written,
licensed an agent to delete a **security** test. If the test about to be moved
asserts a security property — **STOP AND REPORT. That is a plan decision, never
an agent's.**

**How to apply:** when a plan grants a test-edit licence, the licence must name
which tests and why, and must state the security carve-out explicitly. On M9.2
this was recorded correctly: `grid-state.spec.ts:190` and `:196` pin parse rules
the milestone deliberately reverses, **neither asserts a security property**, so
the licence applied and the Angular gate was expected to move.

**One more thing the M9.2 case taught:** the test being reversed argued *in its
own comment* that its behaviour was the more honest choice. When you reverse a
contract, **the comment defending it must be replaced, not left behind
contradicting the code.**

Related: [[dropping-a-gate-for-speed-needs-agreement]], [[the-gate-lies-in-both-directions]]
