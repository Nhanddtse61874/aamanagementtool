using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class SettingsViewModelTests
{
    private static SettingsViewModel Build(
        out Mock<IAppConfig> config,
        out Mock<ISettingsRepository> settings,
        out Mock<ITaskTemplateRepository> templates,
        out Mock<IDefaultTaskSyncService> sync,
        string? warningDays = "3")
        => Build(out config, out settings, out templates, out sync, out _, out _, out _, out _, out _, warningDays);

    private static SettingsViewModel Build(
        out Mock<IAppConfig> config,
        out Mock<ISettingsRepository> settings,
        out Mock<ITaskTemplateRepository> templates,
        out Mock<IDefaultTaskSyncService> sync,
        out Mock<ITagRepository> tags,
        out Mock<IPcaContactRepository> pca,
        out Mock<IHolidayRepository> holidays,
        string? warningDays = "3")
        => Build(out config, out settings, out templates, out sync, out tags, out pca, out holidays, out _, out _, warningDays);

    private static SettingsViewModel Build(
        out Mock<IAppConfig> config,
        out Mock<ISettingsRepository> settings,
        out Mock<ITaskTemplateRepository> templates,
        out Mock<IDefaultTaskSyncService> sync,
        out Mock<ITagRepository> tags,
        out Mock<IPcaContactRepository> pca,
        out Mock<IHolidayRepository> holidays,
        out Mock<ITeamRepository> teams,
        out Mock<IUserRepository> users,
        string? warningDays = "3")
    {
        config = new Mock<IAppConfig>();
        config.Setup(c => c.DbPath).Returns(@"C:\old\timesheet.db");
        settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync(warningDays);
        templates = new Mock<ITaskTemplateRepository>();
        // Canonical template store = ITaskTemplateRepository (reconciliation 2026-06-21).
        // "Std" has 2 task rows, "Bug" has 1 — exercises grouping by template name.
        templates.Setup(t => t.GetAllAsync())
             .ReturnsAsync(new[]
             {
                 new TaskTemplate(1, "Std", "Implement", 0),
                 new TaskTemplate(2, "Std", "Review", 1),
                 new TaskTemplate(3, "Bug", "Triage", 0),
             });
        sync = new Mock<IDefaultTaskSyncService>();

        tags = new Mock<ITagRepository>();
        tags.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Tag>());
        pca = new Mock<IPcaContactRepository>();
        pca.Setup(p => p.GetAllAsync()).ReturnsAsync(Array.Empty<PcaContact>());
        holidays = new Mock<IHolidayRepository>();
        holidays.Setup(h => h.GetForMonthAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(Array.Empty<Holiday>());

        teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Team>());
        users = new Mock<IUserRepository>();
        users.Setup(u => u.GetActiveAsync()).ReturnsAsync(Array.Empty<User>());

        return new SettingsViewModel(
            config.Object, settings.Object, templates.Object, sync.Object,
            tags.Object, pca.Object, holidays.Object, Mock.Of<IBackupService>(),
            teams.Object, users.Object);
    }

    // EX-06: build with an injected export hub so the "Export now" command can be exercised.
    private static SettingsViewModel BuildWithHub(IExportHubService exportHub)
    {
        var config = new Mock<IAppConfig>();
        config.Setup(c => c.DbPath).Returns(@"C:\old\timesheet.db");
        var settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync("3");
        var templates = new Mock<ITaskTemplateRepository>();
        templates.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<TaskTemplate>());
        var tags = new Mock<ITagRepository>();
        tags.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Tag>());
        var pca = new Mock<IPcaContactRepository>();
        pca.Setup(p => p.GetAllAsync()).ReturnsAsync(Array.Empty<PcaContact>());
        var holidays = new Mock<IHolidayRepository>();
        holidays.Setup(h => h.GetForMonthAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(Array.Empty<Holiday>());
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Team>());
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetActiveAsync()).ReturnsAsync(Array.Empty<User>());

        return new SettingsViewModel(
            config.Object, settings.Object, templates.Object, Mock.Of<IDefaultTaskSyncService>(),
            tags.Object, pca.Object, holidays.Object, Mock.Of<IBackupService>(),
            teams.Object, users.Object, messenger: null, exportHub: exportHub);
    }

    // ---------- SET-02: N-days default 3 + persist ----------
    [Fact]
    public async Task WarningDays_DefaultsTo3_WhenSettingMissing()
    {
        var vm = Build(out _, out _, out _, out _, warningDays: null);
        await vm.LoadAsync();
        Assert.Equal(3, vm.WarningDays);
    }

    [Fact]
    public async Task WarningDays_LoadsPersistedValue()
    {
        var vm = Build(out _, out _, out _, out _, warningDays: "7");
        await vm.LoadAsync();
        Assert.Equal(7, vm.WarningDays);
    }

    [Fact]
    public async Task SaveWarningDays_PersistsToSharedSettingsTable()
    {
        var vm = Build(out _, out var settings, out _, out _);
        await vm.LoadAsync();
        vm.WarningDays = 5;
        await vm.SaveWarningDaysCommand.ExecuteAsync(null);
        settings.Verify(s => s.SetAsync(ReportsViewModel.NDaysKey, "5"), Times.Once);
    }

    // ---------- SET-01: DB path -> app-local config, NOT shared DB ----------
    [Fact]
    public async Task ApplyDbPath_WritesToAppConfig_NotSettingsRepo()
    {
        var vm = Build(out var config, out var settings, out _, out _);
        await vm.LoadAsync();
        vm.DbPath = @"D:\new\timesheet.db";
        await vm.ApplyDbPathCommand.ExecuteAsync(null);
        config.Verify(c => c.SetDbPath(@"D:\new\timesheet.db"), Times.Once);
        settings.Verify(s => s.SetAsync(It.Is<string>(k => k.Contains("path")), It.IsAny<string>()), Times.Never);
    }

    // ---------- SET-03: template CRUD (editor-based, via ITaskTemplateRepository) ----------
    [Fact]
    public async Task LoadAsync_GroupsTemplatesByName()
    {
        var vm = Build(out _, out _, out _, out _);
        await vm.LoadAsync();
        // "Bug" (1 task) + "Std" (2 tasks), ordered by name.
        Assert.Equal(2, vm.TemplateGroups.Count);
        Assert.Equal("Bug", vm.TemplateGroups[0].Name);
        Assert.Equal(1, vm.TemplateGroups[0].TaskCount);
        Assert.Equal("Std", vm.TemplateGroups[1].Name);
        Assert.Equal(2, vm.TemplateGroups[1].TaskCount);
    }

    [Fact]
    public async Task BeginCreateTemplate_OpensEmptyEditor()
    {
        var vm = Build(out _, out _, out _, out _);
        await vm.LoadAsync();
        vm.BeginCreateTemplateCommand.Execute(null);
        Assert.NotNull(vm.TemplateEditor);
        Assert.False(vm.TemplateEditor!.IsEditMode);
        Assert.Empty(vm.TemplateEditor.Tasks);
    }

    [Fact]
    public async Task BeginEditTemplate_LoadsExistingRowsIntoEditor()
    {
        var vm = Build(out _, out _, out _, out _);
        await vm.LoadAsync();
        vm.BeginEditTemplateCommand.Execute("Std");
        Assert.NotNull(vm.TemplateEditor);
        Assert.True(vm.TemplateEditor!.IsEditMode);
        Assert.Equal("Std", vm.TemplateEditor.TemplateName);
        Assert.Equal(2, vm.TemplateEditor.Tasks.Count); // Implement, Review
    }

    [Fact]
    public async Task SaveTemplate_Create_InsertsOneRowPerTask()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        vm.BeginCreateTemplateCommand.Execute(null);
        vm.TemplateEditor!.TemplateName = "Bugfix";
        vm.TemplateEditor.AddTask("Triage");
        vm.TemplateEditor.AddTask("Fix");
        vm.TemplateEditor.AddTask("Verify");

        await vm.SaveTemplateCommand.ExecuteAsync(null);

        templates.Verify(t => t.InsertAsync(
            It.Is<TaskTemplate>(x => x.TemplateName == "Bugfix")), Times.Exactly(3));
        templates.Verify(t => t.DeleteByTemplateNameAsync(It.IsAny<string>()), Times.Never);
        Assert.Null(vm.TemplateEditor); // editor closes on save
    }

    [Fact]
    public async Task SaveTemplate_Edit_DeletesOriginalThenReinserts()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        vm.BeginEditTemplateCommand.Execute("Std");
        vm.TemplateEditor!.AddTask("Deploy");

        await vm.SaveTemplateCommand.ExecuteAsync(null);

        templates.Verify(t => t.DeleteByTemplateNameAsync("Std"), Times.Once);
        templates.Verify(t => t.InsertAsync(
            It.Is<TaskTemplate>(x => x.TemplateName == "Std")), Times.Exactly(3)); // Implement, Review, Deploy
    }

    [Fact]
    public async Task SaveTemplate_DoesNothing_WhenNameOrTasksBlank()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        vm.BeginCreateTemplateCommand.Execute(null);
        vm.TemplateEditor!.TemplateName = "  "; // blank name, no tasks

        await vm.SaveTemplateCommand.ExecuteAsync(null);

        templates.Verify(t => t.InsertAsync(It.IsAny<TaskTemplate>()), Times.Never);
        Assert.NotNull(vm.TemplateEditor); // stays open
    }

    [Fact]
    public async Task CancelTemplate_ClosesEditorWithoutSaving()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        vm.BeginCreateTemplateCommand.Execute(null);
        vm.CancelTemplateCommand.Execute(null);
        Assert.Null(vm.TemplateEditor);
        templates.Verify(t => t.InsertAsync(It.IsAny<TaskTemplate>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTemplate_DeletesAllRowsByNameAndReloads()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        await vm.DeleteTemplateCommand.ExecuteAsync("Std");
        templates.Verify(t => t.DeleteByTemplateNameAsync("Std"), Times.Once);
        templates.Verify(t => t.GetAllAsync(), Times.Exactly(2)); // initial load + reload
    }

    // ---------- SET-04: DefaultTask edit triggers sync ----------
    [Fact]
    public async Task SaveDefaultTasks_CallsSync()
    {
        var vm = Build(out _, out _, out _, out var sync);
        await vm.LoadAsync();
        await vm.SaveDefaultTasksCommand.ExecuteAsync(null);
        sync.Verify(s => s.SyncAsync(), Times.Once);
    }

    // ---------- TAG-01: custom tag CRUD persists via ITagRepository ----------
    [Fact]
    public async Task CreateTag_InsertsTagAndClosesEditor()
    {
        var vm = Build(out _, out _, out _, out _, out var tags, out _, out _);
        await vm.LoadAsync();
        vm.BeginCreateTagCommand.Execute(null);
        vm.TagEditor!.Text = "  Bug  ";
        vm.TagEditor.Icon = "\U0001F41B";
        vm.TagEditor.Color = "#DC2626";

        await vm.SaveTagCommand.ExecuteAsync(null);

        tags.Verify(t => t.InsertAsync(
            It.Is<Tag>(x => x.Text == "Bug" && x.Icon == "\U0001F41B" && x.Color == "#DC2626")), Times.Once);
        Assert.Null(vm.TagEditor);
    }

    [Fact]
    public async Task EditTag_UpdatesExistingTag()
    {
        var vm = Build(out _, out _, out _, out _, out var tags, out _, out _);
        await vm.LoadAsync();
        vm.BeginEditTagCommand.Execute(new Tag(5, "Old", "x", "#000000", DateTimeOffset.UtcNow));
        vm.TagEditor!.Text = "New";

        await vm.SaveTagCommand.ExecuteAsync(null);

        tags.Verify(t => t.UpdateAsync(It.Is<Tag>(x => x.Id == 5 && x.Text == "New")), Times.Once);
        tags.Verify(t => t.InsertAsync(It.IsAny<Tag>()), Times.Never);
    }

    [Fact]
    public async Task SaveTag_DoesNothing_WhenLabelBlank()
    {
        var vm = Build(out _, out _, out _, out _, out var tags, out _, out _);
        await vm.LoadAsync();
        vm.BeginCreateTagCommand.Execute(null);
        vm.TagEditor!.Text = "   ";

        await vm.SaveTagCommand.ExecuteAsync(null);

        tags.Verify(t => t.InsertAsync(It.IsAny<Tag>()), Times.Never);
        Assert.NotNull(vm.TagEditor); // stays open
    }

    [Fact]
    public async Task DeleteTag_CallsRepoAndReloads()
    {
        var vm = Build(out _, out _, out _, out _, out var tags, out _, out _);
        await vm.LoadAsync();
        await vm.DeleteTagCommand.ExecuteAsync(9);
        tags.Verify(t => t.DeleteAsync(9), Times.Once);
        tags.Verify(t => t.GetAllAsync(), Times.Exactly(2)); // load + reload
    }

    // ---------- TL-11: PCA contacts (soft-delete, mirrors Users) ----------
    [Fact]
    public async Task AddPcaContact_InsertsActiveAndClearsInput()
    {
        var vm = Build(out _, out _, out _, out _, out _, out var pca, out _);
        await vm.LoadAsync();
        vm.NewPcaName = "  Acme  ";

        await vm.AddPcaContactCommand.ExecuteAsync(null);

        pca.Verify(p => p.InsertAsync(It.Is<PcaContact>(x => x.Name == "Acme" && x.IsActive)), Times.Once);
        Assert.Equal(string.Empty, vm.NewPcaName);
    }

    [Fact]
    public async Task RenamePcaContact_UpdatesNameFromEditedRow()
    {
        var pca = new Mock<IPcaContactRepository>();
        pca.SetupSequence(p => p.GetAllAsync())
           .ReturnsAsync(new[] { new PcaContact(3, "Old", true) })
           .ReturnsAsync(new[] { new PcaContact(3, "Renamed", true) });
        var holidays = new Mock<IHolidayRepository>();
        holidays.Setup(h => h.GetForMonthAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(Array.Empty<Holiday>());
        var tags = new Mock<ITagRepository>();
        tags.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Tag>());
        var config = new Mock<IAppConfig>();
        var settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync("3");
        var templates = new Mock<ITaskTemplateRepository>();
        templates.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<TaskTemplate>());
        var sync = new Mock<IDefaultTaskSyncService>();
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Team>());
        var usersRepo = new Mock<IUserRepository>();
        usersRepo.Setup(u => u.GetActiveAsync()).ReturnsAsync(Array.Empty<User>());

        var vm = new SettingsViewModel(config.Object, settings.Object, templates.Object, sync.Object,
            tags.Object, pca.Object, holidays.Object, Mock.Of<IBackupService>(),
            teams.Object, usersRepo.Object);
        await vm.LoadAsync();
        vm.PcaContacts[0].Name = "Renamed";

        await vm.RenamePcaContactCommand.ExecuteAsync(3);

        pca.Verify(p => p.UpdateNameAsync(3, "Renamed"), Times.Once);
    }

    [Fact]
    public async Task DeactivatePcaContact_SoftDeletes()
    {
        var vm = Build(out _, out _, out _, out _, out _, out var pca, out _);
        await vm.LoadAsync();
        await vm.DeactivatePcaContactCommand.ExecuteAsync(4);
        pca.Verify(p => p.SetActiveAsync(4, false), Times.Once);
    }

    // ---------- HOL-01: holiday toggle upserts then deletes ----------
    [Fact]
    public async Task ToggleHoliday_UpsertsWhenNotMarked_ThenDeletesWhenMarked()
    {
        var holidays = new Mock<IHolidayRepository>();
        var date = new DateOnly(2026, 6, 15);
        // First toggle: month has no holidays -> upsert. Second toggle: that date is now present -> delete.
        holidays.SetupSequence(h => h.GetForMonthAsync(2026, 6))
                .ReturnsAsync(Array.Empty<Holiday>())   // SettingsViewModel.LoadAsync initial grid load
                .ReturnsAsync(Array.Empty<Holiday>())   // ToggleHoliday existence check #1 (not marked)
                .ReturnsAsync(new[] { new Holiday(date, null) })   // refresh after upsert
                .ReturnsAsync(new[] { new Holiday(date, null) })   // ToggleHoliday existence check #2 (marked)
                .ReturnsAsync(Array.Empty<Holiday>());  // refresh after delete

        var cal = new HolidayCalendarViewModel(holidays.Object,
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default)
        { Year = 2026, Month = 6 };
        await cal.LoadAsync();

        await cal.ToggleHolidayCommand.ExecuteAsync(date);
        holidays.Verify(h => h.UpsertAsync(date, null), Times.Once);

        await cal.ToggleHolidayCommand.ExecuteAsync(date);
        holidays.Verify(h => h.DeleteAsync(date), Times.Once);
    }

    // ---------- TM-03/TM-04: teams (soft-delete CRUD) + membership editor ----------
    [Fact]
    public async Task AddTeam_InsertsTeam_SeedsItsDefault_AndBroadcasts()
    {
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Team>());
        teams.Setup(t => t.InsertAsync(It.IsAny<Team>())).ReturnsAsync(42);
        var sync = new Mock<IDefaultTaskSyncService>();
        var bus = new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger();
        var vm = BuildWith(teams, new Mock<IUserRepository>(), sync, bus);
        await vm.LoadAsync();
        vm.NewTeamName = "  Team A  ";

        var seen = false;
        bus.Register<DataChangedMessage>(this, (_, m) => { if (m.Kind == DataKind.Teams) seen = true; });

        await vm.AddTeamCommand.ExecuteAsync(null);

        teams.Verify(t => t.InsertAsync(It.Is<Team>(x => x.Name == "Team A" && x.IsActive)), Times.Once);
        // TM-04: the new team gets its DEFAULT backlog + a sync pass materializing default tasks.
        sync.Verify(s => s.EnsureDefaultBacklogIdAsync(42), Times.Once);
        sync.Verify(s => s.SyncAsync(), Times.Once);
        Assert.Equal(string.Empty, vm.NewTeamName);
        Assert.True(seen);
    }

    [Fact]
    public async Task AddTeam_DoesNothing_WhenNameBlank()
    {
        var vm = Build(out _, out _, out _, out var sync, out _, out _, out _, out var teams, out _);
        await vm.LoadAsync();
        vm.NewTeamName = "   ";

        await vm.AddTeamCommand.ExecuteAsync(null);

        teams.Verify(t => t.InsertAsync(It.IsAny<Team>()), Times.Never);
        sync.Verify(s => s.EnsureDefaultBacklogIdAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task RenameTeam_UpdatesNameFromEditedRow()
    {
        var teams = new Mock<ITeamRepository>();
        teams.SetupSequence(t => t.GetAllAsync())
             .ReturnsAsync(new[] { new Team(3, "Old", true, DateTimeOffset.UtcNow) })
             .ReturnsAsync(new[] { new Team(3, "Renamed", true, DateTimeOffset.UtcNow) });
        var vm = BuildWith(teams, new Mock<IUserRepository>());
        await vm.LoadAsync();
        vm.Teams[0].Name = "Renamed";

        await vm.RenameTeamCommand.ExecuteAsync(3);

        teams.Verify(t => t.UpdateNameAsync(3, "Renamed"), Times.Once);
    }

    [Fact]
    public async Task DeactivateTeam_SoftDeletes()
    {
        var vm = Build(out _, out _, out _, out _, out _, out _, out _, out var teams, out _);
        await vm.LoadAsync();
        await vm.DeactivateTeamCommand.ExecuteAsync(4);
        teams.Verify(t => t.SetActiveAsync(4, false), Times.Once);
    }

    [Fact]
    public async Task BeginEditMembers_OpensEditor_PreChecksCurrentMembers()
    {
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetAllAsync()).ReturnsAsync(new[] { new Team(7, "T", true, DateTimeOffset.UtcNow) });
        teams.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(new Team(7, "T", true, DateTimeOffset.UtcNow));
        teams.Setup(t => t.GetUserIdsForTeamAsync(7)).ReturnsAsync(new[] { 2 });
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetActiveAsync()).ReturnsAsync(new[]
        {
            new User(1, "Alice", null, true),
            new User(2, "Bob", null, true),
        });

        var vm = BuildWith(teams, users);
        await vm.LoadAsync();
        await vm.BeginEditMembersCommand.ExecuteAsync(7);

        Assert.NotNull(vm.MembershipEditor);
        Assert.Equal(2, vm.MembershipEditor!.Users.Count);
        Assert.False(vm.MembershipEditor.Users[0].IsChecked); // Alice (id 1) not a member
        Assert.True(vm.MembershipEditor.Users[1].IsChecked);  // Bob (id 2) is a member
    }

    [Fact]
    public async Task SaveMembers_ReplaceAll_ThenReopenReflectsNewSet()
    {
        var stored = new List<int> { 2 };
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetAllAsync()).ReturnsAsync(new[] { new Team(7, "T", true, DateTimeOffset.UtcNow) });
        teams.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(new Team(7, "T", true, DateTimeOffset.UtcNow));
        teams.Setup(t => t.GetUserIdsForTeamAsync(7)).ReturnsAsync(() => stored.ToArray());
        teams.Setup(t => t.SetMembersAsync(7, It.IsAny<IReadOnlyList<int>>()))
             .Callback<int, IReadOnlyList<int>>((_, ids) => stored = ids.ToList())
             .Returns(Task.CompletedTask);
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetActiveAsync()).ReturnsAsync(new[]
        {
            new User(1, "Alice", null, true),
            new User(2, "Bob", null, true),
        });

        var vm = BuildWith(teams, users);
        await vm.LoadAsync();

        // Open, check Alice (was unchecked) + uncheck Bob, then save (replace-all).
        await vm.BeginEditMembersCommand.ExecuteAsync(7);
        vm.MembershipEditor!.Users[0].IsChecked = true;   // Alice in
        vm.MembershipEditor.Users[1].IsChecked = false;   // Bob out
        await vm.SaveMembersCommand.ExecuteAsync(null);

        teams.Verify(t => t.SetMembersAsync(7, It.Is<IReadOnlyList<int>>(ids =>
            ids.Count == 1 && ids[0] == 1)), Times.Once);
        Assert.Null(vm.MembershipEditor); // editor closes on save

        // Reopen → reflects the replaced set ({1}).
        await vm.BeginEditMembersCommand.ExecuteAsync(7);
        Assert.True(vm.MembershipEditor!.Users[0].IsChecked);  // Alice now a member
        Assert.False(vm.MembershipEditor.Users[1].IsChecked);  // Bob removed
    }

    [Fact]
    public async Task CancelMembers_ClosesEditorWithoutSaving()
    {
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetAllAsync()).ReturnsAsync(new[] { new Team(7, "T", true, DateTimeOffset.UtcNow) });
        teams.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(new Team(7, "T", true, DateTimeOffset.UtcNow));
        teams.Setup(t => t.GetUserIdsForTeamAsync(7)).ReturnsAsync(Array.Empty<int>());
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetActiveAsync()).ReturnsAsync(Array.Empty<User>());
        var vm = BuildWith(teams, users);
        await vm.LoadAsync();
        await vm.BeginEditMembersCommand.ExecuteAsync(7);

        vm.CancelMembersCommand.Execute(null);

        Assert.Null(vm.MembershipEditor);
        teams.Verify(t => t.SetMembersAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<int>>()), Times.Never);
    }

    // Helper for team-focused tests that need custom team/user mocks (other deps stubbed minimally).
    private static SettingsViewModel BuildWith(
        Mock<ITeamRepository> teams,
        Mock<IUserRepository> users,
        Mock<IDefaultTaskSyncService>? sync = null,
        CommunityToolkit.Mvvm.Messaging.IMessenger? messenger = null)
    {
        var config = new Mock<IAppConfig>();
        var settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync("3");
        var templates = new Mock<ITaskTemplateRepository>();
        templates.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<TaskTemplate>());
        sync ??= new Mock<IDefaultTaskSyncService>();
        var tags = new Mock<ITagRepository>();
        tags.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Tag>());
        var pca = new Mock<IPcaContactRepository>();
        pca.Setup(p => p.GetAllAsync()).ReturnsAsync(Array.Empty<PcaContact>());
        var holidays = new Mock<IHolidayRepository>();
        holidays.Setup(h => h.GetForMonthAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(Array.Empty<Holiday>());
        return new SettingsViewModel(config.Object, settings.Object, templates.Object, sync.Object,
            tags.Object, pca.Object, holidays.Object, Mock.Of<IBackupService>(),
            teams.Object, users.Object, messenger);
    }

    [Fact]
    public async Task HolidayCalendar_BuildsSixWeekGridWithWeekendFlags()
    {
        var holidays = new Mock<IHolidayRepository>();
        holidays.Setup(h => h.GetForMonthAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(Array.Empty<Holiday>());
        var cal = new HolidayCalendarViewModel(holidays.Object,
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default)
        { Year = 2026, Month = 6 };

        await cal.LoadAsync();

        Assert.Equal(42, cal.Days.Count);
        Assert.All(cal.Days.Where(d => d.IsWeekend),
            d => Assert.True(d.Date.DayOfWeek is System.DayOfWeek.Saturday or System.DayOfWeek.Sunday));
        Assert.Contains(cal.Days, d => d.IsInMonth && d.DayNumber == 1);
    }

    // ---------- EX-06: manual "Export now" ----------

    [Fact]
    public async Task ExportNow_CallsHub_AndSurfacesItsStatus()
    {
        var hub = new Mock<IExportHubService>();
        hub.Setup(h => h.ExportNowAsync()).ReturnsAsync("ok: C:\\root1\nok: C:\\root2");
        var vm = BuildWithHub(hub.Object);

        await vm.ExportNowCommand.ExecuteAsync(null);

        hub.Verify(h => h.ExportNowAsync(), Times.Once);
        Assert.Equal("ok: C:\\root1\nok: C:\\root2", vm.ExportStatus);
    }

    [Fact]
    public async Task ExportNow_WhenHubFails_SurfacesFailureStatus()
    {
        var hub = new Mock<IExportHubService>();
        hub.Setup(h => h.ExportNowAsync()).ThrowsAsync(new System.IO.IOException("disk full"));
        var vm = BuildWithHub(hub.Object);

        await vm.ExportNowCommand.ExecuteAsync(null);

        Assert.Contains("disk full", vm.ExportStatus);
        Assert.StartsWith("Export failed", vm.ExportStatus);
    }

    [Fact]
    public async Task ExportNow_WithoutHub_ReportsUnavailable()
    {
        var vm = Build(out _, out _, out _, out _);   // no hub injected (legacy ctor path)

        await vm.ExportNowCommand.ExecuteAsync(null);

        Assert.Equal("Export now is not available.", vm.ExportStatus);
    }
}
