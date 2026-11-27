using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests.Incidents;

/// <summary>Cómo siembran novedades las pruebas mientras no pasan por la API: el agregado se arma con el dominio y se guarda por los
/// mismos puertos que usa un handler (numeración dentro de una transacción incluida).</summary>
internal static class IncidentTestSupport
{
    public static Incident NewIncident(
        LedgerSetup s,
        string number,
        IncidentType type = IncidentType.ShortMove,
        IncidentSubject? subject = null,
        string? note = null,
        string? rawCode = null,
        string? rawPayload = null,
        IReadOnlyList<IncidentEvidence>? evidence = null,
        Guid? reportedBy = null,
        string? operationKey = null,
        Guid? tenantId = null,
        DateTimeOffset? reportedAt = null) =>
        Incident.Report(
            tenantId ?? s.TenantId,
            number,
            new WarehouseId(s.WarehouseId),
            new OwnerId(s.OwnerId),
            type,
            subject ?? new IncidentSubject(null, null, new LocationId(s.BinA), s.VariantId, null, StockStatus.Available, null, null),
            note,
            rawCode,
            rawPayload,
            evidence ?? [],
            reportedBy ?? Guid.CreateVersion7(),
            "zebra-01",
            operationKey,
            reportedAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    /// <summary>Numera y guarda en una misma transacción, como lo hace un handler.</summary>
    public static async Task<Incident> NumberAndSaveAsync(
        QepApiFactory factory, LedgerSetup s, Func<string, Incident> build, DateOnly? today = null)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IIncidentRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var number = await incidents.NextNumberAsync(s.TenantId, today ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var incident = build(number);
        incidents.Add(incident);
        await unitOfWork.SaveChangesAsync(ct);
        return incident;
    }

    public static async Task SaveAsync(QepApiFactory factory, params Incident[] incidents)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IIncidentRepository>();
        foreach (var incident in incidents)
        {
            repository.Add(incident);
        }

        await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(ct);
    }

    /// <summary>La ficha tal como está en la base.</summary>
    public static async Task<Incident> ReloadAsync(QepApiFactory factory, Incident incident)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var reloaded = await scope.ServiceProvider.GetRequiredService<IIncidentRepository>()
            .GetWithEventsAsync(incident.TenantId, incident.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        return reloaded;
    }

    /// <summary>Una fila de <c>wms.incidents</c> tal como la dejó una acción, leída con SQL: lo que importa de ella en las pruebas.</summary>
    internal sealed record IncidentRow(
        Guid Id,
        string Type,
        string Status,
        decimal? ExpectedQty,
        decimal? ActualQty,
        Guid ReportedBy,
        string? OperationKey,
        Guid? VerificationTaskId,
        Guid? LotId,
        Guid? SourceTaskLineId,
        string? RawPayload,
        DateTimeOffset ReportedAt);

    public static async Task<IReadOnlyList<IncidentRow>> IncidentsOfTaskAsync(PostgreSqlContainer database, Guid taskId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, type, status, expected_qty, actual_qty, reported_by, operation_key, verification_task_id, lot_id,
                   source_task_line_id, raw_payload::text, reported_at
            FROM wms.incidents WHERE source_task_id = @task ORDER BY reported_at, number
            """,
            connection);
        command.Parameters.AddWithValue("task", taskId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<IncidentRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new IncidentRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetFieldValue<DateTimeOffset>(11)));
        }

        return rows;
    }

    /// <summary>El cuerpo de <c>short</c>: lo que el operario escaneó al no encontrar todo.</summary>
    public static ShortTaskLineRequest ShortBody(
        Guid token, decimal found, string type = "ShortMove", Guid? lotId = null, DateTimeOffset? occurredAt = null, string? note = null) =>
        new(token, found, lotId, type, note, occurredAt ?? DateTimeOffset.UtcNow, "zebra-01");

    public static async Task<HttpResponseMessage> ShortRawAsync(
        HttpClient client, Guid taskId, Guid lineId, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{Tasks.TaskApiSupport.TasksUrl()}/{taskId}/lines/{lineId}/short")
        {
            Content = JsonContent.Create(body),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static string NumberOf(DateOnly day, int sequence) =>
        "NOV-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + sequence.ToString("D4", CultureInfo.InvariantCulture);
}
