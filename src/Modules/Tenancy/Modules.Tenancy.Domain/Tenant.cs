using System.Globalization;
using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

public sealed class Tenant
{
    private static readonly HashSet<string> AllowedDateFormats =
    [
        "yyyy-MM-dd",
        "dd/MM/yyyy",
        "MM/dd/yyyy"
    ];

    private readonly List<IDomainEvent> _domainEvents = [];

    private Tenant()
    {
    }

    private Tenant(
        TenantId id,
        string slug,
        string displayName,
        string defaultCulture,
        string timeZone,
        string dateFormat,
        MembershipId ownerMembershipId,
        DateTimeOffset createdAt)
    {
        Id = id;
        OwnerMembershipId = ownerMembershipId;
        Slug = ValidateSlug(slug);
        DisplayName = ValidateDisplayName(displayName);
        DefaultCulture = ValidateCulture(defaultCulture);
        TimeZone = ValidateTimeZone(timeZone);
        DateFormat = ValidateDateFormat(dateFormat);
        Status = TenantStatus.Active;
        Version = 1;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public TenantId Id { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string DefaultCulture { get; private set; } = string.Empty;

    public string TimeZone { get; private set; } = string.Empty;

    public string DateFormat { get; private set; } = string.Empty;

    /// <summary>
    /// El archivo del logo en Storage, o null sin logo. La URL pública **no** se guarda acá: se
    /// arma al leer con <see cref="LogoPublicKey"/> y la base pública configurada (decisión 3 del
    /// spec 2026-09-19) — igual que <c>FileResourceDto.PublicUrl</c>. `Tenant` no conoce
    /// `FileResource`: los límites de tipo y tamaño del logo los valida el adaptador que sí lo
    /// tiene a mano (<c>ITenantLogoStorage</c>).
    /// </summary>
    public Guid? LogoFileId { get; private set; }

    /// <summary>La clave pública en el bucket, sólo para armar la URL sin volver a preguntarle a
    /// Storage en cada <c>GET /settings</c>.</summary>
    public string? LogoPublicKey { get; private set; }

    /// <summary>
    /// La membresía que manda en este tenant: la última autoridad (ADR 0017), la que no se puede
    /// suspender, quitar ni dejar sin el rol admin.
    /// </summary>
    /// <remarks>
    /// Hasta este cambio el owner se deducía de <c>Membership.Origin == "registration"</c>. Esa
    /// columna responde **cómo nació** la membresía, no **quién manda**: dos preguntas distintas
    /// que coincidían sólo porque el owner siempre era el que auto-registró el tenant. El roce ya
    /// estaba a la vista en <c>TenancySeeder</c>, que reusaba el origen de registro en un tenant
    /// que nunca se auto-registró, sólo para heredar la protección.
    ///
    /// <b>Nunca nulo: lo exige <see cref="Create"/>.</b> Existió como opcional entre
    /// <c>d4a26b8</c> y el 2026-09-21, con la idea de cubrir la ventana entre crear el tenant y
    /// nombrar su owner. Esa ventana no hacía falta —<c>MembershipId.New()</c> da el id antes de
    /// persistir, así que el owner se puede pasar al constructor— y el único que se quedaba
    /// realmente sin owner era el <c>qcode-demo</c> del inicializador de desarrollo, un tenant sin
    /// ninguna membresía que además daba 403 en todo. Se eliminó junto con este opcional.
    ///
    /// Que no pueda ser nulo es lo que hace que la guarda del agregado valga siempre: con un owner
    /// nulo <see cref="IsOwner"/> devolvía <c>false</c> para todos, y la membresía dueña se podía
    /// suspender y eliminar como cualquier otra.
    /// </remarks>
    public MembershipId OwnerMembershipId { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public static Tenant Create(
        TenantId id,
        string slug,
        string displayName,
        string defaultCulture,
        string timeZone,
        string dateFormat,
        MembershipId ownerMembershipId,
        DateTimeOffset createdAt) =>
        new(id, slug, displayName, defaultCulture, timeZone, dateFormat, ownerMembershipId,
            createdAt);

    /// <summary>Si esa membresía es la autoridad de este tenant.</summary>
    public bool IsOwner(MembershipId membershipId) => OwnerMembershipId == membershipId;

    public bool UpdateSettings(
        string displayName,
        string defaultCulture,
        string timeZone,
        string dateFormat,
        DateTimeOffset occurredAt)
    {
        EnsureActive();

        var validatedDisplayName = ValidateDisplayName(displayName);
        var validatedCulture = ValidateCulture(defaultCulture);
        var validatedTimeZone = ValidateTimeZone(timeZone);
        var validatedDateFormat = ValidateDateFormat(dateFormat);
        List<string> changedFields = [];

        TrackChange(nameof(DisplayName), DisplayName, validatedDisplayName, changedFields);
        TrackChange(nameof(DefaultCulture), DefaultCulture, validatedCulture, changedFields);
        TrackChange(nameof(TimeZone), TimeZone, validatedTimeZone, changedFields);
        TrackChange(nameof(DateFormat), DateFormat, validatedDateFormat, changedFields);

        if (changedFields.Count == 0)
        {
            return false;
        }

        DisplayName = validatedDisplayName;
        DefaultCulture = validatedCulture;
        TimeZone = validatedTimeZone;
        DateFormat = validatedDateFormat;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new TenantSettingsUpdatedDomainEvent(
            Guid.CreateVersion7(),
            occurredAt,
            Id,
            Version,
            changedFields));

        return true;
    }

    /// <summary>
    /// Asigna el logo. `false` sin cambios (mismo `fileId` ya vigente) — el caller no sube
    /// `Version` ni escribe auditoría en ese caso. `publicKey` vacía es un error del adaptador, no
    /// de la persona que sube el archivo: por eso `ArgumentException` y no un código de dominio.
    /// </summary>
    public bool SetLogo(Guid fileId, string publicKey, DateTimeOffset occurredAt)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);

        if (LogoFileId == fileId)
        {
            return false;
        }

        LogoFileId = fileId;
        LogoPublicKey = publicKey;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new TenantLogoUpdatedDomainEvent(
            Guid.CreateVersion7(), occurredAt, Id, Version, LogoFileId));

        return true;
    }

    /// <summary>`false` si el tenant ya no tenía logo.</summary>
    public bool RemoveLogo(DateTimeOffset occurredAt)
    {
        EnsureActive();

        if (LogoFileId is null)
        {
            return false;
        }

        LogoFileId = null;
        LogoPublicKey = null;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new TenantLogoUpdatedDomainEvent(
            Guid.CreateVersion7(), occurredAt, Id, Version, null));

        return true;
    }

    public IReadOnlyCollection<IDomainEvent> PullDomainEvents()
    {
        var events = _domainEvents.ToArray();
        _domainEvents.Clear();
        return events;
    }

    private void EnsureActive()
    {
        if (Status != TenantStatus.Active)
        {
            throw new TenantDomainException(
                "tenancy.tenant.not_active",
                "Only an active tenant can update settings.");
        }
    }

    private static void TrackChange(
        string field,
        string currentValue,
        string newValue,
        List<string> changedFields)
    {
        if (!StringComparer.Ordinal.Equals(currentValue, newValue))
        {
            changedFields.Add(char.ToLowerInvariant(field[0]) + field[1..]);
        }
    }

    private static string ValidateSlug(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 63 ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new TenantDomainException(
                "tenancy.slug.invalid",
                "Tenant slug must contain 3-63 lowercase letters, digits or hyphens.");
        }

        return normalized;
    }

    private static string ValidateDisplayName(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 2 or > 120)
        {
            throw new TenantDomainException(
                "tenancy.settings.display_name.invalid",
                "Display name must contain between 2 and 120 characters.");
        }

        return normalized;
    }

    private static string ValidateCulture(string value)
    {
        try
        {
            return CultureInfo.GetCultureInfo(value.Trim()).Name;
        }
        catch (CultureNotFoundException)
        {
            throw new TenantDomainException(
                "tenancy.settings.culture.invalid",
                "Default culture must be a valid BCP 47 culture.");
        }
    }

    private static string ValidateTimeZone(string value)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value.Trim()).Id;
        }
        catch (TimeZoneNotFoundException)
        {
            throw new TenantDomainException(
                "tenancy.settings.time_zone.invalid",
                "Time zone must be a valid IANA time zone.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new TenantDomainException(
                "tenancy.settings.time_zone.invalid",
                "Time zone data is invalid.");
        }
    }

    private static string ValidateDateFormat(string value)
    {
        var normalized = value.Trim();
        if (!AllowedDateFormats.Contains(normalized))
        {
            throw new TenantDomainException(
                "tenancy.settings.date_format.invalid",
                $"Date format must be one of: {string.Join(", ", AllowedDateFormats)}.");
        }

        return normalized;
    }
}
