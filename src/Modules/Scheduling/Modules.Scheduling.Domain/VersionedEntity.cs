namespace Modules.Scheduling.Domain;

/// <summary>Lo común de las filas que se editan: tenant, la versión que cada escritura exige por <c>If-Match</c> y las marcas de tiempo.</summary>
public abstract class VersionedEntity
{
    public Guid TenantId { get; protected set; }

    public long Version { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset UpdatedAt { get; protected set; }

    protected void Initialize(Guid tenantId, DateTimeOffset now)
    {
        TenantId = tenantId;
        Version = 1;
        CreatedAt = now;
        UpdatedAt = now;
    }

    protected void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    /// <summary>Un nombre en blanco o demasiado largo es un error de programación: el validador de la petición lo rechazó antes con el campo marcado.</summary>
    protected static string RequireName(string value, int maxLength = 120)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.Trim();
        if (name.Length == 0 || name.Length > maxLength)
        {
            throw new ArgumentException($"The name must have between 1 and {maxLength} characters.", nameof(value));
        }

        return name;
    }
}
