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
    Guid CityId,
    Guid ClassificationId,
    bool WithRetention,
    bool VatSurplus) : ICommand<CustomerDto>, ICustomerWriteCommand;

// Las reglas viven en CustomerWriteRules y se incluyen, no se copian. Ver el hallazgo `D` alla.
public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator() => Include(new CustomerWriteRules());
}

public sealed class CreateCustomerHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
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

        // La clasificacion y la ciudad se resuelven **antes** de pedir el CUC: hacen falta su
        // prefijo y su codigo de departamento para armarlo, no son un pre-chequeo de integridad
        // redundante con la FK de base. Resolverlas antes tambien evita quemar un numero del
        // consecutivo cuando la referencia del cuerpo es invalida.
        var classification = await classificationRepository.FindAsync(
            command.TenantId, new ClientClassificationId(command.ClassificationId), cancellationToken)
            ?? throw new CustomersDomainException(
                "customers.customer.classification_not_found",
                "The client classification was not found in this tenant.");
        var city = await geographyLookup.FindCityAsync(command.CityId, cancellationToken)
            ?? throw new CustomersDomainException(
                "customers.customer.city_not_found",
                "The city was not found.");

        // El CUC se pide **despues** de resolver clasificacion y ciudad: cada llamada consume un
        // numero del consecutivo del tenant, y pedirlo antes quemaria uno por cada referencia mal
        // escrita. Un consecutivo con huecos no rompe nada, pero nadie tiene una buena respuesta
        // para el cliente que pregunta por que su primer codigo salto un numero.
        var sequence = await cucGenerator.NextAsync(command.TenantId, cancellationToken);
        var cuc = CucFormatter.Build(classification.Prefix, city.DepartmentDivipolaCode, sequence);
        var now = clock.UtcNow;

        var customer = Customer.Create(
            CustomerId.New(),
            command.TenantId,
            cuc,
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
            CustomerMapping.ToCommercialInfo(
                command.ClassificationId, command.WithRetention, command.VatSurplus),
            now);

        repository.Add(customer);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.created",
            customer.Id.ToString(),
            "success",
            now);

        // La unicidad de la identificacion y del CUC, y la existencia de la ciudad y la
        // clasificacion en la carrera entre esta lectura y el commit, NO se comprueban con un
        // segundo SELECT: el unico arbitro real son los indices y las FK de base, y
        // CustomersUnitOfWork traduce sus violaciones.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return customer.ToDto(city, classification);
    }
}
