namespace Modules.Support.Domain;

public readonly record struct SupportTicketId(Guid Value)
{
    public static SupportTicketId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
