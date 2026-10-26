using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Spec de Shopify 2026-10-15 §6.2.1: el cliente que encontró o creó el sistema para una venta externa. Tipos
/// primitivos y no el agregado: el consumidor vive en otro módulo y entra por su propio puerto con un adaptador en
/// Bootstrapper. <see cref="Created"/> dice si esta llamada lo dio de alta o si ganó otra (la carrera del documento).
/// </summary>
public sealed record ProvisionedCustomer(Guid Id, bool IsComplete, bool IsActive, bool Created, long Version = 0);

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

/// <summary>Lo que la caja del POS sabe del comprador al darlo de alta: nombre, documento y, si lo dio, teléfono.</summary>
public sealed record RegisterCustomerData(string Name, string IdentificationType, string IdentificationNumber, string? Phone);

/// <summary>
/// Altas de clientes que hace el sistema, casi todas por una integración sin persona detrás (spec de Shopify §6.2.1
/// pasos 3 y 4): no pasan por <c>IExecutionContext</c> ni por permisos, el que llama ya resolvió el tenant (la tienda
/// conectada) y el aislamiento lo da el <c>tenantId</c> que llega a cada consulta. La excepción es
/// <see cref="CreateFromRegisterAsync"/>: la caja da el alta con una persona detrás, y esa persona es el actor.
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
    /// debería pasar: el índice es parcial sobre los no fusionados, pero un cliente no se borra y el que ganó la carrera no
    /// está fusionado.</exception>
    Task<ProvisionedCustomer> CreateCompleteAsync(Guid tenantId, CompleteCustomerData data, CancellationToken cancellationToken);

    Task<ProvisionedCustomer> CreateIncompleteAsync(
        Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken cancellationToken);

    /// <summary>
    /// La ficha incompleta que da de alta la caja: nombre y documento, sin CUC. A diferencia de
    /// <see cref="CreateCompleteAsync"/>, la carrera del documento no se traga: si otra alta ganó, el llamador tiene que
    /// enterarse para volver a buscar al cliente que quedó.
    /// </summary>
    /// <param name="source">El origen que va a la bitácora (<c>"pos"</c> desde la caja); <c>null</c> deja el del módulo.</param>
    /// <exception cref="CustomersDomainException">Con <c>customers.customer.identification_type_invalid</c> si el tipo de
    /// documento no existe; con el código de la regla rota si el nombre, el documento o el teléfono no valen; o con
    /// <c>customers.customer.identification_taken</c> si el documento ya lo tiene otro cliente del tenant.</exception>
    Task<ProvisionedCustomer> CreateFromRegisterAsync(
        Guid tenantId, Guid actorId, RegisterCustomerData data, string? source, CancellationToken cancellationToken);

    /// <summary>
    /// Spec de ciudades 2026-10-26 §4.4: completa al cliente incompleto que creó una importación de Shopify, con el mismo
    /// camino de <see cref="CreateCompleteAsync"/> (clasificación y ciudad antes del CUC). Sólo si sigue como lo dejó la
    /// importación: activo, sin fusionar, incompleto y en <paramref name="expectedVersion"/>. No lanza por datos: lo que
    /// no se puede hacer vuelve como <see cref="ExternalCustomerCityOutcomeKind.NotEligible"/> o
    /// <see cref="ExternalCustomerCityOutcomeKind.NotCompletable"/>. Es idempotente: completo en
    /// <c>expectedVersion + 1</c>, con los mismos datos salvo la ciudad y en una ciudad que el llamador pidió (la de
    /// <paramref name="data"/> o una de <paramref name="requestedCityIds"/>, las que pidió en intentos anteriores) es
    /// <see cref="ExternalCustomerCityOutcomeKind.AlreadyApplied"/>, con la ciudad que quedó.
    /// </summary>
    Task<ExternalCustomerCityOutcome> CompleteExternalSaleAsync(
        Guid tenantId,
        Guid customerId,
        long expectedVersion,
        CompleteCustomerData data,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Spec de ciudades 2026-10-26 §4.6 (D-C4b): cambia la ciudad de un cliente completo de <paramref name="previousCityId"/>
    /// a <paramref name="cityId"/> (y la de su dirección principal si sigue ahí), sólo si no cambió desde
    /// <paramref name="expectedVersion"/>. Sin auditoría propia: la corrección la audita Shopify una sola vez. En
    /// <c>expectedVersion + 1</c> y en una ciudad que el llamador pidió (<paramref name="cityId"/> o una de
    /// <paramref name="requestedCityIds"/>) es <see cref="ExternalCustomerCityOutcomeKind.AlreadyApplied"/>.
    /// </summary>
    Task<ExternalCustomerCityOutcome> CorrectCityAsync(
        Guid tenantId,
        Guid customerId,
        long expectedVersion,
        Guid previousCityId,
        Guid cityId,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken);
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

    private const string ValidationFailed = "validation.failed";

    public async Task<ProvisionedCustomer> CreateCompleteAsync(
        Guid tenantId, CompleteCustomerData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        // Las mismas reglas del POST, armando su comando: un cliente que nace de Shopify no puede quedar con algo que el
        // formulario habría rechazado. El rechazo sale con el mapa errors, aunque acá nadie lo pinte: el llamador lo
        // trata como «no alcanza para completo».
        var command = ToCommand(tenantId, data);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var parts = await PrepareAsync(command, cancellationToken);
        var now = clock.UtcNow;

        var customer = Customer.Create(
            CustomerId.New(),
            tenantId,
            parts.Cuc,
            command.Name,
            command.BusinessName,
            parts.Address,
            parts.Identification,
            parts.Contact,
            parts.Commercial,
            now,
            phoneNormalizer);

        repository.Add(customer);
        auditPublisher.PublishByIntegration(tenantId, CustomerAuditActions.Created, customer.Id.ToString(), Success, now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (CustomersDomainException exception) when (exception.Code == CustomersErrorCodes.IdentificationTaken)
        {
            // §6.2.1 paso 3 (H8): el documento lo tomó otra alta entre la búsqueda del llamador y este commit (dos
            // pedidos del mismo comprador a la vez). El árbitro es IX_customers_tenant_identification; acá se relee y
            // se usa el que ganó. Con el par ya normalizado por el agregado, que es como quedó en la base. El número del
            // consecutivo que se pidió arriba queda quemado, igual que en un POST que pierde la misma carrera.
            // CustomersUnitOfWork ya limpió el tracker, así que el scope sirve para la siguiente alta.
            var winner = await FindByIdentificationAsync(tenantId, customer, cancellationToken);
            return winner is null
                ? throw new InvalidOperationException("The identification was taken but its customer cannot be read back.", exception)
                : new ProvisionedCustomer(winner.Id.Value, winner.IsComplete, winner.IsActive, Created: false, winner.Version);
        }

        return new ProvisionedCustomer(customer.Id.Value, IsComplete: true, IsActive: true, Created: true, customer.Version);
    }

    public async Task<ExternalCustomerCityOutcome> CompleteExternalSaleAsync(
        Guid tenantId,
        Guid customerId,
        long expectedVersion,
        CompleteCustomerData data,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(requestedCityIds);
        var customer = await repository.FindAsync(tenantId, new CustomerId(customerId), cancellationToken);
        if (customer is null)
        {
            return new(ExternalCustomerCityOutcomeKind.NotFound, null, null);
        }

        // Un reintento: un intento anterior lo completó con estos datos y cayó antes de que Shopify guardara. La ciudad puede ser
        // otra de las que pidió el llamador (el sinónimo cambió entre intentos): vuelve la que quedó, y Shopify la recorrige. Una
        // que no pidió nunca la puso una persona, y eso no es un reintento.
        if (customer.IsComplete
            && customer.Version == expectedVersion + 1
            && WasRequested(customer, data.CityId, requestedCityIds)
            && WasCompletedWith(customer, data))
        {
            return new(ExternalCustomerCityOutcomeKind.AlreadyApplied, customer.Version, null, customer.CityId);
        }

        if (IneligibleReason(customer, expectedVersion, mustBeComplete: false) is { } reason)
        {
            return new(ExternalCustomerCityOutcomeKind.NotEligible, null, reason);
        }

        var command = ToCommand(tenantId, data);
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return new(ExternalCustomerCityOutcomeKind.NotCompletable, null, ValidationFailed);
        }

        CompletionParts parts;
        try
        {
            parts = await PrepareAsync(command, cancellationToken);
        }
        catch (CustomersDomainException exception) when (
            exception.Code is CustomersErrorCodes.ClassificationNotFound or CustomersErrorCodes.CityNotFound)
        {
            return new(ExternalCustomerCityOutcomeKind.NotCompletable, null, exception.Code);
        }

        var now = clock.UtcNow;
        customer.Complete(
            parts.Cuc, command.Name, command.BusinessName, parts.Address, parts.Identification, parts.Contact, parts.Commercial,
            now, phoneNormalizer);
        auditPublisher.PublishByIntegration(tenantId, CustomerAuditActions.Completed, customer.Id.ToString(), Success, now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (CustomersDomainException exception) when (exception.Code == CustomersErrorCodes.IdentificationTaken)
        {
            // Otro cliente del tenant ya tiene ese documento: esta ficha se queda incompleta. El número del consecutivo
            // que se pidió queda quemado, igual que en un POST que pierde la misma carrera.
            return new(ExternalCustomerCityOutcomeKind.NotCompletable, null, exception.Code);
        }

        return new(ExternalCustomerCityOutcomeKind.Applied, customer.Version, null, customer.CityId);
    }

    public async Task<ExternalCustomerCityOutcome> CorrectCityAsync(
        Guid tenantId,
        Guid customerId,
        long expectedVersion,
        Guid previousCityId,
        Guid cityId,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedCityIds);
        var customer = await repository.FindAsync(tenantId, new CustomerId(customerId), cancellationToken);
        if (customer is null)
        {
            return new(ExternalCustomerCityOutcomeKind.NotFound, null, null);
        }

        // Un reintento: un intento anterior ya lo llevó a una de las ciudades que pidió el llamador (ésta, o la que el sinónimo
        // tenía entonces) y cayó antes de que Shopify guardara. Vuelve la ciudad que quedó. Una persona que le cambió la ciudad
        // una vez la llevó a una que nadie pidió: no es un reintento, y sale Edited.
        if (customer.IsComplete
            && customer.Version == expectedVersion + 1
            && customer.CityId != previousCityId
            && WasRequested(customer, cityId, requestedCityIds))
        {
            return new(ExternalCustomerCityOutcomeKind.AlreadyApplied, customer.Version, null, customer.CityId);
        }

        if (IneligibleReason(customer, expectedVersion, mustBeComplete: true) is { } reason)
        {
            return new(ExternalCustomerCityOutcomeKind.NotEligible, null, reason);
        }

        try
        {
            customer.CorrectCity(previousCityId, cityId, clock.UtcNow);
        }
        catch (CustomersDomainException exception) when (exception.Code == CustomersErrorCodes.CityChanged)
        {
            return new(ExternalCustomerCityOutcomeKind.NotEligible, null, ExternalCustomerCityReasons.CityChanged);
        }

        // Sin auditoría acá: la corrección de ciudad la audita Shopify una sola vez (IShopifyAuditRecorder).
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(ExternalCustomerCityOutcomeKind.Applied, customer.Version, null, customer.CityId);
    }

    private static bool WasRequested(Customer customer, Guid? cityId, IReadOnlyCollection<Guid> requestedCityIds) =>
        customer.CityId is { } current && (current == cityId || requestedCityIds.Contains(current));

    // Lo que la venta trajo y no es la ciudad: el documento, la clasificación y el país. Una persona que completó la ficha con
    // otro documento no es un reintento nuestro.
    private static bool WasCompletedWith(Customer customer, CompleteCustomerData data) =>
        IdentificationTypeParser.SupportedWireValues.Contains(data.IdentificationType.Trim(), StringComparer.OrdinalIgnoreCase)
        && customer.IdentificationType == IdentificationTypeParser.Parse(data.IdentificationType)
        && string.Equals(customer.IdentificationNumber, data.IdentificationNumber.Trim(), StringComparison.Ordinal)
        && customer.ClassificationId?.Value == data.ClassificationId
        && string.Equals(customer.Country, data.Country.Trim(), StringComparison.OrdinalIgnoreCase);

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
        return new ProvisionedCustomer(customer.Id.Value, IsComplete: false, IsActive: true, Created: true, customer.Version);
    }

    public async Task<ProvisionedCustomer> CreateFromRegisterAsync(
        Guid tenantId, Guid actorId, RegisterCustomerData data, string? source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        var now = clock.UtcNow;
        var customer = Customer.CreateFromRegister(
            CustomerId.New(),
            tenantId,
            data.Name,
            CustomerMapping.ToIdentification(data.IdentificationType, data.IdentificationNumber),
            data.Phone,
            now,
            phoneNormalizer);
        repository.Add(customer);
        // Hay una persona detrás (el cajero), así que va como Human y no por PublishByIntegration.
        auditPublisher.Publish(tenantId, actorId, CustomerAuditActions.Created, customer.Id.ToString(), Success, now, source);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ProvisionedCustomer(customer.Id.Value, IsComplete: false, IsActive: true, Created: true, customer.Version);
    }

    // Las mismas reglas del POST, armando su comando: un cliente que nace de Shopify no puede quedar con algo que el
    // formulario habría rechazado.
    private static CreateCustomerCommand ToCommand(Guid tenantId, CompleteCustomerData data) =>
        new(
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

    // Mismo orden que CreateCustomerHandler, y por lo mismo: clasificación y ciudad antes del CUC, para no quemar un
    // número del consecutivo por una referencia que no existe.
    private async Task<CompletionParts> PrepareAsync(CreateCustomerCommand command, CancellationToken cancellationToken)
    {
        var classification = await classificationRepository.ResolveForCustomerAsync(
            command.TenantId, command.ClassificationId, cancellationToken);
        var isColombian = string.Equals(
            command.Country.Trim(), Customer.ColombiaCountryCode, StringComparison.OrdinalIgnoreCase);
        var city = isColombian
            ? await geographyLookup.FindCityAsync(command.CityId!.Value, cancellationToken)
                ?? throw new CustomersDomainException(CustomersErrorCodes.CityNotFound, "The city was not found.")
            : null;

        var sequence = await cucGenerator.NextAsync(command.TenantId, cancellationToken);
        var cuc = CucFormatter.Build(
            classification?.Prefix,
            city?.DepartmentDivipolaCode ?? CucFormatter.ForeignDepartmentCode,
            sequence);
        return new CompletionParts(
            cuc,
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
            CustomerMapping.ToCommercialInfo(command.ClassificationId, command.WithRetention, command.VatSurplus));
    }

    // Spec de ciudades §4.4 / §4.6: el cliente sigue como lo dejó la importación, o no se toca. La versión es la marca de
    // «nadie lo editó» (S7): el incompleto de una venta externa nace en 1.
    private static string? IneligibleReason(Customer customer, long expectedVersion, bool mustBeComplete) =>
        customer.IsMerged ? ExternalCustomerCityReasons.Merged
        : !customer.IsActive ? ExternalCustomerCityReasons.Inactive
        : customer.IsComplete != mustBeComplete
            ? mustBeComplete ? ExternalCustomerCityReasons.Incomplete : ExternalCustomerCityReasons.Complete
        : customer.Version != expectedVersion ? ExternalCustomerCityReasons.Edited
        : null;

    private sealed record CompletionParts(
        string Cuc,
        CustomerAddressDetails? Address,
        CustomerIdentification Identification,
        CustomerContactInfo Contact,
        CustomerCommercialInfo Commercial);

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
