using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// Spec 2026-10-26 D-5: una fila del registro de consentimiento. Las del bot llevan la evidencia y nunca una nota; una
/// revocación la registra un miembro, con nota (1–500) y medio, y con <see cref="DataConsentMedium.Other"/> sin identidad.
/// </summary>
public sealed class CustomerDataConsentTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly CustomerId CustomerId = CustomerId.New();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly DataConsentIdentity Identity =
        new(Guid.CreateVersion7(), DataConsentChannel.WhatsApp, "CO.123456789", "573001234567");

    private static readonly ChatbotConsentEvidence Evidence = new(
        "Hola Ana, ¿aceptas nuestra política de datos? https://qcode.co/datos",
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "wamid.HBgM",
        Guid.CreateVersion7(),
        "Bienvenida",
        Guid.CreateVersion7(),
        3,
        "n4");

    private static CustomerDataConsent RevocationWith(string note) =>
        CustomerDataConsent.Revocation(TenantId, CustomerId, Now, Member, DataConsentMedium.Other, identity: null, note);

    [Fact]
    public void AChatbotRowCarriesTheEvidenceAndNoNote()
    {
        var consent = CustomerDataConsent.FromChatbot(TenantId, CustomerId, DataConsentEvent.Accepted, Now, Identity, Evidence);

        Assert.Equal(TenantId, consent.TenantId);
        Assert.Equal(CustomerId, consent.CustomerId);
        Assert.Equal(DataConsentEvent.Accepted, consent.Event);
        Assert.Equal(Now, consent.OccurredAt);
        Assert.Equal(DataConsentActorKind.Chatbot, consent.ActorKind);
        Assert.Null(consent.ActorUserId);
        Assert.Null(consent.Medium);
        Assert.Null(consent.Note);
        Assert.Equal(Identity.ConversationId, consent.ConversationId);
        Assert.Equal(Identity.Channel, consent.Channel);
        Assert.Equal(Identity.ChannelUserId, consent.ChannelUserId);
        Assert.Equal(Identity.WaId, consent.WaId);
        Assert.Equal(Evidence.ShownText, consent.ShownText);
        Assert.Equal(Evidence.PromptMessageId, consent.PromptMessageId);
        Assert.Equal(Evidence.ReplyMessageId, consent.ReplyMessageId);
        Assert.Equal(Evidence.ReplyWamid, consent.ReplyWamid);
        Assert.Equal(Evidence.ChatbotId, consent.ChatbotId);
        Assert.Equal(Evidence.ChatbotName, consent.ChatbotName);
        Assert.Equal(Evidence.ChatbotVersionId, consent.ChatbotVersionId);
        Assert.Equal(Evidence.ChatbotVersionNumber, consent.ChatbotVersionNumber);
        Assert.Equal(Evidence.NodeId, consent.NodeId);
        Assert.NotEqual(Guid.Empty, consent.Id);
    }

    [Fact]
    public void AChatbotRowCannotBeARevocation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CustomerDataConsent.FromChatbot(TenantId, CustomerId, DataConsentEvent.Revoked, Now, Identity, Evidence));
    }

    [Fact]
    public void ARevocationByAnotherMediumHasNoIdentity()
    {
        var consent = RevocationWith("  Lo pidió por correo. ");

        Assert.Equal(DataConsentEvent.Revoked, consent.Event);
        Assert.Equal("Lo pidió por correo.", consent.Note);
        Assert.Equal(DataConsentMedium.Other, consent.Medium);
        Assert.Equal(DataConsentActorKind.Member, consent.ActorKind);
        Assert.Equal(Member, consent.ActorUserId);
        Assert.Null(consent.Channel);
        Assert.Null(consent.ChannelUserId);
        Assert.Null(consent.WaId);
        Assert.Null(consent.ConversationId);
        Assert.Null(consent.ShownText);
        Assert.Null(consent.ReplyMessageId);
        Assert.Null(consent.ChatbotId);
    }

    [Fact]
    public void ARevocationInThisConversationKeepsItsIdentity()
    {
        var consent = CustomerDataConsent.Revocation(
            TenantId, CustomerId, Now, Member, DataConsentMedium.ThisConversation, Identity, "Lo escribió en el chat.");

        Assert.Equal(DataConsentMedium.ThisConversation, consent.Medium);
        Assert.Equal(Identity.ConversationId, consent.ConversationId);
        Assert.Equal(Identity.Channel, consent.Channel);
        Assert.Equal(Identity.ChannelUserId, consent.ChannelUserId);
        Assert.Equal(Identity.WaId, consent.WaId);
    }

    public static TheoryData<string> InvalidNotes => ["", "   ", new string('a', CustomerDataConsent.NoteMaxLength + 1)];

    [Theory]
    [MemberData(nameof(InvalidNotes))]
    public void ARevocationNeedsANoteUpTo500(string note)
    {
        var error = Assert.Throws<CustomersDomainException>(() => RevocationWith(note));

        Assert.Equal("customers.data_consent.note_invalid", error.Code);
    }

    [Fact]
    public void ARevocationAcceptsANoteOfExactly500()
    {
        Assert.Equal(CustomerDataConsent.NoteMaxLength, RevocationWith(new string('a', CustomerDataConsent.NoteMaxLength)).Note!.Length);
    }

    [Fact]
    public void AThisConversationRevocationNeedsTheIdentity()
    {
        Assert.Throws<ArgumentException>(() => CustomerDataConsent.Revocation(
            TenantId, CustomerId, Now, Member, DataConsentMedium.ThisConversation, identity: null, "Lo escribió en el chat."));
    }

    [Fact]
    public void AnOtherMediumRevocationCannotCarryAnIdentity()
    {
        Assert.Throws<ArgumentException>(() => CustomerDataConsent.Revocation(
            TenantId, CustomerId, Now, Member, DataConsentMedium.Other, Identity, "Lo pidió por teléfono."));
    }
}
