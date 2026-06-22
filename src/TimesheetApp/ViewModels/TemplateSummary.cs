namespace TimesheetApp.ViewModels;

// One row in the Settings template list: a template name and how many task rows it groups.
// Built from ITaskTemplateRepository.GetAllAsync() grouped by TemplateName (SET-03).
public sealed record TemplateSummary(string Name, int TaskCount);
