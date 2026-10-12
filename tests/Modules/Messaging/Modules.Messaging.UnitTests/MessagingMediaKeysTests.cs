using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Media;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.3: el saliente va a la misma clave de R2 que un entrante, para que el GET de media lo
/// sirva igual. La clave vive en Application porque el handler no puede ver Infrastructure.</summary>
public sealed class MessagingMediaKeysTests
{
    [Fact]
    public void TheOutboundKeyIsTheSameAsTheInboundOne()
    {
        var tenantId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        Assert.Equal($"messaging/{tenantId}/{messageId}", MessagingMediaKeys.For(tenantId, messageId));
        Assert.Equal(MessagingMediaKeys.For(tenantId, messageId), MediaTransfer.KeyFor(tenantId, messageId));
    }

    [Fact]
    public void TheUploadFailureCodeIsTheOneOfTheContract() =>
        Assert.Equal("messaging.media.upload_failed", MessagingErrorCodes.MediaUploadFailed);
}
