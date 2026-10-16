using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Messaging-media-reply R4, R5 y R18: la guarda resuelve la conexión sin reclamo (la subida a Meta va antes del
/// reclamo) y la idempotencia devuelve el nombre y el pie del adjunto guardado.</summary>
public sealed class OutboundGuardsMediaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OutboundGuards GuardsFor(MessagingTestBed bed)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(bed.SubjectId, bed.TenantId)] = bed.MemberId;
        return new OutboundGuards(
            bed.Conversations, bed.Repository, bed.Messages, bed.Outbound, bed.Connections, new FakeMemberNames(bed.MemberId), membership, bed.TenantModules,
            new FakeExecutionContext(bed.TenantId, bed.SubjectId, MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage),
            new FakeClock(bed.Now),
            bed.Notifier,
            bed.Receipts);
    }

    [Fact]
    public async Task TheSenderIsResolvedWithoutAClaim()
    {
        var bed = new MessagingTestBed();

        var sender = await GuardsFor(bed).ResolveSenderAsync(bed.TenantId, bed.ConnectionId, Ct);

        Assert.Equal(new MessagingSender("111", "token"), sender);
        Assert.Empty(bed.Outbound.Claims);
    }

    [Fact]
    public async Task APausedConnectionIsConnectionUnavailableAndNeverTouchesAClaim()
    {
        var bed = new MessagingTestBed();
        bed.Connections.Sender = null;

        var failure = await Assert.ThrowsAsync<MessagingDomainException>(() => GuardsFor(bed).ResolveSenderAsync(bed.TenantId, bed.ConnectionId, Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, failure.Code);
        Assert.Empty(bed.Outbound.Claims);
    }

    [Fact]
    public void AStoredDocumentComesBackWithItsFileNameAndCaption()
    {
        var existing = new ExistingOutbound(
            Guid.CreateVersion7(), MessageStatus.Sent, DateTimeOffset.UnixEpoch, Guid.CreateVersion7(), string.Empty, null, MessageKind.Document,
            "application/pdf", null, "Cotización mayo.pdf", "Aquí va");
        var clientId = Guid.CreateVersion7();

        var row = OutboundGuards.ExistingRow(Guid.CreateVersion7(), existing, clientId);

        Assert.Equal((MessageKind.Document, (string?)null, "Aquí va", clientId), (row.Kind, row.Text, row.Caption, row.ClientId));
        Assert.Equal(new MessageMediaRow("application/pdf", "Cotización mayo.pdf"), row.Media);
    }

    [Fact]
    public void AStoredImageWithoutCaptionHasNoneAndAVoiceNoteKeepsNoFileName()
    {
        var image = new ExistingOutbound(
            Guid.CreateVersion7(), MessageStatus.Sent, DateTimeOffset.UnixEpoch, null, string.Empty, null, MessageKind.Image, "image/jpeg", null, "foto.jpg", null);
        var voice = new ExistingOutbound(
            Guid.CreateVersion7(), MessageStatus.Sent, DateTimeOffset.UnixEpoch, null, string.Empty, null, MessageKind.Audio, "audio/ogg");

        var imageRow = OutboundGuards.ExistingRow(Guid.CreateVersion7(), image, null);
        var voiceRow = OutboundGuards.ExistingRow(Guid.CreateVersion7(), voice, null);

        Assert.Equal((new MessageMediaRow("image/jpeg", "foto.jpg"), (string?)null), (imageRow.Media, imageRow.Caption));
        Assert.Equal((new MessageMediaRow("audio/ogg", null), (string?)null), (voiceRow.Media, voiceRow.Caption));
    }
}
