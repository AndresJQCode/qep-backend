namespace Modules.Hr.Domain;

public sealed class Area : OrganizationSetting
{
    private Area()
    {
    }

    public AreaId Id { get; private set; }

    public static Area Create(AreaId id, Guid tenantId, string name, DateTimeOffset now)
    {
        var setting = new Area { Id = id };
        setting.Initialize(tenantId, name, now);
        return setting;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        SetName(name);
        Touch(now);
    }
}
