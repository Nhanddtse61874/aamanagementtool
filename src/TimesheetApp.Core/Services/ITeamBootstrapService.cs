namespace TimesheetApp.Services;

// P10 Multi-Team (TM-02 / TM-09, architecture §1d, spec §3). Post-init data migration + first-run
// team creation. Runs once at startup AFTER DatabaseInitializer.InitializeAsync() and BEFORE the
// archive backfills + DefaultTaskSync. Idempotent: a no-op once any team exists.
public interface ITeamBootstrapService
{
    Task EnsureBootstrappedAsync();
}
