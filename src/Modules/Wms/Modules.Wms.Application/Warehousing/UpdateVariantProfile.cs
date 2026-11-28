using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado (S25): las claves del mapa <c>errors</c> son <c>Tracking</c> y <c>Conversions[0].Code</c>, sin prefijo
/// <c>Request.</c>.</summary>
public sealed record UpdateVariantProfileCommand(
    Guid TenantId,
    Guid VariantId,
    string BaseUom,
    int BaseUomPrecision,
    string Tracking,
    bool TracksExpiry,
    decimal? WeightKg,
    decimal? VolumeM3,
    decimal? LengthCm,
    decimal? WidthCm,
    decimal? HeightCm,
    string? StorageClass,
    string? DisplayUom,
    IReadOnlyList<UomConversionRequest> Conversions,
    long Version) : ICommand<VariantProfileDto>
{
    public static UpdateVariantProfileCommand From(Guid tenantId, Guid variantId, UpdateVariantProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Lo ausente llega null aunque el record lo declare no nulo: se vuelve vacío para que lo atrape el validador.
        return new(
            tenantId,
            variantId,
            request.BaseUom ?? string.Empty,
            request.BaseUomPrecision,
            request.Tracking ?? string.Empty,
            request.TracksExpiry,
            request.WeightKg,
            request.VolumeM3,
            request.LengthCm,
            request.WidthCm,
            request.HeightCm,
            request.StorageClass,
            request.DisplayUom,
            (request.Conversions ?? []).Select(c => c ?? new UomConversionRequest(string.Empty, 0m, null)).ToList(),
            request.Version);
    }
}

/// <summary>
/// El dominio lanza <see cref="ArgumentException"/> con un código de presentación vacío o largo, y la base rechaza un número que no
/// cabe en su columna: ambos serían 500. El validador los cubre para que sean 422 <c>validation.failed</c> con el campo. Los topes
/// numéricos salen de la precisión de cada columna (<c>WmsDbContext</c>): peso (12,3), volumen (12,6), medidas (10,2), factor (18,6).
/// </summary>
public sealed class UpdateVariantProfileValidator : AbstractValidator<UpdateVariantProfileCommand>
{
    public const int MaxConversions = 50;
    public const int StorageClassMaxLength = 32;
    public const int BarcodeMaxLength = 64;

    public UpdateVariantProfileValidator()
    {
        RuleFor(command => command.Tracking)
            .Must(tracking => Enum.GetNames<TrackingPolicy>().Contains(tracking, StringComparer.Ordinal))
            .WithMessage($"The tracking must be one of: {string.Join(", ", Enum.GetNames<TrackingPolicy>())}.");
        RuleFor(command => command.BaseUomPrecision)
            .InclusiveBetween(UnitOfMeasure.MinPrecision, UnitOfMeasure.MaxPrecision)
            .WithMessage($"The precision must be between {UnitOfMeasure.MinPrecision} and {UnitOfMeasure.MaxPrecision}.");
        RuleFor(command => command.BaseUom)
            .Must(code => !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= UomCode.MaxLength)
            .WithMessage($"The base unit is required and has at most {UomCode.MaxLength} characters.");
        RuleFor(command => command.DisplayUom)
            .Must(code => code is null || string.IsNullOrWhiteSpace(code) || code.Trim().Length <= UomCode.MaxLength)
            .WithMessage($"The display unit has at most {UomCode.MaxLength} characters.");
        RuleFor(command => command.StorageClass)
            .MaximumLength(StorageClassMaxLength);

        RuleFor(command => command.WeightKg).InclusiveBetween(0m, 999_999_999m).When(command => command.WeightKg.HasValue);
        RuleFor(command => command.VolumeM3).InclusiveBetween(0m, 999_999m).When(command => command.VolumeM3.HasValue);
        RuleFor(command => command.LengthCm).InclusiveBetween(0m, 99_999_999m).When(command => command.LengthCm.HasValue);
        RuleFor(command => command.WidthCm).InclusiveBetween(0m, 99_999_999m).When(command => command.WidthCm.HasValue);
        RuleFor(command => command.HeightCm).InclusiveBetween(0m, 99_999_999m).When(command => command.HeightCm.HasValue);

        RuleFor(command => command.Conversions)
            .Must(conversions => conversions.Count <= MaxConversions)
            .WithMessage($"A variant has at most {MaxConversions} conversions.");
        RuleForEach(command => command.Conversions).ChildRules(conversion =>
        {
            conversion.RuleFor(item => item.Code)
                .Must(code => !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= UomConversion.CodeMaxLength)
                .WithMessage($"The conversion code is required and has at most {UomConversion.CodeMaxLength} characters.");
            // numeric(18,6): más de seis decimales se redondea a 0 al guardar y dispara CK_uom_conversions_factor (500).
            conversion.RuleFor(item => item.Factor)
                .GreaterThan(0m)
                .LessThan(1_000_000_000_000m)
                .Must(factor => decimal.Round(factor, 6) == factor)
                .WithMessage("The factor has at most 6 decimals.");
            conversion.RuleFor(item => item.Barcode)
                .MaximumLength(BarcodeMaxLength);
        });
    }
}

/// <summary>
/// Crea (<c>Version == 0</c> y sin fila) o actualiza el perfil de una variante, y deja los identificadores de las presentaciones
/// alineados con los códigos de barras de sus conversiones. Todo en una sola transacción con la auditoría.
/// </summary>
public sealed class UpdateVariantProfileHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IWmsCatalogReader catalog,
    IVariantProfileRepository profiles,
    IVariantStockProbe stockProbe,
    IUnitOfMeasureRepository unitsOfMeasure,
    IIdentifierRepository identifiers,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateVariantProfileCommand> validator)
    : ICommandHandler<UpdateVariantProfileCommand, VariantProfileDto>
{
    public async Task<VariantProfileDto> HandleAsync(UpdateVariantProfileCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.ProfileManage, ct: cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Catalog es la autoridad de lo que la variante es: un id que no conoce, o que es de otro tenant, no se perfila.
        var variant = (await catalog.GetVariantsAsync(command.TenantId, [command.VariantId], cancellationToken))
            .FirstOrDefault(found => found.VariantId == command.VariantId)
            ?? throw new WmsDomainException(WmsErrorCodes.ProfileVariantUnknown, "The variant does not exist in the catalog.");

        var baseUom = await unitsOfMeasure.FindAsync(command.TenantId, UomCode.Parse(command.BaseUom), cancellationToken)
            ?? throw new WmsDomainException(WmsErrorCodes.UomUnknown, "The base unit of measure does not exist.");
        var profile = await profiles.FindAsync(command.TenantId, command.VariantId, cancellationToken);

        // El perfil por defecto de un producto que se vende por peso, longitud, área o volumen nace con la precisión de su unidad de
        // venta (2) sobre la unidad base UND (0): sin esta excepción no podría guardarse tal como se lee. Quien ya tiene esa precisión
        // con la misma unidad base la conserva; subirla por encima de la de la unidad sigue siendo un error.
        // Sin fila, «lo actual» es el perfil por defecto que el GET devuelve: UND con la precisión de la unidad de venta.
        var currentBaseUom = profile?.BaseUom ?? UomCode.Parse("UND");
        var currentPrecision = profile?.BaseUomPrecision ?? VariantLogisticsProfile.DefaultPrecisionFor(variant.SalesUnit);
        var allowedPrecision = currentBaseUom == baseUom.Code
            ? Math.Max(baseUom.Precision, currentPrecision)
            : baseUom.Precision;
        if (command.BaseUomPrecision > allowedPrecision)
        {
            throw new WmsDomainException(
                WmsErrorCodes.UomPrecisionExceeded,
                $"The precision cannot exceed the {allowedPrecision} decimals of the unit '{baseUom.Code}'.");
        }

        UomCode? displayUom = null;
        if (!string.IsNullOrWhiteSpace(command.DisplayUom))
        {
            displayUom = (await unitsOfMeasure.FindAsync(command.TenantId, UomCode.Parse(command.DisplayUom), cancellationToken))?.Code
                ?? throw new WmsDomainException(WmsErrorCodes.UomUnknown, "The display unit of measure does not exist.");
        }

        var changes = new ProfileChanges(
            baseUom.Code,
            command.BaseUomPrecision,
            Enum.Parse<TrackingPolicy>(command.Tracking),
            command.TracksExpiry,
            command.WeightKg,
            command.VolumeM3,
            command.LengthCm,
            command.WidthCm,
            command.HeightCm,
            command.StorageClass,
            displayUom,
            command.Conversions.Select(conversion => (conversion.Code, conversion.Factor, conversion.Barcode)).ToList());

        // Sin fila la «versión actual» es 0: es lo que el GET devolvió para el default, y cualquier otra es una lectura vieja.
        WmsVersions.Ensure(profile?.Version ?? 0, command.Version);

        var hasStock = await stockProbe.HasStockAsync(command.TenantId, command.VariantId, cancellationToken);
        var now = clock.UtcNow;
        var changed = new Dictionary<string, string?> { ["variantId"] = command.VariantId.ToString() };
        string action;
        if (profile is null)
        {
            profile = VariantLogisticsProfile.Create(command.TenantId, command.VariantId, variant.ProductId, changes, now);
            profiles.Add(profile);
            identifiers.AddRange(Identifier.ForVariant(command.TenantId, command.VariantId, variant.Code, variant.Barcode, now));
            changed["code"] = variant.Code;
            action = ProfileCreated;
        }
        else
        {
            var before = (profile.BaseUom, profile.BaseUomPrecision, profile.Tracking, profile.TracksExpiry);
            profile.Update(changes, hasStock, now);
            if (before.BaseUom != profile.BaseUom)
            {
                changed["baseUom"] = profile.BaseUom.Value;
            }

            if (before.BaseUomPrecision != profile.BaseUomPrecision)
            {
                changed["baseUomPrecision"] = profile.BaseUomPrecision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (before.Tracking != profile.Tracking)
            {
                changed["tracking"] = profile.Tracking.ToString();
            }

            if (before.TracksExpiry != profile.TracksExpiry)
            {
                changed["tracksExpiry"] = profile.TracksExpiry ? "true" : "false";
            }

            action = ProfileUpdated;
        }

        await SyncPresentationIdentifiersAsync(command, profile, now, cancellationToken);

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            action,
            Resources.Profile,
            command.VariantId.ToString(),
            changed,
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return VariantProfileDtoMapper.ToDto(profile, variant, hasStock, stored: true);
    }

    /// <summary>Un identificador <c>Presentation</c> por cada conversión con código de barras. Sólo toca lo que cambió: borrar y volver
    /// a insertar el mismo valor en un mismo guardado choca con el índice único (<c>tenant_id, value</c>).</summary>
    private async Task SyncPresentationIdentifiersAsync(
        UpdateVariantProfileCommand command, VariantLogisticsProfile profile, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var wanted = profile.Conversions
            .Where(conversion => conversion.Barcode is not null)
            .Select(conversion => (Value: conversion.Barcode!, ConversionId: conversion.Id))
            .ToList();

        var current = (await identifiers.ListByTargetAsync(command.TenantId, command.VariantId, cancellationToken))
            .Where(identifier => identifier.Kind == IdentifierKind.Presentation)
            .ToList();

        identifiers.RemoveRange(current.Where(identifier =>
            !wanted.Any(w => w.Value == identifier.Value && w.ConversionId == identifier.ConversionId)));
        identifiers.AddRange(wanted
            .Where(w => !current.Any(identifier => identifier.Value == w.Value && identifier.ConversionId == w.ConversionId))
            .Select(w => Identifier.Create(
                Guid.CreateVersion7(),
                command.TenantId,
                IdentifierKind.Presentation,
                IdentifierSymbology.Gtin,
                w.Value,
                command.VariantId,
                w.ConversionId,
                isPrimary: false,
                now)));
    }
}
