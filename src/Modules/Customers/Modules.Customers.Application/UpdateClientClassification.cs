using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record UpdateClientClassificationCommand(
    Guid TenantId,
    Guid ClassificationId,
    string Name,
    string Prefix) : ICommand<ClientClassificationDto>;

public sealed class UpdateClientClassificationValidator
    : AbstractValidator<UpdateClientClassificationCommand>
{
    public UpdateClientClassificationValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(ClientClassification.NameMaxLength);
        RuleFor(command => command.Prefix)
            .NotEmpty()
            .MaximumLength(ClientClassification.PrefixMaxLength);
    }
}

public sealed class UpdateClientClassificationHandler(
    IClientClassificationRepository repository,
    ICustomerRepository customerRepository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateClientClassificationCommand> validator)
    : ICommandHandler<UpdateClientClassificationCommand, ClientClassificationDto>
{
    public async Task<ClientClassificationDto> HandleAsync(
        UpdateClientClassificationCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razon en CreateClientClassificationHandler.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.ClassificationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var classificationId = new ClientClassificationId(command.ClassificationId);
        var classification = await repository.FindAsync(
            command.TenantId, classificationId, cancellationToken)
            ?? throw ClientClassificationNotFound.For(command.ClassificationId);

        // Regla de negocio confirmada: una clasificacion en uso no se puede editar, igual que no
        // se puede borrar ni inactivar. El prefijo del CUC de un cliente se congela al
        // asignarse — reescribirlo aca por detras, sin que el cliente cambie de clasificacion,
        // lo dejaria desalineado con lo que esta clasificacion dice ser hoy.
        if (await customerRepository.AnyWithClassificationAsync(
                command.TenantId, classificationId, cancellationToken))
        {
            throw new CustomersDomainException(
                "customers.classification.in_use",
                "The client classification cannot be updated because at least one customer " +
                "uses it.");
        }

        var now = clock.UtcNow;
        classification.Update(command.Name, command.Prefix, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.classification.updated",
            classification.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return classification.ToDto();
    }
}
