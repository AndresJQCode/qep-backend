namespace Modules.Customers.Domain;

/// <summary>
/// Que un cliente tiene asignada una lista de precios del módulo <c>pricing</c>. Es la relación
/// N:N entre <see cref="Customer"/> y la lista de precios — un cliente puede tener varias a la
/// vez (Mayorista y VIP, por ejemplo), así que no es un campo de <see cref="Customer"/> ni de
/// <see cref="CustomerCommercialInfo"/>, es su propio agregado chico.
///
/// Entidad propia con <c>Id</c> propio, no una clave compuesta pura — mismo criterio que
/// <c>Membership</c> en Tenancy (User↔Tenant, también una referencia cruzada de módulo): más
/// simple de mapear en EF Core que una clave primaria de dos columnas, y deja espacio para que
/// el día que el negocio pida vigencia o prioridad entre listas (ver la nota en el módulo
/// `pricing`), esos campos tengan dónde vivir sin migrar la clave primaria.
///
/// <c>PriceListId</c> es <c>Guid</c> **sin clave foránea**: `pricing` es otro módulo de negocio,
/// y ninguno referencia las tablas del otro — mismo criterio que `Customer.CityId` no lo tiene
/// hacia `Geography` cuando cruza de módulo... salvo que acá, a diferencia de la ciudad,
/// tampoco hay una migración a mano que agregue la FK real: la única red es
/// <c>ICustomerPriceListLookup</c> (ver <c>CustomerPriceListResolver</c>), que valida existencia
/// y estado activo antes de que este agregado se construya.
/// </summary>
public sealed class CustomerPriceList
{
    // EF Core materializa por acá. El código nunca construye el agregado así: Create es el único
    // punto de entrada.
    private CustomerPriceList()
    {
    }

    private CustomerPriceList(
        CustomerPriceListId id,
        Guid tenantId,
        CustomerId customerId,
        Guid priceListId,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        CustomerId = customerId;
        PriceListId = priceListId;
        CreatedAt = occurredAt;
    }

    public CustomerPriceListId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public CustomerId CustomerId { get; private set; }

    public Guid PriceListId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static CustomerPriceList Create(
        CustomerPriceListId id,
        Guid tenantId,
        CustomerId customerId,
        Guid priceListId,
        DateTimeOffset occurredAt)
    {
        if (priceListId == Guid.Empty)
        {
            throw new CustomersDomainException(
                "customers.customer.price_list_required",
                "The price list id is required.");
        }

        return new CustomerPriceList(id, tenantId, customerId, priceListId, occurredAt);
    }
}
