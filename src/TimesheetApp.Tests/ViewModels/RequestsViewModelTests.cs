using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public sealed class BacklogsViewModelTests
{
    private readonly Mock<IBacklogRepository> _requests = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ITaskTemplateRepository> _templates = new();

    private BacklogsViewModel CreateVm() =>
        new(_requests.Object, _tasks.Object, _templates.Object);

    // P10: a VM wired with a multi-team filter (two teams, active = 20).
    private static Mock<ICurrentTeamService> TeamSvc(int activeId = 20)
    {
        var mock = new Mock<ICurrentTeamService>();
        mock.SetupGet(s => s.AvailableTeams).Returns(new[]
        {
            new Team(10, "Alpha", true, DateTimeOffset.UtcNow),
            new Team(20, "Beta", true, DateTimeOffset.UtcNow),
        });
        mock.SetupGet(s => s.ActiveTeamId).Returns(activeId);
        return mock;
    }

    private BacklogsViewModel CreateVmWithTeam(ICurrentTeamService team) =>
        new(_requests.Object, _tasks.Object, _templates.Object,
            messenger: null, currentUser: null, users: null, pcaContacts: null, tagsRepo: null,
            currentTeam: team);

    private static Backlog R(int id, string code, string proj) =>
        new(id, code, proj, DateTimeOffset.UtcNow);

    public BacklogsViewModelTests()
    {
        _templates.Setup(t => t.GetAllAsync())
            .ReturnsAsync(new[] { new TaskTemplate(1, "Web", "Setup", 0) });
        // LoadAsync batch-loads task counts in one query (N+1 fix) — default to an empty map.
        _tasks.Setup(t => t.GetActiveByBacklogsAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(new Dictionary<int, IReadOnlyList<TaskItem>>());
    }

    [Fact] // REQ-01: LoadAsync populates the list with all requests
    public async Task LoadAsync_loads_all_requests()
    {
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>()))
            .ReturnsAsync(new[] { R(1, "RQ-1", "Alpha"), R(2, "RQ-2", "Beta") });
        var vm = CreateVm();

        await vm.LoadAsync();

        Assert.Equal(2, vm.Backlogs.Count);
    }

    [Fact] // REQ-01: search filters the loaded list live (in-memory), by code or project
    public async Task SearchTerm_filters_loaded_list_live()
    {
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>()))
            .ReturnsAsync(new[] { R(1, "RQ-1", "Alpha"), R(2, "RQ-2", "Beta") });
        var vm = CreateVm();
        await vm.LoadAsync();
        Assert.Equal(2, vm.Backlogs.Count);

        vm.SearchTerm = "alp"; // live — no second DB query

        Assert.Single(vm.Backlogs);
        Assert.Equal("RQ-1", vm.Backlogs[0].BacklogCode);
        _requests.Verify(r => r.SearchAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>?>()), Times.Once); // loaded once
    }

    [Fact] // Structured filters (project/status/assignee/month) combine with the search, all in-memory.
    public async Task Filters_combine_in_memory()
    {
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(new[]
        {
            new Backlog(1, "RQ-1", "ARCS", DateTimeOffset.UtcNow, Type: "Implement"),
            new Backlog(2, "RQ-2", "ARMS", DateTimeOffset.UtcNow, Type: "Implement"),
            new Backlog(3, "RQ-3", "ARCS", DateTimeOffset.UtcNow, Type: "Estimate"),
        });
        var vm = CreateVm();
        await vm.LoadAsync();

        vm.FilterProject = "ARCS";          // RQ-1, RQ-3
        Assert.Equal(2, vm.Backlogs.Count);
        vm.FilterType = "Implement";      // RQ-1 only
        Assert.Single(vm.Backlogs);
        Assert.Equal("RQ-1", vm.Backlogs[0].BacklogCode);
    }

    [Fact] // REQ-02: SaveNewAsync inserts request then inserts each active task with order_index
    public async Task SaveNewAsync_inserts_request_and_ordered_tasks()
    {
        _requests.Setup(r => r.InsertAsync(It.IsAny<Backlog>())).ReturnsAsync(42);
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        vm.Editor!.BacklogCode = "RQ-9";
        vm.Editor.Project = "Gamma";
        vm.Editor.AddTask("First");
        vm.Editor.AddTask("Second");

        await vm.SaveNewAsync();

        _requests.Verify(r => r.InsertAsync(
            It.Is<Backlog>(x => x.BacklogCode == "RQ-9" && x.Project == "Gamma")), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.BacklogId == 42 && x.TaskName == "First" && x.OrderIndex == 0)), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.BacklogId == 42 && x.TaskName == "Second" && x.OrderIndex == 1)), Times.Once);
        Assert.Null(vm.Editor); // editor closed after save
    }

    [Fact] // A new request with no tasks is rejected: editor stays open with an error, nothing inserted.
    public async Task SaveNewAsync_requires_at_least_one_task()
    {
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        vm.Editor!.BacklogCode = "RQ-EMPTY";
        vm.Editor.Project = "ARCS";
        // no tasks added

        await vm.SaveNewAsync();

        _requests.Verify(r => r.InsertAsync(It.IsAny<Backlog>()), Times.Never);
        Assert.NotNull(vm.Editor);                       // editor stays open
        Assert.False(string.IsNullOrEmpty(vm.Editor!.ErrorMessage));
    }

    [Fact] // REQ-02: applying a template before save inserts the template's tasks
    public async Task SaveNewAsync_with_template_inserts_template_tasks()
    {
        _requests.Setup(r => r.InsertAsync(It.IsAny<Backlog>())).ReturnsAsync(7);
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        vm.Editor!.BacklogCode = "RQ-T";
        vm.Editor.Project = "Tmpl";
        vm.Editor.SelectedTemplateName = "Web";
        vm.Editor.ApplyTemplate();

        await vm.SaveNewAsync();

        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.BacklogId == 7 && x.TaskName == "Setup" && x.OrderIndex == 0)), Times.Once);
        // template-only: exactly one task inserted, no stray rows from other templates
        _tasks.Verify(t => t.InsertAsync(It.IsAny<TaskItem>()), Times.Once);
    }

    [Fact] // REQ-03: SaveEditAsync updates the request and inserts only new tasks
    public async Task SaveEditAsync_updates_request_and_inserts_new_tasks()
    {
        _requests.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(R(5, "RQ-5", "Old"));
        _tasks.Setup(t => t.GetActiveByBacklogAsync(5))
            .ReturnsAsync(new[] { new TaskItem(50, 5, "Existing", 0, true) });
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginEditAsync(5);
        vm.Editor!.Project = "New";
        vm.Editor.AddTask("Added");

        await vm.SaveEditAsync();

        // The editor save is a bump-only write (it carries no version), so it lands on UpdateAsync.
        _requests.Verify(r => r.UpdateAsync(
            It.Is<Backlog>(x => x.Id == 5 && x.Project == "New"),
            It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.BacklogId == 5 && x.TaskName == "Added")), Times.Once);
        // existing task NOT re-inserted
        _tasks.Verify(t => t.InsertAsync(It.Is<TaskItem>(x => x.TaskName == "Existing")), Times.Never);
    }

    [Fact] // REQ-04: removing an existing task in edit calls SetActiveAsync(false), not request delete
    public async Task SaveEditAsync_soft_deletes_removed_existing_task()
    {
        _requests.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(R(5, "RQ-5", "Old"));
        _tasks.Setup(t => t.GetActiveByBacklogAsync(5))
            .ReturnsAsync(new[] { new TaskItem(50, 5, "Existing", 0, true) });
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginEditAsync(5);
        vm.Editor!.RemoveTask(vm.Editor.Tasks[0]); // flag existing task removed

        await vm.SaveEditAsync();

        _tasks.Verify(t => t.SetActiveAsync(50, false), Times.Once);
    }

    [Fact] // REQ-04: IBacklogRepository has no SetActiveAsync — assert the type contract
    public void BacklogRepository_has_no_SetActiveAsync()
    {
        Assert.Null(typeof(IBacklogRepository).GetMethod("SetActiveAsync"));
    }

    // ===== P10 W7 (TM-07 / FIX-2) =====

    [Fact] // Default filter = active team only → SearchAsync is scoped to [activeTeamId].
    public async Task Load_scopes_search_to_active_team_by_default()
    {
        var team = TeamSvc(activeId: 20);
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>()))
            .ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVmWithTeam(team.Object);

        Assert.Equal(new[] { 20 }, vm.TeamFilter!.CheckedTeamIds);

        await vm.LoadAsync();

        _requests.Verify(r => r.SearchAsync(null,
            It.Is<IReadOnlyList<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 20 }))), Times.Once);
    }

    [Fact] // Checking a second team aggregates → SearchAsync gets both ids on reload.
    public async Task Checking_second_team_reloads_with_both_ids()
    {
        var team = TeamSvc(activeId: 20);
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>()))
            .ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVmWithTeam(team.Object);
        await vm.LoadAsync();

        vm.TeamFilter!.Teams.First(t => t.Team.Id == 10).IsChecked = true; // triggers reload

        await Task.Yield();
        _requests.Verify(r => r.SearchAsync(null,
            It.Is<IReadOnlyList<int>?>(ids => ids != null && ids.OrderBy(x => x).SequenceEqual(new[] { 10, 20 }))),
            Times.AtLeastOnce);
    }

    [Fact] // FIX-2 (TM-06): a NEW backlog is stamped with the active team id.
    public async Task SaveNew_stamps_active_team_id()
    {
        var team = TeamSvc(activeId: 20);
        _requests.Setup(r => r.InsertAsync(It.IsAny<Backlog>())).ReturnsAsync(99);
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>?>()))
            .ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVmWithTeam(team.Object);
        await vm.BeginCreateAsync();
        vm.Editor!.BacklogCode = "RQ-T";
        vm.Editor.Project = "ARCS";
        vm.Editor.AddTask("First");

        await vm.SaveNewAsync();

        _requests.Verify(r => r.InsertAsync(It.Is<Backlog>(b => b.TeamId == 20)), Times.Once);
    }
}

// v7 (P8 / TL-03): sqlite-backed round-trip of the editor — the 7 tracking columns, the selected PCA
// contact and the checked tags must survive a SaveNew -> reload, and an edit must replace the tag set.
public sealed class BacklogsViewModelV7RoundTripTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private BacklogRepository _backlogs = null!;
    private TaskRepository _tasks = null!;
    private TaskTemplateRepository _templates = null!;
    private PcaContactRepository _pca = null!;
    private TagRepository _tags = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _backlogs = new BacklogRepository(_db);
        _tasks = new TaskRepository(_db);
        _templates = new TaskTemplateRepository(_db);
        _pca = new PcaContactRepository(_db);
        _tags = new TagRepository(_db);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private BacklogsViewModel CreateVm() =>
        new(_backlogs, _tasks, _templates, null, null, null, _pca, _tags);

    [Fact] // SaveNew persists all 7 v7 fields + PCA + checked tags; reload round-trips them.
    public async Task SaveNew_roundtrips_tracking_fields_pca_and_tags()
    {
        var pcaId = await _pca.InsertAsync(new PcaContact(0, "Acme", true));
        var t1 = await _tags.InsertAsync(new Tag(0, "Urgent", "⚡", "#FF0000", DateTimeOffset.UtcNow));
        var t2 = await _tags.InsertAsync(new Tag(0, "Review", "👀", "#00FF00", DateTimeOffset.UtcNow));

        var vm = CreateVm();
        await vm.BeginCreateAsync();
        var e = vm.Editor!;
        e.BacklogCode = "REQ-RT";
        e.Project = "ARCS";
        e.AddTask("Do it");
        e.DeadlineInternal = new DateOnly(2026, 7, 10);
        e.DeadlineExternal = new DateOnly(2026, 7, 20);
        e.RoughEstimateText = "12.5";
        e.OfficialEstimateText = "16";
        e.ProgressText = "40";
        e.Note = "first cut";
        e.SelectedPcaContact = e.PcaContacts.First(p => p.Id == pcaId);
        e.TagPicks.First(p => p.Tag.Id == t1).IsChecked = true;
        e.TagPicks.First(p => p.Tag.Id == t2).IsChecked = true;

        await vm.SaveNewAsync();

        var saved = (await _backlogs.SearchAsync(null)).Single(b => b.BacklogCode == "REQ-RT");
        Assert.Equal(new DateOnly(2026, 7, 10), saved.DeadlineInternal);
        Assert.Equal(new DateOnly(2026, 7, 20), saved.DeadlineExternal);
        Assert.Equal(12.5m, saved.RoughEstimateHours);
        Assert.Equal(16m, saved.OfficialEstimateHours);
        Assert.Equal(40, saved.ProgressPercent);
        Assert.Equal("first cut", saved.Note);
        Assert.Equal(pcaId, saved.PcaContactId);
        Assert.Equal(new[] { t1, t2 }, (await _backlogs.GetTagIdsAsync(saved.Id)).OrderBy(x => x).ToArray());
    }

    [Fact] // Editing replaces the tag set (SetTagsAsync replace-all) and re-checks the right boxes on reload.
    public async Task SaveEdit_replaces_tag_set_and_reload_rechecks()
    {
        var t1 = await _tags.InsertAsync(new Tag(0, "A", "a", "#111111", DateTimeOffset.UtcNow));
        var t2 = await _tags.InsertAsync(new Tag(0, "B", "b", "#222222", DateTimeOffset.UtcNow));
        var t3 = await _tags.InsertAsync(new Tag(0, "C", "c", "#333333", DateTimeOffset.UtcNow));

        var id = await _backlogs.InsertAsync(new Backlog(0, "REQ-EDIT", "ARCS", DateTimeOffset.UtcNow));
        var taskId = await _db.SeedTaskAsync(id, "Existing");
        await _backlogs.SetTagsAsync(id, new[] { t1, t2 });

        var vm = CreateVm();
        await vm.BeginEditAsync(id);
        // Reload pre-checks the saved tags.
        Assert.Equal(new[] { t1, t2 }, vm.Editor!.CheckedTagIds.OrderBy(x => x).ToArray());

        // Replace {t1,t2} -> {t3}.
        vm.Editor.TagPicks.First(p => p.Tag.Id == t1).IsChecked = false;
        vm.Editor.TagPicks.First(p => p.Tag.Id == t2).IsChecked = false;
        vm.Editor.TagPicks.First(p => p.Tag.Id == t3).IsChecked = true;

        await vm.SaveEditAsync();

        Assert.Equal(new[] { t3 }, await _backlogs.GetTagIdsAsync(id));
    }

    [Fact] // Out-of-range progress is rejected (null) so it is never persisted.
    public async Task SaveNew_does_not_persist_out_of_range_progress()
    {
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        var e = vm.Editor!;
        e.BacklogCode = "REQ-PROG";
        e.Project = "ARCS";
        e.AddTask("T");
        e.ProgressText = "150"; // rejected

        await vm.SaveNewAsync();

        var saved = (await _backlogs.SearchAsync(null)).Single(b => b.BacklogCode == "REQ-PROG");
        Assert.Null(saved.ProgressPercent);
    }
}
