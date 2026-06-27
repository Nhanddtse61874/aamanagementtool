using System.Threading.Tasks;
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
        => Build(out config, out settings, out templates, out sync, out _, out _, out _, warningDays);

    private static SettingsViewModel Build(
        out Mock<IAppConfig> config,
        out Mock<ISettingsRepository> settings,
        out Mock<ITaskTemplateRepository> templates,
        out Mock<IDefaultTaskSyncService> sync,
        out Mock<ITagRepository> tags,
        out Mock<IPcaContactRepository> pca,
        out Mock<IHolidayRepository> holidays,
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

        return new SettingsViewModel(
            config.Object, settings.Object, templates.Object, sync.Object,
            tags.Object, pca.Object, holidays.Object);
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

        var vm = new SettingsViewModel(config.Object, settings.Object, templates.Object, sync.Object,
            tags.Object, pca.Object, holidays.Object);
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
}
