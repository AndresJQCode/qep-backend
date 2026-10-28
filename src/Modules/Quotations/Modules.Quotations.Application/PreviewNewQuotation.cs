using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// La cotización que quedaría si se creara con este cuerpo, sin crearla (spec 2026-10-18 §7). Mismo cuerpo que
/// <see cref="CreateQuotationCommand"/>: "Nueva cotización" pinta el documento entero antes de que exista.
/// </summary>
public sealed record PreviewNewQuotationQuery(
    Guid TenantId,
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<QuotationItemAddition> Items,
    int? GlobalScaleFloor,
    bool IsRetail,
    ShippingRequest? Shipping = null) : IQuery<QuotationDto>, IQuotationEdits;

/// <summary>Las reglas de la creación salvo el documento de la facturación, que el cálculo previo no exige: corre
/// mientras la persona escribe, igual que <see cref="PreviewQuotationValidator"/>.</summary>
public sealed class PreviewNewQuotationValidator : QuotationEditsValidator<PreviewNewQuotationQuery>
{
    public PreviewNewQuotationValidator()
        : base(requireBillingIdentification: false)
    {
        RuleFor(query => query.ClientId).NotEmpty();
    }
}

/// <summary>
/// Arma la cotización en memoria con <see cref="QuotationDraftBuilder"/>, el mismo armado que la creación, así
/// que el total que devuelve es el que la creación deja. <b>No escribe nada</b>: no pide número del consecutivo
/// ni deja historial, auditoría, outbox ni <c>SaveChangesAsync</c>; por eso no recibe ninguno de esos puertos.
///
/// Id, número y versión se borran antes de responder: la pantalla no debe poder enlazar ni mandar en
/// <c>If-Match</c> algo que no existe — mismo motivo y misma solución que <c>PreviewOrderHandler</c>.
/// </summary>
public sealed class PreviewNewQuotationHandler(
    QuotationDraftBuilder drafts,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<PreviewNewQuotationQuery> validator)
    : IQueryHandler<PreviewNewQuotationQuery, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        PreviewNewQuotationQuery query,
        CancellationToken cancellationToken)
    {
        // Antes de validar, como la creación: 403 para un tenant ajeno, nunca un 422 que hable de su cuerpo.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var advisorId = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, query.TenantId, cancellationToken);
        // El hoy del tenant: la vigencia ausente vuelve resuelta con la misma fecha que dejaría crear.
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        var draft = await drafts.BuildAsync(
            new QuotationDraftInput(
                query.TenantId, query.ClientId, QuotationDraftBuilder.PreviewQuotationNumber, advisorId, query,
                calendar.Today, calendar.UtcNow),
            cancellationToken);

        return draft.Quotation.ToDto() with { Id = Guid.Empty, QuotationNumber = string.Empty, Version = 0 };
    }
}
