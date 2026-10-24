using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-15 §6.2.1: Customers → Shopify. Todas las lecturas van con el <c>tenantId</c>, que los repositorios de Customers
/// ponen en la consulta. Lo que vuelve es el estado (completo, activo) y nunca los campos que un cliente incompleto no tiene.
/// Un fusionado nunca sale de acá: por id, teléfono, correo o documento se devuelve el que quedó (spec 2026-10-24 §3.3), así un
/// pedido que reusa su <c>qep_customer_id</c> cae en la ficha viva. Un salto basta: <c>MergedIntoId</c> apunta a un no fusionado (D-F2).
/// </summary>
internal sealed class ShopifyCustomerDirectory(
    ICustomerRepository customers, ICustomerMergeStore mergeStore, ICustomerProvisioning provisioning, IPhoneNumberNormalizer phones)
    : IShopifyCustomerDirectory
{
    private const string ValidationFailed = "validation.failed";

    private const string DefaultCountry = "CO";

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
        if (owners.TryGetValue(key, out var ownerId))
        {
            return await FindAsync(tenantId, ownerId, cancellationToken);
        }

        // Desvío F9: la búsqueda de arriba ya no ve fusionados (F8) y un fusionado completo conserva su documento (F1); sin dueño
        // vivo, el documento todavía puede ser de alguien que se fusionó.
        return await mergeStore.FindSurvivorIdByIdentificationAsync(tenantId, key.Item1, trimmed, cancellationToken) is { } survivorId
            ? await FindAsync(tenantId, new CustomerId(survivorId), cancellationToken)
            : null;
    }

    // §6.2.1 paso 2: phone_e164 lo escribió Customers con este mismo normalizador, así que el número de Shopify (el de la
    // dirección lo teclea el comprador: «300 123 4567») se lleva a E.164 igual antes de buscar. Sin país, el de la tienda
    // colombiana; uno que no normaliza no tiene a quién encontrar.
    public async Task<ShopifyCustomerRef?> FindByPhoneAsync(
        Guid tenantId, string phone, string? country, CancellationToken cancellationToken)
    {
        var region = string.IsNullOrWhiteSpace(country) ? DefaultCountry : country.Trim().ToUpperInvariant();
        var e164 = string.IsNullOrWhiteSpace(phone) ? null : phones.ToE164(phone.Trim(), region);
        return e164 is null ? null : await ToRefAsync(tenantId, await customers.FindOldestByPhoneE164Async(tenantId, e164, cancellationToken), cancellationToken);
    }

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
        await ToRefAsync(tenantId, await customers.FindAsync(tenantId, id, cancellationToken), cancellationToken);

    // Un fusionado se cambia por el que quedó, entero: id y estado son del que quedó, no del fusionado (que siempre está inactivo).
    private async Task<ShopifyCustomerRef?> ToRefAsync(Guid tenantId, Customer? customer, CancellationToken cancellationToken)
    {
        if (customer?.MergedIntoId is { } survivorId)
        {
            customer = await customers.FindAsync(tenantId, survivorId, cancellationToken);
        }

        return customer is null ? null : new ShopifyCustomerRef(customer.Id.Value, customer.IsComplete, customer.IsActive);
    }
}
