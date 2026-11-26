using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Suppliers.Application;

internal static class SupplierMapping
{
    /// <summary>
    /// Del agregado al detalle, con la ciudad ya resuelta por el llamador (<c>null</c> fuera de
    /// Colombia). No la resuelve esta función: cada handler decide cómo (una consulta puntual en
    /// Get/Create/Update, un lote en List) y esto sólo ensambla. Lo mismo con el mayorista vinculado (<c>null</c> si no lo
    /// hay): <see cref="ResolveLinkedTenantAsync"/>.
    /// </summary>
    public static SupplierResponse ToResponse(
        this Supplier supplier,
        SupplierCityRef? city,
        SupplierLinkedTenantDto? linkedTenant,
        string? linkedRelationshipStatus = null,
        SupplierRegionRef? region = null) => new(
        supplier.Id.Value,
        supplier.Name,
        supplier.BusinessName,
        supplier.Identification.Type.ToWire(),
        supplier.Identification.Number,
        supplier.Contact.Phone,
        supplier.Contact.Email,
        supplier.Contact.Address,
        supplier.Contact.Country,
        city is null ? null : new SupplierCityDto(city.CityId, city.CityDivipolaCode, city.CityName),
        city is null
            ? null
            : new SupplierDepartmentDto(city.DepartmentId, city.DepartmentDivipolaCode, city.DepartmentName),
        supplier.CityName,
        supplier.PaymentTermDays,
        supplier.Category,
        supplier.Notes,
        [.. supplier.BankAccounts.Select(account =>
            new SupplierBankAccountPayload(account.BankName, account.AccountNumber, account.Currency))],
        [.. supplier.Contacts.Select(contact =>
            new SupplierContactPayload(contact.Name, contact.Role, contact.Phone, contact.Email))],
        supplier.IsActive,
        supplier.CreatedAt,
        supplier.UpdatedAt,
        linkedTenant,
        linkedTenant is null ? null : linkedRelationshipStatus,
        region is null ? null : new SupplierRegionDto(region.Id, region.Name));

    /// <summary>
    /// La versión de un solo proveedor: resuelve su ciudad (sólo si la tiene) y arma el detalle. La
    /// FK blanda a Geography no la garantiza la base, pero un proveedor guardado pasó por
    /// <see cref="ResolveCityAsync"/>: un miss aquí es corrupción de datos, no una entrada inválida,
    /// y por eso es <see cref="InvalidOperationException"/> (500) y no un 422.
    /// </summary>
    public static async Task<SupplierResponse> ToResponseAsync(
        this Supplier supplier,
        ISupplierGeographyLookup geographyLookup,
        ITenantDirectory tenantDirectory,
        ISupplierRelationshipLookup relationships,
        CancellationToken cancellationToken)
    {
        var linkedTenant = await supplier.ResolveLinkedTenantAsync(tenantDirectory, cancellationToken);
        var status = await supplier.ResolveRelationshipStatusAsync(relationships, cancellationToken);
        // Spec 2026-11-23: una región que Geography ya no devuelve se omite en vez de tirar la ficha (referencia blanda).
        SupplierRegionRef? region = null;
        if (supplier.RegionId is { } regionId)
        {
            (await geographyLookup.FindRegionsAsync([regionId], cancellationToken)).TryGetValue(regionId, out region);
        }

        if (supplier.CityId is not { } cityId)
        {
            return supplier.ToResponse(null, linkedTenant, status, region);
        }

        var city = await geographyLookup.FindCityAsync(cityId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"City '{cityId}' referenced by supplier '{supplier.Id}' was not found.");

        return supplier.ToResponse(city, linkedTenant, status, region);
    }

    /// <summary>
    /// Mayorista fase 3 (adenda): el estado de la relación con el mayorista vinculado, o <c>null</c> para un proveedor externo
    /// (sin consultar) o si la relación ya no existe.
    /// </summary>
    public static async Task<string?> ResolveRelationshipStatusAsync(
        this Supplier supplier,
        ISupplierRelationshipLookup relationships,
        CancellationToken cancellationToken)
    {
        if (supplier.LinkedTenantId is not { } linkedTenantId)
        {
            return null;
        }

        var statuses = await relationships.FindStatusesAsync(supplier.TenantId, [linkedTenantId], cancellationToken);
        return statuses.GetValueOrDefault(linkedTenantId);
    }

    /// <summary>
    /// Spec 2026-11-17 (mayorista): el mayorista vinculado con su nombre vigente, o <c>null</c> para un proveedor externo. Los
    /// tenants no se borran (sólo se suspenden), así que un vínculo sin tenant es corrupción de datos: 500, como la ciudad.
    /// </summary>
    public static async Task<SupplierLinkedTenantDto?> ResolveLinkedTenantAsync(
        this Supplier supplier,
        ITenantDirectory tenantDirectory,
        CancellationToken cancellationToken)
    {
        if (supplier.LinkedTenantId is not { } linkedTenantId)
        {
            return null;
        }

        var name = await tenantDirectory.GetDisplayNameAsync(new TenantId(linkedTenantId), cancellationToken)
            ?? throw new InvalidOperationException(
                $"Tenant '{linkedTenantId}' linked to supplier '{supplier.Id}' was not found.");

        return new SupplierLinkedTenantDto(linkedTenantId, name);
    }

    public static SupplierListItemResponse ToListItem(
        this Supplier supplier, SupplierCityRef? city, string? linkedRelationshipStatus) => new(
        supplier.Id.Value,
        supplier.Name,
        supplier.BusinessName,
        supplier.Identification.Type.ToWire(),
        supplier.Identification.Number,
        supplier.Contact.Phone,
        city?.CityName ?? supplier.CityName,
        supplier.Category,
        supplier.PaymentTermDays,
        [.. supplier.BankAccounts.Select(account =>
            new SupplierListBankAccount(account.BankName, account.AccountNumber))],
        supplier.IsActive,
        supplier.LinkedTenantId is not null,
        supplier.LinkedTenantId is null ? null : linkedRelationshipStatus);

    /// <summary>
    /// Sólo un proveedor colombiano tiene ciudad DIVIPOLA que resolver. El validador ya se aseguró
    /// de que venga la que corresponde al país; aquí se comprueba que Geography la conozca.
    /// </summary>
    public static async Task<SupplierCityRef?> ResolveCityAsync(
        this ISupplierWriteCommand command,
        ISupplierGeographyLookup geographyLookup,
        CancellationToken cancellationToken)
    {
        var isColombian = string.Equals(
            command.Country.Trim(),
            Supplier.ColombiaCountryCode,
            StringComparison.OrdinalIgnoreCase);

        return isColombian
            ? await geographyLookup.FindCityAsync(command.CityId!.Value, cancellationToken)
                ?? throw new SuppliersDomainException(
                    "suppliers.supplier.city_not_found",
                    "The city was not found.")
            : null;
    }

    /// <summary>
    /// Spec 2026-11-23: la región existe y es del país del proveedor, o <c>null</c> si no vino ninguna. El validador ya
    /// rechazó la región con Colombia. Un país sin catálogo no tiene filas en <c>geography.regions</c>, así que falla igual
    /// que una región inexistente o de otro país; lo mismo un id de departamento colombiano mandado como región.
    /// </summary>
    public static async Task<SupplierRegionRef?> ResolveRegionAsync(
        this ISupplierWriteCommand command,
        ISupplierGeographyLookup geographyLookup,
        CancellationToken cancellationToken)
    {
        if (command.RegionId is not { } regionId)
        {
            return null;
        }

        var found = await geographyLookup.FindRegionsAsync([regionId], cancellationToken);
        var country = command.Country.Trim().ToUpperInvariant();
        return found.TryGetValue(regionId, out var region)
            && string.Equals(region.CountryCode, country, StringComparison.Ordinal)
            ? region
            : throw new SuppliersDomainException(
                "suppliers.supplier.region_invalid",
                "The region does not exist or does not belong to the country.");
    }

    /// <summary>
    /// Del contrato HTTP al dominio, sin normalizar ni validar: eso es trabajo de
    /// <see cref="Supplier.Create"/> y de <see cref="Supplier.Update"/>, y repetirlo aquí crearía una
    /// segunda definición de «válido». El <c>?? []</c> cubre una lista ausente del JSON, que el
    /// validador ya rechaza antes con su mapa por campo.
    /// </summary>
    public static SupplierWriteData ToWriteData(this ISupplierWriteCommand command) => new(
        command.Name,
        command.BusinessName,
        command.IdentificationType,
        command.IdentificationNumber,
        command.Country,
        command.CityId,
        command.CityName,
        command.Phone,
        command.Email,
        command.Address,
        command.PaymentTermDays,
        command.Category,
        command.Notes,
        [.. (command.BankAccounts ?? []).Select(account => new SupplierBankAccount
        {
            BankName = account.BankName,
            AccountNumber = account.AccountNumber,
            Currency = account.Currency
        })],
        [.. (command.Contacts ?? []).Select(contact => new SupplierContact
        {
            Name = contact.Name,
            Role = contact.Role,
            Phone = contact.Phone,
            Email = contact.Email
        })],
        command.RegionId);
}
