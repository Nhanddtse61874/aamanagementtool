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
    {
        config = new Mock<IAppConfig>();
        config.Setup(c => c.DbPath).Returns(@"C:\old\timesheet.db");
        settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync(warningDays);
        templates = new Mock<ITaskTemplateRepository>();
        // Canonical template store = ITaskTemplateRepository (reconciliation 2026-06-21).
        templates.Setup(t => t.GetAllAsync())
             .ReturnsAsync(new[] { new TaskTemplate(1, "Std", "Implement", 0) });
        sync = new Mock<IDefaultTaskSyncService>();
        return new SettingsViewModel(config.Object, settings.Object, templates.Object, sync.Object);
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

    // ---------- SET-03: template CRUD (via ITaskTemplateRepository) ----------
    [Fact]
    public async Task LoadAsync_LoadsTemplates()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        Assert.Single(vm.Templates);
        Assert.Equal("Std", vm.Templates[0].TemplateName);
    }

    [Fact]
    public async Task AddTemplate_InsertsAndReloads()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        vm.NewTemplateName = "Bugfix";
        vm.NewTemplateTaskName = "Triage";
        await vm.AddTemplateCommand.ExecuteAsync(null);
        templates.Verify(t => t.InsertAsync(
            It.Is<TaskTemplate>(x => x.TemplateName == "Bugfix" && x.TaskName == "Triage")),
            Times.Once);
        templates.Verify(t => t.GetAllAsync(), Times.Exactly(2)); // initial load + reload
    }

    [Fact]
    public async Task DeleteTemplate_RemovesAndReloads()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        vm.SelectedTemplate = vm.Templates[0];
        await vm.DeleteTemplateCommand.ExecuteAsync(null);
        templates.Verify(t => t.DeleteAsync(1), Times.Once);
        templates.Verify(t => t.GetAllAsync(), Times.Exactly(2));
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
}
