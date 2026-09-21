using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El documento que quedaría si se guardara el borrador del editor, sin persistir nada. Mismo
/// cuerpo que <see cref="SaveQuotationCommand"/> salvo el <c>If-Match</c>: el cálculo previo no
/// compite con nadie porque no escribe.
/// </summary>
public sealed record PreviewQuotationQuery(
    Guid TenantId,
    Guid QuotationId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<QuotationItemAddition> Items) : IQuery<QuotationDto>, IQuotationEdits;

public sealed class PreviewQuotationValidator : QuotationEditsValidator<PreviewQuotationQuery>;

/// <summary>
/// Aplica en memoria los mismos pasos que el guardado sobre un agregado leído **sin rastreo**, con
/// los mismos métodos de dominio, así que los errores salen con el mismo código. Sin historial,
/// auditoría, outbox ni <c>SaveChangesAsync</c>.
///
/// La versión que vuelve es la guardada: la pantalla nunca debe mandar en <c>If-Match</c> una
/// versión que sólo existió en este cálculo — mismo motivo y misma solución que
/// <c>PreviewOrderEditsHandler</c> (PreviewOrderEdits.cs:71,:111).
/// </summary>
public sealed class PreviewQuotationHandler(
    IQuotationRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductPricingLookup pricingLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<PreviewQuotationQuery> validator)
    : IQueryHandler<PreviewQuotationQuery, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        PreviewQuotationQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        // Sin rastreo: el cálculo muta el agregado en memoria, así que la lectura no puede dejar
        // nada en el change tracker que un SaveChangesAsync del mismo scope llegue a persistir.
        var quotation = await repository.FindUntrackedAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(query.QuotationId);

        QuotationEditable.Ensure(quotation);

        var storedVersion = quotation.Version;

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, query.TenantId, cancellationToken);

        await QuotationEditsApplication.ApplyAsync(
            quotation, query, query.TenantId, customerLookup, companyLookup, pricingLookup,
            updatedBy, clock.UtcNow, cancellationToken);

        return quotation.ToDto() with { Version = storedVersion };
    }
}
