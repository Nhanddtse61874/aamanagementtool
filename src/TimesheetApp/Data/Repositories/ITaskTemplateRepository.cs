using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// CANONICAL interface (reconciliation 2026-06-21): single home for TaskTemplates, used by BOTH
// P4 RequestsViewModel (GetAllAsync -> VM groups by TemplateName, REQ-02) and P6 SettingsViewModel
// (GetAllAsync/InsertAsync/DeleteAsync CRUD, SET-03). Template methods are NOT on ITaskRepository.
public interface ITaskTemplateRepository
{
    Task<IReadOnlyList<TaskTemplate>> GetAllAsync();   // all rows ordered by template_name,order_index
    Task<int> InsertAsync(TaskTemplate template);      // SET-03 add (returns new id)
    Task DeleteAsync(int id);                           // SET-03 delete (hard delete — seed data, no TimeLog FK)
    Task DeleteByTemplateNameAsync(string templateName); // SET-03 delete a whole template (all its rows); used by edit (delete-then-reinsert)
}
