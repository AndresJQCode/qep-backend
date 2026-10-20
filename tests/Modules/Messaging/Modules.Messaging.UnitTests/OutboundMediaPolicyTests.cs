using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-20 (video y audio) D2, D3, D6 y D7: una sola tabla dice, por formato, qué kind sale, con qué mime y hasta
/// cuánto pesa en cada canal (<c>null</c> = el canal no lo acepta). Tabla en borrador hasta V-1..V-3 con Meta real.</summary>
public sealed class OutboundMediaPolicyTests
{
    private const long Mb = 1024 * 1024;

    // extension, mime, kind, tope WhatsApp, tope Instagram (0 = no se acepta).
    [Theory]
    [InlineData(".jpg", "image/jpeg", MessageKind.Image, 25 * Mb, 25 * Mb)]
    [InlineData(".jpeg", "image/jpeg", MessageKind.Image, 25 * Mb, 25 * Mb)]
    [InlineData(".png", "image/png", MessageKind.Image, 25 * Mb, 25 * Mb)]
    [InlineData(".pdf", "application/pdf", MessageKind.Document, 100 * Mb, 25 * Mb)]
    [InlineData(".doc", "application/msword", MessageKind.Document, 100 * Mb, 0)]
    [InlineData(".xls", "application/vnd.ms-excel", MessageKind.Document, 100 * Mb, 0)]
    [InlineData(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", MessageKind.Document, 100 * Mb, 0)]
    [InlineData(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", MessageKind.Document, 100 * Mb, 0)]
    [InlineData(".mp4", "video/mp4", MessageKind.Video, 16 * Mb, 25 * Mb)]
    [InlineData(".3gp", "video/3gpp", MessageKind.Video, 16 * Mb, 0)]
    [InlineData(".mp3", "audio/mpeg", MessageKind.Audio, 16 * Mb, 25 * Mb)]
    [InlineData(".m4a", "audio/mp4", MessageKind.Audio, 16 * Mb, 25 * Mb)]
    [InlineData(".aac", "audio/aac", MessageKind.Audio, 16 * Mb, 25 * Mb)]
    [InlineData(".amr", "audio/amr", MessageKind.Audio, 16 * Mb, 0)]
    [InlineData(".ogg", "audio/ogg", MessageKind.Audio, 16 * Mb, 0)]
    public void EveryFormatHasItsKindMimeAndChannelLimits(string extension, string mimeType, MessageKind kind, long whatsAppMax, long instagramMax)
    {
        var format = Assert.NotNull(OutboundMediaPolicy.FormatOf(extension));

        Assert.Equal((mimeType, kind), (OutboundMediaPolicy.MimeTypeOf(format), OutboundMediaPolicy.KindOf(format)));
        Assert.Equal(whatsAppMax, OutboundMediaPolicy.MaxBytesOf(format, ConversationChannel.WhatsApp));
        Assert.True(OutboundMediaPolicy.IsAllowed(format, ConversationChannel.WhatsApp));
        Assert.Equal(instagramMax == 0 ? null : instagramMax, OutboundMediaPolicy.MaxBytesOf(format, ConversationChannel.Instagram));
        Assert.Equal(instagramMax != 0, OutboundMediaPolicy.IsAllowed(format, ConversationChannel.Instagram));
    }

    // Lo que no se manda tal cual queda fuera: no hay transcodificación (spec «No entra»).
    [Theory]
    [InlineData(".mov")]
    [InlineData(".webm")]
    [InlineData(".avi")]
    [InlineData(".wav")]
    [InlineData(".flac")]
    [InlineData(".wma")]
    [InlineData(".webp")]
    [InlineData("")]
    public void AFormatThatNoChannelReceivesAsIsIsUnknown(string extension) => Assert.Null(OutboundMediaPolicy.FormatOf(extension));

    [Theory]
    [InlineData(".jpg", "image")]
    [InlineData(".pdf", "file")]
    [InlineData(".mp4", "video")]
    [InlineData(".mp3", "audio")]
    [InlineData(".m4a", "audio")]
    [InlineData(".aac", "audio")]
    public void TheInstagramAttachmentTypeFollowsTheKind(string extension, string expected) =>
        Assert.Equal(expected, OutboundMediaPolicy.InstagramAttachmentType(OutboundMediaPolicy.FormatOf(extension)!.Value));

    // D7: WhatsApp no admite caption en audio y Instagram no manda texto en ningún adjunto.
    [Theory]
    [InlineData(".mp3", false)]
    [InlineData(".ogg", false)]
    [InlineData(".mp4", true)]
    [InlineData(".jpg", true)]
    [InlineData(".pdf", true)]
    public void OnlyAnAudioRefusesACaption(string extension, bool accepts) =>
        Assert.Equal(accepts, OutboundMediaPolicy.AcceptsCaption(OutboundMediaPolicy.FormatOf(extension)!.Value));

    [Fact]
    public void EveryFormatOfTheEnumIsInTheTable()
    {
        foreach (var format in Enum.GetValues<OutboundMediaFormat>())
        {
            Assert.Equal(format, OutboundMediaPolicy.RuleOf(format).Format);
        }
    }
}
