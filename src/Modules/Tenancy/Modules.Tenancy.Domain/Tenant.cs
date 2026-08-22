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
        DateTimeOffset createdAt)
    {
        Id = id;
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
        DateTimeOffset createdAt) =>
        new(id, slug, displayName, defaultCulture, timeZone, dateFormat, createdAt);

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
