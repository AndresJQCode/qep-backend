namespace Modules.Hr.Domain;

/// <summary>Centro de trabajo. No guarda empresa: la hereda de su sede, y la sede no cambia después de crearlo.</summary>
public sealed class Workplace : OrganizationSetting
{
    private Workplace()
    {
    }

    public WorkplaceId Id { get; private set; }

    public SiteId SiteId { get; private set; }

    public string? CiiuCode { get; private set; }

    public ArlRiskClass RiskClass { get; private set; }

    public bool HighRiskPension { get; private set; }

    public static Workplace Create(
        WorkplaceId id,
        Guid tenantId,
        Site site,
        string name,
        string? ciiuCode,
        ArlRiskClass riskClass,
        bool highRiskPension,
        DateTimeOffset now)
    {
        if (site.Archived)
        {
            throw new HrDomainException(HrErrorCodes.SiteArchived, "A workplace cannot be created on an archived site.");
        }

        var workplace = new Workplace { Id = id, SiteId = site.Id };
        workplace.Initialize(tenantId, name, now);
        workplace.CiiuCode = ciiuCode;
        workplace.RiskClass = riskClass;
        workplace.HighRiskPension = highRiskPension;
        return workplace;
    }

    public void Update(string name, string? ciiuCode, ArlRiskClass riskClass, bool highRiskPension, DateTimeOffset now)
    {
        SetName(name);
        CiiuCode = ciiuCode;
        RiskClass = riskClass;
        HighRiskPension = highRiskPension;
        Touch(now);
    }
}
