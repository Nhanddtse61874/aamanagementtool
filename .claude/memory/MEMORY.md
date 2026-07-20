# Process Memory — Index

Lessons about **how we worked** — process and workflow only. Facts about *what
the code is* do not belong here (see `memory-store-guide.md` §Boundary rule).
Recall is on-demand: grep this directory for the task's keywords, load the top
matches. Do not preload the whole store.

- [Trust the user over the instrument](trust-the-user-over-the-instrument.md) — an empty query result is only as good as its scope assumption; check that before doubting a user
- [Dropping a gate for speed needs agreement](dropping-a-gate-for-speed-needs-agreement.md) — Plan Checker was skipped silently on M9.2; a breach, not a deviation
- [A rule in a brief is not a rule an agent follows](a-rule-in-a-brief-is-not-a-rule-an-agent-follows.md) — phrase safety as a mechanical invariant, and prove it with a positive control
- [The gate lies in both directions](the-gate-lies-in-both-directions.md) — false GREEN (both `Passed!` lines), false RED (~15% ApiTests race), and DTO swaps that assert nothing
- [UAT finds what green tests cannot](uat-finds-what-green-tests-cannot.md) — 806 green through three defects; no test touches the DOM
- [Write agent output to disk before returning](write-agent-output-to-disk-before-returning.md) — unreachable results cost as much as lost ones
- [Test-edit licence has a hard security limit](test-edit-licence-has-a-hard-security-limit.md) — a deliberately changed contract may move the gate; a security assertion never moves without a plan decision
- [STATE.md must be verified, not trusted](state-md-must-be-verified-not-trusted.md) — an append-forward log keeps stale `(ACTIVE)` markers that do not read as stale
- [An assertion can be vacuous because its baseline is empty](an-assertion-can-be-vacuous-because-its-baseline-is-empty.md) — "X must not change" proves nothing when X starts equal to the expected value; mutate to prove it
- [Gates catch what the author cannot](gates-catch-what-the-author-cannot.md) — M12's plan was factually correct and still carried four defects, each found by a different gate
