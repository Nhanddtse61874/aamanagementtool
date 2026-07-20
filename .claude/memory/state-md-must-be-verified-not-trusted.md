---
scope: project
type: gotcha
tags: [state-files, drift, validate-state, step-0, resume]
date: 2026-07-20
---

# STATE.md is an append-forward log, so stale sections outlive the work

`.planning/STATE.md` in this project grows by prepending new detail above old.
That keeps history, but on 2026-07-20 a `validate-state` pass found **four
sections still marked `(ACTIVE)`, `(PARKED)` or `(QUEUED)`** for milestones the
file's own header said had shipped, plus a `RESUME HERE` block pointing seven
commits behind `main` and — worse — claiming the running app was pointed at the
**real company database** when it was pointed at a disposable one.

ROADMAP.md had the mirror problem: M10 appeared under both `## Active` and
`## Shipped`, M11 under both `## Planned` and `## Shipped`.

**Why this is dangerous rather than untidy:** a stale marker does not read as
stale. A session resuming from that file would have concluded M10 was at STEP 2
Brainstorm and, following the resume pointer, might have aimed a run at
production data.

**How to apply:**

- Run `validate-state` at STEP 0 **before** reading STATE.md as fact, every time.
  Treat WARN as a real signal, never as noise.
- At STEP 11, when a milestone ships, **re-label its section in the same commit**
  — the ROADMAP move and the STATE marker are one action, not two.
- When superseding a block, relabel and keep the original quoted rather than
  deleting it, and **name explicitly which claims are now wrong**. A section
  marked "superseded" with no list of what changed still misleads.

Related: [[trust-the-user-over-the-instrument]]
