using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
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

    public static string NumberOf(DateOnly day, int sequence) =>
        "NOV-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + sequence.ToString("D4", CultureInfo.InvariantCulture);
}
