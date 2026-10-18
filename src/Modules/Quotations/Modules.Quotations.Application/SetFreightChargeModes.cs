using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Las opciones de «¿Quién paga el envío?» que quedan activas (spec §4.4); las que no vienen se
/// apagan. Vacía las apaga todas. Nullable para que sea el validador el que marque el campo que falta.
///
/// El por defecto (§4.4.1) distingue «no lo toques» de «quítalo»: con <paramref name="SetsDefaultMode"/> en
/// <c>false</c> se conserva el actual (salvo que se apague, que lo quita); en <c>true</c>,
/// <paramref name="DefaultMode"/> es el nuevo, y <c>null</c> lo quita.</summary>
public sealed record SetFreightChargeModesCommand(
    Guid TenantId,
    IReadOnlyList<string?>? EnabledModes,
    bool SetsDefaultMode = false,
    string? DefaultMode = null)
    : ICommand<CarrierSettingsDto>;

/// <summary>
/// Los nombres exactos del enum, con la misma regla que <c>shipping.freightChargeMode</c>: ni otra
/// capitalización ni números. Un desconocido sale en <c>EnabledModes[i]</c>, lo que el formulario sabe marcar.
/// Un modo repetido se tolera: la lista dice cuáles quedan activos, y nombrar dos veces el mismo dice lo mismo.
/// </summary>
public sealed class SetFreightChargeModesValidator : AbstractValidator<SetFreightChargeModesCommand>
{
    private static readonly string[] ModeNames = Enum.GetNames<FreightChargeMode>();

    public SetFreightChargeModesValidator()
    {
        RuleFor(command => command.EnabledModes)
            .NotNull()
            .WithMessage("'Enabled Modes' is required; send an empty list to disable every mode.");
        RuleForEach(command => command.EnabledModes)
            .Must(mode => mode is not null && ModeNames.Contains(mode, StringComparer.Ordinal))
            .WithMessage($"Each enabled mode must be one of: {string.Join(", ", ModeNames)}.");
        // Un nombre desconocido tampoco está entre los activos: una sola regla, un solo error en DefaultMode.
        RuleFor(command => command.DefaultMode)
            .Must((command, mode) => command.EnabledModes is { } enabled && enabled.Contains(mode, StringComparer.Ordinal))
            .When(command => command.SetsDefaultMode && command.DefaultMode is not null)
            .WithMessage("'Default Mode' must be null or one of the enabled modes.");
    }
}

/// <summary>
/// Con su propia operación, como la tasa del envío (P2): así el <c>PUT /quotations/settings</c> del mínimo de
/// compra no la conoce y no la puede pisar. No toca documentos: los que ya tienen un modo que se apaga lo
/// conservan (D-F18), y la conversión y Shopify no lo miran.
/// </summary>
public sealed class SetFreightChargeModesHandler(
    ICarrierRepository carriers,
    IQuotationSettingsStore store,
    IQuotationTaxRateLookup taxRates,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SetFreightChargeModesCommand> validator)
    : ICommandHandler<SetFreightChargeModesCommand, CarrierSettingsDto>
{
    public const string AuditAction = "quotations.freight_charge_modes.updated";

    public async Task<CarrierSettingsDto> HandleAsync(SetFreightChargeModesCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: a otro tenant no se le devuelve el mapa de campos.
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var enabled = command.EnabledModes!
            .Select(mode => Enum.Parse<FreightChargeMode>(mode!, ignoreCase: false))
            .ToHashSet();

        var current = await store.GetFreightChargeModesAsync(command.TenantId, cancellationToken);
        var defaultMode = command.SetsDefaultMode
            ? command.DefaultMode is null ? (FreightChargeMode?)null : Enum.Parse<FreightChargeMode>(command.DefaultMode, ignoreCase: false)
            : current.Default;
        // Apagar el que era el por defecto lo quita en este mismo guardado (§4.4.1): el por defecto siempre es
        // uno activo. Si vino en el cuerpo, el validador ya comprobó que está entre los activos.
        if (defaultMode is { } mode && !enabled.Contains(mode))
        {
            defaultMode = null;
        }

        // Lo mismo que ya estaba no es un cambio: no se guarda ni se audita, como la tasa.
        if (!current.Enabled.SetEquals(enabled) || current.Default != defaultMode)
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await store.SetFreightChargeModesAsync(
                command.TenantId, new FreightChargeModeSettings(enabled, defaultMode), cancellationToken);
            // El recurso es el tenant: las opciones son parte de su configuración.
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                AuditAction,
                command.TenantId.ToString(),
                "success",
                clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await CarrierMapping.SettingsAsync(carriers, store, taxRates, command.TenantId, cancellationToken);
    }
}
