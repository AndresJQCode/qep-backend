using System.Text.RegularExpressions;

namespace Modules.Authorization.Domain;

/// <summary>
/// Un rol definido por un tenant, con el conjunto de permisos que concede.
/// </summary>
/// <remarks>
/// Convive con los roles de sistema, que se versionan con el codigo y no viven aca. La
/// diferencia no es cosmetica: un rol de sistema no se edita ni se borra, y su clave esta
/// reservada — <see cref="SystemRoleKeys"/>.
///
/// El agregado no valida que un permiso exista en el catalogo. No puede: el catalogo se
/// arma en composicion y el dominio no lo conoce. Esa comprobacion es del handler, contra
/// <c>IRoleCatalog.ListPermissions()</c>, igual que <c>InviteMemberHandler</c> valida los
/// roles contra <c>IRoleReferenceValidator</c> en vez de hacerlo en <c>Membership</c>.
/// </remarks>
public sealed class Role
{
    public const int KeyMaxLength = 32;
    public const int DisplayNameMaxLength = 60;
    public const int DescriptionMaxLength = 300;

    // La misma forma que `UserPreference` acepta para un color scheme: minusculas, digitos y
    // guiones. La clave viaja en la membresia y se compara ordinal contra el catalogo.
    private static readonly Regex KeyShape = new(
        "^[a-z0-9-]{1,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<string> _permissions = [];

    private Role()
    {
    }

    private Role(
        RoleId id,
        Guid tenantId,
        string key,
        string displayName,
        string description,
        IEnumerable<string> permissions,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Key = key;
        DisplayName = displayName;
        Description = description;
        _permissions.AddRange(permissions);
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    public RoleId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public IReadOnlyCollection<string> Permissions => _permissions.AsReadOnly();

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Role Create(
        RoleId id,
        Guid tenantId,
        string key,
        string displayName,
        string description,
        IReadOnlyCollection<string> permissions,
        DateTimeOffset createdAt) =>
        new(
            id,
            tenantId,
            NormalizeKey(key),
            NormalizeDisplayName(displayName),
            NormalizeDescription(description),
            NormalizePermissions(permissions),
            createdAt);

    /// <summary>Reemplaza el conjunto de permisos. Es un no-op si el conjunto no cambia.</summary>
    /// <remarks>
    /// El no-op no es una optimizacion. Sin el, abrir el editor y guardar sin tocar nada
    /// consume una version, y el `If-Match` de quien tenia la pantalla abierta en otro lado
    /// falla con un 412 aunque nadie haya cambiado nada.
    /// </remarks>
    public void ChangePermissions(
        IReadOnlyCollection<string> permissions,
        DateTimeOffset occurredAt)
    {
        var normalized = NormalizePermissions(permissions);
        if (normalized.SetEquals(_permissions))
        {
            return;
        }

        _permissions.Clear();
        _permissions.AddRange(normalized);
        Touch(occurredAt);
    }

    public void Rename(string displayName, string description, DateTimeOffset occurredAt)
    {
        var normalizedName = NormalizeDisplayName(displayName);
        var normalizedDescription = NormalizeDescription(description);
        if (StringComparer.Ordinal.Equals(normalizedName, DisplayName) &&
            StringComparer.Ordinal.Equals(normalizedDescription, Description))
        {
            return;
        }

        DisplayName = normalizedName;
        Description = normalizedDescription;
        Touch(occurredAt);
    }

    private void Touch(DateTimeOffset occurredAt)
    {
        Version++;
        UpdatedAt = occurredAt;
    }

    private static string NormalizeKey(string value)
    {
        // Se baja a minusculas antes de validar la forma para que "Ventas-Junior" sea un
        // error de mayusculas y no un rechazo: la clave es un slug, no un nombre.
        var normalized = value.Trim().ToLowerInvariant();
        if (!KeyShape.IsMatch(normalized))
        {
            throw new AuthorizationDomainException(
                "authorization.role.key_invalid",
                $"A role key must match {KeyShape} — lowercase letters, digits and hyphens.");
        }

        if (SystemRoleKeys.IsReserved(normalized))
        {
            throw new AuthorizationDomainException(
                "authorization.role.key_reserved",
                $"The key '{normalized}' belongs to a system role and cannot be reused.");
        }

        return normalized;
    }

    private static string NormalizeDisplayName(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new AuthorizationDomainException(
                "authorization.role.display_name_required",
                "A role needs a name somebody can read.");
        }

        if (normalized.Length > DisplayNameMaxLength)
        {
            throw new AuthorizationDomainException(
                "authorization.role.display_name_too_long",
                $"A role name is at most {DisplayNameMaxLength} characters.");
        }

        return normalized;
    }

    private static string NormalizeDescription(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length > DescriptionMaxLength)
        {
            throw new AuthorizationDomainException(
                "authorization.role.description_too_long",
                $"A role description is at most {DescriptionMaxLength} characters.");
        }

        return normalized;
    }

    private static HashSet<string> NormalizePermissions(IReadOnlyCollection<string> permissions)
    {
        var normalized = permissions
            .Select(permission => permission.Trim())
            .Where(permission => permission.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        if (normalized.Count == 0)
        {
            throw new AuthorizationDomainException(
                "authorization.role.permissions_required",
                "A role that grants nothing is a role nobody can use.");
        }

        return normalized;
    }
}
