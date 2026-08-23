using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record CreateClientClassificationCommand(Guid TenantId, string Name, string Prefix)
    : ICommand<ClientClassificationDto>;

// El dominio hace cumplir las mismas reglas y tiraria un 422 con un solo codigo. El validador
// existe para que la respuesta lleve el mapa de errores por campo que ApiExceptionHandler arma
// desde ValidationException, que es lo que un formulario necesita para marcar el input culpable.
public sealed class CreateClientClassificationValidator
    : AbstractValidator<CreateClientClassificationCommand>
{
    public CreateClientClassificationValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(ClientClassification.NameMaxLength);
        RuleFor(command => command.Prefix)
            .NotEmpty()
            .MaximumLength(ClientClassification.PrefixMaxLength);
    }
}

public sealed class CreateClientClassificationHandler(
    IClientClassificationRepository repository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateClientClassificationCommand> validator)
    : ICommandHandler<CreateClientClassificationCommand, ClientClassificationDto>
{
    public async Task<ClientClassificationDto> HandleAsync(
        CreateClientClassificationCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, y no al reves. La politica del endpoint ya frena a quien
        // le falta el permiso, pero no al que lo tiene para otro tenant: a ese lo rechaza esta
        // revalidacion. Validando primero, ese llamador ajeno se lleva el mapa de errores por
        // campo antes de que nadie le diga que no. Mismo criterio que CreateTaxRateHandler.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.ClassificationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), command.TenantId, command.Name, command.Prefix, now);

        repository.Add(classification);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.classification.created",
            classification.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return classification.ToDto();
    }
}
