namespace Modules.Support.Domain;

public readonly record struct SupportTicketTypeId(Guid Value)
{
    public static SupportTicketTypeId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
