using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Los vínculos de un mensaje del hilo con un ticket y los entrantes sin clasificar (spec §8.2, plan E5, E8 y E9).
public sealed class SupportTicketMessageTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid ConversationId = Guid.Parse("0193a000-0000-7000-8000-0000000000d1");
    private static readonly Guid MessageId = Guid.Parse("0193a000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AReleasedMessageCanBePinnedAgain()
    {
        var ticketId = SupportTicketId.New();
        var link = SupportTicketMessage.Link(
            TenantId, ticketId, ConversationId, MessageId, Now.AddMinutes(-5), SupportTicketMessageState.Linked, Now);

        link.Release(Now.AddMinutes(1));
        Assert.Equal(SupportTicketMessageState.Released, link.State);

        link.Pin(Now.AddMinutes(2));

        Assert.Equal(SupportTicketMessageState.Pinned, link.State);
        Assert.Equal(Now.AddMinutes(2), link.UpdatedAt);
        Assert.Equal(Now, link.CreatedAt);
        Assert.Equal(ticketId, link.TicketId);
        Assert.Equal(MessageId, link.MessageId);
        Assert.Equal(Now.AddMinutes(-5), link.OccurredAt);
    }

    // Revisión del bloque A (AM4): un vínculo no nace soltado ni sin ids.
    [Fact]
    public void ALinkCannotStartReleasedNorWithoutIds()
    {
        var ticketId = SupportTicketId.New();

        Assert.Throws<ArgumentException>(() => SupportTicketMessage.Link(
            TenantId, ticketId, ConversationId, MessageId, Now, SupportTicketMessageState.Released, Now));
        Assert.Throws<ArgumentException>(() => SupportTicketMessage.Link(
            TenantId, ticketId, ConversationId, Guid.Empty, Now, SupportTicketMessageState.Linked, Now));
        Assert.Throws<ArgumentException>(() => SupportTicketMessage.Link(
            TenantId, ticketId, Guid.Empty, MessageId, Now, SupportTicketMessageState.Pinned, Now));
    }

    // AM4: fijar lo fijado y soltar lo soltado no hacen nada, tampoco tocan UpdatedAt.
    [Fact]
    public void PinningAndReleasingAreIdempotent()
    {
        var link = SupportTicketMessage.Link(
            TenantId, SupportTicketId.New(), ConversationId, MessageId, Now.AddMinutes(-5), SupportTicketMessageState.Pinned, Now);

        link.Pin(Now.AddMinutes(1));
        Assert.Equal((SupportTicketMessageState.Pinned, Now), (link.State, link.UpdatedAt));

        link.Release(Now.AddMinutes(2));
        link.Release(Now.AddMinutes(3));
        Assert.Equal((SupportTicketMessageState.Released, Now.AddMinutes(2)), (link.State, link.UpdatedAt));
    }

    [Fact]
    public void AResolvedUnclassifiedMessageCannotBeResolvedAgain()
    {
        var dismissed = SupportUnclassifiedMessage.Create(TenantId, ConversationId, MessageId, Now.AddMinutes(-5), Now);
        dismissed.Dismiss(Actor, Now.AddMinutes(1));

        Assert.Equal(SupportUnclassifiedResolution.Dismissed, dismissed.Resolution);
        Assert.Equal(Actor, dismissed.ResolvedByMemberId);
        Assert.Throws<InvalidOperationException>(() => dismissed.Assign(SupportTicketId.New(), Actor, Now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => dismissed.Dismiss(Actor, Now.AddMinutes(2)));

        var ticketId = SupportTicketId.New();
        var reassigned = SupportUnclassifiedMessage.Create(TenantId, ConversationId, Guid.CreateVersion7(), Now, Now);
        reassigned.Assign(ticketId, null, Now.AddMinutes(1));

        Assert.Equal(SupportUnclassifiedResolution.Reassigned, reassigned.Resolution);
        Assert.Equal(ticketId, reassigned.AssignedTicketId);
        Assert.Null(reassigned.ResolvedByMemberId);
        Assert.Throws<InvalidOperationException>(() => reassigned.Dismiss(Actor, Now.AddMinutes(2)));
    }
}
