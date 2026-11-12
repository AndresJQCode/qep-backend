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
    Guid? CityId,
    string? Phone,
    bool IsPrincipal,
    string? Country = null,
    string? CityName = null) : ICommand<CustomerDto>, ICustomerAddressWriteCommand;

public sealed record UpdateCustomerAddressCommand(
    Guid TenantId,
    Guid CustomerId,
    Guid AddressId,
    string Name,
    string Address,
    Guid? CityId,
    string? Phone,
    bool IsPrincipal,
    string? Country = null,
    string? CityName = null) : ICommand<CustomerDto>, ICustomerAddressWriteCommand;

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

        // El pais es opcional y vacio es Colombia (lo que mandaban todos los cuerpos anteriores).
        // Presente, son dos letras ASCII: el dominio normaliza a mayusculas.
        RuleFor(command => command.Country)
            .Length(CustomerContactInfo.CountryLength)
            .Must(country => country!.Trim().All(char.IsAsciiLetter))
            .WithMessage(
                $"The country must be an ISO-3166-1 alpha-2 code ({CustomerContactInfo.CountryLength} letters).")
            .When(command => !string.IsNullOrWhiteSpace(command.Country));

        // Mismos dos carriles que el cliente (CustomerWriteRules), con el campo marcado segun el
        // carril: el formulario pinta un combobox o un input de texto segun el pais, y un error
        // sobre el campo que no esta en pantalla no se ve. Mezclarlos tambien se rechaza aca, por
        // campo, en vez de dejarlo caer al dominio como un 422 sin el mapa errors.
        RuleFor(command => command.CityId)
            .Must(cityId => cityId is { } value && value != Guid.Empty)
            .WithMessage("The city is required.")
            .When(IsColombian);
        RuleFor(command => command.CityName)
            .Must(string.IsNullOrWhiteSpace)
            .WithMessage("A Colombian address uses a city id, not a city name.")
            .When(IsColombian);
        RuleFor(command => command.CityName)
            .NotEmpty()
            .WithMessage("The city is required.")
            .When(command => !IsColombian(command));
        RuleFor(command => command.CityId)
            .Null()
            .WithMessage("An address outside Colombia uses a city name, not a city id.")
            .When(command => !IsColombian(command));
        RuleFor(command => command.CityName)
            .MaximumLength(CustomerContactInfo.CityNameMaxLength)
            .When(command => !string.IsNullOrWhiteSpace(command.CityName));
        RuleFor(command => command.Phone)
            .MaximumLength(CustomerAddress.PhoneMaxLength);
    }

    private static bool IsColombian(ICustomerAddressWriteCommand command) =>
        string.IsNullOrWhiteSpace(command.Country)
        || string.Equals(
            command.Country.Trim(),
            Customer.ColombiaCountryCode,
            StringComparison.OrdinalIgnoreCase);
}

public interface ICustomerAddressWriteCommand
{
    string Name { get; }

    string Address { get; }

    string? Country { get; }

    Guid? CityId { get; }

    string? CityName { get; }

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
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddCustomerAddressCommand> validator)
    : ICommandHandler<AddCustomerAddressCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        AddCustomerAddressCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, como en CreateCustomerHandler: quien tiene el permiso para
        // otro tenant no debe recibir el mapa de errores por campo antes de que le digan que no.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext,
            command.TenantId, command.CustomerId, command.CityId, cancellationToken);

        customer.AddAddress(
            new CustomerAddressDetails
            {
                Name = command.Name,
                Address = command.Address,
                Country = string.IsNullOrWhiteSpace(command.Country)
                    ? Customer.ColombiaCountryCode
                    : command.Country,
                CityId = command.CityId,
                CityName = command.CityName,
                Phone = command.Phone
            },
            command.IsPrincipal,
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}

public sealed class UpdateCustomerAddressHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCustomerAddressCommand> validator)
    : ICommandHandler<UpdateCustomerAddressCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        UpdateCustomerAddressCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, como en CreateCustomerHandler: quien tiene el permiso para
        // otro tenant no debe recibir el mapa de errores por campo antes de que le digan que no.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext,
            command.TenantId, command.CustomerId, command.CityId, cancellationToken);

        customer.UpdateAddress(
            new CustomerAddressId(command.AddressId),
            new CustomerAddressDetails
            {
                Name = command.Name,
                Address = command.Address,
                Country = string.IsNullOrWhiteSpace(command.Country)
                    ? Customer.ColombiaCountryCode
                    : command.Country,
                CityId = command.CityId,
                CityName = command.CityName,
                Phone = command.Phone
            },
            command.IsPrincipal,
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}

public sealed class RemoveCustomerAddressHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
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
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}

public sealed class MakeCustomerAddressPrincipalHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
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
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
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
