using System.Security.Cryptography;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Fase 4b (H3, H4, H9): el documento de la respuesta formal es prueba legal de lo que se envió. Se crea una vez y no cambia;
/// guarda la decisión con sus saltos de línea, quién firma (el responsable del ticket, no quien envía), los anexos como lista de texto
/// y el hash del PDF.</summary>
public sealed class SupportReplyDocumentTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Signer = Guid.CreateVersion7();
    private static readonly byte[] Content = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];

    private static SupportTicketReply Reply(Guid? uploadId = null, string? fileName = null) =>
        SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Email, SupportReplyDelivery.Email,
            "Te enviamos la respuesta formal.", null, Guid.CreateVersion7(), Now, uploadId: uploadId, fileName: fileName);

    private static SupportReplyDocument Create(
        SupportTicketReply? reply = null,
        string decision = "Decisión",
        IReadOnlyList<string>? annexes = null,
        string signerTitle = "Coordinadora de servicio") =>
        SupportReplyDocument.Create(
            reply ?? Reply(), decision, "  Puedes acudir a la SIC.  ", Signer, "  Ana Gómez  ", signerTitle, annexes ?? [],
            "support/tenants/x/replies/2026/11/a.pdf", "Respuesta-SOP-000001.pdf", Content, new DateOnly(2026, 11, 16), Now);

    [Fact]
    public void ItTrimsAndKeepsTheDecisionWithLineBreaks()
    {
        var reply = Reply();

        var document = Create(reply, "  Revisamos tu caso.\n\nDecidimos devolver el dinero.\r\n\tSin costo.  ", ["  Factura 123  "]);

        Assert.Equal(reply.Id, document.ReplyId);
        Assert.Equal(reply.TenantId, document.TenantId);
        Assert.Equal(reply.TicketId, document.TicketId);
        Assert.Equal("Revisamos tu caso.\n\nDecidimos devolver el dinero.\r\n\tSin costo.", document.Decision);
        Assert.Equal("Puedes acudir a la SIC.", document.NextStep);
        Assert.Equal(Signer, document.SignerMemberId);
        Assert.Equal("Ana Gómez", document.SignerName);
        Assert.Equal("Coordinadora de servicio", document.SignerTitle);
        Assert.Equal(["Factura 123"], document.Annexes);
        Assert.Equal(new DateOnly(2026, 11, 16), document.RespondedOn);
        Assert.Equal(Content.Length, document.SizeBytes);
        Assert.NotEqual(Guid.Empty, document.DocumentClientId);
        Assert.NotEqual(reply.Id, document.DocumentClientId);
        Assert.Equal(Now, document.CreatedAt);
        Assert.Throws<ArgumentException>(() => Create(decision: "   "));
        Assert.Throws<ArgumentException>(() => Create(decision: new string('a', SupportReplyDocument.DecisionMaxLength + 1)));
        Assert.Equal(20_000, Create(decision: new string('a', 20_000)).Decision.Length);
        Assert.Throws<ArgumentException>(() => Create(signerTitle: "  "));
        Assert.Throws<ArgumentException>(() => Create(signerTitle: new string('a', SupportReplyDocument.SignerTitleMaxLength + 1)));
    }

    [Fact]
    public void ItRejectsMoreThanTenAnnexesOrALongOne()
    {
        Assert.Equal(10, SupportReplyDocument.MaxAnnexes);
        Assert.Equal(200, SupportReplyDocument.AnnexMaxLength);
        Assert.Equal(10, Create(annexes: Enumerable.Range(1, 10).Select(index => $"Anexo {index}").ToArray()).Annexes.Count);

        Assert.Throws<ArgumentException>(() => Create(annexes: Enumerable.Range(1, 11).Select(index => $"Anexo {index}").ToArray()));
        Assert.Throws<ArgumentException>(() => Create(annexes: [new string('a', 201)]));
        Assert.Throws<ArgumentException>(() => Create(annexes: ["  "]));
    }

    [Fact]
    public void ItRejectsAReplyWithAnAttachment()
    {
        var withAttachment = SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Text,
            "Te adjunto la foto.", null, Guid.CreateVersion7(), Now, uploadId: Guid.CreateVersion7(), fileName: "foto.jpg");

        Assert.Throws<ArgumentException>(() => Create(withAttachment));
    }

    /// <summary>Sólo una respuesta que sale como texto, plantilla, correo o página de consulta lleva documento: una entrega nueva que se
    /// sume al enum no lo hereda sin decidirlo.</summary>
    [Fact]
    public void ItRejectsAReplyWithAnotherDelivery()
    {
        foreach (var (channel, delivery) in new[]
                 {
                     (SupportReplyChannel.Thread, SupportReplyDelivery.Text),
                     (SupportReplyChannel.WhatsAppTemplate, SupportReplyDelivery.Template),
                     (SupportReplyChannel.Email, SupportReplyDelivery.Email),
                     (SupportReplyChannel.Portal, SupportReplyDelivery.Portal),
                 })
        {
            var reply = SupportTicketReply.Start(
                TenantId, SupportTicketId.New(), Guid.CreateVersion7(), channel, delivery, "Respuesta formal.", null,
                Guid.CreateVersion7(), Now);
            Assert.Equal(reply.Id, Create(reply).ReplyId);
        }

        var unknown = SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, (SupportReplyDelivery)99,
            "Respuesta formal.", null, Guid.CreateVersion7(), Now);

        Assert.Throws<ArgumentException>(() => Create(unknown));
    }

    [Fact]
    public void ItHashesTheContent()
    {
        var document = Create();

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Content)), document.Sha256Hex);
        Assert.Equal(64, document.Sha256Hex.Length);
        Assert.Throws<ArgumentException>(() => SupportReplyDocument.Create(
            Reply(), "Decisión", null, Signer, "Ana", "Coordinadora", [], "support/x.pdf", "Respuesta-SOP-000001.pdf", [],
            new DateOnly(2026, 11, 16), Now));
    }

    [Fact]
    public void TheFileNameCarriesTheTicketNumber()
    {
        Assert.Equal("Respuesta-SOP-000123.pdf", SupportReplyDocument.FileNameFor("SOP-000123"));
        Assert.Equal("Respuesta-SOP-000001.pdf", Create().FileName);
    }
}
