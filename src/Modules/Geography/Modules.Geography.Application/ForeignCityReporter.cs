using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Una ciudad del exterior que no coincidió en un pedido. Lleva la ciudad escrita: el <c>ToString</c> no la escribe.
/// <see cref="OrderId"/> es el pedido: reportarlo dos veces deja una sola ocurrencia.</summary>
public sealed record ForeignCityReport(
    Guid TenantId, Guid OrderId, string CountryCode, string CityText, DateTimeOffset SeenAt)
{
    public override string ToString() =>
        $"{nameof(ForeignCityReport)} {{ TenantId = {TenantId}, OrderId = {OrderId}, CountryCode = {CountryCode} }}";
}

public enum ForeignCityReportOutcome
{
    /// <summary>Se sumó la ocurrencia, en una fila nueva o en la abierta del texto.</summary>
    Recorded,

    /// <summary>El pedido ya tenía su ocurrencia en esa fila: no se escribió nada.</summary>
    AlreadyRecorded,

    /// <summary>El texto ya tiene un sinónimo vivo: no se escribió nada.</summary>
    SynonymExists,

    /// <summary>El país no está en el catálogo de Siigo: no hay ciudad que un operador pueda elegir, y no se escribió nada.</summary>
    CountryNotInCatalog,

    /// <summary>El texto se normaliza a vacío: no hay nada que resolver, y no se escribió nada.</summary>
    Ignored,
}

/// <summary>Escribe y puede fallar: quien lo llama (vista previa y emisión de Siigo) no deja que una falla tumbe el pedido.
/// Global, sin IExecutionContext.</summary>
public interface IForeignCityReporter
{
    Task<ForeignCityReportOutcome> ReportAsync(ForeignCityReport report, CancellationToken cancellationToken);
}

/// <summary>
/// Todo en una transacción y bajo el candado del texto (<see cref="IGeographyUnitOfWork.LockForeignCityTextAsync"/>), el mismo
/// que toma confirmar el texto como sinónimo: sin él, un reporte que cruza la confirmación dejaría una ocurrencia nueva en una
/// fila ya resuelta. Mismo diseño que <see cref="UnresolvedCityReporter"/>, por país.
/// </summary>
public sealed class ForeignCityReporter(
    IGeographyUnitOfWork unitOfWork,
    IBillingCityRepository cities,
    IForeignCitySynonymRepository synonyms,
    IUnresolvedForeignCityRepository unresolved) : IForeignCityReporter
{
    public async Task<ForeignCityReportOutcome> ReportAsync(
        ForeignCityReport report, CancellationToken cancellationToken)
    {
        // La fila que crearía el dominio pone la llave y los recortes: la misma regla que el índice único, sin copiarla acá.
        var candidate = UnresolvedForeignCity.Create(
            UnresolvedForeignCityId.New(), report.CountryCode, report.CityText, report.SeenAt);

        // No lanza: un texto vacío lo seguiría siendo en cada reintento.
        if (candidate.NormalizedText.Length == 0)
        {
            return ForeignCityReportOutcome.Ignored;
        }

        if (!await cities.CountryExistsAsync(candidate.CountryCode, cancellationToken))
        {
            return ForeignCityReportOutcome.CountryNotInCatalog;
        }

        // Disponer sin commit hace rollback: los caminos que no escriben (o descartan lo escrito) no commitean.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockForeignCityTextAsync(candidate.CountryCode, candidate.NormalizedText, cancellationToken);

        if (await synonyms.FindActiveAsync(candidate.CountryCode, candidate.NormalizedText, cancellationToken) is not null)
        {
            return ForeignCityReportOutcome.SynonymExists;
        }

        var (_, occurrenceInserted) = await unresolved.UpsertWithOccurrenceAsync(
            candidate, report.TenantId, report.OrderId, cancellationToken);
        if (!occurrenceInserted)
        {
            return ForeignCityReportOutcome.AlreadyRecorded;
        }

        await transaction.CommitAsync(cancellationToken);
        return ForeignCityReportOutcome.Recorded;
    }
}
