using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado como <see cref="CreateOwnerCommand"/> (S25).</summary>
public sealed record UpdateOwnerCommand(
    Guid TenantId,
    Guid OwnerId,
    string Name,
    string? Identification,
    string? ContactEmail,
    bool IsActive,
    long Version) : ICommand<OwnerDto>
{
    public static UpdateOwnerCommand From(Guid tenantId, Guid ownerId, UpdateOwnerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(tenantId, ownerId, request.Name ?? string.Empty, request.Identification, request.ContactEmail, request.IsActive, request.Version);
    }
}

public sealed class UpdateOwnerValidator : AbstractValidator<UpdateOwnerCommand>
{
    public UpdateOwnerValidator()
    {
        RuleFor(command => command.Name).MustBeAnOwnerName();
        RuleFor(command => command.Identification).MustBeAnOwnerIdentification();
        // Vacío significa «sin correo» (el dominio lo vuelve null): sólo se valida cuando viene.
        RuleFor(command => command.ContactEmail)
            .MaximumLength(254)
            .EmailAddress()
            .When(command => !string.IsNullOrWhiteSpace(command.ContactEmail));
    }
}

public sealed class UpdateOwnerHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IOwnerRepository owners,
    IOwnerStockProbe stockProbe,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateOwnerCommand> validator)
    : ICommandHandler<UpdateOwnerCommand, OwnerDto>
{
    public async Task<OwnerDto> HandleAsync(UpdateOwnerCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.OwnerManage, ct: cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var owner = await owners.FindAsync(command.TenantId, new OwnerId(command.OwnerId), cancellationToken)
            ?? throw WmsNotFound.Owner(command.OwnerId);
        WmsVersions.Ensure(owner.Version, command.Version);

        // Un cliente con saldo no se desactiva: el stock quedaría a nombre de un dueño que ya no opera. El Self lo frena el agregado.
        if (owner.Kind == OwnerKind.Client
            && owner.IsActive
            && !command.IsActive
            && await stockProbe.HasStockAsync(command.TenantId, command.OwnerId, cancellationToken))
        {
            throw new WmsDomainException(WmsErrorCodes.OwnerHasStock, "The owner has stock and cannot be deactivated.");
        }

        var before = (owner.Name, owner.Identification, owner.ContactEmail, owner.IsActive);
        var now = clock.UtcNow;
        owner.Update(command.Name, command.Identification, command.ContactEmail, command.IsActive, now);

        // §10.3: el id del dueño va dentro de changedFields y, además, sólo lo que cambió.
        var changed = new Dictionary<string, string?> { ["ownerId"] = owner.Id.Value.ToString() };
        if (before.Name != owner.Name)
        {
            changed["name"] = owner.Name;
        }

        if (before.Identification != owner.Identification)
        {
            changed["identification"] = owner.Identification;
        }

        if (before.ContactEmail != owner.ContactEmail)
        {
            changed["contactEmail"] = owner.ContactEmail;
        }

        if (before.IsActive != owner.IsActive)
        {
            changed["isActive"] = owner.IsActive ? "true" : "false";
        }

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            OwnerUpdated,
            Resources.Owner,
            owner.Id.Value.ToString(),
            changed,
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return OwnerDtoMapper.ToDto(owner);
    }
}
