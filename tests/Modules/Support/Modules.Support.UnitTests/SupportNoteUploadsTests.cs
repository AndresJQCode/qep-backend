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

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("support-note|0193a0b2-0000-7000-8000-000000000001|0193a0b2-0000-7000-8000-000000000002"));
        var expected = hash[..16];
        expected[6] = (byte)((expected[6] & 0x0F) | 0x80);
        expected[8] = (byte)((expected[8] & 0x3F) | 0x80);

        Assert.Equal(SupportNoteUploads.OwnerKey(ticket, member), SupportNoteUploads.OwnerKey(ticket, member));
        Assert.Equal(new Guid(expected, bigEndian: true), SupportNoteUploads.OwnerKey(ticket, member));
    }

    // Block B review: the public form's attachments_busy cap counts only v4 keys (Guid.NewGuid, the visitor's uploadKey). A note's key
    // is a v8 UUID with the RFC variant, so Postgres' uuid_extract_version reads 8 and an abandoned note upload never counts.
    [Fact]
    public void TheKeyIsAVersion8UuidSoItNeverPassesForAPublicUploadKey()
    {
        Assert.Equal(8, SupportNoteUploads.OwnerKeyVersion);
        for (var i = 0; i < 64; i++)
        {
            var key = SupportNoteUploads.OwnerKey(SupportTicketId.New(), Guid.CreateVersion7());

            Assert.Equal(SupportNoteUploads.OwnerKeyVersion, key.Version);
            Assert.Contains(key.ToString("D")[19], "89ab");
            Assert.NotEqual(Guid.NewGuid().Version, key.Version);
        }
    }
}
