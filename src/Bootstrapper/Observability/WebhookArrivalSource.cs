using System.Data;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Infrastructure.Persistence;

namespace Bootstrapper.Observability;

/// <summary>
/// Spec 2026-11-16: el <c>received_at</c> de la entrega más reciente de cada proveedor, con SQL crudo sobre la conexión de
/// <see cref="MessagingDbContext"/>, como <see cref="QueueMetricsSource"/>. Conoce por nombre <c>messaging.webhook_deliveries</c>
/// (<c>id</c>, <c>provider</c>, <c>received_at</c>): renombrar una columna compila y deja el panel sin dato. Lo amarra
/// <c>WebhookArrivalSourceTests</c>.
///
/// Sólo entra lo que pasó la firma (un 401 no inserta), así que es la hora del último webhook que QEP aceptó. Busca por
/// <c>id</c> descendente (la PK, que crece con la llegada) dentro del proveedor, con <c>IX_webhook_deliveries_provider_id</c>:
/// sin él, un proveedor sin filas (Instagram sin conexiones) recorría la tabla entera cada 15 s. Lo amarra
/// <c>TheLastReceivedQueryUsesTheProviderIndex</c>. Tope de 5 s por consulta.
/// </summary>
public sealed class WebhookArrivalSource(MessagingDbContext messaging) : IWebhookArrivalSource
{
    /// <summary>Pública para la prueba del plan: si deja de poder usar el índice, la prueba se pone en rojo.</summary>
    public const string LastReceivedSql =
        "SELECT received_at FROM messaging.webhook_deliveries WHERE provider = @provider ORDER BY id DESC LIMIT 1";

    private const int CommandTimeoutSeconds = 5;

    // Los valores que escribe WebhookDeliveries.ProviderColumnValue (internal de Messaging); los amarra WebhookArrivalSourceTests.
    private static readonly string[] Providers = ["whatsapp", "instagram"];

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadLastReceivedAsync(CancellationToken cancellationToken)
    {
        var connection = messaging.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var lastReceived = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var provider in Providers)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = LastReceivedSql;
                command.CommandTimeout = CommandTimeoutSeconds;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "provider";
                parameter.Value = provider;
                command.Parameters.Add(parameter);
                if (await command.ExecuteScalarAsync(cancellationToken) is DateTime receivedAt)
                {
                    lastReceived[provider] = new DateTimeOffset(DateTime.SpecifyKind(receivedAt, DateTimeKind.Utc));
                }
            }

            return lastReceived;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }
}
