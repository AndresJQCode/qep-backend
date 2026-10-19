using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Spec de Shopify 2026-10-15 §6.2.1: el cliente que encontró o creó el sistema para una venta externa. Tipos
/// primitivos y no el agregado: el consumidor vive en otro módulo y entra por su propio puerto con un adaptador en
/// Bootstrapper. <see cref="Created"/> dice si esta llamada lo dio de alta o si ganó otra (la carrera del documento).
/// </summary>
public sealed record ProvisionedCustomer(Guid Id, bool IsComplete, bool IsActive, bool Created);

/// <summary>Lo mismo que pide el <c>POST</c> de clientes, sin retención ni excedente de IVA: una venta externa no los
/// sabe, y los dos nacen en <c>false</c> (spec de Shopify §6.2.1 paso 3).</summary>
public sealed record CompleteCustomerData(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string Address,
    string Country,
    Guid? CityId,
    string? CityName,
    Guid ClassificationId);

/// <summary>
/// Altas de clientes que hace el sistema por una integración, sin persona detrás (spec de Shopify §6.2.1 pasos 3 y 4).
/// No pasa por <c>IExecutionContext</c> ni por permisos: el que llama ya resolvió el tenant (la tienda conectada) y
/// el aislamiento lo da el <c>tenantId</c> que llega a cada consulta.
/// </summary>
public interface ICustomerProvisioning
{
    /// <summary>
    /// La ficha completa, con CUC, con las mismas reglas que el <c>POST</c> de clientes. Si el documento ya lo tiene otro
    /// cliente del tenant (la carrera con otra alta, o un llamador que no buscó antes), devuelve ese con
    /// <see cref="ProvisionedCustomer.Created"/> en <c>false</c> en vez de fallar. Ante cualquiera de los errores de abajo,
    /// el llamador cae a <see cref="CreateIncompleteAsync"/>: los datos no alcanzan para una ficha completa, pero el
    /// pedido entra igual.
    /// </summary>
    /// <exception cref="FluentValidation.ValidationException">Los datos no pasan <c>CustomerWriteRules</c>; por ejemplo,
    /// falta el correo o el teléfono, el documento o el país no son válidos, o falta la ciudad que pide el país.</exception>
    /// <exception cref="CustomersDomainException">Con <c>customers.customer.classification_not_found</c> si la
    /// clasificación no existe en el tenant, o con <c>customers.customer.city_not_found</c> si la ciudad DIVIPOLA no
    /// existe.</exception>
    /// <exception cref="InvalidOperationException">El documento quedó tomado pero el ganador no se pudo releer. No
    /// debería pasar, porque el índice no tiene filtro y un cliente no se borra.</exception>
    Task<ProvisionedCustomer> CreateCompleteAsync(Guid tenantId, CompleteCustomerData data, CancellationToken cancellationToken);

    Task<ProvisionedCustomer> CreateIncompleteAsync(
        Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken cancellationToken);
}

public sealed class CustomerProvisioning(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    ICucGenerator cucGenerator,
    IClock clock,
    IValidator<CreateCustomerCommand> validator,
    IPhoneNumberNormalizer phoneNormalizer) : ICustomerProvisioning
{
    /// <summary>Spec de Shopify §6.2.1 paso 4: el nombre de un comprador que no trajo ninguno. Decisión del owner.</summary>
    public const string ExternalSaleFallbackName = "Cliente de Shopify";

    private const string Success = "success";

    private const string IdentificationTaken = "customers.customer.identification_taken";

    public async Task<ProvisionedCustomer> CreateCompleteAsync(
        Guid tenantId, CompleteCustomerData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        // Las mismas reglas del POST, armando su comando: un cliente que nace de Shopify no puede quedar con algo que el
        // formulario habría rechazado. El rechazo sale con el mapa errors, aunque acá nadie lo pinte: el llamador lo
        // trata como «no alcanza para completo».
        var command = new CreateCustomerCommand(
            tenantId,
            data.Name,
            BusinessName: null,
            data.IdentificationType,
            data.IdentificationNumber,
            data.Phone,
            data.Email,
            data.Address,
            data.Country,
            data.CityId,
            data.CityName,
            data.ClassificationId,
            WithRetention: false,
            VatSurplus: false);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Mismo orden que CreateCustomerHandler, y por lo mismo: clasificación y ciudad antes del CUC, para no quemar un
        // número del consecutivo por una referencia que no existe.
        var classification = await classificationRepository.ResolveForCustomerAsync(
            tenantId, command.ClassificationId, cancellationToken);
        var isColombian = string.Equals(
            command.Country.Trim(), Customer.ColombiaCountryCode, StringComparison.OrdinalIgnoreCase);
        var city = isColombian
            ? await geographyLookup.FindCityAsync(command.CityId!.Value, cancellationToken)
                ?? throw new CustomersDomainException("customers.customer.city_not_found", "The city was not found.")
            : null;

        var sequence = await cucGenerator.NextAsync(tenantId, cancellationToken);
        var cuc = CucFormatter.Build(
            classification?.Prefix,
            city?.DepartmentDivipolaCode ?? CucFormatter.ForeignDepartmentCode,
            sequence);
        var now = clock.UtcNow;

        var customer = Customer.Create(
            CustomerId.New(),
            tenantId,
            cuc,
            command.Name,
            command.BusinessName,
            city is null
                ? null
                : new CustomerAddressDetails
                {
                    Name = command.Name,
                    Address = command.Address ?? string.Empty,
                    CityId = city.CityId,
                    Phone = command.Phone
                },
            CustomerMapping.ToIdentification(command.IdentificationType, command.IdentificationNumber),
            new CustomerContactInfo
            {
                Phone = command.Phone,
                Email = command.Email,
                Address = command.Address ?? string.Empty,
                Country = command.Country,
                CityId = command.CityId,
                CityName = command.CityName
            },
            CustomerMapping.ToCommercialInfo(command.ClassificationId, command.WithRetention, command.VatSurplus),
            now,
            phoneNormalizer);

        repository.Add(customer);
        auditPublisher.PublishByIntegration(tenantId, CustomerAuditActions.Created, customer.Id.ToString(), Success, now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (CustomersDomainException exception) when (exception.Code == IdentificationTaken)
        {
            // §6.2.1 paso 3 (H8): el documento lo tomó otra alta entre la búsqueda del llamador y este commit (dos
            // pedidos del mismo comprador a la vez). El árbitro es IX_customers_tenant_identification; acá se relee y
            // se usa el que ganó. Con el par ya normalizado por el agregado, que es como quedó en la base. El número del
            // consecutivo que se pidió arriba queda quemado, igual que en un POST que pierde la misma carrera.
            // CustomersUnitOfWork ya limpió el tracker, así que el scope sirve para la siguiente alta.
            var winner = await FindByIdentificationAsync(tenantId, customer, cancellationToken);
            return winner is null
                ? throw new InvalidOperationException("The identification was taken but its customer cannot be read back.", exception)
                : new ProvisionedCustomer(winner.Id.Value, winner.IsComplete, winner.IsActive, Created: false);
        }

        return new ProvisionedCustomer(customer.Id.Value, IsComplete: true, IsActive: true, Created: true);
    }

    public async Task<ProvisionedCustomer> CreateIncompleteAsync(
        Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var customer = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), tenantId, NameFor(name), phone, email, country, now, phoneNormalizer);
        repository.Add(customer);
        auditPublisher.PublishByIntegration(tenantId, CustomerAuditActions.Created, customer.Id.ToString(), Success, now);
        // Sin índice único que pueda chocar: un incompleto no tiene documento, CUC, BSUID ni IGSID. Dos pedidos del mismo
        // comprador sin datos crean dos fichas; el llamador lo evita anotando el cliente antes del pedido (D-S24).
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ProvisionedCustomer(customer.Id.Value, IsComplete: false, IsActive: true, Created: true);
    }

    // Recortado a la columna y no rechazado, como el nombre de WhatsApp (IncompleteCustomerProfile): un nombre largo en
    // la tienda no puede dejar el pedido afuera.
    private static string NameFor(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? ExternalSaleFallbackName
            : trimmed.Length <= Customer.NameMaxLength ? trimmed : trimmed[..Customer.NameMaxLength].TrimEnd();
    }

    private async Task<Customer?> FindByIdentificationAsync(Guid tenantId, Customer attempted, CancellationToken cancellationToken)
    {
        var key = (attempted.IdentificationType!.Value, attempted.IdentificationNumber!);
        var owners = await repository.FindExistingIdentificationsAsync(tenantId, [key], cancellationToken);
        return owners.TryGetValue(key, out var ownerId)
            ? await repository.FindAsync(tenantId, ownerId, cancellationToken)
            : null;
    }
}
