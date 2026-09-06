using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ListQuotationHistoryQuery(Guid TenantId, Guid QuotationId)
    : IQuery<IReadOnlyList<QuotationHistoryEntryDto>>;

/// <summary>
/// Una entrada de la línea de tiempo de la cotización: quién, cuándo, qué tipo de operación y el
/// resumen de <b>qué</b> cambió (<see cref="QuotationChangeSummary"/>).
/// </summary>
/// <param name="MemberEmail">Resuelto contra Tenancy/Identity para toda la lista de una vez.
/// Null en <c>Expired</c> —lo dispara un job, no una persona— y también si la persona ya no es
/// miembro del tenant: <c>MemberId</c> es una referencia blanda entre módulos y una entrada
/// histórica tiene que poder leerse igual.</param>
/// <param name="Details">Resumen en texto de qué cambió. Null en las entradas anteriores a este
/// slice: se guardaba quién y cuándo, pero no qué.</param>
public sealed record QuotationHistoryEntryDto(
    Guid Id,
    string EventType,
    DateTimeOffset EventAt,
    Guid? MemberId,
    string? MemberEmail,
    string? Details);

/// <summary>
/// US-17: "registro de quién creó o modificó la cotización y la fecha de cada operación". La
/// tabla existía desde el principio y se escribía en la misma transacción que cada mutación; lo
/// que faltaba era poder leerla.
/// </summary>
public sealed class ListQuotationHistoryHandler(
    IQuotationRepository repository,
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListQuotationHistoryQuery, IReadOnlyList<QuotationHistoryEntryDto>>
{
    public async Task<IReadOnlyList<QuotationHistoryEntryDto>> HandleAsync(
        ListQuotationHistoryQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, QuotationsPermissions.QuotationRead);

        var entries = await repository.ListHistoryAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken);

        // Una consulta para todos los correos y no uno por entrada: un historial largo repite las
        // mismas dos o tres personas.
        var memberIds = entries
            .Where(entry => entry.MemberId.HasValue)
            .Select(entry => entry.MemberId!.Value.Value)
            .Distinct()
            .ToArray();
        var emails = memberIds.Length == 0
            ? new Dictionary<Guid, string?>()
            : await advisorLookup.FindEmailsAsync(query.TenantId, memberIds, cancellationToken);

        return entries
            .Select(entry => new QuotationHistoryEntryDto(
                entry.Id.Value,
                entry.EventType.ToString(),
                entry.EventAt,
                entry.MemberId?.Value,
                entry.MemberId is { } memberId
                    ? emails.GetValueOrDefault(memberId.Value)
                    : null,
                entry.Details))
            .ToArray();
    }
}
