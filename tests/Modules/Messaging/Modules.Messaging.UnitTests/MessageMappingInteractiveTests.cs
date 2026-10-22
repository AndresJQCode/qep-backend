using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-21 D-I2: el hilo recibe los botones, la lista o el enlace de un interactivo saliente ya armados desde
/// <c>details</c>; el entrante y un detalle que no se entiende salen con <c>interactive: null</c>, sin romper la página.</summary>
public sealed class MessageMappingInteractiveTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly IReadOnlyDictionary<Guid, string> Empty = new Dictionary<Guid, string>();
    private static readonly IReadOnlyDictionary<Guid, ReplyTargetRow> NoTargets = new Dictionary<Guid, ReplyTargetRow>();

    private static MessageRow Row(MessageDirection direction, string? details) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), direction, MessageKind.Interactive, "¿Qué necesitas?", null, details, MessageStatus.Sent, null,
            DateTimeOffset.UnixEpoch, null, null, null, null, Guid.CreateVersion7());

    private static InteractiveDto? Map(InteractiveSend send) =>
        MessageMapping.ToDto(Row(MessageDirection.Outbound, InteractiveSendJson.ToDetailsJson(send)), TenantId, Empty, NoTargets).Interactive;

    [Fact]
    public void ButtonsComeBackWithTheirIdsAndTitles()
    {
        var dto = Map(new ButtonsSend("¿Qué necesitas?", [new("tech", "Servicio técnico"), new("sales", "Ventas")], Header: "Hola"));

        Assert.Equal("button", dto!.Type);
        Assert.Equal([new InteractiveButtonDto("tech", "Servicio técnico"), new InteractiveButtonDto("sales", "Ventas")], dto.Buttons!);
        Assert.Null(dto.List);
        Assert.Null(dto.Cta);
    }

    [Fact]
    public void AListComesBackWithItsSectionsAndRows()
    {
        var dto = Map(new ListSend("Elige", "Ver equipos", [new("Equipos", [new("1", "Calentador", "A gas"), new("2", "Estufa")]), new(null, [new("3", "Otro")])]));

        Assert.Equal("list", dto!.Type);
        Assert.Null(dto.Buttons);
        Assert.Equal("Ver equipos", dto.List!.ButtonLabel);
        Assert.Equal(2, dto.List.Sections.Count);
        Assert.Equal("Equipos", dto.List.Sections[0].Title);
        Assert.Equal([new InteractiveListRowDto("1", "Calentador", "A gas"), new InteractiveListRowDto("2", "Estufa", null)], dto.List.Sections[0].Rows);
        Assert.Null(dto.List.Sections[1].Title);
    }

    [Fact]
    public void ACtaUrlComesBackWithItsTextAndUrl()
    {
        var dto = Map(new CtaUrlSend("No estás registrado.", "Registrarme", new Uri("https://app.clasic.com.co/registro-clientes")));

        Assert.Equal("cta_url", dto!.Type);
        Assert.Equal(new InteractiveCtaDto("Registrarme", "https://app.clasic.com.co/registro-clientes"), dto.Cta);
    }

    [Fact]
    public void AnInboundInteractiveHasNoCard()
    {
        // Lo que guarda la ingesta: la respuesta del cliente, no una tarjeta (WebhookPayloadParser).
        var row = Row(MessageDirection.Inbound, """{"type":"button_reply","button_reply":{"id":"tech","title":"Servicio técnico"}}""");

        Assert.Null(MessageMapping.ToDto(row, TenantId, Empty, NoTargets).Interactive);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("""{"type":"button","action":{"buttons":[{"type":"reply"}]}}""")]
    [InlineData("""{"type":"carousel","action":{}}""")]
    [InlineData(null)]
    public void UnreadableDetailsGiveNoCard(string? details) =>
        Assert.Null(MessageMapping.ToDto(Row(MessageDirection.Outbound, details), TenantId, Empty, NoTargets).Interactive);
}
