using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Los tres nullable a propósito: un cuerpo sin un campo llega como <c>null</c> y el validador lo
/// rechaza, en vez de apagar el ajuste con un <c>false</c> que nadie mandó.</summary>
public sealed record UpdateOrderSettingsCommand(
    Guid TenantId,
    bool? RequirePaymentProofToMarkPaid,
    bool? RequireFullPaymentToInvoice,
    bool? AutoArchive) : ICommand<OrderSettingsDto>;

public sealed class UpdateOrderSettingsValidator : AbstractValidator<UpdateOrderSettingsCommand>
{
    public UpdateOrderSettingsValidator()
    {
        RuleFor(command => command.RequirePaymentProofToMarkPaid).NotNull();
        RuleFor(command => command.RequireFullPaymentToInvoice).NotNull();
        RuleFor(command => command.AutoArchive).NotNull();
    }
}

/// <summary>Reemplaza el documento entero, sin <c>If-Match</c>, igual que
/// <see cref="UpdateQuotationSettingsHandler"/>: el store bloquea la fila del tenant dentro de la
/// transacción, así que dos guardados se serializan y gana el último.</summary>
public sealed class UpdateOrderSettingsHandler(
    IQuotationSettingsStore store,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateOrderSettingsCommand> validator)
    : ICommandHandler<UpdateOrderSettingsCommand, OrderSettingsDto>
{
    public const string AuditAction = "quotations.order_settings.updated";

    public async Task<OrderSettingsDto> HandleAsync(
        UpdateOrderSettingsCommand command, CancellationToken cancellationToken)
    {
        // Autoriza antes de validar: alguien de otro tenant no recibe el mapa de campos.
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Orders, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var settings = new OrderSettings(
            command.RequirePaymentProofToMarkPaid!.Value,
            command.RequireFullPaymentToInvoice!.Value,
            command.AutoArchive!.Value);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await store.SetOrderSettingsAsync(command.TenantId, settings, cancellationToken);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            AuditAction,
            command.TenantId.ToString(),
            "success",
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return OrderSettingsMapping.ToDto(settings);
    }
}
