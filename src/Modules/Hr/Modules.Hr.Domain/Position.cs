namespace Modules.Hr.Domain;

public sealed class Position : OrganizationSetting
{
    private Position()
    {
    }

    public PositionId Id { get; private set; }

    public static Position Create(PositionId id, Guid tenantId, string name, DateTimeOffset now)
    {
        var setting = new Position { Id = id };
        setting.Initialize(tenantId, name, now);
        return setting;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        SetName(name);
        Touch(now);
    }
}