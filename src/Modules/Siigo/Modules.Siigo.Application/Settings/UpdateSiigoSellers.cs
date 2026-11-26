using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary>Un asesor (membresía) y el usuario de Siigo vinculado; <c>SiigoUserId</c> null es «sin vincular» y no se guarda.</summary>
public sealed record SiigoSellerLinkInput(Guid MembershipId, string? SiigoUserId, string? SiigoUserName);

/// <summary><c>PUT …/sellers</c> (<c>siigo.settings.manage</c>): los vínculos de vendedor del paso entero (spec 2026-11-20 §4.2).</summary>
public sealed record UpdateSiigoSellersCommand(
    Guid TenantId,
    Guid ConnectionId,
    IReadOnlyList<SiigoSellerLinkInput>? Sellers,
    long ExpectedVersion) : ICommand<SiigoSettingsDto>;

/// <summary>La forma del cuerpo: una membresía no se repite, y un usuario de Siigo lleva su nombre (la foto que muestra un vínculo cuyo
/// usuario ya no está activo). Un vendedor nulo falla en su propio campo.</summary>
public sealed class UpdateSiigoSellersValidator : AbstractValidator<UpdateSiigoSellersCommand>
{
    public UpdateSiigoSellersValidator()
    {
        RuleFor(command => command.Sellers)
            .NotNull()
            .Must(sellers => SiigoSettingsSupport.HasNoRepeated(sellers!, seller => seller.MembershipId))
            .When(command => command.Sellers is not null)
            .WithMessage("A member cannot repeat.")
            .OverridePropertyName("sellers");
        RuleForEach(command => command.Sellers).NotNull().ChildRules(seller =>
        {
            seller.RuleFor(value => value.SiigoUserId).MaximumLength(SiigoSellerLink.UserIdMaxLength).OverridePropertyName("siigoUserId");
            seller.RuleFor(value => value.SiigoUserName)
                .NotEmpty()
                .When(value => !string.IsNullOrWhiteSpace(value.SiigoUserId))
                .OverridePropertyName("siigoUserName");
            seller.RuleFor(value => value.SiigoUserName).MaximumLength(SiigoSellerLink.UserNameMaxLength).OverridePropertyName("siigoUserName");
        }).OverridePropertyName("sellers");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).OverridePropertyName("expectedVersion");
    }
}

/// <summary>Spec 2026-11-20 §4.2. Mismo orden de comprobaciones que <see cref="UpdateSiigoPaymentsHandler"/>. Una membresía tiene que
/// ser del tenant: activa, o con un vínculo que ya existía (el de alguien que salió se puede conservar o quitar).</summary>
public sealed class UpdateSiigoSellersHandler(
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
    IValidator<UpdateSiigoSellersCommand> validator) : ICommandHandler<UpdateSiigoSellersCommand, SiigoSettingsDto>
{
    public async Task<SiigoSettingsDto> HandleAsync(UpdateSiigoSellersCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var connection = await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var settings = await SiigoSettingsSupport.SettingsAsync(settingsRepository, command.TenantId, command.ConnectionId, cancellationToken);
        SiigoSettingsSupport.EnsureVersion(settings, command.ExpectedVersion);

        var linked = command.Sellers!.Where(seller => !string.IsNullOrWhiteSpace(seller.SiigoUserId)).ToList();
        var known = (await members.ListAsync(
                command.TenantId, [.. settings.SellerLinks.Select(link => link.MembershipId)], cancellationToken))
            .Select(member => member.MembershipId)
            .ToHashSet();
        // Un vínculo que ya existe se conoce aunque la membresía se haya purgado: se puede conservar o quitar.
        known.UnionWith(settings.SellerLinks.Select(link => link.MembershipId));
        if (linked.FirstOrDefault(seller => !known.Contains(seller.MembershipId)) is { } stranger)
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.SettingsMemberNotFound, $"The member {stranger.MembershipId} does not belong to the tenant.");
        }

        settings.ReplaceSellerLinks([.. linked.Select(seller => new SiigoSellerLink(seller.MembershipId, seller.SiigoUserId!, seller.SiigoUserName!))]);
        SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoSettingsSteps.Sellers, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SiigoSettingsSupport.ToDtoAsync(settings, connection, companies, taxRates, members, cancellationToken);
    }
}
