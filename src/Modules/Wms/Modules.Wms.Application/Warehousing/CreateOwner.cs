using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado (S25): la clave del mapa <c>errors</c> es <c>ContactEmail</c> y no <c>Request.ContactEmail</c>.</summary>
public sealed record CreateOwnerCommand(Guid TenantId, string Name, string? Identification, string? ContactEmail) : ICommand<OwnerDto>
{
    public static CreateOwnerCommand From(Guid tenantId, CreateOwnerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Un nombre ausente llega null aunque el record lo declare no nulo: se vuelve vacío para que lo atrape el validador.
        return new(tenantId, request.Name ?? string.Empty, request.Identification, request.ContactEmail);
    }
}

/// <summary>El dominio lanza <see cref="ArgumentException"/> con un nombre vacío o largo, y eso sería un 500: el validador cubre
/// esos casos para que sean 422 <c>validation.failed</c> con el campo.</summary>
public sealed class CreateOwnerValidator : AbstractValidator<CreateOwnerCommand>
{
    public CreateOwnerValidator()
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

internal static class OwnerRules
{
    public static IRuleBuilderOptions<T, string> MustBeAnOwnerName<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= Owner.NameMaxLength)
            .WithMessage($"The name is required and has at most {Owner.NameMaxLength} characters.");

    public static IRuleBuilderOptions<T, string?> MustBeAnOwnerIdentification<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(identification => identification is null || identification.Trim().Length <= Owner.IdentificationMaxLength)
            .WithMessage($"The identification has at most {Owner.IdentificationMaxLength} characters.");
}

public sealed class CreateOwnerHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IOwnerRepository owners,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<CreateOwnerCommand> validator)
    : ICommandHandler<CreateOwnerCommand, OwnerDto>
{
    public async Task<OwnerDto> HandleAsync(CreateOwnerCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.OwnerManage, ct: cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var owner = Owner.CreateClient(OwnerId.New(), command.TenantId, command.Name, command.Identification, command.ContactEmail, now);
        owners.Add(owner);

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            OwnerCreated,
            Resources.Owner,
            owner.Id.Value.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = owner.Id.Value.ToString(),
                ["name"] = owner.Name,
                ["identification"] = owner.Identification,
            },
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return OwnerDtoMapper.ToDto(owner);
    }
}
