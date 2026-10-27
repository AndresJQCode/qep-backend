using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// <c>Items</c> es nullable en el record y la propiedad de <see cref="IQuotationEdits"/> es explícita para que los
/// llamadores posicionales de antes compilen sin tocarse; ausente es "sin líneas" (spec 2026-10-18 §7.2).
/// </summary>
public sealed record CreateQuotationCommand(
    Guid TenantId,
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    ShippingRequest? Shipping = null,
    IReadOnlyList<QuotationItemAddition>? Items = null,
    int? GlobalScaleFloor = null,
    bool IsRetail = false) : ICommand<QuotationDto>, IQuotationEdits
{
    IReadOnlyList<QuotationItemAddition> IQuotationEdits.Items => Items ?? [];
}

/// <summary>Las reglas del guardado (<see cref="QuotationEditsValidator{TEdits}"/>) más el cliente. Exige el
/// documento de la facturación, como hacía antes de aceptar líneas: crear es guardar, no calcular.</summary>
public sealed class CreateQuotationValidator : QuotationEditsValidator<CreateQuotationCommand>
{
    public CreateQuotationValidator()
        : base(requireBillingIdentification: true)
    {
        RuleFor(command => command.ClientId).NotEmpty();
    }
}

/// <summary>
/// Crea la cotización con el documento que traiga el cuerpo —encabezado, envío, líneas, piso y detal— en una
/// sola escritura (spec 2026-10-18 §7). Sin líneas es el borrador vacío de antes. El armado lo comparte con el
/// cálculo previo (<see cref="QuotationDraftBuilder"/>), así que el total que se ve antes de crear es el que
/// queda.
/// </summary>
public sealed class CreateQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductLookup productLookup,
    QuotationDraftBuilder drafts,
    IQuotationNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    IMembershipDirectory membershipDirectory,
    ITenantDirectory tenantDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<CreateQuotationCommand> validator)
    : ICommandHandler<CreateQuotationCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        CreateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar y no al revés, mismo criterio que CreateProductHandler: la
        // política del endpoint ya frena a quien le falta el permiso, pero no al que lo tiene
        // para otro tenant.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var advisorId = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // El año del consecutivo es el del día del tenant, no el de UTC (spec 2026-09-17, punto 2a):
        // en Bogotá, el 31 de diciembre desde las 19:00 UTC ya es el año siguiente.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        // El formato es un dato del tenant (spec 2026-09-17 de numeración). Sin año, el consecutivo
        // sale de la fila `year = 0`, que no se reinicia; con año, de la fila del año.
        var format = await numberingFormats.GetAsync(
            command.TenantId, DocumentNumberType.Quotation, cancellationToken);
        var counterYear = format.IncludeYear ? year : 0;

        // El contador y la cotización se confirman juntos, como en CreateOrderHandler: con líneas, el armado
        // puede fallar después de pedir el número (un producto inexistente o sin precio), y ese 422 no puede
        // gastar un número del consecutivo.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var sequence = await numberGenerator.NextAsync(command.TenantId, counterYear, cancellationToken);
        var quotationNumber = DocumentNumberFormatter.Format(format, year, sequence);

        var draft = await drafts.BuildAsync(
            new QuotationDraftInput(
                command.TenantId, command.ClientId, quotationNumber, advisorId, command, calendar.Today, now),
            cancellationToken);
        var quotation = draft.Quotation;
        // El armado no lo exige para que el cálculo previo no falle mientras la persona escribe. Crear sí,
        // como el guardado: el validador ya lo frena antes con el campo en `errors`; esto es el respaldo.
        quotation.EnsureBillingIdentified();
        var regional = await tenantDirectory.GetRegionalSettingsAsync(new TenantId(command.TenantId), cancellationToken);
        BillingPartyFiscalRules.EnsureConsistent(quotation.Billing?.ToDetails(), regional?.Country);
        await BillingPersonTypeRequirement.EnsureForQuotationAsync(
            tenantDirectory, command.TenantId, quotation, cancellationToken);
        var historyFormat = await HistoryFormat.ReadAsync(tenantDirectory, command.TenantId, cancellationToken);

        repository.Add(quotation);
        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Created,
            advisorId,
            QuotationChangeSummary.Created(draft.CustomerName, quotation.Currency),
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.created",
            quotation.Id.ToString(),
            "success",
            now);
        // Lo mismo que dejaban crear y después guardar en el editor, sin la fila del encabezado (§7.4).
        await QuotationItemEditHistory.RecordAsync(
            repository, auditPublisher, productLookup, executionContext.SubjectId, command.TenantId, quotation,
            draft.ItemEdits, historyFormat, advisorId, now, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return quotation.ToDto();
    }

    public const int DefaultValidityDays = QuotationDraftBuilder.DefaultValidityDays;
}
