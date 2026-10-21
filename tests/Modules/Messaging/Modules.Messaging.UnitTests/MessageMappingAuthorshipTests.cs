using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-21 D-I3 y §5.5: un saliente de un chatbot sale con <c>sentByChatbot</c> y sin <c>sentBy</c>; el
/// nombre que el puerto no conoce cae a «Chatbot», nunca a un null; y una fila de chatbot no pide nombre de miembro.</summary>
public sealed class MessageMappingAuthorshipTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly IReadOnlyDictionary<Guid, string> Empty = new Dictionary<Guid, string>();
    private static readonly IReadOnlyDictionary<Guid, ReplyTargetRow> NoTargets = new Dictionary<Guid, ReplyTargetRow>();

    private static MessageRow RowWithChatbot(Guid chatbotId) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), MessageDirection.Outbound, MessageKind.Text, "hola", null, null, MessageStatus.Sent, null,
            DateTimeOffset.UnixEpoch, null, null, null, null, chatbotId);

    [Fact]
    public void AChatbotRowMapsToSentByChatbotAndNoMember()
    {
        var id = Guid.CreateVersion7();

        var dto = MessageMapping.ToDto(RowWithChatbot(id), TenantId, Empty, NoTargets, chatbotNames: new Dictionary<Guid, string> { [id] = "Servicio técnico" });

        Assert.Null(dto.SentBy);
        Assert.Equal(new ChatbotRefDto(id, "Servicio técnico"), dto.SentByChatbot);
    }

    [Fact]
    public void AnUnknownChatbotNameFallsBackToChatbot()
    {
        var dto = MessageMapping.ToDto(RowWithChatbot(Guid.CreateVersion7()), TenantId, Empty, NoTargets, chatbotNames: Empty);

        Assert.Equal("Chatbot", dto.SentByChatbot!.Name);
    }

    [Fact]
    public void MemberIdsOfSkipsChatbotRows() => Assert.Empty(MessageMapping.MemberIdsOf([RowWithChatbot(Guid.CreateVersion7())]));

    [Fact]
    public void ChatbotIdsOfCollectsTheDistinctChatbots()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal([id], MessageMapping.ChatbotIdsOf([RowWithChatbot(id), RowWithChatbot(id)]));
    }
}
