namespace Modules.Hr.Domain;

/// <summary>The editable part of <see cref="BirthdayNoticeSetting"/>, as the PUT sends it.</summary>
public sealed record BirthdayNoticeValues(
    IReadOnlyList<Guid> ManagerMemberIds,
    int LeadDays,
    bool WhatsAppEnabled,
    Guid? WhatsAppConnectionId,
    bool EmailEnabled);

/// <summary>
/// One row per tenant: who receives the daily birthday digest and through which channels (spec 2026-11-26 §5.1). No row means the
/// feature is off. The aggregate enforces the connection rule and de-duplicates the managers; the range checks (lead days,
/// managers count) belong to the request validator.
/// </summary>
public sealed class BirthdayNoticeSetting
{
    public const int MaxManagers = 20;
    public const int MaxLeadDays = 7;
    public const int DefaultLeadDays = 1;

    private BirthdayNoticeSetting()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary>Stored as <c>uuid[]</c>: a <see cref="List{T}"/> keeps the EF Core mapping simple.</summary>
    public List<Guid> ManagerMemberIds { get; private set; } = [];

    public int LeadDays { get; private set; }

    public bool WhatsAppEnabled { get; private set; }

    public Guid? WhatsAppConnectionId { get; private set; }

    public bool EmailEnabled { get; private set; }

    public Guid UpdatedByMemberId { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static BirthdayNoticeSetting Create(Guid tenantId, BirthdayNoticeValues values, Guid updatedBy, DateTimeOffset now)
    {
        var setting = new BirthdayNoticeSetting { TenantId = tenantId, Version = 1, CreatedAt = now };
        setting.Apply(values, updatedBy, now);
        return setting;
    }

    public void Update(BirthdayNoticeValues values, Guid updatedBy, DateTimeOffset now)
    {
        Apply(values, updatedBy, now);
        Version++;
    }

    private void Apply(BirthdayNoticeValues values, Guid updatedBy, DateTimeOffset now)
    {
        if (values.WhatsAppEnabled && values.WhatsAppConnectionId is null)
        {
            throw new HrDomainException(
                HrErrorCodes.BirthdayConnectionRequired,
                "A WhatsApp connection is required when WhatsApp notices are enabled.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["whatsAppConnectionId"] = [HrErrorCodes.BirthdayConnectionRequired],
                });
        }

        ManagerMemberIds = values.ManagerMemberIds.Distinct().ToList();
        LeadDays = values.LeadDays;
        WhatsAppEnabled = values.WhatsAppEnabled;
        WhatsAppConnectionId = values.WhatsAppEnabled ? values.WhatsAppConnectionId : null;
        EmailEnabled = values.EmailEnabled;
        UpdatedByMemberId = updatedBy;
        UpdatedAt = now;
    }
}
