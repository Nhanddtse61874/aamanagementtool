using System.Data;

namespace TimesheetApp.Data;

public interface IConnectionFactory
{
    // Returns an OPEN connection with FK on, journal_mode=DELETE, pooling off.
    IDbConnection Create();
}
