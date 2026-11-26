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
        Assert.Empty(Errors(Valid() with { UploadId = Guid.CreateVersion7(), FileName = "contrato.pdf" }));
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

    // Spec 2026-11-22 D3: None es el canal de una tarea y comparte el enum con los de verdad; ninguna respuesta lo elige.
    [Fact]
    public void NoneIsNotAReplyChannel() =>
        Assert.Equal(["Channel"], Errors(Valid("None")));

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
        Assert.Equal(["UploadId"], Errors(Valid("Email") with { UploadId = Guid.CreateVersion7(), FileName = "contrato.pdf" }));
        Assert.Equal(["UploadId"], Errors(Valid("WhatsAppTemplate") with { UploadId = Guid.CreateVersion7(), FileName = "contrato.pdf" }));
        Assert.Equal(["UploadId"], Errors(Valid() with { UploadId = Guid.Empty, FileName = "contrato.pdf" }));
    }

    // Tarea 11: el staging se reconstruye con la extensión del nombre, y el texto de la respuesta viaja como pie del adjunto (≤ 1024).
    [Fact]
    public void TheAttachmentNeedsItsFileNameAndAShortText()
    {
        var upload = Valid() with { UploadId = Guid.CreateVersion7(), FileName = "contrato.pdf" };

        Assert.Equal(["FileName"], Errors(upload with { FileName = null }));
        Assert.Equal(["FileName"], Errors(upload with { FileName = "   " }));
        Assert.Equal(["FileName"], Errors(upload with { FileName = new string('a', SupportReplyAttachmentRules.FileNameMaxLength + 1) }));
        Assert.Equal(["FileName"], Errors(Valid() with { FileName = "contrato.pdf" }));
        Assert.Equal(["Text"], Errors(upload with { Text = new string('a', SupportReplyAttachmentRules.CaptionMaxLength + 1) }));
        Assert.Empty(Errors(upload with { Text = new string('a', SupportReplyAttachmentRules.CaptionMaxLength) }));
    }

    // ---- Fase 4b, Tarea 8: la respuesta formal (H1 a H4, H11) ----

    private static SupportReplyRequest Formal(
        string? decision = "Revisamos tu caso y te damos la razón.", string? signerTitle = "Coordinadora de servicio",
        IReadOnlyList<string>? annexes = null) =>
        Valid() with { Formal = new SupportFormalReplyRequest(decision, signerTitle, annexes ?? ["Factura de octubre"]) };

    [Fact]
    public void AValidFormalReplyPasses()
    {
        Assert.Empty(Errors(Formal()));
        Assert.Empty(Errors(Formal(annexes: [])));
        Assert.Empty(Errors(Formal() with { Formal = new SupportFormalReplyRequest("Decisión", "Gerente", null) }));
        Assert.Empty(Errors(Formal(decision: "Primer punto.\nSegundo punto.\r\n\tTercero.")));
        Assert.Empty(Errors(Formal() with { Channel = "Email" }));
    }

    [Fact]
    public void AFormalReplyNeedsADecisionAndASignerTitle()
    {
        Assert.Equal(["Formal.Decision"], Errors(Formal(decision: null)));
        Assert.Equal(["Formal.Decision"], Errors(Formal(decision: "   ")));
        Assert.Equal(["Formal.Decision"], Errors(Formal(decision: "Decisión\u0007")));
        Assert.Equal(["Formal.SignerTitle"], Errors(Formal(signerTitle: null)));
        Assert.Equal(["Formal.SignerTitle"], Errors(Formal(signerTitle: " ")));
        Assert.Equal(["Formal.SignerTitle"], Errors(Formal(signerTitle: "Gerente\nGeneral")));
        Assert.Equal(["Formal.SignerTitle"], Errors(Formal(signerTitle: new string('a', 121))));
        Assert.Empty(Errors(Formal(signerTitle: "  " + new string('a', 120) + "  ")));
    }

    [Fact]
    public void TheDecisionIsAtMost20000()
    {
        Assert.Equal(["Formal.Decision"], Errors(Formal(decision: new string('a', 20_001))));
        Assert.Empty(Errors(Formal(decision: new string('a', 20_000))));
    }

    // Revisión del Bloque A: un anexo vacío o largo es 422 en su campo, nunca un ArgumentException del dominio (500).
    [Fact]
    public void AtMostTenAnnexesOf200()
    {
        Assert.Equal(["Formal.Annexes"], Errors(Formal(annexes: Enumerable.Range(1, 11).Select(index => $"Anexo {index}").ToList())));
        Assert.Empty(Errors(Formal(annexes: Enumerable.Range(1, 10).Select(index => $"Anexo {index}").ToList())));
        Assert.Equal(["Formal.Annexes[1]"], Errors(Formal(annexes: ["Factura", "   "])));
        Assert.Equal(["Formal.Annexes[0]"], Errors(Formal(annexes: [new string('a', 201)])));
        Assert.Equal(["Formal.Annexes[0]"], Errors(Formal(annexes: ["Factura\nde octubre"])));
        Assert.Empty(Errors(Formal(annexes: [" " + new string('a', 200) + " "])));
    }

    // H11: una respuesta lleva un solo documento.
    [Fact]
    public void FormalAndAnAttachmentAreExclusive() =>
        Assert.Equal(["UploadId"], Errors(Formal() with { UploadId = Guid.CreateVersion7(), FileName = "contrato.pdf" }));
}
