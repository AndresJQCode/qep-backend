namespace Modules.Hr.Domain;

/// <summary>
/// Lo común de la configuración de la organización (sede, centro de trabajo, área, cargo): nombre con su forma normalizada
/// para la unicidad, archivado, y la versión que cada <c>PUT</c> exige por <c>If-Match</c>.
/// </summary>
public abstract class OrganizationSetting
{
    public Guid TenantId { get; protected set; }

    public string Name { get; protected set; } = string.Empty;

    public string NormalizedName { get; protected set; } = string.Empty;

    public DateTimeOffset? ArchivedAt { get; protected set; }

    public bool Archived => ArchivedAt is not null;

    public long Version { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset UpdatedAt { get; protected set; }

    public void Archive(DateTimeOffset now)
    {
        if (Archived)
        {
            throw new HrDomainException(HrErrorCodes.SettingsAlreadyArchived, "The setting is already archived.");
        }

        ArchivedAt = now;
        Touch(now);
    }

    public void Unarchive(DateTimeOffset now)
    {
        if (!Archived)
        {
            throw new HrDomainException(HrErrorCodes.SettingsNotArchived, "The setting is not archived.");
        }

        ArchivedAt = null;
        Touch(now);
    }

    protected void Initialize(Guid tenantId, string name, DateTimeOffset now)
    {
        TenantId = tenantId;
        SetName(name);
        Version = 1;
        CreatedAt = now;
        UpdatedAt = now;
    }

    protected void SetName(string name)
    {
        Name = name.Trim();
        NormalizedName = OrganizationNames.Normalize(name);
    }

    protected void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
