namespace TimesheetApp.Services;

/// <summary>Abstracts the system clock so timestamp-dependent code (e.g. backup
/// filenames) is deterministic under test. Inject <see cref="SystemClock"/> in production.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock backed by <see cref="DateTimeOffset.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
