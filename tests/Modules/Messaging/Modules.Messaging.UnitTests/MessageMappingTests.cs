using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-10 §5.1: un evento sale System/Event/Delivered con <c>event</c> lleno y lo demás en null; sus
/// miembros con nombre o «Miembro eliminado»; <c>replyTo.preview</c> es el texto o la leyenda recortado a 200, y
/// <c>replyTo</c> es null si QEP no tiene el citado.</summary>
public sealed class MessageMappingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid Andres = Guid.CreateVersion7();
    private static readonly Guid Gone = Guid.CreateVersion7();
    private static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string> { [Andres] = "Andrés" };
    private static readonly IReadOnlyDictionary<Guid, ReplyTargetRow> NoTargets = new Dictionary<Guid, ReplyTargetRow>();

    private static MessageRow Row(MessageDirection direction, MessageKind kind, string? text = null, string? details = null, Guid? replyTo = null, bool imported = false) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), direction, kind, text, null, details, MessageStatus.Delivered, null,
            DateTimeOffset.UnixEpoch, null, null, null, replyTo, Imported: imported);

    [Fact]
    public void AnEventCarriesItsMembersAndNothingElse()
    {
        var details = ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.Transferred, Actor: Andres, Target: Gone));

        var dto = MessageMapping.ToDto(Row(MessageDirection.System, MessageKind.Event, details: details), TenantId, Names, NoTargets);

        Assert.Equal(("System", "Event", "Delivered"), (dto.Direction, dto.Kind, dto.Status));
        Assert.Equal(new MessageEventDto("Transferred", new MemberRefDto(Andres, "Andrés"), new MemberRefDto(Gone, MessageMapping.DeletedMemberName), null), dto.Event);
        Assert.Null(dto.Text);
        Assert.Null(dto.Media);
        Assert.Null(dto.Location);
        Assert.Null(dto.SentBy);
        Assert.Null(dto.ReplyTo);
    }

    [Fact]
    public void AMessageThatIsNotAnEventHasNoEvent() =>
        Assert.Null(MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Text, "hola"), TenantId, Names, NoTargets).Event);

    [Fact]
    public void TheMemberIdsOfAPageIncludeEventMembers()
    {
        var details = ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.Taken, Actor: Andres, Previous: Gone));

        var ids = MessageMapping.MemberIdsOf([Row(MessageDirection.System, MessageKind.Event, details: details)]);

        Assert.Equal(new[] { Andres, Gone }.Order(), ids.Order());
    }

    [Fact]
    public void TheReplyPreviewIsTheTextOrCaptionCutTo200()
    {
        var quoted = Guid.CreateVersion7();
        var targets = new Dictionary<Guid, ReplyTargetRow>
        {
            [quoted] = new(quoted, Guid.CreateVersion7(), MessageDirection.Outbound, MessageKind.Image, null, new string('a', 300), "wamid.q"),
        };

        var dto = MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Text, "sí", replyTo: quoted), TenantId, Names, targets);

        Assert.Equal(new ReplyToDto(quoted, "Outbound", "Image", new string('a', 200)), dto.ReplyTo);
    }

    [Fact]
    public void AReplyToAMessageQepDoesNotHaveIsNull() =>
        Assert.Null(MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Text, "sí", replyTo: Guid.CreateVersion7()), TenantId, Names, NoTargets).ReplyTo);

    // Los details de un Unsupported son los que escribe la ingesta: el type de Meta y sus errors tal cual.
    private static string UnsupportedDetails(int code) =>
        $$$"""{"type":"unsupported","errors":[{"code":{{{code}}},"title":"x","message":"x","error_data":{"details":"x"}}]}""";

    [Theory]
    [InlineData(131060, "WhatsApp no entregó el contenido de este mensaje. Pídele a la persona que lo envíe de nuevo.")]
    [InlineData(131051, "WhatsApp no admite este tipo de mensaje. Pídele a la persona que lo envíe como texto, imagen o documento.")]
    public void AnUnsupportedMessageWithAKnownMetaCodeCarriesItsReason(int code, string expected) =>
        Assert.Equal(expected, MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Unsupported, details: UnsupportedDetails(code)), TenantId, Names, NoTargets).UnsupportedReason);

    [Theory]
    [InlineData(131051, true, "Este mensaje no se pudo importar del historial: WhatsApp no entrega este tipo. Puedes verlo en el celular.")]
    [InlineData(131051, false, "WhatsApp no admite este tipo de mensaje. Pídele a la persona que lo envíe como texto, imagen o documento.")]
    [InlineData(131060, true, "WhatsApp no entregó el contenido de este mensaje. Pídele a la persona que lo envíe de nuevo.")]
    public void AnImportedUnsupportedMessageDoesNotAskThePersonToResend(int code, bool imported, string expected) =>
        Assert.Equal(expected, MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Unsupported, details: UnsupportedDetails(code), imported: imported), TenantId, Names, NoTargets).UnsupportedReason);

    // Sin motivo conocido la burbuja dibuja la etiqueta del kind, que ya existe en el frontend.
    [Theory]
    [InlineData("""{"type":"unsupported","errors":[{"code":999999}]}""")]
    [InlineData("""{"type":"order","errors":null}""")]
    [InlineData("""{"type":"unsupported","errors":[{"code":"131060"}]}""")]
    [InlineData("no es json")]
    [InlineData(null)]
    public void AnUnsupportedMessageWithoutAKnownCodeHasNoReason(string? details) =>
        Assert.Null(MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Unsupported, details: details), TenantId, Names, NoTargets).UnsupportedReason);

    [Fact]
    public void OnlyAnUnsupportedMessageHasAReason() =>
        Assert.Null(MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Location, details: UnsupportedDetails(131060)), TenantId, Names, NoTargets).UnsupportedReason);

    private static readonly Guid CustomerA = Guid.CreateVersion7();
    private static readonly Guid CustomerB = Guid.CreateVersion7();
    private static readonly Dictionary<Guid, CustomerRefDto> CustomerRefs = new()
    {
        [CustomerA] = new(CustomerA, "LUIS ALBERTO MARULANDA GONZALEZ", true),
        [CustomerB] = new(CustomerB, "ZIBA TIENDA MULTIMARCA S.A.S.", true),
    };

    private static MessageRow CustomerChangedRow() =>
        Row(MessageDirection.System, MessageKind.Event, details: ConversationEventJson.Serialize(
            new ConversationEvent(ConversationEventType.CustomerChanged, Actor: Andres, CustomerId: CustomerB, PreviousCustomerId: CustomerA)));

    [Fact]
    public void CustomerChangedCarriesBothCustomerNamesAndNoMemberInPreviousOrTarget()
    {
        var dto = MessageMapping.ToDto(CustomerChangedRow(), TenantId, Names, NoTargets, customerRefs: CustomerRefs);

        Assert.Equal(
            new MessageEventDto(
                "CustomerChanged", new MemberRefDto(Andres, "Andrés"), null, null,
                TargetCustomer: new EventCustomerRefDto(CustomerB, "ZIBA TIENDA MULTIMARCA S.A.S."),
                PreviousCustomer: new EventCustomerRefDto(CustomerA, "LUIS ALBERTO MARULANDA GONZALEZ")),
            dto.Event);
    }

    [Fact]
    public void AnUnknownCustomerLeavesTheRefNull()
    {
        var refs = new Dictionary<Guid, CustomerRefDto> { [CustomerB] = CustomerRefs[CustomerB] };

        var dto = MessageMapping.ToDto(CustomerChangedRow(), TenantId, Names, NoTargets, customerRefs: refs);

        Assert.Null(dto.Event!.PreviousCustomer);
        Assert.Equal(CustomerB, dto.Event.TargetCustomer!.Id);
        Assert.Null(MessageMapping.ToDto(CustomerChangedRow(), TenantId, Names, NoTargets).Event!.TargetCustomer);
    }

    [Fact]
    public void TheWireShapeIsTheContract()
    {
        var dto = MessageMapping.ToDto(CustomerChangedRow(), TenantId, Names, NoTargets, customerRefs: CustomerRefs);

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(dto.Event, System.Text.Json.JsonSerializerOptions.Web));
        var root = json.RootElement;

        Assert.Equal("ZIBA TIENDA MULTIMARCA S.A.S.", root.GetProperty("targetCustomer").GetProperty("name").GetString());
        Assert.Equal(CustomerB, root.GetProperty("targetCustomer").GetProperty("id").GetGuid());
        Assert.Equal(CustomerA, root.GetProperty("previousCustomer").GetProperty("id").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("target").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("previous").ValueKind);
        Assert.Equal("Andrés", root.GetProperty("actor").GetProperty("displayName").GetString());
    }

    [Fact]
    public void CustomerIdsAreNeverAskedAsMembers()
    {
        Assert.Equal([Andres], MessageMapping.MemberIdsOf([CustomerChangedRow()]));
        Assert.Equal(new[] { CustomerA, CustomerB }.Order(), MessageMapping.CustomerIdsOf([CustomerChangedRow()]).Order());
    }
}
