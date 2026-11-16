using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Final review S2: the public form's uploadKey is a v4 UUID by contract, not by accident. The attachments_busy cap counts only
/// v4 owners, and complete and file refuse any other version, so a note upload's v8 key never passes for a public one.</summary>
public sealed class SupportPublicUploadsTests
{
    [Fact]
    public void ANewKeyIsAVersion4Uuid()
    {
        Assert.Equal(4, SupportPublicUploads.KeyVersion);
        for (var i = 0; i < 64; i++)
        {
            var key = SupportPublicUploads.NewKey();

            Assert.Equal(SupportPublicUploads.KeyVersion, key.Version);
            Assert.True(SupportPublicUploads.IsKey(key));
        }
    }

    [Fact]
    public void OnlyAVersion4KeyIsAPublicKey()
    {
        Assert.False(SupportPublicUploads.IsKey(Guid.Empty));
        Assert.False(SupportPublicUploads.IsKey(Guid.CreateVersion7()));
        Assert.False(SupportPublicUploads.IsKey(SupportNoteUploads.OwnerKey(SupportTicketId.New(), Guid.CreateVersion7())));
        Assert.NotEqual(SupportNoteUploads.OwnerKeyVersion, SupportPublicUploads.KeyVersion);
    }
}
