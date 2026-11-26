using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary><c>PUT …/document</c> (<c>siigo.settings.manage</c>): comprobante, centro de costo, bodega, los interruptores y el
/// producto del flete. Reemplaza el paso entero; <paramref name="ExpectedVersion"/> llega por <c>If-Match</c>.</summary>
public sealed record UpdateSiigoDocumentCommand(
    Guid TenantId,
    Guid ConnectionId,
    string? DocumentTypeId,
    string? CostCenterId,
    string? WarehouseId,
    bool SendToDian,
    bool SendMail,
    bool FreightSeparateInvoice,
    string? FreightProductCode,
    string? PriceMode,
    long ExpectedVersion) : ICommand<SiigoSettingsDto>;

/// <summary>La forma del cuerpo (§5.2): el comprobante es requerido para facturar y los largos son los de las columnas. Cada
/// error lleva el campo en camelCase (422 <c>validation.failed</c> con <c>errors</c>).</summary>
public sealed class UpdateSiigoDocumentValidator : AbstractValidator<UpdateSiigoDocumentCommand>
{
    public UpdateSiigoDocumentValidator()
    {
        RuleFor(command => command.DocumentTypeId).NotEmpty().MaximumLength(SiigoSettings.IdMaxLength).OverridePropertyName("documentTypeId");
        RuleFor(command => command.CostCenterId).MaximumLength(SiigoSettings.IdMaxLength).OverridePropertyName("costCenterId");
        RuleFor(command => command.WarehouseId).MaximumLength(SiigoSettings.IdMaxLength).OverridePropertyName("warehouseId");
        RuleFor(command => command.FreightProductCode)
            .MaximumLength(SiigoSettings.FreightProductCodeMaxLength)
            .OverridePropertyName("freightProductCode");
        // Ausente conserva el guardado; presente debe ser el nombre de un modo (sin distinguir mayúsculas).
        RuleFor(command => command.PriceMode)
            .Must(value => value is null || SiigoPriceModes.TryParse(value, out _))
            .OverridePropertyName("priceMode");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).OverridePropertyName("expectedVersion");
    }
}

/// <summary>Spec 2026-11-18 §5.3, paso 3. Mismo orden de comprobaciones que <see cref="AssignSiigoCompanyHandler"/>. Los ids de Siigo
/// no se revalidan contra la lista de Siigo: la lista se lee en vivo cuando la pantalla la pide, y una llamada a Siigo por cada
/// guardado gastaría peticiones que cuentan para el bloqueo del 80 %. Audita el nombre del paso, nunca los valores.</summary>
public sealed class UpdateSiigoDocumentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoCompanyDirectory companies,
    ISiigoTaxRateDirectory taxRates,
    ISiigoMemberDirectory members,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateSiigoDocumentCommand> validator) : ICommandHandler<UpdateSiigoDocumentCommand, SiigoSettingsDto>
{
    public async Task<SiigoSettingsDto> HandleAsync(UpdateSiigoDocumentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var connection = await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var settings = await SiigoSettingsSupport.SettingsAsync(settingsRepository, command.TenantId, command.ConnectionId, cancellationToken);
        SiigoSettingsSupport.EnsureVersion(settings, command.ExpectedVersion);

        settings.UpdateDocument(new SiigoDocumentSettings(
            command.DocumentTypeId,
            command.CostCenterId,
            command.WarehouseId,
            command.SendToDian,
            command.SendMail,
            command.FreightSeparateInvoice,
            command.FreightProductCode,
            SiigoPriceModes.TryParse(command.PriceMode, out var priceMode) ? priceMode : null));
        SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoSettingsSteps.Document, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SiigoSettingsSupport.ToDtoAsync(settings, connection, companies, taxRates, members, cancellationToken);
    }
}
