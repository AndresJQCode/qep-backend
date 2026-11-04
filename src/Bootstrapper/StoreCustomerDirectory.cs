using BuildingBlocks.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Bootstrapper;

/// <summary>
/// Customers → Store. Mismo trato que <see cref="ServiceContractsCustomerProvisioning"/>: lecturas por <see cref="ICustomerRepository"/>
/// y altas por <see cref="ICustomerProvisioning"/>. Los errores de Customers que la bandeja sabe nombrar se traducen acá a los códigos
/// <c>store.order_request.*</c>; el resto (<c>validation.failed</c>, <c>customers.customer.*</c>) sube tal cual.
/// </summary>
internal sealed class StoreCustomerDirectory(
    ICustomerRepository customers, ICustomerProvisioning provisioning) : IStoreCustomerDirectory
{
    // Un teléfono compartido por varios clientes no debe inundar el diálogo.
    private const int MaxPhoneMatches = 10;

    private const string Phone = "Phone";

    private const string Document = "Document";

    public async Task<IReadOnlyList<StoreCustomerCandidate>> FindCandidatesAsync(
        Guid tenantId, string phoneE164, string? identificationType, string? identificationNumber, CancellationToken cancellationToken)
    {
        var matches = new Dictionary<Guid, (Customer Customer, List<string> By)>();
        foreach (var byPhone in await customers.ListByPhoneE164Async(tenantId, phoneE164, MaxPhoneMatches, cancellationToken))
        {
            matches[byPhone.Id.Value] = (byPhone, [Phone]);
        }

        if (TryKey(identificationType, identificationNumber) is { } key
            && (await customers.FindExistingIdentificationsAsync(tenantId, [key], cancellationToken)).TryGetValue(key, out var ownerId)
            && await customers.FindAsync(tenantId, ownerId, cancellationToken) is { IsActive: true, IsMerged: false } byDocument)
        {
            if (matches.TryGetValue(byDocument.Id.Value, out var already))
            {
                already.By.Add(Document);
            }
            else
            {
                matches[byDocument.Id.Value] = (byDocument, [Document]);
            }
        }

        return matches.Values
            .Select(match => new StoreCustomerCandidate(
                match.Customer.Id.Value,
                match.Customer.Name,
                match.Customer.PhoneE164,
                match.Customer.IdentificationType?.ToWireValue(),
                match.Customer.IdentificationNumber,
                match.Customer.IsComplete,
                match.By))
            .ToList();
    }

    public async Task<IReadOnlySet<int>> KnownAsync(
        Guid tenantId,
        IReadOnlyCollection<(string PhoneE164, string? Type, string? Number)> keys,
        CancellationToken cancellationToken)
    {
        var list = keys.ToList();
        var phones = await customers.FindExistingPhonesE164Async(
            tenantId, list.Select(key => key.PhoneE164).Distinct(StringComparer.Ordinal).ToList(), cancellationToken);

        var documentKeys = list.Select(key => TryKey(key.Type, key.Number)).OfType<(IdentificationType, string)>().Distinct().ToList();
        var documents = documentKeys.Count == 0
            ? new Dictionary<(IdentificationType Type, string Number), CustomerId>()
            : (await customers.FindExistingIdentificationsAsync(tenantId, documentKeys, cancellationToken))
                .ToDictionary(pair => pair.Key, pair => pair.Value);

        var known = new HashSet<int>();
        for (var index = 0; index < list.Count; index++)
        {
            if (phones.Contains(list[index].PhoneE164)
                || (TryKey(list[index].Type, list[index].Number) is { } key && documents.ContainsKey(key)))
            {
                known.Add(index);
            }
        }

        return known;
    }

    public async Task<StoreCustomerRef?> FindEligibleAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(tenantId, new CustomerId(customerId), cancellationToken);
        return customer is { IsActive: true, IsMerged: false } ? ToRef(customer) : null;
    }

    public async Task<(StoreCustomerRef Customer, bool Created)> CreateAsync(
        Guid tenantId, Guid actorUserId, StoreNewCustomer data, CancellationToken cancellationToken)
    {
        var provisioned = await provisioning.CreateCompleteAsync(tenantId, ToData(data), cancellationToken, actorUserId, "store");
        // Se relee para traer la retención y el excedente de IVA reales del ganador cuando el documento ya era de otro.
        var customer = await customers.FindAsync(tenantId, new CustomerId(provisioned.Id), cancellationToken)
            ?? throw new InvalidOperationException("The customer that was just provisioned cannot be read back.");
        // El documento ya era de otro cliente y ése está inactivo: no se le cuelga un pedido, igual que si la persona lo hubiera elegido.
        if (customer is not { IsActive: true, IsMerged: false })
        {
            throw NotFound();
        }

        return (ToRef(customer), provisioned.Created);
    }

    public async Task<StoreCustomerRef> CompleteAsync(
        Guid tenantId, Guid actorUserId, Guid customerId, StoreNewCustomer billing, CancellationToken cancellationToken)
    {
        try
        {
            await provisioning.CompleteFromStoreAsync(tenantId, customerId, ToData(billing), actorUserId, cancellationToken);
        }
        catch (ResourceNotFoundException exception) when (exception.Code == "customers.customer.not_found")
        {
            throw NotFound();
        }
        catch (CustomersDomainException exception) when (exception.Code == CustomersErrorCodes.IdentificationTaken)
        {
            // Nunca se cambia en silencio al otro cliente que ya tiene ese documento: la persona decide.
            throw new ResourceConflictException(
                StoreErrorCodes.CustomerDocumentTaken, "Another customer already has the document of the billing block.", exception);
        }
        catch (RequestConcurrencyException exception)
        {
            // Lo mismo que en EnsureAddressesAsync: «Tomar» no manda If-Match, así que no es un 412 sino un conflicto que se reintenta.
            throw new ResourceConflictException(
                StoreErrorCodes.OrderRequestConflict, "The customer changed while it was being completed.", exception);
        }

        var customer = await customers.FindAsync(tenantId, new CustomerId(customerId), cancellationToken) ?? throw NotFound();
        return ToRef(customer);
    }

    public async Task<IReadOnlyList<StoreAddressOutcome>> EnsureAddressesAsync(
        Guid tenantId, Guid customerId, IReadOnlyList<StoreAddressInput> addresses, CancellationToken cancellationToken)
    {
        var details = addresses.Select(ToDetails).ToList();
        try
        {
            var ensured = await provisioning.EnsureAddressesAsync(tenantId, customerId, details, cancellationToken);
            return ensured
                .Select(entry => entry.Kind == EnsuredAddressKind.Added ? StoreAddressOutcome.Added : StoreAddressOutcome.Reused)
                .ToList();
        }
        catch (ResourceNotFoundException exception) when (exception.Code == "customers.customer.not_found")
        {
            throw NotFound();
        }
        catch (RequestConcurrencyException exception)
        {
            // El cliente cambió mientras se agregaban sus direcciones: la bandeja pide reintentar.
            throw new ResourceConflictException(
                StoreErrorCodes.OrderRequestConflict, "The customer changed while its addresses were being added.", exception);
        }
    }

    private static StoreDomainException NotFound() =>
        new(StoreErrorCodes.CustomerNotFound, "The customer does not exist, is inactive or was merged.");

    private static StoreCustomerRef ToRef(Customer customer) =>
        new(customer.Id.Value, customer.IsComplete, customer.WithRetention, customer.VatSurplus);

    // Un tipo que no es de la lista no tiene a quién encontrar (Parse lanzaría).
    private static (IdentificationType Type, string Number)? TryKey(string? type, string? number)
    {
        var trimmed = number?.Trim();
        return string.IsNullOrEmpty(trimmed)
            || type is null
            || !IdentificationTypeParser.SupportedWireValues.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase)
                ? null
                : (IdentificationTypeParser.Parse(type), trimmed);
    }

    private static CompleteCustomerData ToData(StoreNewCustomer data) =>
        new(
            data.Name, data.IdentificationType, data.IdentificationNumber, data.Phone, data.Email, data.Address, data.Country,
            data.CityId, data.CityName, data.ClassificationId);

    // El nombre de una dirección de la libreta admite 120; el del cliente, 150.
    private static CustomerAddressDetails ToDetails(StoreAddressInput input)
    {
        var name = input.Name.Trim();
        return new CustomerAddressDetails
        {
            Name = name.Length <= CustomerAddress.NameMaxLength ? name : name[..CustomerAddress.NameMaxLength].TrimEnd(),
            Address = input.Address,
            Country = input.Country,
            CityId = input.CityId,
            CityName = input.CityName,
            Phone = input.Phone,
        };
    }
}
