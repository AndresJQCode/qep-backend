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

    private static MessageRow Row(MessageDirection direction, MessageKind kind, string? text = null, string? details = null, Guid? replyTo = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), direction, kind, text, null, details, MessageStatus.Delivered, null,
            DateTimeOffset.UnixEpoch, null, null, null, replyTo);

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
}
