namespace TimesheetApp.Data;

public interface IDatabaseInitializer
{
    // Idempotent: CREATE TABLE IF NOT EXISTS (all 7); PRAGMA user_version migrations;
    // ensure hidden DEFAULT request; seed DefaultTasks only if the table is empty.
    Task InitializeAsync();   // DATA-01/02/03/04/05
}
