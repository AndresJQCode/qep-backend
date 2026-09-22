using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Fija —o quita, con <c>null</c>— el piso de escala global de una cotización: el
/// <c>FromUnit</c> del tramo que el asesor eligió para todas sus líneas.
///
/// No es un porcentaje. Cada línea resuelve contra el tramo de **su propio producto** que
/// arranca en ese piso, y sólo si mejora lo que ya tenía — el global es un piso y no un techo.
/// La restricción de cantidad se sigue exigiendo. Todo eso vive en
/// <see cref="QuotationScaleGroupPricing"/>; acá sólo se guarda la elección y se recalcula.
/// </summary>
public sealed record SetQuotationGlobalScaleCommand(
    Guid TenantId, Guid QuotationId, int? Floor) : ICommand<QuotationDto>;

public sealed class SetQuotationGlobalScaleValidator
    : AbstractValidator<SetQuotationGlobalScaleCommand>
{
    public SetQuotationGlobalScaleValidator()
    {
        // Mismo piso que PriceScaleRequestRules.FromUnit en Catalog: una escala nunca arranca por
        // debajo de 1, así que cero o negativo no puede coincidir con ninguna. Se corta como
        // error de forma y no como "ese piso no existe", que mandaría a buscar el problema en el
        // catálogo.
        RuleFor(command => command.Floor)
            .GreaterThanOrEqualTo(1)
            .When(command => command.Floor.HasValue);
    }
}

public sealed class SetQuotationGlobalScaleHandler(
    IQuotationRepository repository,
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SetQuotationGlobalScaleCommand> validator)
    : ICommandHandler<SetQuotationGlobalScaleCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        SetQuotationGlobalScaleCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        // Mismo endpoint para dos pantallas, igual que UpdateQuotationItem (a pedido,
        // 2026-09-15): una cotización Converted ya no admite EnsureEditable, pero "Editar" pedido
        // pendiente ofrece corregir el descuento global como el editor de Draft/Sent.
        Order? order = null;
        if (quotation.Status == QuotationStatus.Converted)
        {
            order = await orderRepository.FindByQuotationIdAsync(
                command.TenantId, quotation.Id, cancellationToken)
                ?? throw OrderNotFound.For(command.QuotationId);

            if (order.Status != OrderStatus.Pending)
            {
                throw new QuotationsDomainException(
                    "order.order.not_pending",
                    "The global scale discount can only be changed while the order is pending.");
            }
        }

        await EnsureFloorIsAvailableAsync(command, quotation, cancellationToken);

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        if (order is not null)
        {
            quotation.SetGlobalScaleFloorAfterConversion(command.Floor, updatedBy, now);
        }
        else
        {
            quotation.SetGlobalScaleFloor(command.Floor, updatedBy, now);
        }

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Edited,
            updatedBy,
            QuotationChangeSummary.GlobalScaleFloorChanged(command.Floor),
            now));
        // Accion y target distintos segun por donde se entro, igual que UpdateQuotationItemHandler:
        // la auditoria se lee por recurso, y quien busque que le paso a un pedido no encontraria
        // este cambio si quedara archivado bajo el id de su cotizacion.
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            order is not null
                ? "quotation.order.global_scale_changed"
                : "quotation.quotation.global_scale_changed",
            order?.Id.ToString() ?? quotation.Id.ToString(),
            "success",
            now);

        // El piso cambia el tramo de todas las líneas a la vez, así que el recálculo es la
        // operación, no un efecto secundario.
        await QuotationPricingRecalculation.ApplyAsync(
            pricingLookup, command.TenantId, quotation, now, cancellationToken);

        // El total de la cotización cambió: lo cargado en comprobantes no, pero el estado del
        // pago sí puede — mismo motivo que UpdateQuotationItemHandler.
        order?.RecalculatePaymentStatus(quotation.Total, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }

    /// <summary>
    /// El piso tiene que ser el <c>FromUnit</c> de alguna escala de algún producto de esta
    /// cotización. Las opciones salen de esa misma lista (<c>AvailableGlobalScaleFloors</c> en la
    /// respuesta), así que mandar uno que no está es un bug del cliente y se dice como tal.
    ///
    /// Es un código de dominio y no un error de campo: no hay un input del formulario al que
    /// apuntar — el select estaba ofreciendo otra cosa.
    /// </summary>
    private async Task EnsureFloorIsAvailableAsync(
        SetQuotationGlobalScaleCommand command,
        Quotation quotation,
        CancellationToken cancellationToken)
    {
        if (command.Floor is not { } floor)
        {
            return;
        }

        var products = await pricingLookup.FindManyAsync(
            command.TenantId,
            quotation.Items.Select(item => item.ProductId).Distinct().ToArray(),
            cancellationToken);

        var available = products.Values
            .SelectMany(product => product.Scales)
            .Any(scale => scale.FromUnit == floor);

        if (!available)
        {
            throw new QuotationsDomainException(
                "quotation.global_scale.floor_not_available",
                "No product in this quotation has a price scale starting at that unit.");
        }
    }
}
