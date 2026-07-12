namespace TimesheetApp.Services;

/// <summary>Abstracts the system clock so timestamp-dependent code (e.g. backup
/// filenames) is deterministic under test. Inject <see cref="SystemClock"/> in production.</summary>
public interface IClock
{
    DateOnly Today { get; }           // injected for deterministic week / N-day tests (spec §4)
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock backed by <see cref="DateTimeOffset.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
