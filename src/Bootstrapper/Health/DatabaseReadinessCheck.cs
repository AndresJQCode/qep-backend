using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Bootstrapper.Health;

/// <summary>
/// El pod está listo para recibir tráfico sólo si llega a PostgreSQL: abre una conexión y corre
/// <c>SELECT 1</c>.
/// </summary>
/// <remarks>
/// No atrapa nada a propósito. <c>HealthCheckService</c> convierte cualquier excepción —y la
/// cancelación por el timeout de la registración— en el <c>failureStatus</c> registrado, que acá
/// es <see cref="HealthStatus.Unhealthy"/>, así que el endpoint responde 503 igual. La respuesta
/// por defecto sólo escribe el estado, nunca el mensaje de la excepción: la cadena de conexión y
/// el host de la base no salen del pod.
/// </remarks>
internal sealed class DatabaseReadinessCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
