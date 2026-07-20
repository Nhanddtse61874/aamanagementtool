---
scope: project
type: gotcha
tags: [test-gate, dotnet, false-green, false-red, flaky, verification]
date: 2026-07-19
---

# The test gate lies in both directions — read it, do not trust it

**False GREEN.** If the API is running it holds `TimesheetApp.Core.dll` open, so
the API project fails to build — and `dotnet test` **still exits 0 printing
`Passed!`**. A green exit code here can mean "one suite never ran."
Counter-check: nothing may be listening on 5080, and **both `Passed!` lines must
appear.** An absent `TimesheetApp.ApiTests.dll` line **is a failed gate**, not a
quiet success.

🔴 **The running API is NOT the only thing that takes that lock — "close the app
first" is necessary and not sufficient.** On 2026-07-20 a build failed with
`CSC : error CS2012: Cannot open …\TimesheetApp.Core\obj\Release\net8.0\
TimesheetApp.Core.dll for writing` while **no API was running**. The holder was
**VS Code's C# Dev Kit build host** (`ms-dotnettools.csdevkit` →
`Microsoft.VisualStudio.ProjectSystem.Server.BuildHost.dll`), alive since hours
earlier. An editor left open reproduces the same silent-stale-build outcome.

Worse, that run still *looked* fine: the app launched and served HTTP 200,
because the previously-built binary was already current. Only the build log said
otherwise. **When an error names a locked `obj/` DLL, identify the holder** —
`Get-CimInstance Win32_Process -Filter "ProcessId=N"` and read its command line —
rather than assuming it is the app.

**False RED.** There is a pre-existing ~15% race in the ApiTests host-startup
path. A lone `SqliteException: no such table: Backlogs` is **not a regression** —
re-run it. Baseline the untouched tree before believing any new red.

**A third failure the gate cannot see at all.** `System.Text.Json` binds record
constructor parameters **by name** and defaults the missing ones — so swapping a
DTO leaves old tests deserialising happily into `default`, green, asserting
nothing. **A gate that cannot move is a gate that cannot notice.**

**How to apply:** never report a gate as a number without reading the run output
that produced it. When a gate number is expected to move because a contract
changed deliberately, say so in advance — a moved gate is then evidence, and an
unmoved one is the surprise worth investigating.

Related: [[a-rule-in-a-brief-is-not-a-rule-an-agent-follows]], [[uat-finds-what-green-tests-cannot]]
