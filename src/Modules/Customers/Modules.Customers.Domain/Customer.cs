namespace Modules.Customers.Domain;

/// <summary>
/// Un cliente del tenant: la contraparte a la que se le cotiza y se le vende. Guarda solo los
/// datos maestros vivos; una cotizacion congela su propia copia de a quien se le emitio.
///
/// La identificacion (tipo + numero) es **unica por tenant**; esa unicidad la arbitra
/// <c>IX_customers_tenant_identification</c> y la traduce <c>CustomersUnitOfWork</c>. El CUC lo
/// emite el backend al crear y no cambia nunca mas.
/// </summary>
public sealed class Customer
{
    // Espejan los anchos de columna de customers.customers. Guardar aca significa que un valor
    // demasiado largo falla como 422 con codigo de dominio en vez de llegar a PostgreSQL y volver
    // como 500 server.unexpected. Salen del schema del formulario que ya existe en el frontend
    // (features/customers/types/customer-form.schema.ts).
    public const int NameMaxLength = 160;

    // El CUC que emite el backend es "{prefijo}{depto}{consecutivo}" (ej. CLI08000001): el
    // prefijo de la clasificacion (hasta 20), el codigo DIVIPOLA del departamento (2) y el
    // consecutivo (6). 28 como maximo, y 32 deja margen sin tener que tocar esta constante.
    public const int CucMaxLength = 32;

    // EF Core materializa por aca. El codigo nunca construye el agregado asi: Create es el unico
    // punto de entrada, y es el que hace cumplir los invariantes.
    private Customer()
    {
        Cuc = string.Empty;
        Name = string.Empty;
        IdentificationNumber = string.Empty;
    }

    private Customer(
        CustomerId id,
        Guid tenantId,
        string cuc,
        string name,
        Guid cityId,
        CustomerIdentification identification,
        CustomerContactInfo contact,
        CustomerCommercialInfo commercial,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Cuc = cuc;
        Name = name;
        CityId = EnsureValidCityId(cityId);
        // Directo y no por Assign: el analisis de flujo del compilador no atraviesa metodos, asi
        // que asignar IdentificationNumber alla deja el constructor con un CS8618.
        IdentificationType = identification.Type;
        IdentificationNumber = identification.Number;
        Assign(contact);
        Assign(commercial);
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public CustomerId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>
    /// Codigo Unico de Cliente. Lo emite el backend al crear y **no se edita nunca**: es el
    /// identificador con el que una persona habla de este cliente por telefono, y volverlo mutable
    /// hace que la conversacion de ayer deje de referirse a nadie. Por eso no viaja en el request
    /// y <see cref="Update"/> no lo toca.
    ///
    /// Donde vive su emision es `SDD-OD-06`, que sigue abierta: hoy la resuelve este modulo porque
    /// no existe un modulo `identifiers` al que delegarla.
    /// </summary>
    public string Cuc { get; private set; }

    public string Name { get; private set; }

    /// <summary>
    /// La ciudad del cliente, FK al modulo <c>Geography</c>. Es estructural y obligatoria — no es
    /// "info de contacto libre" como lo era el texto plano anterior, asi que vive de primer nivel
    /// en el agregado y no dentro de <see cref="CustomerContactInfo"/>. Es tambien la mitad del
    /// departamento que arma el CUC: <c>CreateCustomerHandler</c> resuelve el departamento de esta
    /// ciudad antes de emitir el codigo.
    ///
    /// Tipada como <see cref="Guid"/> y no con un id fuertemente tipado de
    /// <c>Modules.Geography.Domain</c>: ningun modulo de dominio de este repo referencia el
    /// dominio de otro (ni siquiera Catalog hacia Storage), y esta es la primera FK real entre
    /// modulos de negocio — cambiar ese precedente ahora acoplaria los dos ensamblados de dominio.
    /// </summary>
    public Guid CityId { get; private set; }

    /// <summary>
    /// El tipo de documento. Junto con <see cref="IdentificationNumber"/> forma la clave unica del
    /// cliente dentro del tenant.
    /// </summary>
    public IdentificationType IdentificationType { get; private set; }

    public string IdentificationNumber { get; private set; }

    /// <summary>
    /// Las dos partes de la identificacion como el value object que las valida.
    ///
    /// **Calculada, no mapeada.** Las columnas son las dos propiedades planas de arriba porque el
    /// indice unico las necesita indexables, y EF Core no soporta indices sobre las propiedades de
    /// un complex type. Aplanar el mapeo y conservar el value object para las firmas deja las dos
    /// cosas: un `Create` que no permite intercambiar tipo y numero, y un indice que se declara
    /// como cualquier otro.
    /// </summary>
    public CustomerIdentification Identification =>
        new() { Type = IdentificationType, Number = IdentificationNumber };

    public bool IsActive { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Address { get; private set; }

    /// <summary>
    /// La clasificacion del cliente, FK a <see cref="ClientClassification"/> — que vive en este
    /// mismo modulo, asi que va tipada de forma fuerte igual que el resto del agregado.
    /// Obligatoria: reemplaza al viejo enum fijo <c>CustomerClassification</c>, que no tenia
    /// relacion con este catalogo y ya no tiene consumidores.
    /// </summary>
    public ClientClassificationId ClassificationId { get; private set; }

    public bool WithRetention { get; private set; }

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

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Customer Create(
        CustomerId id,
        Guid tenantId,
        string cuc,
        string name,
        Guid cityId,
        CustomerIdentification identification,
        CustomerContactInfo contact,
        CustomerCommercialInfo commercial,
        DateTimeOffset occurredAt) =>
        new(
            id,
            tenantId,
            NormalizeCuc(cuc),
            NormalizeName(name),
            cityId,
            identification.Normalized(),
            contact,
            commercial,
            occurredAt);

    /// <summary>
    /// Reemplaza el recurso entero: lo que no viene en el cuerpo se **limpia**. El CUC es la unica
    /// excepcion, y no porque se conserve "por las dudas" — es que no viaja en el request.
    ///
    /// La ciudad y la clasificacion **si** se pueden reemplazar aca: un cliente se puede mudar de
    /// ciudad o cambiar de categoria comercial, a diferencia del CUC, que es un identificador y no
    /// un dato que describa al cliente hoy.
    /// </summary>
    public void Update(
        string name,
        Guid cityId,
        CustomerIdentification identification,
        CustomerContactInfo contact,
        CustomerCommercialInfo commercial,
        DateTimeOffset occurredAt)
    {
        EnsureActive();

        // Todo se normaliza a locales **antes** de asignar nada. Asignar campo por campo mientras
        // se valida deja el agregado a medio escribir cuando el segundo falla: el 422 le dice al
        // llamador que no se guardo nada, pero la instancia en memoria ya tiene el nombre nuevo, y
        // esa es la que EF persiste en el siguiente SaveChanges de la misma unidad de trabajo. Es
        // el defecto que EMP-08 tuvo que corregir en Company; aca nace cubierto.
        var normalizedName = NormalizeName(name);
        var normalizedCityId = EnsureValidCityId(cityId);
        var normalizedIdentification = identification.Normalized();
        var normalizedContact = contact.Normalized();

        Name = normalizedName;
        CityId = normalizedCityId;
        Assign(normalizedIdentification);
        Assign(normalizedContact);
        Assign(commercial);
        Version++;
        UpdatedAt = occurredAt;
    }

    // Desarma el value object en las dos columnas. Ver Customer.Identification para el porque del
    // aplanado.
    private void Assign(CustomerIdentification identification)
    {
        IdentificationType = identification.Type;
        IdentificationNumber = identification.Number;
    }

    // Asigna los tres siempre, incluidos los null. Se puede **limpiar** un campo, no solo
    // setearlo: una implementacion que ignore los null "para no pisar" deja campos imborrables y
    // pasa todas las demas pruebas.
    private void Assign(CustomerContactInfo contact)
    {
        var normalized = contact.Normalized();

        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
    }

    private void Assign(CustomerCommercialInfo commercial)
    {
        ClassificationId = EnsureValidClassificationId(commercial.ClassificationId);
        WithRetention = commercial.WithRetention;
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
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
    /// No revalida la unicidad de la identificacion: <c>IX_customers_tenant_identification</c> es
    /// unico **sin filtro parcial**, asi que inactivar nunca libero el documento y reactivar no
    /// puede colisionar con nadie. Si alguien le agrega un filtro parcial al indice, esta
    /// suposicion deja de valer.
    /// </summary>
    public void Activate(DateTimeOffset occurredAt)
    {
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

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new CustomersDomainException(
                "customers.customer.inactive",
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

    // La FK de base (customers.customers.city_id -> geography.cities.id) garantiza que la ciudad
    // exista, pero no corre hasta el SaveChanges. Este chequeo estructural minimo —no vacia— es lo
    // unico que el dominio puede afirmar por si mismo, mismo criterio que el resto de los
    // required de este agregado: fallar rapido con un codigo de dominio en vez de dejar que un
    // Guid.Empty viaje hasta Postgres y vuelva como una violacion de FK que no dice nada util.
    private static Guid EnsureValidCityId(Guid cityId) =>
        cityId == Guid.Empty
            ? throw new CustomersDomainException(
                "customers.customer.city_required",
                "The customer city is required.")
            : cityId;

    private static ClientClassificationId EnsureValidClassificationId(
        ClientClassificationId classificationId) =>
        classificationId.Value == Guid.Empty
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
