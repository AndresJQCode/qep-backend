using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>El documento del comprador como lo leyó el importador: tipo ya normalizado (D-S22) y número recortado.</summary>
public sealed record ShopifyBuyerDocument(string Type, string Number);

/// <summary>
/// Spec 2026-10-15 §6.2.1: quién es el comprador de un pedido de Shopify en QEP. Está partido en dos a propósito: el importador
/// <b>busca</b> antes de las precondiciones de productos (un cliente encontrado inactivo frena con <c>customer_inactive</c>, que va
/// antes que <c>product_unmatched</c>) y <b>crea</b> sólo cuando ninguna precondición frena, para que un pedido que espera un
/// producto no deje una ficha incompleta suelta en Clientes. El cliente nunca detiene la importación: si no alcanza para completo,
/// nace incompleto.
/// </summary>
public sealed class ShopifyCustomerResolver(
    IShopifyCustomerDirectory customers, IShopifyOrderMirrorRepository mirrors, IShopifyGeographyLookup geography)
{
    /// <summary>§6.2.1 paso 4: el nombre de un comprador que no trae ninguno. Customers pone el mismo si llega en blanco; se manda
    /// explícito porque es un texto de Shopify, no de Customers.</summary>
    public const string FallbackName = "Cliente de Shopify";

    /// <summary>El ancho de <c>customers.identification_number</c>. Uno más largo no es un documento que Customers pueda guardar ni
    /// encontrar, y recortarlo daría un documento que no es el del comprador: se trata como si no hubiera venido.</summary>
    public const int DocumentNumberMaxLength = 32;

    /// <summary>El ancho de <c>customers.name</c> (<c>Customer.NameMaxLength</c>). Shopify acepta 255 en el nombre y otros 255
    /// en el apellido: sin recortar acá, el alta completa no pasa la validación del <c>POST</c> de clientes y un comprador con
    /// todo para ficha completa nacería incompleto. Se recorta igual que Customers recorta el nombre de un incompleto.</summary>
    public const int NameMaxLength = 160;

    private const string Colombia = "CO";

    /// <summary>§6.2.1 paso 1: <c>customAttributes[document_attribute_key]</c> y su tipo, o el tipo por defecto de la tienda si no
    /// viene o no es uno de los cuatro (D-S22). La clave se busca exacta y, si no está, sin mayúsculas ni espacios: la escribe a
    /// mano quien arma el checkout.</summary>
    public static ShopifyBuyerDocument? DocumentOf(ShopifyStore store, ShopifyOrderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshot);
        var number = ShopifyDocumentSources.Read(snapshot, store.DocumentAttributeKey)?.Trim();
        if (string.IsNullOrEmpty(number) || number.Length > DocumentNumberMaxLength)
        {
            return null;
        }

        var type = ShopifyDocumentTypes.Normalize(ShopifyDocumentSources.Attribute(snapshot.Attributes, store.DocumentTypeAttributeKey))
            ?? ShopifyDocumentTypes.Normalize(store.DefaultIdentificationType)
            ?? ShopifyStore.DefaultIdentificationTypeCode;
        return new ShopifyBuyerDocument(type, number);
    }

    /// <summary>
    /// §6.2.1 paso 2, en orden, y gana el primero: el cliente que esta misma fila ya anotó (D-S24: el reintento usa ese y no crea
    /// otro), el documento, el comprador de Shopify ya visto en el espejo de esta tienda, el teléfono y el correo. Un cliente
    /// encontrado se devuelve tal cual, completo o incompleto, activo o no: decidir qué hacer con uno inactivo es del importador.
    /// </summary>
    public async Task<ShopifyCustomerRef?> FindAsync(
        ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var tenantId = row.TenantId;
        if (row.QepCustomerId is { } attached && await customers.FindByIdAsync(tenantId, attached, cancellationToken) is { } own)
        {
            return own;
        }

        if (DocumentOf(store, snapshot) is { } document
            && await customers.FindByDocumentAsync(tenantId, document.Type, document.Number, cancellationToken) is { } byDocument)
        {
            return byDocument;
        }

        if (snapshot.CustomerId is { } shopifyCustomerId
            && await mirrors.FindSeenCustomerIdAsync(tenantId, row.ConnectionId, shopifyCustomerId, cancellationToken) is { } seenId
            && seenId != row.QepCustomerId
            && await customers.FindByIdAsync(tenantId, seenId, cancellationToken) is { } seen)
        {
            return seen;
        }

        if (PhoneOf(snapshot) is { } phone
            && await customers.FindByPhoneAsync(tenantId, phone, CountryOf(snapshot), cancellationToken) is { } byPhone)
        {
            return byPhone;
        }

        return EmailOf(snapshot) is { } email
            ? await customers.FindByEmailAsync(tenantId, email, cancellationToken)
            : null;
    }

    /// <summary>
    /// §6.2.1 pasos 3 y 4: completo si hay documento, nombre, clasificación por defecto en la tienda, dirección de envío y ciudad
    /// DIVIPOLA (o un país que no es <c>CO</c>, con la ciudad como texto); si Customers dice que esos datos no alcanzan
    /// (<see cref="ShopifyCustomerNotCompletableException"/>: sin correo, sin teléfono, clasificación o ciudad que ya no existen),
    /// incompleto. Cualquier otro error se propaga: es transitorio y la entrega reintenta.
    /// </summary>
    public async Task<ShopifyCustomerRef> CreateAsync(ShopifyStore store, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshot);
        var name = NameOf(snapshot);
        var phone = PhoneOf(snapshot);
        var email = EmailOf(snapshot);
        var country = CountryOf(snapshot);
        if (await CompleteDataAsync(store, snapshot, name, phone, email, country, cancellationToken) is { } complete)
        {
            try
            {
                return await customers.CreateCompleteAsync(store.TenantId, complete, cancellationToken);
            }
            catch (ShopifyCustomerNotCompletableException)
            {
                // §6.2: el cliente nunca detiene la importación. El documento que vino queda en el aviso customer_incomplete.
            }
        }

        return await customers.CreateIncompleteAsync(store.TenantId, name ?? FallbackName, phone, email, country, cancellationToken);
    }

    /// <summary>
    /// §6.2.1 paso 5: la ciudad DIVIPOLA de la dirección de envío, sólo en Colombia. Primero por el código del departamento
    /// (<see cref="ColombiaProvinceCodes"/>) y, si no da, por el nombre de <c>province</c>. Geography compara sin tildes ni
    /// mayúsculas (<c>NameMatching.Normalize</c>), así que «MEDELLÍN» y «medellin» son la misma. Público porque la dirección de envío
    /// del pedido (<see cref="ShopifyOrderImporter"/>) usa la misma regla.
    /// </summary>
    public async Task<ShopifyCityRef?> ResolveCityAsync(ShopifyAddress? address, CancellationToken cancellationToken)
    {
        var city = address?.City?.Trim();
        if (address is null || string.IsNullOrEmpty(city) || !string.Equals(Country(address), Colombia, StringComparison.Ordinal))
        {
            return null;
        }

        var department = ColombiaProvinceCodes.DepartmentNameFor(address.ProvinceCode ?? string.Empty);
        if (department is not null && await geography.FindCityAsync(department, city, cancellationToken) is { } byCode)
        {
            return byCode;
        }

        var province = address.Province?.Trim();
        return string.IsNullOrEmpty(province) || string.Equals(province, department, StringComparison.OrdinalIgnoreCase)
            ? null
            : await geography.FindCityAsync(province, city, cancellationToken);
    }

    private async Task<ShopifyCompleteCustomer?> CompleteDataAsync(
        ShopifyStore store,
        ShopifyOrderSnapshot snapshot,
        string? name,
        string? phone,
        string? email,
        string? country,
        CancellationToken cancellationToken)
    {
        // Un cliente completo con el nombre de relleno sería peor que uno incompleto: el que complete la ficha no sabría que falta.
        if (DocumentOf(store, snapshot) is not { } document
            || store.DefaultClassificationId is not { } classificationId
            || name is null
            || country is null
            || snapshot.ShippingAddress is not { } address
            || AddressLine(address) is not { } line)
        {
            return null;
        }

        Guid? cityId = null;
        string? cityName = null;
        if (string.Equals(country, Colombia, StringComparison.Ordinal))
        {
            if (await ResolveCityAsync(address, cancellationToken) is not { } city)
            {
                return null;
            }

            cityId = city.CityId;
        }
        else
        {
            cityName = Blank(address.City);
        }

        return new ShopifyCompleteCustomer(
            name, document.Type, document.Number, phone, email, line, country, cityId, cityName, classificationId);
    }

    private static string? NameOf(ShopifyOrderSnapshot snapshot)
    {
        var buyer = string.Join(' ', new[] { snapshot.Buyer.FirstName, snapshot.Buyer.LastName }
            .Select(Blank)
            .OfType<string>());
        var name = buyer.Length > 0 ? buyer : Blank(snapshot.ShippingAddress?.Name);
        return name is null || name.Length <= NameMaxLength ? name : name[..NameMaxLength].TrimEnd();
    }

    private static string? PhoneOf(ShopifyOrderSnapshot snapshot) => Blank(snapshot.Buyer.Phone) ?? Blank(snapshot.ShippingAddress?.Phone);

    private static string? EmailOf(ShopifyOrderSnapshot snapshot) => Blank(snapshot.Buyer.Email);

    private static string? CountryOf(ShopifyOrderSnapshot snapshot) =>
        snapshot.ShippingAddress is { } address ? Country(address) : null;

    private static string? Country(ShopifyAddress address) => Blank(address.CountryCode)?.ToUpperInvariant();

    private static string? AddressLine(ShopifyAddress address)
    {
        var line = string.Join(' ', new[] { address.Address1, address.Address2 }.Select(Blank).OfType<string>());
        return line.Length > 0 ? line : null;
    }

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
