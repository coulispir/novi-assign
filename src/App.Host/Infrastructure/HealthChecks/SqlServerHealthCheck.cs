using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace App.Host.Infrastructure.HealthChecks;

/// <summary>
/// Opens a connection and runs <c>SELECT 1</c>. Deliberately bypasses EF Core, whose retrying execution strategy would
/// hold a probe for seconds on an outage. Failures surface as exceptions, which the health check service logs and
/// reports with the registration's failure status.
/// </summary>
internal sealed class SqlServerHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public SqlServerHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return HealthCheckResult.Healthy();
    }
}
