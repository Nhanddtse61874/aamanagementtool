---
scope: global
type: gotcha
tags: [investigation, uat, scoping, false-negative, measurement]
date: 2026-07-20
---

# When the instrument disagrees with the user, suspect the instrument first

During the 2026-07-20 UAT session the controller queried the backlog API, saw
nothing, and told the user *"your backlog does not exist in the database"* —
asking them to justify data they had watched themselves create. **They were
right and the measurement was wrong.** It cost about an hour.

The mechanism: every backlog/tasklist API in this project is scoped to the
caller's teams. The controller was authenticated as `admin` (team 1); the
user's data lives in team 2. An empty result was read as "no data" when it
meant "not visible from where you are standing."

**The process lesson, which generalises past this API:** a user reporting
behaviour they personally observed is primary evidence. A query returning
empty is *secondary* evidence — it is only as good as the assumption that the
query's scope matches the user's. Before contradicting a user, state the
assumption the instrument rests on and check that one first.

**How to apply:** when a read contradicts a user report, do not ask the user to
justify themselves. Ask instead: *what would have to be true about my query for
this empty result to be wrong?* Then test that. Only after the instrument is
cleared does the user's report come into doubt.

Related: [[uat-finds-what-green-tests-cannot]], [[state-md-must-be-verified-not-trusted]]
