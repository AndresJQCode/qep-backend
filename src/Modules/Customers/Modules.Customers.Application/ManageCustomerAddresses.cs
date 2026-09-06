using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// La libreta de direcciones de un cliente (CLI-DIR-01): alta, edición, baja y "marcar como
/// principal". Cuatro comandos y no un PUT con la lista entera, por la misma razón por la que las
/// líneas de una cotización son sub-recursos: dos personas editando el mismo cliente no se pisan
/// la libreta completa, y cada operación tiene su propia regla —quitar la principal no se puede,
/// marcar otra desmarca la anterior— que un reemplazo masivo escondería.
///
/// Todas devuelven el <c>CustomerDto</c> completo: quien las llama está mirando la ficha del
/// cliente y necesita la lista al día, no sólo la fila que tocó.
/// </summary>
public sealed record AddCustomerAddressCommand(
    Guid TenantId,
    Guid CustomerId,
    string Name,
    string Address,
    Guid CityId,
    string? Phone,
    bool IsPrincipal) : ICommand<CustomerDto>, ICustomerAddressWriteCommand;

public sealed record UpdateCustomerAddressCommand(
    Guid TenantId,
    Guid CustomerId,
    Guid AddressId,
    string Name,
    string Address,
    Guid CityId,
    string? Phone,
    bool IsPrincipal) : ICommand<CustomerDto>, ICustomerAddressWriteCommand;

public sealed record RemoveCustomerAddressCommand(
    Guid TenantId,
    Guid CustomerId,
    Guid AddressId) : ICommand<CustomerDto>;

public sealed record MakeCustomerAddressPrincipalCommand(
    Guid TenantId,
    Guid CustomerId,
    Guid AddressId) : ICommand<CustomerDto>;

/// <summary>Las reglas de texto libre de una dirección, compartidas por el alta y la edición —
/// incluidas, no copiadas, mismo criterio que <c>CustomerWriteRules</c>. El dominio valida lo
/// mismo; esto es lo que convierte el 422 en un error **por campo** que el formulario puede
/// pintar donde corresponde.</summary>
public sealed class CustomerAddressWriteRules : AbstractValidator<ICustomerAddressWriteCommand>
{
    public CustomerAddressWriteRules()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(CustomerAddress.NameMaxLength);
        RuleFor(command => command.Address)
            .NotEmpty()
            .MaximumLength(CustomerAddress.AddressMaxLength);
        RuleFor(command => command.CityId).NotEmpty();
        RuleFor(command => command.Phone)
            .MaximumLength(CustomerAddress.PhoneMaxLength);
    }
}

public interface ICustomerAddressWriteCommand
{
    string Name { get; }

    string Address { get; }

    Guid CityId { get; }

    string? Phone { get; }
}

public sealed class AddCustomerAddressValidator : AbstractValidator<AddCustomerAddressCommand>
{
    public AddCustomerAddressValidator() => Include(new CustomerAddressWriteRules());
}

public sealed class UpdateCustomerAddressValidator
    : AbstractValidator<UpdateCustomerAddressCommand>
{
    public UpdateCustomerAddressValidator() => Include(new CustomerAddressWriteRules());
}

public sealed class AddCustomerAddressHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<AddCustomerAddressCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        AddCustomerAddressCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext,
            command.TenantId, command.CustomerId, command.CityId, cancellationToken);

        customer.AddAddress(
            new CustomerAddressDetails
            {
                Name = command.Name,
                Address = command.Address,
                CityId = command.CityId,
                Phone = command.Phone
            },
            command.IsPrincipal,
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, cancellationToken);
    }
}

public sealed class UpdateCustomerAddressHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<UpdateCustomerAddressCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        UpdateCustomerAddressCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext,
            command.TenantId, command.CustomerId, command.CityId, cancellationToken);

        customer.UpdateAddress(
            new CustomerAddressId(command.AddressId),
            new CustomerAddressDetails
            {
                Name = command.Name,
                Address = command.Address,
                CityId = command.CityId,
                Phone = command.Phone
            },
            command.IsPrincipal,
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, cancellationToken);
    }
}

public sealed class RemoveCustomerAddressHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RemoveCustomerAddressCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        RemoveCustomerAddressCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext,
            command.TenantId, command.CustomerId, cityId: null, cancellationToken);

        customer.RemoveAddress(new CustomerAddressId(command.AddressId), clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, cancellationToken);
    }
}

public sealed class MakeCustomerAddressPrincipalHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<MakeCustomerAddressPrincipalCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        MakeCustomerAddressPrincipalCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext,
            command.TenantId, command.CustomerId, cityId: null, cancellationToken);

        customer.MakeAddressPrincipal(new CustomerAddressId(command.AddressId), clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, cancellationToken);
    }
}

/// <summary>Lo que los cuatro handlers hacen igual: revalidar tenant y permiso, traer el cliente
/// y —cuando el comando trae una— comprobar que la ciudad exista antes de tocar el agregado.</summary>
internal static class CustomerAddressWorkflow
{
    public static async Task<Domain.Customer> LoadAsync(
        ICustomerRepository repository,
        ICustomerGeographyLookup geographyLookup,
        IExecutionContext executionContext,
        Guid tenantId,
        Guid customerId,
        Guid? cityId,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, tenantId, CustomersPermissions.CustomerManage);

        var customer = await repository.FindAsync(
            tenantId, new CustomerId(customerId), cancellationToken)
            ?? throw CustomerNotFound.For(customerId);

        // La FK de base garantiza la ciudad, pero recién en el SaveChanges: comprobarla acá deja
        // un 422 con código de dominio en vez de una violación de FK que no dice qué corregir.
        if (cityId is { } city)
        {
            _ = await geographyLookup.FindCityAsync(city, cancellationToken)
                ?? throw new CustomersDomainException(
                    "customers.customer.city_not_found", "The city was not found.");
        }

        return customer;
    }
}
