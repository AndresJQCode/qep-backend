using BuildingBlocks.Application;
using FluentValidation;
using Modules.Coordinadora.Domain;
using Modules.Tenancy.Application;

namespace Modules.Coordinadora.Application.Settings;

/// <summary>El paquete tal como llega en un cuerpo: todo nullable para que el validador diga qué campo falta, no el enlace del JSON.</summary>
public sealed record CoordinadoraPackageInput(int? HeightCm, int? WidthCm, int? LengthCm, decimal? WeightKg, int? Units);

/// <summary><c>PUT …/settings</c> (<c>coordinadora.settings.manage</c>): la configuración entera. <paramref name="ExpectedVersion"/> llega por
/// <c>If-Match</c>. <paramref name="DefaultPackage"/> nulo deja el paquete por defecto sin configurar.</summary>
public sealed record UpdateCoordinadoraSettingsCommand(
    Guid TenantId,
    Guid ConnectionId,
    string? SenderName,
    string? SenderAddress,
    string? SenderPhone,
    Guid? SenderCityId,
    string? AccountCode,
    string? ProductCode,
    int? ServiceLevel,
    string? Div,
    CoordinadoraPackageInput? DefaultPackage,
    int PackagingType,
    decimal DeclaredValueMinimum,
    bool CollectOnDelivery,
    string? LabelFormat,
    long ExpectedVersion) : ICommand<CoordinadoraSettingsDto>;

/// <summary>La forma del cuerpo (§5.2): los largos son los de las columnas y los rangos los del dominio. Cada error lleva el campo en
/// camelCase (422 <c>validation.failed</c> con <c>errors</c>). Todo es opcional: la pantalla guarda de a pedazos y <c>readiness</c> dice qué falta.</summary>
public sealed class UpdateCoordinadoraSettingsValidator : AbstractValidator<UpdateCoordinadoraSettingsCommand>
{
    public UpdateCoordinadoraSettingsValidator()
    {
        RuleFor(command => command.SenderName).MaximumLength(CoordinadoraSettings.NameMaxLength).OverridePropertyName("senderName");
        RuleFor(command => command.SenderAddress).MaximumLength(CoordinadoraSettings.AddressMaxLength).OverridePropertyName("senderAddress");
        RuleFor(command => command.SenderPhone).MaximumLength(CoordinadoraSettings.PhoneMaxLength).OverridePropertyName("senderPhone");
        RuleFor(command => command.AccountCode).MaximumLength(CoordinadoraSettings.CodeMaxLength).OverridePropertyName("accountCode");
        RuleFor(command => command.ProductCode).MaximumLength(CoordinadoraSettings.CodeMaxLength).OverridePropertyName("productCode");
        RuleFor(command => command.Div).MaximumLength(CoordinadoraSettings.CodeMaxLength).OverridePropertyName("div");
        RuleFor(command => command.LabelFormat).MaximumLength(CoordinadoraSettings.LabelFormatMaxLength).OverridePropertyName("labelFormat");
        RuleFor(command => command.ServiceLevel)
            .InclusiveBetween(0, CoordinadoraSettings.ServiceLevelMax)
            .When(command => command.ServiceLevel is not null)
            .OverridePropertyName("serviceLevel");
        RuleFor(command => command.PackagingType)
            .Must(value => value is CoordinadoraSettings.PackagingAutomatic or CoordinadoraSettings.PackagingGoods)
            .WithMessage("'packagingType' must be 0 (automatic) or 1 (goods).")
            .OverridePropertyName("packagingType");
        RuleFor(command => command.DeclaredValueMinimum)
            .GreaterThanOrEqualTo(0)
            .Must(value => value == decimal.Truncate(value))
            .WithMessage("'declaredValueMinimum' must be a whole amount in pesos.")
            .OverridePropertyName("declaredValueMinimum");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).OverridePropertyName("expectedVersion");
        When(command => command.DefaultPackage is not null, () =>
            RuleFor(command => command.DefaultPackage!).SetValidator(new CoordinadoraPackageInputValidator("defaultPackage")));
    }
}

/// <summary>Las reglas de <see cref="CoordinadoraPackage.Create"/> con el campo de cada una, para el paquete por defecto y el de un envío.
/// Interno a propósito: <c>AddValidatorsFromAssemblyContaining</c> no registra los internos, y éste no tiene constructor sin parámetros
/// (el prefijo del campo lo pone quien lo anida).</summary>
internal sealed class CoordinadoraPackageInputValidator : AbstractValidator<CoordinadoraPackageInput>
{
    public CoordinadoraPackageInputValidator(string prefix)
    {
        RuleFor(package => package.HeightCm).NotNull().InclusiveBetween(1, CoordinadoraPackage.MaxDimensionCm).OverridePropertyName($"{prefix}.heightCm");
        RuleFor(package => package.WidthCm).NotNull().InclusiveBetween(1, CoordinadoraPackage.MaxDimensionCm).OverridePropertyName($"{prefix}.widthCm");
        RuleFor(package => package.LengthCm).NotNull().InclusiveBetween(1, CoordinadoraPackage.MaxDimensionCm).OverridePropertyName($"{prefix}.lengthCm");
        RuleFor(package => package.WeightKg)
            .NotNull()
            .GreaterThan(0)
            .LessThanOrEqualTo(CoordinadoraPackage.MaxWeightKg)
            .Must(value => value is null || decimal.Round(value.Value, 2) == value.Value)
            .WithMessage($"'{prefix}.weightKg' admits two decimals at most.")
            .OverridePropertyName($"{prefix}.weightKg");
        RuleFor(package => package.Units)
            .InclusiveBetween(1, CoordinadoraPackage.MaxUnits)
            .When(package => package.Units is not null)
            .OverridePropertyName($"{prefix}.units");
    }

    /// <summary>El paquete validado del dominio, o <c>null</c> si no vino. Sin unidades, una.</summary>
    public static CoordinadoraPackage? ToPackage(CoordinadoraPackageInput? input) =>
        input is null
            ? null
            : CoordinadoraPackage.Create(input.HeightCm!.Value, input.WidthCm!.Value, input.LengthCm!.Value, input.WeightKg!.Value, input.Units ?? 1);
}

/// <summary>Spec 2026-11-21 §5.2. Mismo orden de comprobaciones que <see cref="GetCoordinadoraSettingsHandler"/>, más la versión. No llama
/// a Coordinadora: la cuenta y el producto se validan con la primera cotización. Audita la versión que quedó, nunca los valores.</summary>
public sealed class UpdateCoordinadoraSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICoordinadoraConnectionDirectory connections,
    ICoordinadoraSettingsRepository settingsRepository,
    ICoordinadoraGeographyLookup geography,
    ICoordinadoraUnitOfWork unitOfWork,
    ICoordinadoraAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateCoordinadoraSettingsCommand> validator) : ICommandHandler<UpdateCoordinadoraSettingsCommand, CoordinadoraSettingsDto>
{
    public async Task<CoordinadoraSettingsDto> HandleAsync(UpdateCoordinadoraSettingsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await CoordinadoraSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var connection = await CoordinadoraSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var settings = await CoordinadoraSettingsSupport.SettingsAsync(settingsRepository, command.TenantId, command.ConnectionId, cancellationToken);
        CoordinadoraSettingsSupport.EnsureVersion(settings, command.ExpectedVersion);

        settings.Update(new CoordinadoraSettingsInput(
            command.SenderName,
            command.SenderAddress,
            command.SenderPhone,
            command.SenderCityId,
            command.AccountCode,
            command.ProductCode,
            command.ServiceLevel,
            command.Div,
            CoordinadoraPackageInputValidator.ToPackage(command.DefaultPackage),
            command.PackagingType,
            command.DeclaredValueMinimum,
            command.CollectOnDelivery,
            command.LabelFormat));
        CoordinadoraSettingsSupport.Audit(auditRecorder, executionContext, settings, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await CoordinadoraSettingsSupport.ToDtoAsync(settings, connection, geography, cancellationToken);
    }
}
