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

        var classification = await repository.FindAsync(
            command.TenantId, new ClientClassificationId(command.ClassificationId), cancellationToken)
            ?? throw ClientClassificationNotFound.For(command.ClassificationId);

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
