using System.Reflection;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Automations.Application;
using Modules.Automations.Domain.Engine;
using Modules.Customers.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-26 D-5 y D-7, final review F2 y F3: lo que <see cref="AutomationDataConsents"/> decide sin la base. El resto
/// del adaptador se prueba contra PostgreSQL en <c>AutomationDataConsentsAdapterTests</c>.</summary>
public sealed class AutomationDataConsentsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConversationId = Guid.CreateVersion7();
    private static readonly Guid CustomerId = Guid.CreateVersion7();
    private static readonly Guid ReplyId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConversationRow Conversation(ConversationChannel channel) => new(
        ConversationId, TenantId, Guid.CreateVersion7(), "CO.laura1", "573001234567", null, "Laura", CustomerId, null,
        ConversationStatus.Open, 0, Now, null, null, null, null, null, null, Now, 1, channel);

    private static InboundForAutomation Inbound(Guid? conversationId) =>
        new(ReplyId, MessageKind.Interactive, "Acepto", "accepted", null, Now, "wamid.R1", ConversationId: conversationId);

    private static DataConsentRecord Record() => new(
        TenantId, ConversationId, DataConsentDecision.Accepted, "¿Aceptas?", Guid.CreateVersion7(), ReplyId, Guid.CreateVersion7(),
        "Asistente", Guid.CreateVersion7(), 1, "consent");

    private static (AutomationDataConsents Adapter, List<string> Customers) Arrange(ConversationRow row, InboundForAutomation? inbound)
    {
        var calls = new List<string>();
        var consents = Stub<ICustomerDataConsents>((method, _) =>
        {
            calls.Add(method.Name);
            return method.Name == nameof(ICustomerDataConsents.HasCurrentAcceptanceAsync)
                ? Task.FromResult(true)
                : Task.FromResult(DataConsentRecordOutcome.Recorded);
        });
        var adapter = new AutomationDataConsents(
            Stub<IConversationQueries>((method, _) => method.Name == nameof(IConversationQueries.FindAsync)
                ? Task.FromResult<ConversationRow?>(row)
                : throw new NotSupportedException(method.Name)),
            Unused<IConversationRepository>(),
            Unused<IMessagingCustomerDirectory>(),
            Stub<IAutomationInbox>((method, _) => method.Name == nameof(IAutomationInbox.ReadInboundAsync)
                ? Task.FromResult(inbound)
                : throw new NotSupportedException(method.Name)),
            consents,
            Unused<IMessagingNotifier>(),
            Unused<IClock>(),
            NullLogger<AutomationDataConsents>.Instance);
        return (adapter, calls);
    }

    [Fact]
    public async Task AKnownChannelChecksAndRecords()
    {
        var (adapter, calls) = Arrange(Conversation(ConversationChannel.WhatsApp), Inbound(ConversationId));

        Assert.Equal(DataConsentCheck.Accepted, await adapter.CheckAsync(TenantId, ConversationId, Ct));
        Assert.True(await adapter.RecordAsync(Record(), Ct));

        Assert.Equal([nameof(ICustomerDataConsents.HasCurrentAcceptanceAsync), nameof(ICustomerDataConsents.RecordFromChatbotAsync)], calls);
    }

    /// <summary>F3: un canal de Mensajería que Customers todavía no conoce no puede registrarse. Lanzar haría fallar cada reintento de la
    /// unidad y el toque se perdería; «no se puede registrar» deja al bot seguir por «No acepto» sin preguntar.</summary>
    [Fact]
    public async Task AnUnmappedChannelCannotBeRecorded()
    {
        var (adapter, calls) = Arrange(Conversation((ConversationChannel)99), Inbound(ConversationId));

        Assert.Equal(DataConsentCheck.Unavailable, await adapter.CheckAsync(TenantId, ConversationId, Ct));
        Assert.False(await adapter.RecordAsync(Record(), Ct));

        Assert.Empty(calls);
    }

    /// <summary>F2: el entrante tiene que ser de la conversación del registro.</summary>
    [Fact]
    public async Task AReplyFromAnotherConversationIsNotRecorded()
    {
        var (otherConversation, calls) = Arrange(Conversation(ConversationChannel.WhatsApp), Inbound(Guid.CreateVersion7()));
        var (unknownConversation, unknownCalls) = Arrange(Conversation(ConversationChannel.WhatsApp), Inbound(conversationId: null));

        Assert.False(await otherConversation.RecordAsync(Record(), Ct));
        Assert.False(await unknownConversation.RecordAsync(Record(), Ct));

        Assert.Empty(calls);
        Assert.Empty(unknownCalls);
    }

    /// <summary>Un doble de interfaz que responde con <c>handler</c>; <see cref="Unused{T}"/> lanza en cualquier llamada.</summary>
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static T Unused<T>()
        where T : class => Stub<T>((method, _) => throw new NotSupportedException($"{typeof(T).Name}.{method.Name}"));

    public class StubProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return Handler(targetMethod, args);
        }
    }
}
