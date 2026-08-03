namespace Modules.Audit.Domain;

public readonly record struct AuditEntryId(Guid Value)
{
    public static AuditEntryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
