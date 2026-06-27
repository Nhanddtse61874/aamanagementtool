using System.Collections.ObjectModel;
using TimesheetApp.Models;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public sealed class BacklogEditorViewModelTests
{
    private static IReadOnlyList<TaskTemplate> WebTemplate() => new[]
    {
        new TaskTemplate(1, "Web", "Setup", 0),
        new TaskTemplate(2, "Web", "Build", 1),
        new TaskTemplate(3, "API", "Design", 0),
    };

    [Fact] // REQ-02: create mode starts empty
    public void Create_mode_starts_empty()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());

        Assert.False(vm.IsEditMode);
        Assert.Equal(0, vm.EditingBacklogId);
        Assert.Empty(vm.Tasks);
        Assert.Equal(new[] { "API", "Web" }, vm.TemplateNames); // distinct, ordered
    }

    [Fact] // v4: create mode defaults to Unassigned (null id); the pick list is "(unassigned)" + users.
    public void Create_mode_defaults_to_unassigned()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate(), new[] { new User(5, "Cara", null, true) });

        Assert.Same(BacklogEditorViewModel.Unassigned, vm.SelectedAssignee);
        Assert.Null(vm.AssigneeUserId);
        Assert.Equal(2, vm.Users.Count); // Unassigned + Cara
    }

    [Fact] // v4: edit mode preselects the saved assignee; reselecting Unassigned maps back to null.
    public void Edit_mode_preselects_assignee_and_unassigned_maps_to_null()
    {
        var users = new[] { new User(5, "Cara", null, true) };
        var req = new Backlog(3, "REQ-X", "ARCS", DateTimeOffset.UtcNow, AssigneeUserId: 5);

        var vm = BacklogEditorViewModel.ForEdit(req, System.Array.Empty<TaskItem>(), WebTemplate(), null, users);
        Assert.Equal(5, vm.AssigneeUserId);
        Assert.Equal("Cara", vm.SelectedAssignee.Name);

        vm.SelectedAssignee = BacklogEditorViewModel.Unassigned;
        Assert.Null(vm.AssigneeUserId);
    }

    [Fact] // UX fix: selecting a template auto-applies its tasks (no separate Apply click needed)
    public void SelectingTemplate_autoApplies_withoutExplicitApplyCall()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());

        vm.SelectedTemplateName = "Web"; // auto-apply on selection

        Assert.Equal(2, vm.Tasks.Count); // Setup + Build appended without calling ApplyTemplate()
    }

    [Fact] // Applying the same template again must NOT duplicate its tasks
    public void SameTemplate_appliedAgain_doesNotDuplicate()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());

        vm.SelectedTemplateName = "Web"; // auto-applies (2)
        vm.ApplyTemplate();               // explicit re-apply, same template

        Assert.Equal(2, vm.Tasks.Count);
    }

    [Fact] // REQ-02: applying a template appends only that template's tasks, ordered
    public void ApplyTemplate_appends_selected_template_tasks_in_order()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());
        vm.SelectedTemplateName = "Web";

        vm.ApplyTemplate();

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("Setup", vm.Tasks[0].TaskName);
        Assert.Equal("Build", vm.Tasks[1].TaskName);
        Assert.Equal(0, vm.Tasks[0].OrderIndex);
        Assert.Equal(1, vm.Tasks[1].OrderIndex);
        Assert.All(vm.Tasks, t => Assert.Equal(0, t.ExistingTaskId)); // template tasks are new
    }

    [Fact] // REQ-02: custom add appends after template tasks with next order_index
    public void AddTask_appends_with_next_order_index()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());
        vm.SelectedTemplateName = "Web";
        vm.ApplyTemplate();

        vm.AddTask("Custom");

        Assert.Equal(3, vm.Tasks.Count);
        Assert.Equal("Custom", vm.Tasks[2].TaskName);
        Assert.Equal(2, vm.Tasks[2].OrderIndex);
    }

    [Fact] // REQ-02: removing a NEW task drops it and reindexes
    public void RemoveTask_new_drops_and_reindexes()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());
        vm.AddTask("A");
        vm.AddTask("B");
        vm.AddTask("C");

        vm.RemoveTask(vm.Tasks[1]); // remove B

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("A", vm.Tasks[0].TaskName);
        Assert.Equal("C", vm.Tasks[1].TaskName);
        Assert.Equal(0, vm.Tasks[0].OrderIndex);
        Assert.Equal(1, vm.Tasks[1].OrderIndex);
    }

    [Fact] // REQ-02: reorder via MoveUp swaps and reindexes order_index
    public void MoveUp_swaps_and_reindexes()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());
        vm.AddTask("A");
        vm.AddTask("B");

        vm.MoveUp(vm.Tasks[1]); // B moves above A

        Assert.Equal("B", vm.Tasks[0].TaskName);
        Assert.Equal("A", vm.Tasks[1].TaskName);
        Assert.Equal(0, vm.Tasks[0].OrderIndex);
        Assert.Equal(1, vm.Tasks[1].OrderIndex);
    }

    [Fact] // REQ-03: edit mode preloads request + existing tasks
    public void Edit_mode_preloads_request_and_existing_tasks()
    {
        var req = new Backlog(7, "RQ-7", "Billing", DateTimeOffset.UtcNow);
        var existing = new[]
        {
            new TaskItem(11, 7, "Analyse", 0, true),
            new TaskItem(12, 7, "Implement", 1, true),
        };

        var vm = BacklogEditorViewModel.ForEdit(req, existing, WebTemplate());

        Assert.True(vm.IsEditMode);
        Assert.Equal(7, vm.EditingBacklogId);
        Assert.Equal("RQ-7", vm.BacklogCode);
        Assert.Equal("Billing", vm.Project);
        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal(11, vm.Tasks[0].ExistingTaskId);
        Assert.Equal(12, vm.Tasks[1].ExistingTaskId);
    }

    [Fact] // REQ-04: removing an EXISTING task flags IsRemoved (soft-delete), keeps it in Tasks
    public void RemoveTask_existing_flags_removed_not_dropped()
    {
        var req = new Backlog(7, "RQ-7", "Billing", DateTimeOffset.UtcNow);
        var existing = new[] { new TaskItem(11, 7, "Analyse", 0, true) };
        var vm = BacklogEditorViewModel.ForEdit(req, existing, WebTemplate());

        vm.RemoveTask(vm.Tasks[0]);

        Assert.Single(vm.Tasks);              // still present (so Save can SetActiveAsync(false))
        Assert.True(vm.Tasks[0].IsRemoved);
        Assert.Empty(vm.ActiveTasks);         // excluded from the active set
    }

    [Fact] // ActiveTasks excludes removed + reindexes 0..n
    public void ActiveTasks_excludes_removed_and_reindexes()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());
        vm.AddTask("A");
        vm.AddTask("B");
        vm.AddTask("C");
        vm.RemoveTask(vm.Tasks[0]); // A is new -> dropped

        var active = vm.ActiveTasks;

        Assert.Equal(2, active.Count);
        Assert.Equal("B", active[0].TaskName);
        Assert.Equal(0, active[0].OrderIndex);
        Assert.Equal("C", active[1].TaskName);
        Assert.Equal(1, active[1].OrderIndex);
    }

    // ---- v7 tracking fields (P8 / TL-03) ------------------------------------------------

    private static IReadOnlyList<PcaContact> TwoContacts() => new[]
    {
        new PcaContact(8, "Acme", true),
        new PcaContact(9, "Globex", true),
    };

    private static IReadOnlyList<Tag> ThreeTags() => new[]
    {
        new Tag(1, "Urgent", "⚡", "#FF0000", DateTimeOffset.UtcNow),
        new Tag(2, "Blocked", "🚧", "#0000FF", DateTimeOffset.UtcNow),
        new Tag(3, "Review", "👀", "#00FF00", DateTimeOffset.UtcNow),
    };

    [Fact] // v7: create mode defaults — no PCA, all tags listed unchecked, nulls everywhere.
    public void Create_mode_v7_defaults()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate(), null, TwoContacts(), ThreeTags());

        Assert.Same(BacklogEditorViewModel.NoPcaContact, vm.SelectedPcaContact);
        Assert.Null(vm.PcaContactId);
        Assert.Equal(3, vm.PcaContacts.Count); // NoPcaContact + Acme + Globex
        Assert.Equal(3, vm.TagPicks.Count);
        Assert.All(vm.TagPicks, p => Assert.False(p.IsChecked));
        Assert.Empty(vm.CheckedTagIds);
        Assert.Null(vm.DeadlineInternal);
        Assert.Null(vm.RoughEstimateHours);
        Assert.Null(vm.OfficialEstimateHours);
        Assert.Null(vm.ProgressPercent);
        Assert.Null(vm.Note);
    }

    [Fact] // v7: edit mode preloads all tracking fields, the PCA contact, and the checked tags.
    public void Edit_mode_preloads_v7_fields_pca_and_checked_tags()
    {
        var backlog = new Backlog(
            7, "RQ-7", "ARCS", DateTimeOffset.UtcNow,
            DeadlineInternal: new DateOnly(2026, 7, 10),
            DeadlineExternal: new DateOnly(2026, 7, 20),
            RoughEstimateHours: 12.5m, OfficialEstimateHours: 16m,
            ProgressPercent: 40, Note: "first cut", PcaContactId: 9);

        var vm = BacklogEditorViewModel.ForEdit(
            backlog, System.Array.Empty<TaskItem>(), WebTemplate(), null, null,
            TwoContacts(), ThreeTags(), new[] { 1, 3 });

        Assert.Equal(new DateOnly(2026, 7, 10), vm.DeadlineInternal);
        Assert.Equal(new DateOnly(2026, 7, 20), vm.DeadlineExternal);
        Assert.Equal(12.5m, vm.RoughEstimateHours);
        Assert.Equal(16m, vm.OfficialEstimateHours);
        Assert.Equal("12.5", vm.RoughEstimateText);
        Assert.Equal("16", vm.OfficialEstimateText);
        Assert.Equal(40, vm.ProgressPercent);
        Assert.Equal("40", vm.ProgressText);
        Assert.Equal("first cut", vm.Note);
        Assert.Equal(9, vm.PcaContactId);
        Assert.Equal("Globex", vm.SelectedPcaContact.Name);
        Assert.Equal(new[] { 1, 3 }, vm.CheckedTagIds.OrderBy(x => x).ToArray());
    }

    [Fact] // v7: NoPcaContact sentinel maps back to a null id.
    public void Selecting_no_pca_contact_maps_to_null()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate(), null, TwoContacts(), ThreeTags());
        vm.SelectedPcaContact = vm.PcaContacts.First(p => p.Id == 8);
        Assert.Equal(8, vm.PcaContactId);

        vm.SelectedPcaContact = BacklogEditorViewModel.NoPcaContact;
        Assert.Null(vm.PcaContactId);
    }

    [Fact] // v7: estimate text parses decimals; blank clears to null.
    public void Estimate_text_parses_decimal_and_clears_on_blank()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());

        vm.RoughEstimateText = "12.5";
        Assert.Equal(12.5m, vm.RoughEstimateHours);

        vm.RoughEstimateText = "";
        Assert.Null(vm.RoughEstimateHours);
    }

    [Fact] // v7: garbage / negative estimate is rejected (null) with an error surfaced.
    public void Estimate_text_rejects_garbage_and_negative()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());

        vm.OfficialEstimateText = "abc";
        Assert.Null(vm.OfficialEstimateHours);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));

        vm.ErrorMessage = null;
        vm.OfficialEstimateText = "-5";
        Assert.Null(vm.OfficialEstimateHours);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
    }

    [Fact] // v7: progress accepts 0-100, rejects out-of-range and non-integers.
    public void Progress_validates_zero_to_hundred()
    {
        var vm = BacklogEditorViewModel.ForCreate(WebTemplate());

        vm.ProgressText = "0";
        Assert.Equal(0, vm.ProgressPercent);
        vm.ProgressText = "100";
        Assert.Equal(100, vm.ProgressPercent);

        vm.ProgressText = "101";
        Assert.Null(vm.ProgressPercent);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));

        vm.ErrorMessage = null;
        vm.ProgressText = "x";
        Assert.Null(vm.ProgressPercent);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
    }
}
