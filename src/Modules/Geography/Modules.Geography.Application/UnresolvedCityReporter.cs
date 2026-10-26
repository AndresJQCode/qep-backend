using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Una ciudad que no emparejó en un pedido. <see cref="SourceRef"/> es la fila de origen (en Shopify, la de
/// <c>shopify.orders</c>): reportarla dos veces deja una sola ocurrencia.</summary>
public sealed record UnresolvedCityReport(
    Guid TenantId,
    string Source,
    Guid SourceRef,
    Guid OrderId,
    Guid? CustomerId,
    DepartmentId DepartmentId,
    string CityText,
    string SampleAddress,
    string? ProvinceCode,
    bool PartyCityOnly,
    bool CustomerCityOnly,
    DateTimeOffset SeenAt);

public enum UnresolvedCityReportOutcome
{
    /// <summary>Se sumó la ocurrencia, en una fila nueva o en la abierta del texto.</summary>
    Recorded,

    /// <summary>La fila de origen ya tenía su ocurrencia: no se escribió nada.</summary>
    AlreadyRecorded,

    /// <summary>El texto ya tiene un sinónimo vivo: no se escribió nada y el que llama corrige con él.</summary>
    SynonymExists,

    /// <summary>El texto se normaliza a vacío: no hay nada que un operador pueda resolver, y no se escribió nada.</summary>
    Ignored,
}

/// <summary><see cref="UnresolvedCityId"/> viene con <see cref="UnresolvedCityReportOutcome.Recorded"/> y
/// <see cref="UnresolvedCityReportOutcome.AlreadyRecorded"/>; <see cref="SynonymId"/> y <see cref="CityId"/>, con
/// <see cref="UnresolvedCityReportOutcome.SynonymExists"/>.</summary>
public sealed record UnresolvedCityReportResult(
    UnresolvedCityReportOutcome Outcome, UnresolvedCityId? UnresolvedCityId, CitySynonymId? SynonymId, CityId? CityId);

/// <summary>Lo llama Bootstrapper por Shopify (SC-06) y por el recorrido (SC-13). Global, sin IExecutionContext.</summary>
public interface IUnresolvedCityReporter
{
    Task<UnresolvedCityReportResult> ReportAsync(UnresolvedCityReport report, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-10-26 §4.2. Todo en una transacción y bajo el candado del texto (<see cref="IGeographyUnitOfWork.LockCityTextAsync"/>),
/// que es el mismo que toma confirmar el texto como sinónimo (SC-11): sin él, un reporte que cruza la confirmación dejaría
/// una ocurrencia nueva en una fila ya resuelta.
/// </summary>
public sealed class UnresolvedCityReporter(
    IGeographyUnitOfWork unitOfWork,
    ICitySynonymRepository synonyms,
    IUnresolvedCityRepository unresolvedCities) : IUnresolvedCityReporter
{
    public async Task<UnresolvedCityReportResult> ReportAsync(
        UnresolvedCityReport report, CancellationToken cancellationToken)
    {
        // La fila que crearía el dominio pone la llave y los recortes: la misma regla que el índice único
        // (sin espacio final ni par sustituto partido), sin copiarla acá.
        var candidate = UnresolvedCity.Create(
            UnresolvedCityId.New(),
            report.DepartmentId,
            report.CityText,
            report.SampleAddress,
            report.ProvinceCode,
            report.SeenAt);

        // No lanza: la importación que lanza se reintenta, y un texto vacío lo seguiría siendo en cada reintento.
        if (candidate.NormalizedText.Length == 0)
        {
            return new UnresolvedCityReportResult(UnresolvedCityReportOutcome.Ignored, null, null, null);
        }

        // Disponer sin commit hace rollback: los caminos que no escriben (o descartan lo escrito) no commitean.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockCityTextAsync(report.DepartmentId, candidate.NormalizedText, cancellationToken);

        var synonym = await synonyms.FindActiveAsync(report.DepartmentId, candidate.NormalizedText, cancellationToken);
        if (synonym is not null)
        {
            return new UnresolvedCityReportResult(
                UnresolvedCityReportOutcome.SynonymExists, null, synonym.Id, synonym.CityId);
        }

        var trimmed = report with
        {
            CityText = candidate.CityText,
            SampleAddress = candidate.SampleAddress,
            ProvinceCode = candidate.ProvinceCode,
        };
        var (id, occurrenceInserted) = await unresolvedCities.UpsertWithOccurrenceAsync(
            trimmed, candidate.NormalizedText, cancellationToken);
        if (!occurrenceInserted)
        {
            return new UnresolvedCityReportResult(UnresolvedCityReportOutcome.AlreadyRecorded, id, null, null);
        }

        await transaction.CommitAsync(cancellationToken);
        return new UnresolvedCityReportResult(UnresolvedCityReportOutcome.Recorded, id, null, null);
    }
}
