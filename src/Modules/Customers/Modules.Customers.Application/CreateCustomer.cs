using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record CreateCustomerCommand(
    Guid TenantId,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Department,
    string? City,
    string? Classification,
    Guid? PriceListId,
    bool WithRetention) : ICommand<CustomerDto>, ICustomerWriteCommand;

// Las reglas viven en CustomerWriteRules y se incluyen, no se copian. Ver el hallazgo `D` alla.
public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator() => Include(new CustomerWriteRules());
}

public sealed class CreateCustomerHandler(
    ICustomerRepository repository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    ICucGenerator cucGenerator,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateCustomerCommand> validator)
    : ICommandHandler<CreateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        CreateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, y no al reves. La politica del endpoint ya frena a quien le
        // falta el permiso, pero no al que lo tiene para otro tenant: a ese lo rechaza esta
        // revalidacion. Validando primero, ese llamador ajeno se lleva el mapa de errores por
        // campo —la forma del contrato— antes de que nadie le diga que no. Lo encontro la revision
        // de riesgo de CAT-02.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // El CUC se pide **despues** de validar: cada llamada consume un numero del consecutivo
        // del tenant, y pedirlo antes quemaria uno por cada cuerpo mal escrito. Un consecutivo con
        // huecos no rompe nada, pero nadie tiene una buena respuesta para el cliente que pregunta
        // por que su primer codigo es el 47.
        var cuc = await cucGenerator.NextAsync(command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        var customer = Customer.Create(
            CustomerId.New(),
            command.TenantId,
            cuc,
            command.Name,
            CustomerMapping.ToIdentification(
                command.IdentificationType, command.IdentificationNumber),
            new CustomerContactInfo
            {
                Phone = command.Phone,
                Email = command.Email,
                Address = command.Address,
                Department = command.Department,
                City = command.City
            },
            CustomerMapping.ToCommercialInfo(
                command.Classification, command.PriceListId, command.WithRetention),
            now);

        repository.Add(customer);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.created",
            customer.Id.ToString(),
            "success",
            now);

        // La unicidad de la identificacion NO se comprueba con un SELECT previo: entre la consulta
        // y el commit cabe otra transaccion, y el unico arbitro real es
        // IX_customers_tenant_identification. La violacion la traduce CustomersUnitOfWork.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }
}
