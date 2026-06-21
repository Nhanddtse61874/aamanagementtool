using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Maintains the hidden DEFAULT request and reconciles DefaultTasks into Tasks under it.
/// Rename = soft-delete old + insert new (TimeLogs preserved, decision 7). (DATA-03, SET-04, XC-10)</summary>
public sealed class DefaultTaskSyncService : IDefaultTaskSyncService
{
    private const string DefaultCode = "DEFAULT";

    private readonly IRequestRepository _requests;
    private readonly ITaskRepository _tasks;
    private readonly IDefaultTaskRepository _defaults;
    private readonly IDbBackupHelper _backup;

    public DefaultTaskSyncService(
        IRequestRepository requests, ITaskRepository tasks,
        IDefaultTaskRepository defaults, IDbBackupHelper backup)
    {
        _requests = requests;
        _tasks = tasks;
        _defaults = defaults;
        _backup = backup;
    }

    public async Task<int> EnsureDefaultRequestIdAsync()
    {
        var existing = await _requests.GetByCodeAsync(DefaultCode);
        if (existing is not null) return existing.Id;
        return await _requests.InsertAsync(new Request(0, DefaultCode, DefaultCode, DateTimeOffset.UtcNow));
    }

    public async Task SyncAsync()
    {
        await _backup.BackupAsync(); // XC-10: sync is a bulk write -> backup first (no-op when DB absent).

        var defReqId = await EnsureDefaultRequestIdAsync();

        var activeDefaults = (await _defaults.GetActiveAsync())
            .Select(d => d.TaskName)
            .ToHashSet(StringComparer.Ordinal);
        var tasksUnderDefault = await _tasks.GetActiveByRequestAsync(defReqId);
        var existingNames = tasksUnderDefault
            .Select(t => t.TaskName)
            .ToHashSet(StringComparer.Ordinal);

        // (a) active DefaultTask with no active Task under DEFAULT -> insert (covers new + rename-new).
        var order = 0;
        foreach (var name in activeDefaults)
        {
            if (!existingNames.Contains(name))
                await _tasks.InsertAsync(new TaskItem(0, defReqId, name, order, true));
            order++;
        }

        // (b) active Task under DEFAULT with no active DefaultTask -> soft-delete
        //     (covers hide + rename-old; preserves TimeLogs, XC-06).
        foreach (var task in tasksUnderDefault)
            if (!activeDefaults.Contains(task.TaskName))
                await _tasks.SetActiveAsync(task.Id, false);
    }
}
