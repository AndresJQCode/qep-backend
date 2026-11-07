namespace Modules.Store.Domain;

public enum StoreDomainKind
{
    Subdomain,
    Custom,
}

/// <summary>Hostname que resuelve a un tenant. El MVP sólo inserta la fila <see cref="StoreDomainKind.Subdomain"/>.</summary>
public sealed class StoreDomain
{
    public const int HostnameMaxLength = 253;

    // EF Core materializa por acá.
    private StoreDomain()
    {
        Hostname = string.Empty;
    }

    public string Hostname { get; private set; }

    public Guid TenantId { get; private set; }

    public StoreDomainKind Kind { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static StoreDomain CreateSubdomain(Guid tenantId, string slug, string rootDomain, DateTimeOffset now)
    {
        var hostname = $"{slug}.{rootDomain}".ToLowerInvariant();
        if (hostname.Length > HostnameMaxLength)
        {
            throw new StoreDomainException(StoreErrorCodes.SlugInvalid, "El hostname de la tienda es demasiado largo.");
        }

        return new StoreDomain
        {
            Hostname = hostname,
            TenantId = tenantId,
            Kind = StoreDomainKind.Subdomain,
            CreatedAt = now,
        };
    }
}
