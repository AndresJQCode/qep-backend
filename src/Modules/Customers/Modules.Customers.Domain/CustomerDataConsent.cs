namespace Modules.Customers.Domain;

/// <summary>Spec 2026-10-26 D-5: lo que pasó con la autorización de tratamiento de datos. Se guarda por nombre.</summary>
public enum DataConsentEvent
{
    Accepted,
    Rejected,
    Revoked,
}

/// <summary>Por dónde respondió el titular. Instagram existe en el modelo aunque el bot todavía no corra ahí (D-M6).</summary>
public enum DataConsentChannel
{
    WhatsApp,
    Instagram,
}

/// <summary>Por dónde pidió el titular la revocación: la conversación abierta o cualquier otro medio (teléfono, correo, en
/// persona).</summary>
public enum DataConsentMedium
{
    ThisConversation,
    Other,
}

/// <summary>Quién registró la fila: el bot (aceptación o rechazo) o un miembro del equipo (revocación).</summary>
public enum DataConsentActorKind
{
    Chatbot,
    Member,
}

/// <summary>La identidad que respondió, de la conversación. <see cref="WaId"/> es siempre opcional: Meta no siempre manda el
/// número y en Instagram nunca existe.</summary>
public sealed record DataConsentIdentity(Guid ConversationId, DataConsentChannel Channel, string ChannelUserId, string? WaId);

/// <summary>Lo que prueba una respuesta al bot (D-5, D-6): el texto ya interpolado que se le mostró, el mensaje que la llevó y
/// qué flujo la pidió. El nombre del chatbot se copia porque Customers no puede leer chatbots.</summary>
public sealed record ChatbotConsentEvidence(
    string ShownText,
    Guid? PromptMessageId,
    Guid ReplyMessageId,
    string? ReplyWamid,
    Guid ChatbotId,
    string ChatbotName,
    Guid ChatbotVersionId,
    int ChatbotVersionNumber,
    string NodeId);

/// <summary>
/// Spec 2026-10-26 D-5: una fila del registro de consentimiento de datos (Ley 1581). **Sólo se agregan filas**: no hay ningún
/// método que la cambie, y el estado vigente del cliente es su última fila por <c>(occurred_at, sequence)</c>. Los CHECK de
/// <c>customers.data_consents</c> repiten estas reglas en la base, porque la tabla es prueba y no se reescribe.
/// </summary>
public sealed class CustomerDataConsent
{
    // Espeja customers.data_consents.note: una nota larga sale como 422 con código y no como 500 de PostgreSQL.
    public const int NoteMaxLength = 500;

    // EF Core materializa por acá; FromChatbot y Revocation son los únicos puntos de entrada.
    private CustomerDataConsent()
    {
    }

    private CustomerDataConsent(
        Guid tenantId,
        CustomerId customerId,
        DataConsentEvent consentEvent,
        DateTimeOffset occurredAt,
        DataConsentIdentity? identity,
        DataConsentActorKind actorKind,
        Guid? actorUserId)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        CustomerId = customerId;
        Event = consentEvent;
        OccurredAt = occurredAt;
        Channel = identity?.Channel;
        ChannelUserId = identity?.ChannelUserId;
        WaId = identity?.WaId;
        ConversationId = identity?.ConversationId;
        ActorKind = actorKind;
        ActorUserId = actorUserId;
    }

    public Guid Id { get; private set; }

    /// <summary>La da la base (identity): desempata dos filas del mismo cliente con el mismo <see cref="OccurredAt"/>.</summary>
    public long Sequence { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>El cliente dueño de la fila. La fusión es lo único que la reescribe (D-7).</summary>
    public CustomerId CustomerId { get; private set; }

    public DataConsentEvent Event { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DataConsentChannel? Channel { get; private set; }

    public string? ChannelUserId { get; private set; }

    public string? WaId { get; private set; }

    public Guid? ConversationId { get; private set; }

    public DataConsentActorKind ActorKind { get; private set; }

    /// <summary>El usuario (no la membresía) que revocó, como <see cref="CustomerNote.AuthorUserId"/>. Nulo en las del bot.</summary>
    public Guid? ActorUserId { get; private set; }

    public string? ShownText { get; private set; }

    public Guid? PromptMessageId { get; private set; }

    public Guid? ReplyMessageId { get; private set; }

    public string? ReplyWamid { get; private set; }

    public Guid? ChatbotId { get; private set; }

    public string? ChatbotName { get; private set; }

    public Guid? ChatbotVersionId { get; private set; }

    public int? ChatbotVersionNumber { get; private set; }

    public string? NodeId { get; private set; }

    public DataConsentMedium? Medium { get; private set; }

    public string? Note { get; private set; }

    /// <summary>La respuesta del titular a los botones del bloque. El bot nunca revoca: eso lo registra un miembro.</summary>
    public static CustomerDataConsent FromChatbot(
        Guid tenantId,
        CustomerId customerId,
        DataConsentEvent decision,
        DateTimeOffset occurredAt,
        DataConsentIdentity identity,
        ChatbotConsentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidence);
        if (decision is not (DataConsentEvent.Accepted or DataConsentEvent.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "The chatbot only records an acceptance or a rejection.");
        }

        return new CustomerDataConsent(tenantId, customerId, decision, occurredAt, identity, DataConsentActorKind.Chatbot, actorUserId: null)
        {
            ShownText = evidence.ShownText,
            PromptMessageId = evidence.PromptMessageId,
            ReplyMessageId = evidence.ReplyMessageId,
            ReplyWamid = evidence.ReplyWamid,
            ChatbotId = evidence.ChatbotId,
            ChatbotName = evidence.ChatbotName,
            ChatbotVersionId = evidence.ChatbotVersionId,
            ChatbotVersionNumber = evidence.ChatbotVersionNumber,
            NodeId = evidence.NodeId,
        };
    }

    /// <summary>
    /// La revocación que registra un miembro. Con <see cref="DataConsentMedium.ThisConversation"/> la fila guarda la identidad
    /// de esa conversación (el «mismo medio» que pide la ley queda como dato); con <see cref="DataConsentMedium.Other"/> no
    /// lleva identidad y la nota explica cómo llegó el pedido.
    /// </summary>
    public static CustomerDataConsent Revocation(
        Guid tenantId,
        CustomerId customerId,
        DateTimeOffset occurredAt,
        Guid actorUserId,
        DataConsentMedium medium,
        DataConsentIdentity? identity,
        string note)
    {
        if (medium == DataConsentMedium.ThisConversation && identity is null)
        {
            throw new ArgumentException("A revocation in this conversation needs the conversation's identity.", nameof(identity));
        }

        if (medium == DataConsentMedium.Other && identity is not null)
        {
            throw new ArgumentException("A revocation by another medium carries no identity.", nameof(identity));
        }

        return new CustomerDataConsent(
            tenantId, customerId, DataConsentEvent.Revoked, occurredAt, identity, DataConsentActorKind.Member, actorUserId)
        {
            Medium = medium,
            Note = NormalizeNote(note),
        };
    }

    private static string NormalizeNote(string? note)
    {
        var trimmed = note?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > NoteMaxLength
            ? throw new CustomersDomainException(
                "customers.data_consent.note_invalid",
                $"The revocation note is required and cannot exceed {NoteMaxLength} characters.")
            : trimmed;
    }
}
