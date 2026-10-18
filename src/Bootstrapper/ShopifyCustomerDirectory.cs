using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-15 §6.2.1: Customers → Shopify. Todas las lecturas van con el <c>tenantId</c>, que los repositorios de Customers
/// ponen en la consulta. Lo que vuelve es el estado (completo, activo) y nunca los campos que un cliente incompleto no tiene.
/// </summary>
internal sealed class ShopifyCustomerDirectory(ICustomerRepository customers, ICustomerProvisioning provisioning)
    : IShopifyCustomerDirectory
{
    private const string ValidationFailed = "validation.failed";

    // Los dos códigos de ICustomerProvisioning.CreateCompleteAsync que dicen «no alcanza para completo».
    private static readonly HashSet<string> NotCompletableCodes =
        new(StringComparer.Ordinal) { "customers.customer.classification_not_found", "customers.customer.city_not_found" };

    public async Task<ShopifyCustomerRef?> FindByDocumentAsync(
        Guid tenantId, string type, string number, CancellationToken cancellationToken)
    {
        // IdentificationTypeParser.Parse lanza con un tipo desconocido (H7); el importador ya normalizó, así que uno que no es de
        // la lista no tiene a quién encontrar.
        var trimmed = number?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || type is null
            || !IdentificationTypeParser.SupportedWireValues.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = (IdentificationTypeParser.Parse(type), trimmed);
        var owners = await customers.FindExistingIdentificationsAsync(tenantId, [key], cancellationToken);
        return owners.TryGetValue(key, out var ownerId) ? await FindAsync(tenantId, ownerId, cancellationToken) : null;
    }

    public async Task<ShopifyCustomerRef?> FindByPhoneAsync(Guid tenantId, string e164, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(e164)
            ? null
            : ToRef(await customers.FindOldestByPhoneE164Async(tenantId, e164.Trim(), cancellationToken));

    public async Task<ShopifyCustomerRef?> FindByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return await customers.FindIdByEmailAsync(tenantId, email.Trim(), cancellationToken) is { } id
            ? await FindAsync(tenantId, id, cancellationToken)
            : null;
    }

    public Task<ShopifyCustomerRef?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        FindAsync(tenantId, new CustomerId(id), cancellationToken);

    public async Task<ShopifyCustomerRef> CreateCompleteAsync(
        Guid tenantId, ShopifyCompleteCustomer data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            var created = await provisioning.CreateCompleteAsync(
                tenantId,
                new CompleteCustomerData(
                    data.Name, data.IdentificationType, data.IdentificationNumber, data.Phone, data.Email, data.Address,
                    data.Country, data.CityId, data.CityName, data.ClassificationId),
                cancellationToken);
            return new ShopifyCustomerRef(created.Id, created.IsComplete, created.IsActive);
        }
        catch (ValidationException error)
        {
            throw new ShopifyCustomerNotCompletableException(ValidationFailed, error);
        }
        catch (CustomersDomainException error) when (NotCompletableCodes.Contains(error.Code))
        {
            throw new ShopifyCustomerNotCompletableException(error.Code, error);
        }
    }

    public async Task<ShopifyCustomerRef> CreateIncompleteAsync(
        Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken cancellationToken)
    {
        var created = await provisioning.CreateIncompleteAsync(tenantId, name, phone, email, country, cancellationToken);
        return new ShopifyCustomerRef(created.Id, created.IsComplete, created.IsActive);
    }

    private async Task<ShopifyCustomerRef?> FindAsync(Guid tenantId, CustomerId id, CancellationToken cancellationToken) =>
        ToRef(await customers.FindAsync(tenantId, id, cancellationToken));

    private static ShopifyCustomerRef? ToRef(Customer? customer) =>
        customer is null ? null : new ShopifyCustomerRef(customer.Id.Value, customer.IsComplete, customer.IsActive);
}
