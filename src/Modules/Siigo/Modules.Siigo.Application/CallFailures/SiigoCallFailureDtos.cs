using Modules.Siigo.Domain;

namespace Modules.Siigo.Application.CallFailures;

/// <summary>
/// Una fila de la lista de fallas: lo que la pantalla necesita para elegir cuál abrir. <b>Sin el curl ni la respuesta</b>: llevan datos
/// del cliente y una página de 25 pasaría de los 900 KB; se piden de a una con <see cref="SiigoCallFailureDetailDto"/>.
/// <paramref name="IdempotencyKey"/> y <paramref name="SiigoInvoiceId"/> atan la fila a la factura (<c>siigo.invoices</c>); <paramref name="TraceId"/>,
/// al log de la aplicación y a <c>platform/request-log</c>.
/// </summary>
public sealed record SiigoCallFailureSummaryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Operation,
    string Method,
    string Path,
    int? Status,
    string Reason,
    string? IdempotencyKey,
    string? SiigoInvoiceId,
    string? TraceId)
{
    public static SiigoCallFailureSummaryDto From(SiigoCallFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new SiigoCallFailureSummaryDto(
            failure.Id,
            failure.OccurredAt,
            failure.Operation.ToString(),
            failure.Method,
            failure.Path,
            failure.Status,
            failure.Reason,
            failure.IdempotencyKey,
            failure.SiigoInvoiceId,
            failure.TraceId);
    }
}

public sealed record SiigoCallFailurePageDto(IReadOnlyList<SiigoCallFailureSummaryDto> Items, int Total, int Page, int PageSize);

/// <summary>
/// La falla completa: el resumen más el <c>curl</c> de la petición (token y <c>access_key</c> enmascarados) y la respuesta de Siigo. Va por
/// aparte de la lista por tamaño y porque lleva datos del cliente: el <c>ToString</c> no los escribe.
/// </summary>
public sealed record SiigoCallFailureDetailDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Operation,
    string Method,
    string Path,
    int? Status,
    string Reason,
    string? IdempotencyKey,
    string? SiigoInvoiceId,
    string? TraceId,
    string RequestCurl,
    string? ResponseBody)
{
    public static SiigoCallFailureDetailDto From(SiigoCallFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new SiigoCallFailureDetailDto(
            failure.Id,
            failure.OccurredAt,
            failure.Operation.ToString(),
            failure.Method,
            failure.Path,
            failure.Status,
            failure.Reason,
            failure.IdempotencyKey,
            failure.SiigoInvoiceId,
            failure.TraceId,
            failure.RequestCurl,
            failure.ResponseBody);
    }

    public override string ToString() => "SiigoCallFailureDetailDto { }";
}
