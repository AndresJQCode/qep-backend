using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary>Un IVA de QEP (por porcentaje entero) y el impuesto de Siigo que lo representa; <c>null</c> es «sin vincular».</summary>
public sealed record SiigoVatLinkInput(int TaxPercentage, string? SiigoTaxId);

/// <summary>Una retención de QEP (la tasa del catálogo) y el impuesto de Siigo que la representa; <c>null</c> es «sin vincular».</summary>
public sealed record SiigoWithholdingLinkInput(Guid TaxRateId, string? SiigoTaxId);

/// <summary><c>PUT …/taxes</c> (<c>siigo.settings.manage</c>). La lista vuelve entera y en orden: lo que no viene se quita.</summary>
public sealed record UpdateSiigoTaxesCommand(
    Guid TenantId,
    Guid ConnectionId,
    IReadOnlyList<SiigoVatLinkInput>? Vat,
    IReadOnlyList<SiigoWithholdingLinkInput>? Withholdings,
    long ExpectedVersion) : ICommand<SiigoSettingsDto>;

/// <summary>La forma del cuerpo: porcentajes de 0 a 100 sin repetir, tasas sin repetir y ids de Siigo del largo de la columna. Un elemento
/// nulo de las listas falla en su propio campo.</summary>
public sealed class UpdateSiigoTaxesValidator : AbstractValidator<UpdateSiigoTaxesCommand>
{
    public UpdateSiigoTaxesValidator()
    {
        RuleFor(command => command.Vat)
            .NotNull()
            .Must(vat => SiigoSettingsSupport.HasNoRepeated(vat!, link => link.TaxPercentage))
            .When(command => command.Vat is not null)
            .WithMessage("A VAT percentage cannot repeat.")
            .OverridePropertyName("vat");
        RuleForEach(command => command.Vat).NotNull().ChildRules(link =>
        {
            link.RuleFor(value => value.TaxPercentage).InclusiveBetween(0, 100).OverridePropertyName("taxPercentage");
            link.RuleFor(value => value.SiigoTaxId).MaximumLength(SiigoTaxLink.TaxIdMaxLength).OverridePropertyName("siigoTaxId");
        }).OverridePropertyName("vat");
        RuleFor(command => command.Withholdings)
            .NotNull()
            .Must(rates => SiigoSettingsSupport.HasNoRepeated(rates!, link => link.TaxRateId))
            .When(command => command.Withholdings is not null)
            .WithMessage("A withholding rate cannot repeat.")
            .OverridePropertyName("withholdings");
        RuleForEach(command => command.Withholdings).NotNull().ChildRules(link =>
        {
            link.RuleFor(value => value.TaxRateId).NotEmpty().OverridePropertyName("taxRateId");
            link.RuleFor(value => value.SiigoTaxId).MaximumLength(SiigoTaxLink.TaxIdMaxLength).OverridePropertyName("siigoTaxId");
        }).OverridePropertyName("withholdings");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).OverridePropertyName("expectedVersion");
    }
}

/// <summary>Spec 2026-11-18 §5.3, paso 4. Mismo orden de comprobaciones que <see cref="AssignSiigoCompanyHandler"/>. Siigo no recibe
/// porcentaje ni valor, sólo el id de un impuesto de <c>GET /v1/taxes</c>, y puede haber varios activos con el mismo tipo y porcentaje:
/// el vínculo lo elige una persona y acá no se adivina ni se revalida contra Siigo.</summary>
public sealed class UpdateSiigoTaxesHandler(
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
    IValidator<UpdateSiigoTaxesCommand> validator) : ICommandHandler<UpdateSiigoTaxesCommand, SiigoSettingsDto>
{
    public async Task<SiigoSettingsDto> HandleAsync(UpdateSiigoTaxesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var connection = await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var settings = await SiigoSettingsSupport.SettingsAsync(settingsRepository, command.TenantId, command.ConnectionId, cancellationToken);
        SiigoSettingsSupport.EnsureVersion(settings, command.ExpectedVersion);

        settings.ReplaceTaxLinks([.. command.Vat!.Select(link => new SiigoTaxLink(link.TaxPercentage, link.SiigoTaxId))]);
        // Las retenciones de una empresa que no se ofrecen en la pantalla (otra empresa antes) no se tocan: sólo se reemplazan las que
        // la pantalla mostró, que son las de la empresa actual. Lo que no viene de esas se conserva.
        settings.ReplaceWithholdingLinks(MergeWithholdings(settings, command.Withholdings!, await OfferedRatesAsync(settings, cancellationToken)));
        SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoSettingsSteps.Taxes, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SiigoSettingsSupport.ToDtoAsync(settings, connection, companies, taxRates, members, cancellationToken);
    }

    private async Task<HashSet<Guid>> OfferedRatesAsync(SiigoSettings settings, CancellationToken cancellationToken) =>
        settings.CompanyId is { } companyId
            ? (await taxRates.ListWithholdingRatesAsync(settings.TenantId, companyId, cancellationToken)).Select(rate => rate.TaxRateId).ToHashSet()
            : [];

    private static SiigoWithholdingLink[] MergeWithholdings(
        SiigoSettings settings, IReadOnlyList<SiigoWithholdingLinkInput> incoming, HashSet<Guid> offered) =>
    [
        .. incoming.Select(link => new SiigoWithholdingLink(link.TaxRateId, link.SiigoTaxId)),
        .. settings.WithholdingLinks
            .Where(link => !offered.Contains(link.TaxRateId) && incoming.All(value => value.TaxRateId != link.TaxRateId))
            .Select(link => new SiigoWithholdingLink(link.TaxRateId, link.SiigoTaxId)),
    ];
}
