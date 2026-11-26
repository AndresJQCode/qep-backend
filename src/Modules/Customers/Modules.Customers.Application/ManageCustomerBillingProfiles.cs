using BuildingBlocks.Application;
using BuildingBlocks.Domain.Identification;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// La libreta de perfiles de facturación de un cliente (spec 2026-11-24 §3), gemela de la libreta de direcciones: alta,
/// edición, baja y «marcar como principal», sin <c>If-Match</c> y sin auditoría (paridad con direcciones, decisión del
/// owner). Las cuatro responden el <c>CustomerDto</c> completo, porque quien las llama repinta la lista entera.
/// </summary>
public sealed record AddCustomerBillingProfileCommand(
    Guid TenantId,
    Guid CustomerId,
    string? Name,
    string? PersonType,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Country,
    Guid? CityId,
    string? CityName,
    Guid? RegionId,
    IReadOnlyList<Guid>? WithholdingTaxRateIds,
    bool VatSurplus,
    bool IsPrincipal) : ICommand<CustomerDto>, ICustomerBillingProfileWriteCommand;

public sealed record UpdateCustomerBillingProfileCommand(
    Guid TenantId,
    Guid CustomerId,
    Guid ProfileId,
    string? Name,
    string? PersonType,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Country,
    Guid? CityId,
    string? CityName,
    Guid? RegionId,
    IReadOnlyList<Guid>? WithholdingTaxRateIds,
    bool VatSurplus,
    bool IsPrincipal) : ICommand<CustomerDto>, ICustomerBillingProfileWriteCommand;

public sealed record RemoveCustomerBillingProfileCommand(
    Guid TenantId,
    Guid CustomerId,
    Guid ProfileId) : ICommand<CustomerDto>;

public sealed record MakeCustomerBillingProfilePrincipalCommand(
    Guid TenantId,
    Guid CustomerId,
    Guid ProfileId) : ICommand<CustomerDto>;

public interface ICustomerBillingProfileWriteCommand
{
    string? Name { get; }

    string? PersonType { get; }

    string? IdentificationType { get; }

    string? IdentificationNumber { get; }

    string? Phone { get; }

    string? Email { get; }

    string? Address { get; }

    string? Country { get; }

    Guid? CityId { get; }

    string? CityName { get; }

    Guid? RegionId { get; }

    IReadOnlyList<Guid>? WithholdingTaxRateIds { get; }

    bool VatSurplus { get; }
}

/// <summary>Las reglas de forma de un perfil, compartidas por el alta y la edición: convierten el 422 en un error por
/// campo. Lo que cruza dos campos (el tipo de documento contra el país, la jurídica sin NIT) lo dice el dominio con su
/// código y su mapa <c>errors</c>.</summary>
public sealed class CustomerBillingProfileWriteRules : AbstractValidator<ICustomerBillingProfileWriteCommand>
{
    public CustomerBillingProfileWriteRules()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(CustomerBillingProfile.NameMaxLength);
        RuleFor(command => command.IdentificationNumber)
            .NotEmpty()
            .MaximumLength(CustomerBillingProfile.IdentificationNumberMaxLength);
        RuleFor(command => command.IdentificationType).NotEmpty();
        RuleFor(command => command.IdentificationType)
            .Must(DocumentTypesByCountry.IsKnown)
            .WithMessage($"The identification type must be one of {string.Join(", ", DocumentTypesByCountry.All)}.")
            .When(command => !string.IsNullOrWhiteSpace(command.IdentificationType));
        RuleFor(command => command.PersonType)
            .Must(personType => PersonTypeParser.SupportedWireValues.Contains(
                personType!.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage($"The person type must be one of {string.Join(", ", PersonTypeParser.SupportedWireValues)}.")
            .When(command => !string.IsNullOrWhiteSpace(command.PersonType));
        RuleFor(command => command.Phone)
            .MaximumLength(CustomerBillingProfile.PhoneMaxLength);
        RuleFor(command => command.Email)
            .MaximumLength(CustomerBillingProfile.EmailMaxLength)
            .EmailAddress()
            .When(command => !string.IsNullOrWhiteSpace(command.Email));
        RuleFor(command => command.Address)
            .NotEmpty()
            .MaximumLength(CustomerBillingProfile.AddressMaxLength);
        RuleFor(command => command.WithholdingTaxRateIds)
            .Must(ids => ids is null || !ids.Contains(Guid.Empty))
            .WithMessage("A withholding tax rate id cannot be empty.");

        // El país y los dos carriles de ciudad, como CustomerAddressWriteRules: el campo marcado es el que está en pantalla.
        RuleFor(command => command.Country)
            .Length(CustomerContactInfo.CountryLength)
            .Must(country => country!.Trim().All(char.IsAsciiLetter))
            .WithMessage(
                $"The country must be an ISO-3166-1 alpha-2 code ({CustomerContactInfo.CountryLength} letters).")
            .When(command => !string.IsNullOrWhiteSpace(command.Country));
        RuleFor(command => command.CityId)
            .Must(cityId => cityId is { } value && value != Guid.Empty)
            .WithMessage("The city is required.")
            .When(IsColombian);
        RuleFor(command => command.CityName)
            .Must(string.IsNullOrWhiteSpace)
            .WithMessage("A Colombian billing profile uses a city id, not a city name.")
            .When(IsColombian);
        RuleFor(command => command.CityName)
            .NotEmpty()
            .WithMessage("The city is required.")
            .When(command => !IsColombian(command));
        RuleFor(command => command.CityId)
            .Null()
            .WithMessage("A billing profile outside Colombia uses a city name, not a city id.")
            .When(command => !IsColombian(command));
        RuleFor(command => command.CityName)
            .MaximumLength(CustomerContactInfo.CityNameMaxLength)
            .When(command => !string.IsNullOrWhiteSpace(command.CityName));
        RuleFor(command => command.RegionId)
            .Null()
            .WithMessage("A Colombian billing profile takes its region from the city, not from a region id.")
            .When(IsColombian);
        RuleFor(command => command.RegionId).NotEqual(Guid.Empty);
    }

    private static bool IsColombian(ICustomerBillingProfileWriteCommand command) =>
        string.IsNullOrWhiteSpace(command.Country)
        || string.Equals(
            command.Country.Trim(),
            Customer.ColombiaCountryCode,
            StringComparison.OrdinalIgnoreCase);
}

public sealed class AddCustomerBillingProfileValidator : AbstractValidator<AddCustomerBillingProfileCommand>
{
    public AddCustomerBillingProfileValidator() => Include(new CustomerBillingProfileWriteRules());
}

public sealed class UpdateCustomerBillingProfileValidator : AbstractValidator<UpdateCustomerBillingProfileCommand>
{
    public UpdateCustomerBillingProfileValidator() => Include(new CustomerBillingProfileWriteRules());
}

public sealed class AddCustomerBillingProfileHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddCustomerBillingProfileCommand> validator)
    : ICommandHandler<AddCustomerBillingProfileCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        AddCustomerBillingProfileCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, como en la libreta de direcciones.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var customer = await CustomerBillingProfileWorkflow.LoadForWriteAsync(
            repository, geographyLookup, taxRateLookup, executionContext,
            command.TenantId, command.CustomerId, command, cancellationToken);

        // Idempotente por contenido: un perfil igual se devuelve sin tocarlo, así que el guardado no escribe nada.
        customer.EnsureBillingProfile(
            CustomerBillingProfileWorkflow.ToDetails(command), command.IsPrincipal, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}

public sealed class UpdateCustomerBillingProfileHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCustomerBillingProfileCommand> validator)
    : ICommandHandler<UpdateCustomerBillingProfileCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        UpdateCustomerBillingProfileCommand command, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var customer = await CustomerBillingProfileWorkflow.LoadForWriteAsync(
            repository, geographyLookup, taxRateLookup, executionContext,
            command.TenantId, command.CustomerId, command, cancellationToken);

        customer.UpdateBillingProfile(
            new CustomerBillingProfileId(command.ProfileId),
            CustomerBillingProfileWorkflow.ToDetails(command),
            command.IsPrincipal,
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}

public sealed class RemoveCustomerBillingProfileHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RemoveCustomerBillingProfileCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        RemoveCustomerBillingProfileCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext,
            command.TenantId, command.CustomerId, cityId: null, cancellationToken);

        customer.RemoveBillingProfile(new CustomerBillingProfileId(command.ProfileId), clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}

public sealed class MakeCustomerBillingProfilePrincipalHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<MakeCustomerBillingProfilePrincipalCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        MakeCustomerBillingProfilePrincipalCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext,
            command.TenantId, command.CustomerId, cityId: null, cancellationToken);

        customer.MakeBillingProfilePrincipal(new CustomerBillingProfileId(command.ProfileId), clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}

/// <summary>Lo que el alta y la edición comprueban fuera del agregado antes de tocarlo: la región contra Geography, el
/// cliente y la ciudad (<see cref="CustomerAddressWorkflow"/>) y las tasas contra el catálogo, con las mismas reglas y
/// códigos que las del cliente.</summary>
internal static class CustomerBillingProfileWorkflow
{
    public static async Task<Domain.Customer> LoadForWriteAsync(
        ICustomerRepository repository,
        ICustomerGeographyLookup geographyLookup,
        ICustomerTaxRateLookup taxRateLookup,
        IExecutionContext executionContext,
        Guid tenantId,
        Guid customerId,
        ICustomerBillingProfileWriteCommand command,
        CancellationToken cancellationToken)
    {
        _ = await CustomerRegionRules.ResolveRegionAsync(
            geographyLookup, command.Country, command.RegionId, CustomerRegionRules.BillingProfileRegionInvalid,
            cancellationToken);

        var customer = await CustomerAddressWorkflow.LoadAsync(
            repository, geographyLookup, executionContext, tenantId, customerId, command.CityId, cancellationToken);

        await CustomerWithholdingRules.ValidateAsync(
            taxRateLookup, tenantId, command.WithholdingTaxRateIds ?? [], cancellationToken);

        return customer;
    }

    // El validador ya exigio los textos; el dominio vuelve a comprobarlos y los normaliza.
    public static CustomerBillingProfileDetails ToDetails(ICustomerBillingProfileWriteCommand command) =>
        new()
        {
            Name = command.Name ?? string.Empty,
            PersonType = command.PersonType,
            IdentificationType = command.IdentificationType ?? string.Empty,
            IdentificationNumber = command.IdentificationNumber ?? string.Empty,
            Phone = command.Phone,
            Email = command.Email,
            Address = command.Address ?? string.Empty,
            Country = command.Country ?? string.Empty,
            CityId = command.CityId,
            CityName = command.CityName,
            RegionId = command.RegionId,
            WithholdingTaxRateIds = command.WithholdingTaxRateIds ?? [],
            VatSurplus = command.VatSurplus
        };
}
