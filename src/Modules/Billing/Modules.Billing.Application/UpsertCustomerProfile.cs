using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>El canal y el encargado van como texto y Guid del cable; el validador garantiza que se pueden convertir.</summary>
public sealed record UpsertCustomerProfileCommand(Guid TenantId, Guid CustomerId, string Channel, Guid AssigneeMemberId)
    : ICommand<ProfileDto>
{
    public static UpsertCustomerProfileCommand From(Guid tenantId, Guid customerId, ProfileInput input) =>
        new(tenantId, customerId, input.Channel ?? string.Empty, input.AssigneeMemberId ?? Guid.Empty);
}

public sealed class UpsertCustomerProfileValidator : AbstractValidator<UpsertCustomerProfileCommand>
{
    public UpsertCustomerProfileValidator()
    {
        RuleFor(command => command.Channel).MustBeABillingChannel();
        RuleFor(command => command.AssigneeMemberId).NotEqual(Guid.Empty);
    }
}

/// <summary>
/// Crea o reemplaza el perfil (spec §4: sin <c>If-Match</c>, la última escritura gana, como los ajustes de cotización). Las
/// validaciones corren en el orden del contrato: el cliente existe, el canal es alcanzable, el encargado puede gestionar.
/// </summary>
public sealed class UpsertCustomerProfileHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IBillingCustomerLookup customers,
    IBillingMemberDirectory members,
    ICustomerBillingProfileRepository profiles,
    IBillingUnitOfWork unitOfWork,
    IBillingAuditPublisher auditPublisher,
    IClock clock,
    IValidator<UpsertCustomerProfileCommand> validator)
    : ICommandHandler<UpsertCustomerProfileCommand, ProfileDto>
{
    private const string ChannelField = "channel";
    private const string AssigneeField = "assigneeMemberId";

    public async Task<ProfileDto> HandleAsync(UpsertCustomerProfileCommand command, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, BillingPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // La búsqueda va acotada al tenant de la ruta: un cliente de otro tenant llega como «no existe».
        var customer = await customers.FindAsync(command.TenantId, command.CustomerId, cancellationToken)
            ?? throw new BillingDomainException(
                BillingErrorCodes.CustomerNotFound,
                $"Customer '{command.CustomerId}' was not found.");
        if (!ProfileRules.TryParseChannel(command.Channel, out var channel))
        {
            // El validador ya garantizó el nombre del canal: sólo se llega acá si alguien lo quitó del contenedor.
            throw new InvalidOperationException($"Channel '{command.Channel}' passed validation but is not a billing channel.");
        }

        ProfileRules.EnsureChannelIsReachable(channel, customer);

        var found = await members.FindAsync(command.TenantId, [command.AssigneeMemberId], cancellationToken);
        var assignee = found.Count > 0 ? found[0] : null;
        ProfileRules.EnsureAssigneeCanManage(assignee);

        var now = clock.UtcNow;
        var profile = await profiles.FindAsync(command.TenantId, command.CustomerId, cancellationToken);
        var changedFields = new List<string>(2);
        if (profile is null)
        {
            profile = CustomerBillingProfile.Create(command.TenantId, command.CustomerId, channel, command.AssigneeMemberId, now);
            profiles.Add(profile);
            changedFields.AddRange([ChannelField, AssigneeField]);
        }
        else
        {
            if (profile.Channel != channel)
            {
                changedFields.Add(ChannelField);
            }

            if (profile.AssigneeMemberId != command.AssigneeMemberId)
            {
                changedFields.Add(AssigneeField);
            }

            profile.Change(channel, command.AssigneeMemberId, now);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            BillingAuditActions.ProfileUpdated,
            BillingAuditActions.Resources.Profile,
            command.CustomerId.ToString(),
            "success",
            changedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return BillingMapping.ToProfileDto(profile, BillingMapping.ToMemberRefDto(command.AssigneeMemberId, assignee));
    }
}
