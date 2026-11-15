using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>
/// Ruling A-c4: el validador de responder cubre cada guarda de <c>SupportTicketReply.Start</c> (el <c>clientId</c> y la regla del texto
/// de <c>SendMessageValidator</c>) y las combinaciones del cuerpo (conexión sólo con la plantilla, adjunto sólo por el hilo), así ningún
/// cuerpo llega al dominio a lanzar un <c>ArgumentException</c>.
/// </summary>
public sealed class SendTicketReplyValidatorTests
{
    private static List<string> Errors(SupportReplyRequest request) =>
        new SendTicketReplyValidator()
            .Validate(SendTicketReplyCommand.From(Guid.CreateVersion7(), Guid.CreateVersion7(), request, 1))
            .Errors.Select(error => error.PropertyName)
            .Distinct()
            .ToList();

    private static SupportReplyRequest Valid(string channel = "Thread") =>
        new(Guid.CreateVersion7(), channel, "Ya revisamos tu caso.", null, null, false);

    [Fact]
    public void AValidReplyPasses()
    {
        Assert.Empty(Errors(Valid()));
        Assert.Empty(Errors(Valid("Email")));
        Assert.Empty(Errors(Valid("WhatsAppTemplate") with { ConnectionId = Guid.CreateVersion7() }));
        Assert.Empty(Errors(Valid() with { UploadId = Guid.CreateVersion7() }));
        Assert.Empty(Errors(Valid() with { Text = "Línea uno\nLínea dos\tcon tabulador" }));
    }

    [Fact]
    public void TheClientIdAndTheChannelAreRequiredAndExact()
    {
        Assert.Equal(["ClientId"], Errors(Valid() with { ClientId = null }));
        Assert.Equal(["ClientId"], Errors(Valid() with { ClientId = Guid.Empty }));
        Assert.Equal(["Channel"], Errors(Valid() with { Channel = null }));
        Assert.Equal(["Channel"], Errors(Valid() with { Channel = "thread" }));
        Assert.Equal(["Channel"], Errors(Valid() with { Channel = "0" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hola\u0007")]
    [InlineData("\0")]
    public void ATextOutsideTheAdvisorRuleFails(string? text) =>
        Assert.Equal(["Text"], Errors(Valid() with { Text = text }));

    [Fact]
    public void ATextLongerThan4096Fails()
    {
        Assert.Equal(["Text"], Errors(Valid() with { Text = new string('a', 4097) }));
        Assert.Empty(Errors(Valid() with { Text = "  " + new string('a', 4096) + "  " }));
    }

    [Fact]
    public void TheConnectionGoesOnlyWithTheTemplateAndTheAttachmentOnlyByTheThread()
    {
        Assert.Equal(["ConnectionId"], Errors(Valid() with { ConnectionId = Guid.CreateVersion7() }));
        Assert.Equal(["ConnectionId"], Errors(Valid("Email") with { ConnectionId = Guid.CreateVersion7() }));
        Assert.Equal(["ConnectionId"], Errors(Valid("WhatsAppTemplate") with { ConnectionId = Guid.Empty }));
        Assert.Equal(["UploadId"], Errors(Valid("Email") with { UploadId = Guid.CreateVersion7() }));
        Assert.Equal(["UploadId"], Errors(Valid("WhatsAppTemplate") with { UploadId = Guid.CreateVersion7() }));
        Assert.Equal(["UploadId"], Errors(Valid() with { UploadId = Guid.Empty }));
    }
}
