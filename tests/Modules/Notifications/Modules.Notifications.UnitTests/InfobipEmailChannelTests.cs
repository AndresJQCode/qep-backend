using System.Net;
using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure;
using Modules.Notifications.Infrastructure.Channels;
using Modules.Notifications.Infrastructure.Messaging;

namespace Modules.Notifications.UnitTests;

/// <summary>El timeout del canal de Infobip (spec 2026-09-13, A5) y su relación con el lease del reclamo.</summary>
public sealed class InfobipEmailChannelTests
{
    // 30 s y no los 100 s implícitos de new HttpClient().
    [Fact]
    public void TheHttpClientGivesUpAfterThirtySeconds()
    {
        using var client = InfobipEmailChannel.CreateHttpClient();

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    // Si el lease venciera antes que el timeout, otra réplica retomaría un mensaje que todavía se está
    // enviando, y saldrían dos correos.
    [Fact]
    public void TheClaimLeaseOutlastsTheProviderTimeout()
    {
        Assert.True(
            OutboxDeliveryWorker.Lease > InfobipEmailChannel.RequestTimeout,
            $"Lease {OutboxDeliveryWorker.Lease} must be longer than the Infobip timeout {InfobipEmailChannel.RequestTimeout}.");
    }

    /// <summary>Soporte fase 4b (H10): el PDF de la respuesta formal viaja como una parte <c>attachment</c> del multipart, con su nombre
    /// y su tipo.</summary>
    [Fact]
    public async Task AnAttachmentTravelsAsAMultipartPart()
    {
        var handler = new CapturingHandler();
        var pdf = "%PDF-1.7 formal"u8.ToArray();

        await SendAsync(handler, Message() with
        {
            Attachments = [new EmailAttachment("Respuesta-SOP-000127.pdf", "application/pdf", pdf)],
        });

        var attachment = Assert.Single(handler.Parts, part => part.Name == "attachment");
        Assert.Equal(("Respuesta-SOP-000127.pdf", "application/pdf"), (attachment.FileName, attachment.ContentType));
        Assert.Equal(pdf, attachment.Bytes);
        Assert.Equal(["from", "to", "subject", "text", "html", "attachment"], handler.Parts.Select(part => part.Name));
    }

    [Fact]
    public async Task WithoutAttachmentsTheRequestDoesNotChange()
    {
        var handler = new CapturingHandler();

        await SendAsync(handler, Message());
        await SendAsync(handler, Message() with { Attachments = [] });

        Assert.Equal(
            ["from", "to", "subject", "text", "html", "from", "to", "subject", "text", "html"],
            handler.Parts.Select(part => part.Name));
        Assert.All(handler.Parts, part => Assert.Null(part.FileName));
    }

    private static EmailMessage Message() => new("ana@example.com", "Asunto", "<p>Hola</p>", "Hola");

    private static async Task SendAsync(CapturingHandler handler, EmailMessage message)
    {
        using var client = new HttpClient(handler);
        var options = Options.Create(new NotificationsOptions
        {
            EmailProvider = NotificationsOptions.InfobipProvider,
            Infobip = new InfobipOptions { BaseUrl = "https://infobip.test", ApiKey = "test-key", SenderEmail = "soporte@qep.test" },
        });

        await new InfobipEmailChannel(client, options).SendAsync(message, TestContext.Current.CancellationToken);
    }

    private sealed record Part(string? Name, string? FileName, string? ContentType, byte[] Bytes);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Part> Parts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in multipart)
            {
                var disposition = part.Headers.ContentDisposition;
                Parts.Add(new Part(
                    disposition?.Name?.Trim('"'), disposition?.FileName?.Trim('"'), part.Headers.ContentType?.MediaType,
                    await part.ReadAsByteArrayAsync(cancellationToken)));
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
