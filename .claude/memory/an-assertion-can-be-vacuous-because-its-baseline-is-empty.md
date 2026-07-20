---
scope: global
type: gotcha
tags: [testing, vacuous-assertion, mutation-testing, baseline, review]
date: 2026-07-20
---

# An assertion can prove nothing because its baseline is empty, not because it is wrong

M12 shipped this test, and three gates passed it:

```typescript
expect(inputs[1].value).withContext('the text input must be untouched').toBe('');
```

It guards *"clicking a preset icon must not touch the tag's text."* On the create
path the text field is `''` **before** the click and `''` **after**. So it catches
a preset that *writes* to text — that would read `Risk` — and is **blind to one
that clears it**, because `setTagText('')` is indistinguishable from doing nothing.
The colour field was not asserted at all.

Two of the milestone's `must_haves` were therefore passing because there was
nothing for them to fail against.

**This is the same family as M9.2's `grid-state.spec.ts:190`** — an assertion that
is *correct about what it checks* and *proves nothing about what it guards*. The
mechanisms differ (that one asserted a parse in isolation and never traced the
`null` two files onward to a delete; this one had no baseline to differ from) but
the shape is identical, and it has now cost this project twice.

**How to apply.** For any "X must not change" assertion, ask: **what is X's value
before the action?** If it equals the expected-after value, the assertion cannot
fail and is decoration. Fix it by choosing a starting state where the two differ —
in M12 that meant testing the *edit* path, where the tag already has text and a
colour, rather than the *create* path where everything starts blank.

**Then prove it, do not argue it.** Mutate the code under test so the property is
genuinely violated and confirm the test goes red. M12 did this: mutating
`setTagIcon` to also clear the text made the new test fail and left the old one
**passing** — the vacuity demonstrated rather than asserted. A test written to fix
a vacuous test, that is itself vacuous, is worse than what it replaced.

Related: [[the-gate-lies-in-both-directions]], [[uat-finds-what-green-tests-cannot]], [[gates-catch-what-the-author-cannot]]
