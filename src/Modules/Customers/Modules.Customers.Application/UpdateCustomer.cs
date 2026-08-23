using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record UpdateCustomerCommand(
    Guid TenantId,
    Guid CustomerId,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    Guid CityId,
    Guid ClassificationId,
    bool WithRetention) : ICommand<CustomerDto>, ICustomerWriteCommand;

// Mismas reglas que el POST, por inclusion y no por copia. Ver CustomerWriteRules.
public sealed class UpdateCustomerValidator : AbstractValidator<UpdateCustomerCommand>
{
    public UpdateCustomerValidator() => Include(new CustomerWriteRules());
}

public sealed class UpdateCustomerHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCustomerCommand> validator)
    : ICommandHandler<UpdateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        UpdateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razon en CreateCustomerHandler.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var customer = await repository.FindAsync(
            command.TenantId,
            new CustomerId(command.CustomerId),
            cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);

        // Se resuelven aca, no solo por la FK de base: la respuesta del PUT lleva la ciudad, el
        // departamento y la clasificacion resueltos, igual que el detalle. No es el CUC —ese no
        // cambia nunca— asi que este chequeo es exclusivamente para poder devolver el DTO.
        var classification = await classificationRepository.FindAsync(
            command.TenantId, new ClientClassificationId(command.ClassificationId), cancellationToken)
            ?? throw new CustomersDomainException(
                "customers.customer.classification_not_found",
                "The client classification was not found in this tenant.");
        var city = await geographyLookup.FindCityAsync(command.CityId, cancellationToken)
            ?? throw new CustomersDomainException(
                "customers.customer.city_not_found",
                "The city was not found.");

        var now = clock.UtcNow;

        // Los opcionales se mandan siempre, incluidos los null: el PUT reemplaza el recurso
        // entero, asi que un campo ausente se limpia. El CUC no esta en la firma porque no viaja
        // en el request — lo emite el backend al crear y no se edita nunca.
        customer.Update(
            command.Name,
            command.CityId,
            CustomerMapping.ToIdentification(
                command.IdentificationType, command.IdentificationNumber),
            new CustomerContactInfo
            {
                Phone = command.Phone,
                Email = command.Email,
                Address = command.Address
            },
            CustomerMapping.ToCommercialInfo(command.ClassificationId, command.WithRetention),
            now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.updated",
            customer.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return customer.ToDto(city, classification);
    }
}
