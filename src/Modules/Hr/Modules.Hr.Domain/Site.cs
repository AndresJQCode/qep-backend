namespace Modules.Hr.Domain;

/// <summary>
/// Sede. <see cref="CompanyId"/> (FK a <c>companies.companies</c>, D-11) se fija al crearla y ningún método la cambia; que la
/// empresa exista y esté activa lo decide Application.
/// </summary>
public sealed class Site : OrganizationSetting
{
    private Site()
    {
    }

    public SiteId Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public string? Address { get; private set; }

    public Guid? CityId { get; private set; }

    public string? CompensationFund { get; private set; }

    public static Site Create(
        SiteId id,
        Guid tenantId,
        Guid companyId,
        string name,
        string? address,
        Guid? cityId,
        string? compensationFund,
        DateTimeOffset now)
    {
        var site = new Site { Id = id, CompanyId = companyId };
        site.Initialize(tenantId, name, now);
        site.Address = address;
        site.CityId = cityId;
        site.CompensationFund = compensationFund;
        return site;
    }

    public void Update(string name, string? address, Guid? cityId, string? compensationFund, DateTimeOffset now)
    {
        SetName(name);
        Address = address;
        CityId = cityId;
        CompensationFund = compensationFund;
        Touch(now);
    }
}
