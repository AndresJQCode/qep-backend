using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Plan N18: a note upload's Storage owner is derived from the ticket and the member, so only that member can use it in a
/// note of that ticket, and no public form upload (a random v4 key) ever matches it.</summary>
public sealed class SupportNoteUploadsTests
{
    [Fact]
    public void TheKeyIsStablePerTicketAndMember()
    {
        var ticket = SupportTicketId.New();
        var member = Guid.CreateVersion7();

        Assert.Equal(SupportNoteUploads.OwnerKey(ticket, member), SupportNoteUploads.OwnerKey(new SupportTicketId(ticket.Value), member));
        Assert.NotEqual(Guid.Empty, SupportNoteUploads.OwnerKey(ticket, member));
    }

    [Fact]
    public void AnotherMemberOrTicketGetsAnotherKey()
    {
        var ticket = SupportTicketId.New();
        var member = Guid.CreateVersion7();
        var key = SupportNoteUploads.OwnerKey(ticket, member);

        Assert.NotEqual(key, SupportNoteUploads.OwnerKey(ticket, Guid.CreateVersion7()));
        Assert.NotEqual(key, SupportNoteUploads.OwnerKey(SupportTicketId.New(), member));
        // Swapping the two ids is not the same key either: each has its own place in the hashed text.
        Assert.NotEqual(key, SupportNoteUploads.OwnerKey(new SupportTicketId(member), ticket.Value));
    }

    // Pinned value: the key is what Storage stored as owner_id, so changing the derivation orphans every upload not yet in a note.
    [Fact]
    public void TheDerivationIsPinned()
    {
        var ticket = new SupportTicketId(Guid.Parse("0193a0b2-0000-7000-8000-000000000001"));
        var member = Guid.Parse("0193a0b2-0000-7000-8000-000000000002");

        Assert.Equal(SupportNoteUploads.OwnerKey(ticket, member), SupportNoteUploads.OwnerKey(ticket, member));
        Assert.Equal(
            new Guid(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("support-note|0193a0b2-0000-7000-8000-000000000001|0193a0b2-0000-7000-8000-000000000002"))[..16]),
            SupportNoteUploads.OwnerKey(ticket, member));
    }
}
