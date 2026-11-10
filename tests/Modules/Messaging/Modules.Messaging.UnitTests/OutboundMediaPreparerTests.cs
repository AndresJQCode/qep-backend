using System.Security.Cryptography;
using System.Text;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Scheduled messages T4: <see cref="OutboundMediaPreparer.UploadStoredAsync"/> sube a Meta un objeto ya validado y guardado,
/// que es lo que hace el ejecutor de un mensaje programado con media.</summary>
public sealed class OutboundMediaPreparerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UploadStoredSendsTheObjectWithTheGenericNameAndHashesIt()
    {
        var bed = new MessagingTestBed();
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 contenido del documento");
        const string key = "messaging/scheduled/doc.pdf";
        bed.Store.Objects[key] = (bytes, "application/pdf");
        var preparer = new OutboundMediaPreparer(bed.Meta, bed.Store, bed.Compressor);
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.doc", null);

        var (upload, sha256Hex) = await preparer.UploadStoredAsync(
            new MessagingSender("111", "token"), key, bytes.Length, "application/pdf", MessageKind.Document, ".pdf", Ct);

        Assert.Equal("media.doc", upload.Value);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha256Hex);
        var sent = Assert.Single(bed.Meta.StreamUploads);
        Assert.Equal(bytes, sent.Content);
        Assert.Equal(((long)bytes.Length, "application/pdf", "document.pdf"), (sent.DeclaredLength, sent.MimeType, sent.FileName));
    }

    [Fact]
    public async Task UploadStoredOfAMissingObjectIsMediaMissing()
    {
        var bed = new MessagingTestBed();
        var preparer = new OutboundMediaPreparer(bed.Meta, bed.Store, bed.Compressor);

        var failure = await Assert.ThrowsAsync<MessagingDomainException>(() => preparer.UploadStoredAsync(
            new MessagingSender("111", "token"), "messaging/scheduled/gone.pdf", 10, "application/pdf", MessageKind.Document, ".pdf", Ct));

        Assert.Equal(MessagingErrorCodes.ScheduledMediaMissing, failure.Code);
        Assert.Empty(bed.Meta.StreamUploads);
    }
}
