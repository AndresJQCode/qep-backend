namespace Modules.Customers.Domain;

/// <summary>
/// Una clasificacion de clientes del catalogo de un tenant: nombre y prefijo. Es un catalogo de
/// referencia chico, tenant-scoped, con el mismo shape que <c>TaxRate</c> en Catalog — nombre
/// unico por tenant, estado activo/inactivo reversible, y borrado fisico separado.
///
/// No confundir con <see cref="CustomerClassification"/>: ese es un enum fijo (tamano de
/// cliente: Pequeno/Mediano/Grande) que ya usa <c>Customer.Classification</c>. Este agregado es
/// un catalogo distinto — clasificaciones con nombre y prefijo configurables por tenant — que
/// hoy no tiene ninguna relacion con Customer.
/// </summary>
public sealed class ClientClassification
{
    // Espeja el ancho de customers.client_classifications.name, por la misma razon que TaxRate:
    // un valor demasiado largo falla como 422 con codigo de dominio en vez de llegar a
    // PostgreSQL y volver como 500 server.unexpected.
    public const int NameMaxLength = 120;

    public const int PrefixMaxLength = 20;

    // EF Core materializa por aca. El codigo nunca construye el agregado asi: Create es el unico
    // punto de entrada, y es el que hace cumplir los invariantes.
    private ClientClassification()
    {
        Name = string.Empty;
        Prefix = string.Empty;
    }

    private ClientClassification(
        ClientClassificationId id, Guid tenantId, string name, string prefix, DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Prefix = prefix;
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public ClientClassificationId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Unico por tenant; la unicidad vive en IX_client_classifications_tenant_name.</summary>
    public string Name { get; private set; }

    /// <summary>Unico por tenant; la unicidad vive en IX_client_classifications_tenant_prefix.</summary>
    public string Prefix { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista, como en <c>TaxRate</c>, <c>Product</c> y <c>Customer</c>.
    /// Nace con el agregado en vez de agregarse despues: sin el, dos escrituras que se solapan
    /// se pisan en silencio y <see cref="EnsureActive"/> se evalua contra la copia en memoria
    /// del que escribe, no contra el estado real al momento del commit.
    /// </summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ClientClassification Create(
        ClientClassificationId id, Guid tenantId, string name, string prefix, DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), NormalizePrefix(prefix), occurredAt);

    public void Update(string name, string prefix, DateTimeOffset occurredAt)
    {
        EnsureActive();

        Name = NormalizeName(name);
        Prefix = NormalizePrefix(prefix);
        Version++;
        UpdatedAt = occurredAt;
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
        if (!IsActive)
        {
            throw new CustomersDomainException(
                "customers.classification.already_inactive",
                "The client classification is already inactive.");
        }

        IsActive = false;
        Version++;
        UpdatedAt = occurredAt;
    }

    // La vuelta de Deactivate. Sin ella una clasificacion inactiva quedaria atrapada: el PUT la
    // rechaza por EnsureActive(), y no habria forma de sacarla de ahi salvo un UPDATE por SQL.
    //
    // No revalida la unicidad del nombre ni del prefijo a proposito: los dos indices unicos son
    // **sin filtro parcial**, asi que desactivar nunca libera el nombre ni el prefijo, y
    // reactivar no puede colisionar con nadie.
    public void Activate(DateTimeOffset occurredAt)
    {
        if (IsActive)
        {
            throw new CustomersDomainException(
                "customers.classification.already_active",
                "The client classification is already active.");
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
                "customers.classification.inactive",
                "An inactive client classification cannot be edited.");
        }
    }

    // Recortar espacios es parte del invariante, no higiene del llamador: el indice unico trata
    // " Mayorista" y "Mayorista" como dos nombres distintos, cosa que nadie leyendo la lista
    // haria.
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CustomersDomainException(
                "customers.classification.name_required", "The name is required.");
        }

        var trimmed = name.Trim();
        return trimmed.Length > NameMaxLength
            ? throw new CustomersDomainException(
                "customers.classification.name_too_long",
                $"The name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new CustomersDomainException(
                "customers.classification.prefix_required", "The prefix is required.");
        }

        var trimmed = prefix.Trim();
        return trimmed.Length > PrefixMaxLength
            ? throw new CustomersDomainException(
                "customers.classification.prefix_too_long",
                $"The prefix cannot exceed {PrefixMaxLength} characters.")
            : trimmed;
    }
}
