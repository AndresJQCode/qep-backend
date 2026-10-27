namespace Modules.Customers.Domain;

/// <summary>
/// Un cliente del tenant: la contraparte a la que se le cotiza y se le vende. Guarda solo los
/// datos maestros vivos; una cotizacion congela su propia copia de a quien se le emitio.
///
/// La identificacion (tipo + numero) es **unica por tenant entre los clientes no fusionados**: un fusionado completo
/// conserva su documento y otro cliente puede tenerlo (spec 2026-10-24 §3.1). Esa unicidad la arbitra
/// <c>IX_customers_tenant_identification</c>, parcial (<c>merged_into_id IS NULL</c>), y la traduce <c>CustomersUnitOfWork</c>. El CUC lo
/// emite el backend al crear; su prefijo se reescribe cuando cambia la clasificacion del cliente
/// (ver <see cref="Cuc"/> y <see cref="Update"/>), pero el departamento y el consecutivo no.
/// </summary>
public sealed class Customer
{
    private readonly List<CustomerAddress> _addresses = [];

    // Espejan los anchos de columna de customers.customers. Guardar aca significa que un valor
    // demasiado largo falla como 422 con codigo de dominio en vez de llegar a PostgreSQL y volver
    // como 500 server.unexpected. Salen del schema del formulario que ya existe en el frontend
    // (features/customers/types/customer-form.schema.ts).
    public const int NameMaxLength = 160;

    // El CUC que emite el backend es "{prefijo}{depto}{consecutivo}" (ej. CLI08000001): el
    // prefijo de la clasificacion (hasta 20), el codigo DIVIPOLA del departamento (2) y el
    // consecutivo (6). 28 como maximo, y 32 deja margen sin tener que tocar esta constante.
    public const int CucMaxLength = 32;

    /// <summary>Spec 2026-10-10 §6.2: el BSUID de Meta mide hasta 131 (2 + 1 + 128); 150 deja margen.</summary>
    public const int WhatsAppUserIdMaxLength = 150;

    /// <summary>Spec 2026-10-14 decisión 10: el IGSID es numérico y hoy tiene 16 o 17 dígitos; 64 deja margen. El usuario de
    /// Instagram tiene hasta 30 caracteres.</summary>
    public const int InstagramUserIdMaxLength = 64;

    public const int InstagramUsernameMaxLength = 64;

    // P14: libphonenumber ignora la región cuando el número empieza con «+», pero ToE164 exige una.
    private const string UnknownRegion = "ZZ";

    /// <summary>
    /// Colombia en ISO-3166-1 alpha-2. Es una bifurcacion de negocio y no un string suelto: con
    /// este pais el domicilio se ubica con la ciudad DIVIPOLA (<see cref="CityId"/>) y con
    /// cualquier otro con la ciudad escrita a mano (<see cref="CityName"/>).
    /// </summary>
    public const string ColombiaCountryCode = "CO";

    // EF Core materializa por aca. El codigo nunca construye el agregado asi: Create es el unico
    // punto de entrada, y es el que hace cumplir los invariantes.
    private Customer()
    {
        Name = string.Empty;
    }

    private Customer(
        CustomerId id,
        Guid tenantId,
        string cuc,
        string name,
        string? businessName,
        CustomerAddressDetails? principalAddress,
        CustomerIdentification identification,
        PersonType? personType,
        CustomerContactInfo contact,
        CustomerCommercialInfo commercial,
        DateTimeOffset occurredAt,
        IPhoneNumberNormalizer? phoneNormalizer)
    {
        // Antes de cualquier asignación: el país que cuenta es el del contacto ya normalizado.
        EnsurePersonTypeMatches(personType, identification.Type, contact.Normalized().Country);
        Id = id;
        TenantId = tenantId;
        Cuc = cuc;
        Name = name;
        BusinessName = businessName;
        // La primera direccion nace principal: un cliente sin direccion principal deja a la
        // cotizacion sin saber a donde entregar, y su ciudad es la que ya emitio el CUC.
        //
        // Un cliente de afuera nace **sin** libreta: CustomerAddress.CityId es una FK a
        // geography.cities, que es DIVIPOLA, y no hay ciudad colombiana que ponerle. Una fila con
        // una ciudad inventada seria peor que no tenerla.
        if (principalAddress is not null)
        {
            var first = CustomerAddress.Create(id, principalAddress, occurredAt);
            first.MarkPrincipal(true, occurredAt);
            _addresses.Add(first);
        }

        // Directo y no por Assign: el analisis de flujo del compilador no atraviesa metodos, asi
        // que asignar IdentificationNumber alla deja el constructor con un CS8618. Address tiene
        // el mismo problema desde que dejo de ser opcional: se inicializa aca por lo mismo, y
        // Assign(contact) la pisa enseguida con el valor normalizado real. Country igual.
        IdentificationType = identification.Type;
        IdentificationNumber = identification.Number;
        PersonType = personType;
        Address = string.Empty;
        Country = string.Empty;
        Assign(contact);
        PhoneE164 = phoneNormalizer?.ToE164(Phone, Country ?? UnknownRegion);
        Assign(commercial);
        Completeness = CustomerCompleteness.Complete;
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public CustomerId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>
    /// Codigo Unico de Cliente. Lo emite el backend al crear y no viaja en el request de
    /// <see cref="Update"/> — pero no es del todo inmutable: regla de negocio confirmada, cuando
    /// cambia la clasificacion (el "tamano") del cliente, <see cref="Update"/> reescribe
    /// unicamente el prefijo del CUC; el codigo de departamento y el consecutivo (los ultimos
    /// ocho caracteres) se conservan siempre. Es el identificador con el que una persona habla de
    /// este cliente por telefono, y esos ocho caracteres son los que no cambian nunca.
    ///
    /// Donde vive su emision es `SDD-OD-06`, que sigue abierta: hoy la resuelve este modulo porque
    /// no existe un modulo `identifiers` al que delegarla.
    ///
    /// Nulo en un cliente incompleto (spec 2026-10-10 §6.2).
    /// </summary>
    public string? Cuc { get; private set; }

    /// <summary>
    /// A quien se le habla: la persona de contacto del cliente. En un cliente que es una empresa
    /// no es la empresa — para eso esta <see cref="BusinessName"/>.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// La razon social, cuando el cliente es una empresa (CLI-RS-01).
    ///
    /// Opcional a proposito: buena parte del padron son personas, y obligar a repetir el nombre de
    /// contacto acá sería pedir un dato que no existe. Nulo significa "no es una empresa, o no se
    /// cargo" — el formulario ofrece copiar el nombre de contacto para quien prefiera tenerlo
    /// lleno, pero no lo impone.
    /// </summary>
    public string? BusinessName { get; private set; }

    /// <summary>
    /// La libreta de direcciones de envío (CLI-DIR-01): a dónde se le entrega. Siempre hay al
    /// menos una mientras el cliente existe: la primera nace del alta con el mismo par que el
    /// domicilio (decisión 3 del spec 2026-09-18), para que un cliente nuevo tenga a dónde
    /// enviar sin abrir la libreta. Desde ahí son dos datos distintos: editar la ficha no la
    /// toca, y marcar otra principal no mueve <see cref="Address"/> ni <see cref="CityId"/>.
    /// </summary>
    public IReadOnlyCollection<CustomerAddress> Addresses => _addresses;

    /// <summary>La direccion que la cotizacion propone por defecto. Nunca es null en un cliente
    /// creado por <see cref="Create"/>; el tipo lo admite solo porque EF materializa el agregado
    /// sin sus hijos cuando la consulta no los incluye.</summary>
    public CustomerAddress? PrincipalAddress =>
        _addresses.FirstOrDefault(address => address.IsPrincipal);

    /// <summary>La principal, o una excepcion clara si la consulta no incluyo las direcciones.
    /// Todo cliente creado tiene una; que falte solo puede ser un <c>Include</c> olvidado en el
    /// repositorio. Desde el spec 2026-09-18 la lectura del domicilio no pasa por acá
    /// (<see cref="Address"/>/<see cref="CityId"/>); queda para la libreta.</summary>
    public CustomerAddress RequirePrincipalAddress() =>
        PrincipalAddress
            ?? throw new InvalidOperationException(
                $"Customer '{Id}' has no principal address loaded. The query must include "
                + "Addresses.");

    /// <summary>
    /// El tipo de documento. Junto con <see cref="IdentificationNumber"/> forma la clave unica del
    /// cliente dentro del tenant.
    ///
    /// Nulo en un cliente incompleto (spec 2026-10-10 §6.2).
    /// </summary>
    public IdentificationType? IdentificationType { get; private set; }

    /// <summary>
    /// Natural o jurídica (spec 2026-10-26). Nulo es «no se sabe»: un cliente viejo, uno incompleto o uno
    /// que el tenant no obliga a clasificar. Ver <see cref="EnsurePersonTypeMatches"/> para la regla con el NIT.
    /// </summary>
    public PersonType? PersonType { get; private set; }

    /// <summary>Nulo en un cliente incompleto (spec 2026-10-10 §6.2).</summary>
    public string? IdentificationNumber { get; private set; }

    /// <summary>
    /// Las dos partes de la identificacion como el value object que las valida.
    ///
    /// **Calculada, no mapeada.** Las columnas son las dos propiedades planas de arriba porque el
    /// indice unico las necesita indexables, y EF Core no soporta indices sobre las propiedades de
    /// un complex type. Aplanar el mapeo y conservar el value object para las firmas deja las dos
    /// cosas: un `Create` que no permite intercambiar tipo y numero, y un indice que se declara
    /// como cualquier otro.
    ///
    /// <c>null</c> mientras la ficha está incompleta (spec 2026-10-10 §6.2).
    /// </summary>
    public CustomerIdentification? Identification =>
        IdentificationType is { } type && IdentificationNumber is { } number
            ? new() { Type = type, Number = number }
            : null;

    public bool IsActive { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>Spec 2026-10-09 §6.5: <see cref="Phone"/> en E.164 con «+», o <c>null</c> si no parsea. Lo
    /// lee Messaging por sus dígitos para emparejar con el <c>wa_id</c> de Meta.</summary>
    public string? PhoneE164 { get; private set; }

    public string? Email { get; private set; }

    /// <summary>
    /// El domicilio del cliente: la calle. Junto con <see cref="CityId"/> es "dónde está el
    /// cliente" — lo que la ficha muestra en los campos planos, la ciudad por la que el listado
    /// y el reporte lo agrupan, y el respaldo de facturación y envío de la cotización
    /// (<c>QuotationResponseComposer.cs:96-110</c>). **No es la principal de la libreta** (spec
    /// 2026-09-18): <see cref="Addresses"/> son destinos de envío y su principal es sólo la que
    /// se ofrece primero. CLI-DIR-01 había fundido las dos cosas y marcar otra principal movía el
    /// domicilio; volvieron a separarse.
    ///
    /// Nulo en un cliente incompleto (spec 2026-10-10 §6.2).
    /// </summary>
    public string? Address { get; private set; }

    /// <summary>
    /// El pais del cliente, ISO-3166-1 alpha-2. Ver <see cref="CustomerContactInfo.Country"/>.
    ///
    /// Es el discriminante del domicilio: <c>CO</c> ⇒ <see cref="CityId"/>; cualquier otro ⇒
    /// <see cref="CityName"/>. Nunca los dos.
    ///
    /// Nulo en un cliente incompleto (spec 2026-10-10 §6.2).
    /// </summary>
    public string? Country { get; private set; }

    /// <summary>
    /// FK blanda a <c>Modules.Geography</c>: <see cref="Guid"/> y no un id fuertemente tipado de
    /// otro dominio, mismo criterio que <see cref="CustomerAddress.CityId"/>. La FK real
    /// (<c>FK_customers_cities_city_id</c>) la escribe a mano la migración, porque City vive en
    /// otro DbContext.
    ///
    /// **Nula en un cliente que no es de Colombia**: DIVIPOLA es el estandar colombiano, y no hay
    /// fila de <c>geography.cities</c> que describa Madrid. Ese cliente usa
    /// <see cref="CityName"/>.
    /// </summary>
    public Guid? CityId { get; private set; }

    /// <summary>
    /// La ciudad escrita a mano de un cliente de afuera. Nula en un cliente colombiano, que usa
    /// <see cref="CityId"/>. Ver <see cref="CustomerContactInfo.CityName"/>.
    /// </summary>
    public string? CityName { get; private set; }

    /// <summary>
    /// La clasificacion del cliente, FK a <see cref="ClientClassification"/> — que vive en este
    /// mismo modulo, asi que va tipada de forma fuerte igual que el resto del agregado. Reemplaza al
    /// viejo enum fijo <c>CustomerClassification</c>, que no tenia relacion con este catalogo y ya
    /// no tiene consumidores.
    ///
    /// Opcional, tambien en una ficha completa (decision del owner, 2026-10-19): sin ella el CUC no
    /// lleva prefijo. Ver <see cref="CustomerCommercialInfo.ClassificationId"/>. Y siempre nulo en
    /// un cliente incompleto (spec 2026-10-10 §6.2).
    /// </summary>
    public ClientClassificationId? ClassificationId { get; private set; }

    /// <summary>Spec 2026-10-10 §6.2. La base lo exige con <c>CK_customers_complete_fields</c>.</summary>
    public CustomerCompleteness Completeness { get; private set; }

    public bool IsComplete => Completeness == CustomerCompleteness.Complete;

    /// <summary>Spec 2026-10-10 §6.2: el BSUID de WhatsApp de esta persona, único por tenant
    /// (<c>IX_customers_tenant_whatsapp_user_id</c>). Lo pone la ingesta de Messaging; nunca viaja por HTTP.</summary>
    public string? WhatsAppUserId { get; private set; }

    /// <summary>Spec 2026-10-14 decisión 10: el IGSID (id de la persona ante la cuenta profesional del tenant), único por tenant
    /// (<c>IX_customers_tenant_instagram_user_id</c>). Lo pone la ingesta de Messaging; nunca viaja por HTTP.</summary>
    public string? InstagramUserId { get; private set; }

    /// <summary>Decisión 10: el <c>@usuario</c> de Instagram, informativo; puede cambiar y viajar en <c>null</c> (V-7).</summary>
    public string? InstagramUsername { get; private set; }

    public bool WithRetention { get; private set; }

    public bool VatSurplus { get; private set; }

    /// <summary>Spec 2026-10-11 decisión 10: el cliente no quiere recibir promociones. Las campañas con plantilla
    /// <c>MARKETING</c> lo excluyen; las <c>UTILITY</c> no, porque el opt-out de Meta es sólo de marketing.</summary>
    public bool MarketingOptOut { get; private set; }

    public DateTimeOffset? MarketingOptOutAt { get; private set; }

    public MarketingConsentSource? MarketingOptOutSource { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista, como en <c>Company</c>, <c>Product</c> y <c>Membership</c>.
    /// Cada mutacion lo incrementa, y la infraestructura lo mapea con <c>IsConcurrencyToken()</c>,
    /// de modo que el <c>UPDATE</c> lleve la version leida en su <c>WHERE</c>.
    ///
    /// Sin el, dos escrituras que se solapan se pisan en silencio: la segunda no solo pierde la
    /// primera, sino que puede dejar el cliente editado **despues** de inactivarse, porque
    /// <see cref="EnsureActive"/> se evalua contra la copia en memoria del que escribe y no contra
    /// el estado real al momento del commit.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>Spec 2026-10-24 D-F2: el cliente en el que se fusionó este. Siempre apunta a uno que no está fusionado.</summary>
    public CustomerId? MergedIntoId { get; private set; }

    public DateTimeOffset? MergedAt { get; private set; }

    public bool IsMerged => MergedIntoId is not null;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Customer Create(
        CustomerId id,
        Guid tenantId,
        string cuc,
        string name,
        string? businessName,
        CustomerAddressDetails? principalAddress,
        CustomerIdentification identification,
        PersonType? personType,
        CustomerContactInfo contact,
        CustomerCommercialInfo commercial,
        DateTimeOffset occurredAt,
        IPhoneNumberNormalizer? phoneNormalizer = null) =>
        new(
            id,
            tenantId,
            NormalizeCuc(cuc),
            NormalizeName(name),
            NormalizeBusinessName(businessName),
            principalAddress,
            identification.Normalized(),
            personType,
            contact,
            commercial,
            occurredAt,
            phoneNormalizer);

    /// <summary>
    /// Spec 2026-10-10 §6.2: la persona que escribió por WhatsApp y todavía no tiene ficha. Sólo nombre,
    /// teléfono (E.164 con «+», como lo arma Messaging), país si se conoce y BSUID; sin libreta. Lo crea el
    /// sistema, nunca una persona por HTTP. El nombre y el país los decide <c>IncompleteCustomerProfile</c>
    /// (Application); acá sólo se validan.
    /// </summary>
    public static Customer CreateIncomplete(
        CustomerId id,
        Guid tenantId,
        string name,
        string? phone,
        string? country,
        string whatsAppUserId,
        DateTimeOffset now,
        IPhoneNumberNormalizer phoneNormalizer)
    {
        ArgumentNullException.ThrowIfNull(phoneNormalizer);
        var normalizedPhone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        var normalizedCountry = string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();
        return new Customer
        {
            Id = id,
            TenantId = tenantId,
            Name = NormalizeName(name),
            Phone = normalizedPhone,
            Country = normalizedCountry,
            PhoneE164 = normalizedPhone is null ? null : phoneNormalizer.ToE164(normalizedPhone, normalizedCountry ?? UnknownRegion),
            WhatsAppUserId = NormalizeWhatsAppUserId(whatsAppUserId),
            Completeness = CustomerCompleteness.Incomplete,
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Spec «Dominio de Customers»: la ficha que da de alta la caja del POS para identificar al comprador. Nombre,
    /// documento y, si lo hay, teléfono; sin CUC (lo emite <see cref="Complete"/> cuando alguien la completa), sin
    /// clasificación, ciudad, correo ni libreta. El país es el del formulario web (<see cref="ColombiaCountryCode"/>):
    /// la caja no lo pregunta y el teléfono se normaliza contra esa región. Todo se valida antes de armar la ficha.
    /// </summary>
    public static Customer CreateFromRegister(
        CustomerId id,
        Guid tenantId,
        string name,
        CustomerIdentification identification,
        string? phone,
        DateTimeOffset now,
        IPhoneNumberNormalizer phoneNormalizer)
    {
        ArgumentNullException.ThrowIfNull(identification);
        ArgumentNullException.ThrowIfNull(phoneNormalizer);
        var normalizedName = NormalizeName(name);
        var normalizedIdentification = identification.Normalized();
        var normalizedPhone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (normalizedPhone?.Length > CustomerContactInfo.PhoneMaxLength)
        {
            throw new CustomersDomainException(
                "customers.customer.phone_too_long",
                $"The customer phone cannot exceed {CustomerContactInfo.PhoneMaxLength} characters.");
        }

        var customer = new Customer
        {
            Id = id,
            TenantId = tenantId,
            Name = normalizedName,
            Phone = normalizedPhone,
            Country = ColombiaCountryCode,
            PhoneE164 = normalizedPhone is null ? null : phoneNormalizer.ToE164(normalizedPhone, ColombiaCountryCode),
            Completeness = CustomerCompleteness.Incomplete,
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        customer.Assign(normalizedIdentification);
        return customer;
    }

    /// <summary>
    /// Spec 2026-10-10 §6.2: el <c>PUT</c> sobre un incompleto. Mismas reglas que <see cref="Create"/> (la
    /// libreta nace igual) más el CUC que el handler acaba de emitir. Todo se valida antes de asignar: un
    /// 422 deja la ficha como estaba.
    /// </summary>
    public void Complete(
        string cuc,
        string name,
        string? businessName,
        CustomerAddressDetails? principalAddress,
        CustomerIdentification identification,
        PersonType? personType,
        CustomerContactInfo contact,
        CustomerCommercialInfo commercial,
        DateTimeOffset occurredAt,
        IPhoneNumberNormalizer? phoneNormalizer = null)
    {
        ArgumentNullException.ThrowIfNull(identification);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(commercial);
        EnsureNotMerged();
        EnsureActive();
        if (IsComplete)
        {
            throw new InvalidOperationException($"Customer '{Id}' is already complete; use Update.");
        }

        var normalizedCuc = NormalizeCuc(cuc);
        var normalizedName = NormalizeName(name);
        var normalizedBusinessName = NormalizeBusinessName(businessName);
        var normalizedIdentification = identification.Normalized();
        var normalizedContact = contact.Normalized();
        var location = EnsureValidLocation(normalizedContact);
        EnsurePersonTypeMatches(personType, normalizedIdentification.Type, normalizedContact.Country);
        _ = EnsureValidClassificationId(commercial.ClassificationId);

        Cuc = normalizedCuc;
        Name = normalizedName;
        BusinessName = normalizedBusinessName;
        Assign(normalizedIdentification);
        PersonType = personType;
        Assign(normalizedContact);
        PhoneE164 = phoneNormalizer?.ToE164(Phone, location.Country);
        Assign(commercial);
        if (principalAddress is not null && _addresses.Count == 0)
        {
            var first = CustomerAddress.Create(Id, principalAddress, occurredAt);
            first.MarkPrincipal(true, occurredAt);
            _addresses.Add(first);
        }

        Completeness = CustomerCompleteness.Complete;
        Touch(occurredAt);
    }

    /// <summary>
    /// Spec de ciudades 2026-10-26 §4.6 (D-C4b): la ciudad que Shopify le puso al cliente se corrige cuando el operador
    /// aprueba un sinónimo. Sólo cambia si sigue siendo <paramref name="previousCityId"/>; si una persona ya la movió,
    /// lanza <c>customers.customer.city_changed</c> y no toca nada. Mueve <see cref="CityId"/> y la ciudad de la
    /// dirección principal, ésta sólo si también sigue en <paramref name="previousCityId"/>. El CUC no cambia: el
    /// departamento de la ciudad nueva es el mismo (el sinónimo sólo existe dentro de un departamento).
    /// </summary>
    public void CorrectCity(Guid previousCityId, Guid cityId, DateTimeOffset occurredAt)
    {
        EnsureNotMerged();
        EnsureActive();
        if (!IsComplete)
        {
            throw new InvalidOperationException($"Customer '{Id}' is incomplete and has no city to correct.");
        }

        if (CityId != previousCityId)
        {
            throw new CustomersDomainException(CustomersErrorCodes.CityChanged, "The customer city is no longer the one being corrected.");
        }

        CityId = cityId;
        if (PrincipalAddress is { } principal && principal.CityId == previousCityId)
        {
            principal.MoveToCity(cityId, occurredAt);
        }

        Touch(occurredAt);
    }

    /// <summary>La regla del BSUID (recortado, 1 a <see cref="WhatsAppUserIdMaxLength"/> caracteres). Pública porque
    /// <c>CustomerWhatsAppDirectory</c> lo escribe con un <c>UPDATE</c> condicional, sin pasar por el agregado.</summary>
    /// <summary>
    /// Spec 2026-10-14 decisión 10: la persona que escribió por Instagram y todavía no tiene ficha. Sólo nombre, IGSID y usuario:
    /// sin teléfono, sin país (Instagram no los da) y sin libreta. Lo crea el sistema, nunca una persona por HTTP.
    /// </summary>
    public static Customer CreateIncompleteFromInstagram(
        CustomerId id,
        Guid tenantId,
        string name,
        string instagramUserId,
        string? instagramUsername,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            Name = NormalizeName(name),
            InstagramUserId = NormalizeInstagramUserId(instagramUserId),
            InstagramUsername = NormalizeInstagramUsername(instagramUsername),
            Completeness = CustomerCompleteness.Incomplete,
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>
    /// Spec de Shopify 2026-10-15 §6.2.1 paso 4: el comprador de una venta externa que no trajo lo necesario para una
    /// ficha completa. Nombre, teléfono, correo y país si vienen; sin documento, domicilio, clasificación, CUC ni libreta.
    /// Lo crea el sistema (<c>CustomerProvisioning</c>), nunca una persona por HTTP. El nombre por defecto lo decide quien
    /// llama; acá sólo se exige que llegue.
    ///
    /// A diferencia del alta completa, un contacto que no sirve se **descarta** en vez de rechazarse: el owner decidió
    /// que el pedido de Shopify entra aunque el comprador venga a medias, y un correo mal escrito no puede frenarlo. La
    /// ficha queda incompleta y quien la complete pone los datos buenos.
    /// </summary>
    public static Customer CreateIncompleteFromExternalSale(
        CustomerId id,
        Guid tenantId,
        string name,
        string? phone,
        string? email,
        string? country,
        DateTimeOffset now,
        IPhoneNumberNormalizer phoneNormalizer)
    {
        ArgumentNullException.ThrowIfNull(phoneNormalizer);
        var normalizedName = NormalizeName(name);
        var normalizedPhone = string.IsNullOrWhiteSpace(phone) || phone.Trim().Length > CustomerContactInfo.PhoneMaxLength
            ? null
            : phone.Trim();
        var trimmedEmail = email?.Trim();
        var normalizedEmail = string.IsNullOrEmpty(trimmedEmail) ||
                              trimmedEmail.Length > CustomerContactInfo.EmailMaxLength ||
                              !CustomerContactInfo.IsPlausibleEmail(trimmedEmail)
            ? null
            : trimmedEmail.ToLowerInvariant();
        var trimmedCountry = country?.Trim();
        var normalizedCountry = trimmedCountry is { Length: CustomerContactInfo.CountryLength } && trimmedCountry.All(char.IsAsciiLetter)
            ? trimmedCountry.ToUpperInvariant()
            : null;
        return new Customer
        {
            Id = id,
            TenantId = tenantId,
            Name = normalizedName,
            Phone = normalizedPhone,
            Email = normalizedEmail,
            Country = normalizedCountry,
            PhoneE164 = normalizedPhone is null ? null : phoneNormalizer.ToE164(normalizedPhone, normalizedCountry ?? UnknownRegion),
            Completeness = CustomerCompleteness.Incomplete,
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public static string NormalizeInstagramUserId(string value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > InstagramUserIdMaxLength || !trimmed.All(char.IsAsciiDigit)
            ? throw new ArgumentException($"An Instagram user id has 1 to {InstagramUserIdMaxLength} digits.", nameof(value))
            : trimmed;
    }

    public static string? NormalizeInstagramUsername(string? value)
    {
        var trimmed = value?.Trim().TrimStart('@');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= InstagramUsernameMaxLength ? trimmed : trimmed[..InstagramUsernameMaxLength];
    }

    public static string NormalizeWhatsAppUserId(string value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > WhatsAppUserIdMaxLength
            ? throw new ArgumentException($"A WhatsApp user id has 1 to {WhatsAppUserIdMaxLength} characters.", nameof(value))
            : trimmed;
    }

    private void EnsureComplete()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException($"Customer '{Id}' is incomplete; complete it before updating it.");
        }
    }

    /// <summary>Agrega una direccion. La primera de un cliente —o una marcada como principal—
    /// desplaza a la que lo era: el agregado no admite dos.</summary>
    public CustomerAddress AddAddress(
        CustomerAddressDetails details, bool isPrincipal, DateTimeOffset occurredAt)
    {
        EnsureNotMerged();
        EnsureActive();
        // Spec 2026-10-10 §5.2: no hay edición parcial de un incompleto. La libreta nace al completarlo
        // (Complete), con la dirección del PUT.
        if (!IsComplete)
        {
            throw new CustomersDomainException(
                "customers.customer.incomplete",
                "An incomplete customer cannot be edited until it is completed.");
        }

        var address = CustomerAddress.Create(Id, details, occurredAt);
        _addresses.Add(address);
        ApplyPrincipal(address, isPrincipal || _addresses.Count == 1, occurredAt);
        Touch(occurredAt);
        return address;
    }

    /// <summary>Reemplaza los datos de una direccion. Reemplaza el recurso entero, mismo criterio
    /// que <see cref="Update"/>.</summary>
    public CustomerAddress UpdateAddress(
        CustomerAddressId addressId,
        CustomerAddressDetails details,
        bool isPrincipal,
        DateTimeOffset occurredAt)
    {
        EnsureNotMerged();
        EnsureActive();

        var address = FindAddress(addressId);
        address.Apply(details, occurredAt);
        // Desmarcarla no se acepta por esta via: dejaria al cliente sin principal. Se cambia
        // marcando otra, que es lo que MakeAddressPrincipal hace.
        if (isPrincipal)
        {
            ApplyPrincipal(address, true, occurredAt);
        }

        Touch(occurredAt);
        return address;
    }

    public void MakeAddressPrincipal(CustomerAddressId addressId, DateTimeOffset occurredAt)
    {
        EnsureNotMerged();
        EnsureActive();

        ApplyPrincipal(FindAddress(addressId), true, occurredAt);
        Touch(occurredAt);
    }

    /// <summary>
    /// Quita una direccion. La principal no se puede quitar: primero hay que nombrar otra. Es la
    /// regla que hace que la cotizacion siempre tenga una direccion de envio que proponer por
    /// defecto; el domicilio del cliente no depende de la libreta.
    /// </summary>
    public void RemoveAddress(CustomerAddressId addressId, DateTimeOffset occurredAt)
    {
        EnsureNotMerged();
        EnsureActive();

        var address = FindAddress(addressId);
        if (address.IsPrincipal)
        {
            throw new CustomersDomainException(
                "customers.address.principal_not_removable",
                "The principal address cannot be removed. Mark another one as principal first.");
        }

        _addresses.Remove(address);
        Touch(occurredAt);
    }

    private void ApplyPrincipal(
        CustomerAddress address, bool isPrincipal, DateTimeOffset occurredAt)
    {
        if (!isPrincipal)
        {
            return;
        }

        foreach (var other in _addresses)
        {
            other.MarkPrincipal(other == address, occurredAt);
        }
    }

    // Una direccion nueva o cambiada es un cambio del cliente: mueve su version optimista igual
    // que editar su nombre, asi que dos pestañas que lo editan a la vez chocan como corresponde.
    private void Touch(DateTimeOffset occurredAt)
    {
        Version++;
        UpdatedAt = occurredAt;
    }

    private CustomerAddress FindAddress(CustomerAddressId addressId) =>
        _addresses.FirstOrDefault(address => address.Id == addressId)
            ?? throw new CustomersDomainException(
                "customers.address.not_found", "The customer address was not found.");

    /// <summary>
    /// Reemplaza el recurso entero: lo que no viene en el cuerpo se **limpia**. El CUC no viaja en
    /// el request, pero no queda intocado: si <paramref name="commercial"/> trae una clasificacion
    /// distinta a la actual, su prefijo se reescribe con <paramref name="classificationPrefix"/> —
    /// regla de negocio confirmada, "cuando cambie el tamano del cliente, cambiara unicamente el
    /// prefijo; el departamento y el consecutivo se conservaran". Quitar la clasificacion es el
    /// mismo cambio con un prefijo vacio: <paramref name="classificationPrefix"/> llega
    /// <c>null</c>, y solo vale asi cuando la clasificacion tambien lo es.
    ///
    /// La ciudad y la clasificacion **si** se pueden reemplazar aca: un cliente se puede mudar de
    /// ciudad o cambiar de categoria comercial. La ciudad no reconstruye el CUC (el departamento
    /// del codigo es el de alta, no el vigente) — el pedido de negocio solo menciona el prefijo.
    /// </summary>
    public void Update(
        string name,
        string? businessName,
        CustomerIdentification identification,
        PersonType? personType,
        CustomerContactInfo contact,
        CustomerCommercialInfo commercial,
        string? classificationPrefix,
        DateTimeOffset occurredAt,
        IPhoneNumberNormalizer? phoneNormalizer = null)
    {
        EnsureNotMerged();
        EnsureComplete();
        EnsureActive();

        // Todo se normaliza a locales **antes** de asignar nada. Asignar campo por campo mientras
        // se valida deja el agregado a medio escribir cuando el segundo falla: el 422 le dice al
        // llamador que no se guardo nada, pero la instancia en memoria ya tiene el nombre nuevo, y
        // esa es la que EF persiste en el siguiente SaveChanges de la misma unidad de trabajo. Es
        // el defecto que EMP-08 tuvo que corregir en Company; aca nace cubierto. La clasificacion
        // se valida aca tambien (no solo dentro de Assign) porque hace falta **antes** de tocar el
        // CUC, y ese cambio tiene que quedar cubierto por la misma garantia de todo-o-nada.
        var normalizedName = NormalizeName(name);
        var normalizedBusinessName = NormalizeBusinessName(businessName);
        var normalizedIdentification = identification.Normalized();
        var normalizedContact = contact.Normalized();
        // El pais y la ciudad se comprueban acá y no sólo en Assign: hacen falta **antes** de
        // asignar el nombre, dentro de la misma garantía de todo-o-nada que el resto del método.
        EnsureValidLocation(normalizedContact);
        EnsurePersonTypeMatches(personType, normalizedIdentification.Type, normalizedContact.Country);
        var normalizedClassificationId = EnsureValidClassificationId(commercial.ClassificationId);
        // Sin clasificacion no hay prefijo que validar: el CUC queda con sus ocho caracteres
        // estables. Con clasificacion, el prefijo es obligatorio, llegue nulo o en blanco.
        var normalizedClassificationPrefix = normalizedClassificationId is null
            ? string.Empty
            : NormalizeClassificationPrefix(classificationPrefix ?? string.Empty);

        Name = normalizedName;
        BusinessName = normalizedBusinessName;
        Assign(normalizedIdentification);
        PersonType = personType;
        Assign(normalizedContact);
        PhoneE164 = phoneNormalizer?.ToE164(Phone, Country ?? UnknownRegion);

        if (normalizedClassificationId != ClassificationId)
        {
            // EnsureComplete garantiza el CUC: una ficha completa siempre lo tiene (CK_customers_complete_fields).
            Cuc = ReplaceClassificationPrefix(Cuc!, normalizedClassificationPrefix);
        }

        Assign(commercial);
        Version++;
        UpdatedAt = occurredAt;
    }

    /// <summary>El backfill (spec §6.5): recalcula sin subir la versión ni <c>UpdatedAt</c>, porque nadie
    /// editó al cliente. <c>true</c> si cambió.</summary>
    public bool RecomputePhoneE164(IPhoneNumberNormalizer phoneNormalizer)
    {
        ArgumentNullException.ThrowIfNull(phoneNormalizer);

        var next = phoneNormalizer.ToE164(Phone, Country ?? UnknownRegion);
        if (string.Equals(next, PhoneE164, StringComparison.Ordinal))
        {
            return false;
        }

        PhoneE164 = next;
        return true;
    }

    // Desarma el value object en las dos columnas. Ver Customer.Identification para el porque del
    // aplanado.
    private void Assign(CustomerIdentification identification)
    {
        IdentificationType = identification.Type;
        IdentificationNumber = identification.Number;
    }

    // Asigna todo siempre. Telefono y correo se pueden **limpiar**, no solo setear: una
    // implementacion que ignore los null "para no pisar" deja campos imborrables y pasa todas las
    // demas pruebas. La direccion llega validada por Normalized; el par pais/ciudad se resuelve
    // aca con el codigo que este agregado emitia antes de la libreta.
    private void Assign(CustomerContactInfo contact)
    {
        var normalized = contact.Normalized();
        var location = EnsureValidLocation(normalized);

        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
        Country = location.Country;
        CityId = location.CityId;
        CityName = location.CityName;
    }

    private void Assign(CustomerCommercialInfo commercial)
    {
        ClassificationId = EnsureValidClassificationId(commercial.ClassificationId);
        WithRetention = commercial.WithRetention;
        VatSurplus = commercial.VatSurplus;
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
        EnsureNotMerged();
        if (!IsActive)
        {
            throw new CustomersDomainException(
                "customers.customer.already_inactive",
                "The customer is already inactive.");
        }

        IsActive = false;
        Version++;
        UpdatedAt = occurredAt;
    }

    /// <summary>
    /// La vuelta de <see cref="Deactivate"/>.
    ///
    /// `CLI-01` no la pide —su tabla de contratos solo lista <c>/deactivate</c>—, pero sin ella un
    /// cliente inactivo seria terminal: <see cref="Update"/> abre con <see cref="EnsureActive"/> y
    /// ningun otro metodo devuelve <see cref="IsActive"/> a true. Es la falta que `CAT-07` tuvo
    /// que corregir en producto despues de entregarlo, y que `EMP-08` ya nacio cubriendo. No
    /// estrena permiso: reactivar es administrar.
    ///
    /// No revalida la unicidad de la identificacion: inactivar no libera el documento, asi que
    /// reactivar no puede colisionar con nadie. Un cliente fusionado si libera el suyo (el indice
    /// pasa a ser parcial, spec 2026-10-24), pero no se reactiva: <see cref="EnsureNotMerged"/>
    /// lo rechaza antes de llegar aca.
    /// </summary>
    public void Activate(DateTimeOffset occurredAt)
    {
        EnsureNotMerged();
        if (IsActive)
        {
            throw new CustomersDomainException(
                "customers.customer.already_active",
                "The customer is already active.");
        }

        IsActive = true;
        Version++;
        UpdatedAt = occurredAt;
    }

    /// <summary>
    /// Apaga el marketing. Idempotente: si ya estaba apagado conserva el momento y el origen originales —el primero
    /// es el que explica por qué— y devuelve <c>false</c>.
    ///
    /// No sube <see cref="Version"/> ni <see cref="UpdatedAt"/> a propósito: es un interruptor aparte de la ficha,
    /// y subirla le daría un 412 a quien la tiene abierta para editarla (misma razón por la que las notas son otro
    /// agregado).
    /// </summary>
    public bool OptOutOfMarketing(MarketingConsentSource source, DateTimeOffset occurredAt)
    {
        EnsureNotMerged();
        if (MarketingOptOut)
        {
            return false;
        }

        MarketingOptOut = true;
        MarketingOptOutAt = occurredAt;
        MarketingOptOutSource = source;
        return true;
    }

    /// <summary>La vuelta de <see cref="OptOutOfMarketing"/>: borra el rastro. <c>false</c> si ya aceptaba marketing.</summary>
    public bool OptInToMarketing()
    {
        EnsureNotMerged();
        if (!MarketingOptOut)
        {
            return false;
        }

        MarketingOptOut = false;
        MarketingOptOutAt = null;
        MarketingOptOutSource = null;
        return true;
    }

    /// <summary>Spec 2026-10-24 §3.1: va primero en toda escritura, antes de <see cref="EnsureActive"/>, para que el
    /// código diga «fusionado» y no «inactivo».</summary>
    public void EnsureNotMerged()
    {
        if (IsMerged)
        {
            throw new CustomersDomainException(CustomersErrorCodes.Merged, "A merged customer cannot be edited.");
        }
    }

    // Las usa sólo CustomerMergePlan (mismo ensamblado): la fusión escribe campos que ninguna operación pública toca.
    internal void ApplyMergedInto(
        CustomerId survivorId, DateTimeOffset now, bool releaseWhatsAppUserId, bool releaseInstagram, bool releaseIdentification)
    {
        MergedIntoId = survivorId;
        MergedAt = now;
        IsActive = false;
        if (releaseWhatsAppUserId)
        {
            WhatsAppUserId = null;
        }

        if (releaseInstagram)
        {
            InstagramUserId = null;
            InstagramUsername = null;
        }

        if (releaseIdentification)
        {
            IdentificationType = null;
            IdentificationNumber = null;
        }

        Touch(now);
    }

    internal void FillEmail(string value) => Email = value;

    internal void FillBusinessName(string value) => BusinessName = value;

    internal void FillPhone(string value, string? e164)
    {
        Phone = value;
        PhoneE164 = e164;
    }

    internal void FillWhatsAppUserId(string value) => WhatsAppUserId = value;

    internal void FillInstagram(string userId, string? username)
    {
        InstagramUserId = userId;
        InstagramUsername = username;
    }

    internal void FillIdentification(IdentificationType type, string number)
    {
        IdentificationType = type;
        IdentificationNumber = number;
    }

    internal void FillCountry(string value) => Country = value;

    internal void FillAddress(string value) => Address = value;

    internal void TouchForMerge(DateTimeOffset now) => Touch(now);

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new CustomersDomainException(
                CustomersErrorCodes.Inactive,
                "An inactive customer cannot be edited.");
        }
    }

    // Recortar espacios es parte del invariante, no higiene del llamador: sin recortar, " Verde" y
    // "Verde" son dos nombres distintos para cualquier comparacion, cosa que nadie leyendo la
    // lista haria.
    private static string NormalizeName(string name) =>
        Normalize(
            name,
            NameMaxLength,
            "customers.customer.name_required",
            "The customer name is required.",
            "customers.customer.name_too_long",
            $"The customer name cannot exceed {NameMaxLength} characters.");

    // Vacio y ausente son lo mismo: un formulario que manda la razon social en blanco esta
    // diciendo "este cliente no es una empresa", no guardando una cadena vacia. Mismo criterio que
    // el resto de los opcionales del modulo.
    private static string? NormalizeBusinessName(string? businessName)
    {
        if (string.IsNullOrWhiteSpace(businessName))
        {
            return null;
        }

        var trimmed = businessName.Trim();
        return trimmed.Length > NameMaxLength
            ? throw new CustomersDomainException(
                "customers.customer.business_name_too_long",
                $"The customer business name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }

    // El CUC llega ya formado desde ICucGenerator + CucFormatter; aca solo se comprueba que
    // llegue y quepa. Un cliente sin CUC es una celda vacia en la grilla y un cliente que la caja
    // de busqueda del listado —que busca por nombre, identificacion o CUC— no encuentra.
    private static string NormalizeCuc(string cuc) =>
        Normalize(
            cuc,
            CucMaxLength,
            "customers.customer.cuc_required",
            "The customer CUC is required.",
            "customers.customer.cuc_too_long",
            $"The customer CUC cannot exceed {CucMaxLength} characters.");

    // El CUC es "{prefijo}{depto}{consecutivo}" (CucFormatter, en Application): el codigo de
    // departamento DIVIPOLA son siempre 2 digitos y el consecutivo siempre 6
    // (CucFormatter.SequenceDigits) — los ultimos ocho caracteres de cualquier CUC valido. Cambiar
    // de clasificacion solo reescribe lo que viene antes de eso. Publico porque la importacion
    // masiva (Application) tambien lo necesita: matchear un cliente existente por este mismo
    // sufijo, no por el CUC completo, es lo unico estable si el prefijo cambio desde que se
    // exporto ese CUC.
    public const int CucSuffixLength = 8;

    /// <summary>
    /// La parte de un CUC que nunca cambia: sus ultimos <see cref="CucSuffixLength"/> caracteres.
    /// Un solo lugar para este invariante — <see cref="ReplaceClassificationPrefix"/> y la
    /// importacion masiva (<c>ImportCustomersHandler</c>) lo comparten en vez de repetir el
    /// "ultimos ocho caracteres" en dos archivos.
    /// </summary>
    public static string StableSuffixOf(string cuc) => cuc[^CucSuffixLength..];

    private static string ReplaceClassificationPrefix(string cuc, string newPrefix) =>
        newPrefix + StableSuffixOf(cuc);

    // Mismo criterio que NormalizeName/NormalizeCuc: el prefijo llega resuelto desde
    // ClientClassification, pero Update lo vuelve a validar aca porque hace falta **antes** de
    // tocar el CUC, dentro de la misma garantia de todo-o-nada que el resto del metodo.
    private static string NormalizeClassificationPrefix(string prefix) =>
        Normalize(
            prefix,
            ClientClassification.PrefixMaxLength,
            "customers.customer.classification_prefix_required",
            "The classification prefix is required.",
            "customers.customer.classification_prefix_too_long",
            $"The classification prefix cannot exceed {ClientClassification.PrefixMaxLength} characters.");

    /// <summary>
    /// Resuelve el domicilio contra el pais y devuelve el trio ya coherente.
    ///
    /// Los dos carriles son excluyentes y este metodo los **limpia**, no solo los valida: un
    /// cliente colombiano se queda sin <c>CityName</c> y uno de afuera sin <c>CityId</c>. Guardar
    /// los dos dejaria dos respuestas a "donde esta el cliente" sin nada que diga cual gana al
    /// pintar la ficha — y la que sobra envejece sola, porque el formulario ya no la muestra.
    ///
    /// Para Colombia el chequeo es estructural y minimo (no vacia): la FK de base
    /// (<c>customers.customers.city_id -> geography.cities.id</c>) garantiza que la ciudad exista,
    /// pero no corre hasta el SaveChanges, y fallar rapido con un codigo de dominio es mejor que
    /// dejar que un Guid.Empty vuelva como una violacion de FK que no dice nada util.
    /// </summary>
    private static (string Country, Guid? CityId, string? CityName) EnsureValidLocation(
        CustomerContactInfo normalized)
    {
        // Normalized() ya rechazo un pais ausente o que no sean dos letras.
        var country = normalized.Country;

        if (country == ColombiaCountryCode)
        {
            return normalized.CityId is { } cityId && cityId != Guid.Empty
                ? (country, cityId, null)
                : throw new CustomersDomainException(
                    "customers.customer.city_required",
                    "The customer city is required.");
        }

        return string.IsNullOrWhiteSpace(normalized.CityName)
            ? throw new CustomersDomainException(
                "customers.customer.city_name_required",
                "The customer city is required for a customer outside Colombia.")
            : (country, null, normalized.CityName);
    }

    /// <summary>
    /// Spec 2026-10-26: en Colombia una persona jurídica se identifica con NIT. Fuera de Colombia el NIT no
    /// aplica y la regla tampoco. Se comprueba **antes** de asignar nada, dentro de la garantía de
    /// todo-o-nada de <see cref="Update"/> y <see cref="Complete"/>. Marca <c>IdentificationType</c> porque es el
    /// campo que la persona corrige en el formulario.
    /// </summary>
    private static void EnsurePersonTypeMatches(
        PersonType? personType, IdentificationType identificationType, string country)
    {
        if (personType != Domain.PersonType.Juridica
            || identificationType == Domain.IdentificationType.Nit
            || !string.Equals(country, ColombiaCountryCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new CustomersDomainException(
            CustomersErrorCodes.PersonTypeRequiresNit,
            "A legal entity is identified by its NIT.",
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["IdentificationType"] = ["A legal entity is identified by its NIT."]
            });
    }

    // Nulo es un cliente sin clasificacion y vale. Un Guid.Empty, en cambio, es un id roto y no
    // una forma de decir "ninguna": se sigue rechazando.
    private static ClientClassificationId? EnsureValidClassificationId(
        ClientClassificationId? classificationId) =>
        classificationId is { Value: var value } && value == Guid.Empty
            ? throw new CustomersDomainException(
                "customers.customer.classification_required",
                "The customer classification is required.")
            : classificationId;

    private static string Normalize(
        string value,
        int maxLength,
        string requiredCode,
        string requiredMessage,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CustomersDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CustomersDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }
}
