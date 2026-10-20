using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Messaging-media-reply R1, R2, R8 y R17: la sesión de subida firma un PUT a la clave de staging del miembro que
/// llama. Sólo carga la conversación (no autoasigna, no mira la ventana): el dueño y la ventana se miden al confirmar.</summary>
public sealed class CreateMediaUploadSessionTests
{
    private const long Mb = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateMediaUploadSessionCommand Command(
        MessagingTestBed bed, Guid conversationId, string fileName = "photo.jpg", string mimeType = "image/jpeg", long sizeBytes = 3 * Mb) =>
        new(bed.TenantId, conversationId, fileName, mimeType, sizeBytes);

    [Fact]
    public async Task AValidRequestSignsAnUploadToTheStagingKeyOfTheCaller()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);

        var session = await bed.CreateMediaUploadSessionHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.NotEqual(Guid.Empty, session.UploadId);
        var signed = Assert.Single(bed.Staging.Signed);
        Assert.Equal(MessagingMediaKeys.Staging(bed.TenantId, conversation.Id, bed.MemberId, session.UploadId, ".jpg"), signed.Key);
        Assert.Equal("image/jpeg", signed.ContentType);
        Assert.Equal(FakeUploadStaging.UrlFor(signed.Key).AbsoluteUri, session.UploadUrl);
        Assert.Equal(bed.Now.AddMinutes(5), session.ExpiresAt);
        Assert.Equal("image/jpeg", session.Headers["Content-Type"]);
        Assert.Equal("*", session.Headers["If-None-Match"]);
    }

    [Theory]
    [InlineData("photo.jpeg", "image/jpeg", 1)]
    [InlineData("a.PNG", "image/png", 25 * Mb)]
    [InlineData("a.pdf", "application/pdf", 100 * Mb)]
    [InlineData("a.doc", "application/msword", 1)]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 1)]
    [InlineData("a.xls", "application/vnd.ms-excel", 1)]
    [InlineData("a.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 1)]
    [InlineData("a.mp4", "video/mp4", 16 * Mb)]
    [InlineData("a.3gp", "video/3gpp", 16 * Mb)]
    [InlineData("a.mp3", "audio/mpeg", 16 * Mb)]
    [InlineData("a.m4a", "audio/mp4", 16 * Mb)]
    [InlineData("a.aac", "audio/aac", 16 * Mb)]
    [InlineData("a.amr", "audio/amr", 16 * Mb)]
    [InlineData("a.ogg", "audio/ogg", 16 * Mb)]
    public async Task EveryAllowedTypeAtItsLimitIsAccepted(string fileName, string mimeType, long sizeBytes)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var session = await bed.CreateMediaUploadSessionHandler().HandleAsync(Command(bed, conversation.Id, fileName, mimeType, sizeBytes), Ct);

        Assert.NotEqual(Guid.Empty, session.UploadId);
    }

    [Theory]
    [InlineData("a.webp", "image/webp", 1 * Mb)]
    [InlineData("a.mov", "video/quicktime", 1 * Mb)]
    [InlineData("a.wav", "audio/wav", 1 * Mb)]
    [InlineData("a.mp4", "audio/mp4", 1 * Mb)]
    [InlineData("a.m4a", "video/mp4", 1 * Mb)]
    [InlineData("a.exe", "application/octet-stream", 1 * Mb)]
    [InlineData("a.pdf", "image/png", 1 * Mb)]
    [InlineData("a.jpg", "application/pdf", 1 * Mb)]
    [InlineData("a", "image/jpeg", 1 * Mb)]
    public async Task ATypeThatIsNotAllowedOrDoesNotMatchTheExtensionIsRejectedBeforeSigning(string fileName, string mimeType, long sizeBytes)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.CreateMediaUploadSessionHandler().HandleAsync(Command(bed, conversation.Id, fileName, mimeType, sizeBytes), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUnsupportedFormat, error.Code);
        Assert.Empty(bed.Staging.Signed);
    }

    [Theory]
    [InlineData("a.jpg", "image/jpeg", 25 * Mb + 1)]
    [InlineData("a.pdf", "application/pdf", 100 * Mb + 1)]
    [InlineData("a.mp4", "video/mp4", 16 * Mb + 1)]
    [InlineData("a.mp3", "audio/mpeg", 16 * Mb + 1)]
    public async Task ABiggerFileThanItsLimitIsRejectedBeforeSigning(string fileName, string mimeType, long sizeBytes)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.CreateMediaUploadSessionHandler().HandleAsync(Command(bed, conversation.Id, fileName, mimeType, sizeBytes), Ct));

        Assert.Equal(MessagingErrorCodes.MediaTooLarge, error.Code);
        Assert.Empty(bed.Staging.Signed);
    }

    // Spec 2026-10-20 D2: el tope y los formatos son los del canal, así que el 422 llega antes del PUT y no al confirmar.
    [Theory]
    [InlineData("a.mp4", "video/mp4", 25 * Mb)]
    [InlineData("a.mp3", "audio/mpeg", 25 * Mb)]
    [InlineData("a.m4a", "audio/mp4", 25 * Mb)]
    [InlineData("a.aac", "audio/aac", 25 * Mb)]
    [InlineData("a.pdf", "application/pdf", 25 * Mb)]
    public async Task InstagramAcceptsItsFormatsUpToItsOwnLimit(string fileName, string mimeType, long sizeBytes)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, userId: "1789", waId: null, channel: ConversationChannel.Instagram);

        var session = await bed.CreateMediaUploadSessionHandler().HandleAsync(Command(bed, conversation.Id, fileName, mimeType, sizeBytes), Ct);

        Assert.NotEqual(Guid.Empty, session.UploadId);
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf", 25 * Mb + 1, MessagingErrorCodes.MediaTooLarge)]
    [InlineData("a.mp4", "video/mp4", 25 * Mb + 1, MessagingErrorCodes.MediaTooLarge)]
    [InlineData("a.3gp", "video/3gpp", 1 * Mb, MessagingErrorCodes.MediaUnsupportedFormat)]
    [InlineData("a.amr", "audio/amr", 1 * Mb, MessagingErrorCodes.MediaUnsupportedFormat)]
    [InlineData("a.ogg", "audio/ogg", 1 * Mb, MessagingErrorCodes.MediaUnsupportedFormat)]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 1 * Mb, MessagingErrorCodes.MediaUnsupportedFormat)]
    public async Task InstagramRejectsWhatItDoesNotReceiveOrIsOverItsLimitBeforeSigning(string fileName, string mimeType, long sizeBytes, string expected)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, userId: "1789", waId: null, channel: ConversationChannel.Instagram);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.CreateMediaUploadSessionHandler().HandleAsync(Command(bed, conversation.Id, fileName, mimeType, sizeBytes), Ct));

        Assert.Equal(expected, error.Code);
        Assert.Empty(bed.Staging.Signed);
    }

    [Fact]
    public async Task ItNeitherAutoAssignsNorMindsTheWindow()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 30);

        var session = await bed.CreateMediaUploadSessionHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.NotEqual(Guid.Empty, session.UploadId);
        Assert.Empty(bed.Repository.AutoAssigned);
    }

    [Fact]
    public async Task AnotherTenantIsForbiddenAndAMissingConversationIsNotFound()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.CreateMediaUploadSessionHandler(tenantId: Guid.CreateVersion7()).HandleAsync(Command(bed, conversation.Id), Ct));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.CreateMediaUploadSessionHandler().HandleAsync(Command(bed, Guid.CreateVersion7()), Ct));

        Assert.Equal(MessagingErrorCodes.ConversationNotFound, missing.Code);
        Assert.Empty(bed.Staging.Signed);
    }

    [Fact]
    public void TheValidatorMarksTheFieldsWithTheNamesOfTheJsonBody()
    {
        var validator = new CreateMediaUploadSessionValidator();

        var result = validator.Validate(new CreateMediaUploadSessionCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "", "", 0));

        var fields = result.Errors.Select(failure => failure.PropertyName).Distinct().Order().ToArray();
        Assert.Equal(["fileName", "mimeType", "sizeBytes"], fields);
    }
}
