namespace TimesheetApp.Models;

// --- Entities (1:1 with tables). Names are VERBATIM from architecture spec §2. ---
// 'Task' collides with System.Threading.Tasks.Task -> the entity is named TaskItem.

public sealed record User(int Id, string Name, string? WindowsUsername, bool IsActive);

public sealed record Request(int Id, string RequestCode, string Project, DateTimeOffset CreatedAt);

public sealed record TaskItem(int Id, int RequestId, string TaskName, int OrderIndex, bool IsActive);

public sealed record TimeLog(
    int Id, int UserId, int TaskId, DateOnly WorkDate, decimal Hours, DateTimeOffset CreatedAt);

public sealed record TaskTemplate(int Id, string TemplateName, string TaskName, int OrderIndex);

public sealed record DefaultTask(int Id, string TaskName, int OrderIndex, bool IsActive);
