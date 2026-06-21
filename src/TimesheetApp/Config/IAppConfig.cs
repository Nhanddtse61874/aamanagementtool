namespace TimesheetApp.Config;

// DATA-07 / SET-01: the .db PATH is stored app-locally (%APPDATA%), never inside the
// shared DB (avoids the chicken-and-egg of reading the path from the DB it points to).
public interface IAppConfig
{
    string DbPath { get; }
    void SetDbPath(string dbPath);
}
