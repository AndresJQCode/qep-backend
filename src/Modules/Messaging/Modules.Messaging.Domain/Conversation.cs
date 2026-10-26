using System.Text.RegularExpressions;

namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-09 §7.2: una persona (<c>wa_id</c>) hablando con un número de la organización
/// (<c>connection_id</c>). La ingesta de entrantes <b>no</b> pasa por acá (SQL atómico, §7.5), y marcar
/// leído tampoco (un UPDATE atómico, §8.4: con el token de concurrencia, un entrante en medio daría 412):
/// este agregado sólo resuelve, reabre y asigna (spec 2026-10-10 §6.1.3), con <see cref="Version"/> como token de
/// concurrencia.
/// La «foto» del último mensaje la escribe la ingesta; acá es de sólo lectura.
/// </summary>
public sealed partial class Conversation
{
    public const int WaIdMaxLength = 20;
    public const int ProfileNameMaxLength = 256;
    public const int PreviewMaxLength = 200;

    /// <summary>Spec 2026-10-10 §6.1.1: el BSUID mide hasta 131 (2 + 1 + 128); 150 deja margen.</summary>
    public const int UserIdMaxLength = 150;

    /// <summary>Meta: hasta 35; la columna deja margen sin costo.</summary>
    public const int UsernameMaxLength = 64;

    /// <summary>§8.3: 24 h desde el último mensaje de la persona.</summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(24);

    /// <summary>§8.4: Meta sólo marca leído dentro de 30 días desde la recepción.</summary>
    public static readonly TimeSpan ReadReceiptWindow = TimeSpan.FromDays(30);

    /// <summary>§6.1.1: código ISO 3166 alfa-2 + «.» + 1 a 128 alfanuméricos.</summary>
    public static bool IsValidUserId(string? value) => value is not null && UserIdShape().IsMatch(value);

    /// <summary>Spec 2026-10-14 §6.2: en Instagram la clave es el IGSID, numérico; en WhatsApp el BSUID.</summary>
    public static bool IsValidUserId(string? value, ConversationChannel channel) =>
        channel == ConversationChannel.Instagram ? IsValidInstagramUserId(value) : IsValidUserId(value);

    public static bool IsValidInstagramUserId(string? value) => value is not null && InstagramUserIdShape().IsMatch(value);

    [GeneratedRegex("^[0-9]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstagramUserIdShape();

    // «\z» y no «$»: en .NET «$» también acepta un «\n» final, y "CO.1\n" llegaría a la columna.
    [GeneratedRegex("^[A-Z]{2}\\.[A-Za-z0-9]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdShape();

    private Conversation() { }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>Spec 2026-10-14 decisión 4: lo fija la conexión al crear la conversación; nunca cambia.</summary>
    public ConversationChannel Channel { get; private set; }

    /// <summary>Spec 2026-10-10 §6.1.1: el BSUID, la clave de la conversación. <c>null</c> sólo en una fila vieja que
    /// todavía no recibió un entrante con BSUID (§8.1, adopción).</summary>
    public string? UserId { get; private set; }

    /// <summary>Dígitos, sin «+», como lo manda Meta. <c>null</c> cuando Meta no mandó el teléfono (§3).</summary>
    public string? WaId { get; private set; }

    public string? Username { get; private set; }

    /// <summary>Se guarda, no se usa en este slice (§1, fuera de alcance).</summary>
    public string? ParentUserId { get; private set; }

    /// <summary>§6.1.2: lo fija la ingesta; reemplaza el emparejamiento por teléfono al leer (D-A12).</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>§6.1.3: referencia blanda a la membresía (sin FK, como <c>sent_by_member_id</c>).</summary>
    public Guid? AssignedMemberId { get; private set; }

    public DateTimeOffset? AssignedAt { get; private set; }

    public string? ProfileName { get; private set; }

    public ConversationStatus Status { get; private set; }

    /// <summary>Spec 2026-10-15 D-R2 y D-R7: el motivo de la resolución vigente, sólo para que el resumen lo dibuje. El de cada
    /// resolución vive en su evento <c>Resolved</c>. <c>null</c> si está abierta o se resolvió sin motivo; la ingesta lo limpia por SQL
    /// al reabrir (§3.6).</summary>
    public Guid? ResolutionReasonId { get; private set; }

    /// <summary>Spec 2026-10-21 §4.2: <c>None</c> al nacer. Lo mueven <see cref="StartAutomation"/>, <see cref="PauseAutomation"/>,
    /// <see cref="ResumeAutomation"/>, tomar/transferir (que pausan un asistente activo) y resolver (que lo borra).</summary>
    public ConversationAutomationStatus AutomationStatus { get; private set; }

    public DateTimeOffset? AutomationStartedAt { get; private set; }

    public DateTimeOffset? AutomationPausedAt { get; private set; }

    /// <summary>Quién pausó; <c>null</c> si lo pausó el sistema (p. ej. <c>Handoff</c>).</summary>
    public Guid? AutomationPausedByMemberId { get; private set; }

    public AutomationPauseReason? AutomationPauseReason { get; private set; }

    /// <summary>Spec 2026-10-21 §4.2: el chatbot que atiende (referencia blanda, otro módulo). Obligatorio mientras
    /// <see cref="AutomationStatus"/> no sea <c>None</c> (<c>CK_conversations_automation_chatbot</c>).</summary>
    public Guid? AutomationChatbotId { get; private set; }

    /// <summary>El paso del flujo donde va la conversación; lo escribe el motor.</summary>
    public string? AutomationStep { get; private set; }

    /// <summary>
    /// Spec 2026-10-21 D-M15 y ruling P2-R25: cuándo una persona devolvió la conversación al asistente (<see cref="ResumeAutomation"/>). El
    /// motor no ve el ida y vuelta (tomar pausa y no encola entrantes; devolver vuelve a <c>Active</c>), así que compara esta hora con el
    /// arranque de su corrida viva: si es posterior, esa corrida sobrevivió a un humano y termina <c>Interrupted</c>. Arrancar no la
    /// escribe; se borra con el resto del asistente.
    /// </summary>
    public DateTimeOffset? AutomationResumedAt { get; private set; }

    /// <summary>Spec 2026-10-26 §3.1: cuándo un asesor bloqueó al contacto en WhatsApp. No toca estado, asignación ni asistente.</summary>
    public DateTimeOffset? BlockedAt { get; private set; }

    public Guid? BlockedByMemberId { get; private set; }

    /// <summary>El <c>wa_id</c> que se bloqueó. Se guarda aparte porque el cambio de número reescribe <see cref="WaId"/> por SQL: el
    /// desbloqueo debe apuntar al número que Meta tiene bloqueado, no al nuevo.</summary>
    public string? BlockedWaId { get; private set; }

    public bool IsBlocked => BlockedAt is not null;

    public int UnreadCount { get; private set; }

    public DateTimeOffset? LastInboundAt { get; private set; }

    public string? LastInboundWamid { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public Guid? LastMessageId { get; private set; }

    public MessageDirection? LastMessageDirection { get; private set; }

    public MessageKind? LastMessageKind { get; private set; }

    public string? LastMessagePreview { get; private set; }

    public MessageStatus? LastMessageStatus { get; private set; }

    public DateTimeOffset? LastMessageAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset? CustomerWindowExpiresAt => LastInboundAt?.Add(WindowLength);

    public bool IsWindowOpen(DateTimeOffset now) => CustomerWindowExpiresAt is { } expiresAt && expiresAt > now;

    /// <summary>Para Meta: hay un último entrante y tiene menos de 30 días (§8.4). Estático porque marcar
    /// leído no carga el agregado: recibe lo que devuelve el UPDATE atómico.</summary>
    public static bool CanAcknowledgeReading(string? lastInboundWamid, DateTimeOffset? lastInboundAt, DateTimeOffset now) =>
        lastInboundWamid is not null && lastInboundAt is { } at && now - at < ReadReceiptWindow;

    /// <summary>P12: una fila <b>vieja</b>, sólo teléfono. La usan el harness y las pruebas de adopción; producción
    /// crea por SQL con BSUID.</summary>
    public static Conversation Start(Guid id, Guid tenantId, Guid connectionId, string waId, string? profileName, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(waId) || waId.Length > WaIdMaxLength || !waId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A wa_id is 1 to 20 digits.", nameof(waId));
        }

        return new Conversation
        {
            Id = id,
            TenantId = tenantId,
            ConnectionId = connectionId,
            WaId = waId,
            ProfileName = Truncate(profileName, ProfileNameMaxLength),
            Status = ConversationStatus.Open,
            UnreadCount = 0,
            LastActivityAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Spec 2026-10-10 §6.1.1: una conversación nueva por BSUID, con teléfono si Meta lo mandó. En producción la
    /// crea la ingesta por SQL; esto lo usan las pruebas y el harness.</summary>
    public static Conversation StartWithUserId(
        Guid id, Guid tenantId, Guid connectionId, string userId, string? waId, string? profileName, DateTimeOffset now,
        ConversationChannel channel = ConversationChannel.WhatsApp)
    {
        if (!IsValidUserId(userId, channel))
        {
            throw new ArgumentException(
                channel == ConversationChannel.Instagram
                    ? "An Instagram user id is numeric (the IGSID)."
                    : "A user id is a business-scoped user id (CC.alphanumerics).",
                nameof(userId));
        }

        if (channel == ConversationChannel.Instagram && waId is not null)
        {
            throw new ArgumentException("An Instagram conversation has no phone number.", nameof(waId));
        }

        if (waId is not null && (waId.Length is 0 or > WaIdMaxLength || !waId.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException("A wa_id is 1 to 20 digits.", nameof(waId));
        }

        return new Conversation
        {
            Id = id,
            TenantId = tenantId,
            ConnectionId = connectionId,
            Channel = channel,
            UserId = userId,
            WaId = waId,
            ProfileName = Truncate(profileName, ProfileNameMaxLength),
            Status = ConversationStatus.Open,
            UnreadCount = 0,
            LastActivityAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Sólo pruebas: una conversación con contadores y ventana ya puestos, como los dejaría la ingesta.
    /// Internal (visible sólo para las pruebas): ningún código de producción puede saltarse las reglas.</summary>
    internal static Conversation ForTests(Conversation source, int unreadCount, DateTimeOffset? lastInboundAt, string? lastInboundWamid = "wamid.test")
    {
        source.UnreadCount = unreadCount;
        source.LastInboundAt = lastInboundAt;
        source.LastInboundWamid = lastInboundAt is null ? null : lastInboundWamid;
        return source;
    }

    /// <summary>Spec 2026-10-15 §3.3: la regla de «ya está resuelta» se puede preguntar sin resolver, para que el handler la
    /// compruebe antes de buscar o crear el motivo. No toca el estado ni la versión.</summary>
    public void EnsureCanResolve()
    {
        if (Status == ConversationStatus.Resolved)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AlreadyResolved, "The conversation is already resolved.");
        }
    }

    public void Resolve(DateTimeOffset now, Guid? reasonId = null)
    {
        EnsureCanResolve();

        Status = ConversationStatus.Resolved;
        ResolutionReasonId = reasonId;
        // Spec 2026-10-16 §3.2: resolver es la última palabra del asesor, así que la marca de no leída no sobrevive. Va aquí y no en el
        // handler para que viaje en el mismo SaveChanges (y la misma versión) que la resolución; reabrir no la restaura.
        UnreadCount = 0;
        ClearAutomation();
        Touch(now);
    }

    public void Reopen(DateTimeOffset now)
    {
        if (Status == ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AlreadyOpen, "The conversation is already open.");
        }

        Status = ConversationStatus.Open;
        ResolutionReasonId = null;
        Touch(now);
    }

    /// <summary>Spec 2026-10-10 §6.1.3: quien llama la toma, la tenga quien la tenga. Si ya era suya, nada (D-A2). Spec 2026-10-21 §4.3:
    /// si un asistente la atendía, tomarla lo pausa en el mismo cambio y el segundo evento lo cuenta.</summary>
    public IReadOnlyList<ConversationEvent> Take(Guid memberId, DateTimeOffset now)
    {
        if (AssignedMemberId == memberId)
        {
            return [];
        }

        var previous = AssignedMemberId;
        Assign(memberId, now);
        return WithAutomationPause(new ConversationEvent(ConversationEventType.Taken, Actor: memberId, Previous: previous), memberId, Modules.Messaging.Domain.AutomationPauseReason.AdvisorTook, now);
    }

    /// <summary>§8.4: transferirse a uno mismo es tomar; transferir a quien ya la tiene, nada (P8).</summary>
    public IReadOnlyList<ConversationEvent> TransferTo(Guid actorMemberId, Guid targetMemberId, DateTimeOffset now)
    {
        if (targetMemberId == actorMemberId)
        {
            return Take(actorMemberId, now);
        }

        if (AssignedMemberId == targetMemberId)
        {
            return [];
        }

        var previous = AssignedMemberId;
        Assign(targetMemberId, now);
        return WithAutomationPause(
            new ConversationEvent(ConversationEventType.Transferred, Actor: actorMemberId, Target: targetMemberId, Previous: previous),
            actorMemberId, Modules.Messaging.Domain.AutomationPauseReason.AdvisorTransferred, now);
    }

    /// <summary>§6.1.3: sin asignar ya, nada (D-A2).</summary>
    public ConversationEvent? Release(Guid actorMemberId, DateTimeOffset now)
    {
        if (AssignedMemberId is not { } previous)
        {
            return null;
        }

        AssignedMemberId = null;
        AssignedAt = null;
        Touch(now);
        return new ConversationEvent(ConversationEventType.Released, Actor: actorMemberId, Previous: previous);
    }

    /// <summary>Spec 2026-10-24: cambia el cliente de la conversación. El mismo cliente no hace nada (D-A2): ni evento ni versión.
    /// El evento lleva el nuevo en <c>CustomerId</c> y el anterior en <c>PreviousCustomerId</c> (<c>null</c> en una conversación vieja sin cliente).</summary>
    public IReadOnlyList<ConversationEvent> ChangeCustomer(Guid customerId, Guid actorMemberId, DateTimeOffset now)
    {
        if (CustomerId == customerId)
        {
            return [];
        }

        var previous = CustomerId;
        CustomerId = customerId;
        Touch(now);
        return [new ConversationEvent(ConversationEventType.CustomerChanged, Actor: actorMemberId, CustomerId: customerId, PreviousCustomerId: previous)];
    }

    /// <summary>Spec 2026-10-21 §4.2: arranca el asistente. Sólo sobre una conversación abierta, sin asesor y sin asistente previo:
    /// el motor no reactiva lo que un humano pausó (eso es <see cref="ResumeAutomation"/>).</summary>
    public void StartAutomation(Guid chatbotId, DateTimeOffset now)
    {
        EnsureOpenForAutomation();
        if (AssignedMemberId is not null)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AutomationNotAvailable, "A conversation with an advisor is not attended by the assistant.");
        }

        switch (AutomationStatus)
        {
            case ConversationAutomationStatus.Active:
                throw new MessagingDomainException(MessagingErrorCodes.AutomationAlreadyActive, "The assistant is already attending the conversation.");
            case ConversationAutomationStatus.Paused:
                throw new MessagingDomainException(MessagingErrorCodes.AutomationNotAvailable, "The assistant was paused; resume it instead of starting it.");
        }

        AutomationStatus = ConversationAutomationStatus.Active;
        AutomationStartedAt = now;
        AutomationChatbotId = chatbotId;
        Touch(now);
    }

    /// <summary>Spec 2026-10-21 D-M14: el paso del flujo donde va la conversación. No hace <c>Touch</c>: lo escribe el motor en cada
    /// avance y no es un cambio que el asesor deba ver como actividad ni que mueva la versión por sí solo.</summary>
    public void SetAutomationStep(string? stepTitle)
    {
        // D-M11: el paso vive sólo mientras hay asistente; sin él quedaría un paso huérfano.
        if (AutomationStatus == ConversationAutomationStatus.None)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AutomationNotAvailable, "There is no assistant to hold a step.");
        }

        AutomationStep = FitAutomationStep(stepTitle);
    }

    private const int AutomationStepMaxLength = 120;

    /// <summary>La columna es de 120: el título de un nodo más largo no debe tumbar el guardado a mitad de un flujo. Pública porque el
    /// motor también escribe el paso con un UPDATE directo (<c>IConversationRepository.SetAutomationStepAsync</c>) y la regla es una sola.</summary>
    public static string? FitAutomationStep(string? stepTitle) => Truncate(stepTitle, AutomationStepMaxLength);

    /// <summary>Spec 2026-10-21 D-M11: el flujo terminó. Sólo desde <c>Active</c>: una pausada la decide una persona y se devuelve con
    /// <see cref="ResumeAutomation"/>. Sin evento: el cierre de un flujo no es un hecho de asignación que mostrar en el hilo.</summary>
    public void EndAutomation(DateTimeOffset now)
    {
        EnsureOpenForAutomation();
        if (AutomationStatus != ConversationAutomationStatus.Active)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AutomationNotAvailable, "Only an active assistant can end.");
        }

        ClearAutomation();
        Touch(now);
    }

    /// <summary>Pausa explícita del asistente. <paramref name="actorMemberId"/> es <c>null</c> cuando la hace el sistema.
    /// <paramref name="handoff"/> (spec 2026-10-24 D-T7) es la causa de un traspaso y sólo cabe con
    /// <c>AutomationPauseReason.Handoff</c>.</summary>
    public ConversationEvent PauseAutomation(
        Guid? actorMemberId, AutomationPauseReason reason, DateTimeOffset now, AutomationHandoff? handoff = null)
    {
        // Antes de cualquier comprobación de estado: es un error de programación, no una regla de negocio, y no debe tocar nada.
        if (handoff is not null && reason != Domain.AutomationPauseReason.Handoff)
        {
            throw new ArgumentException("Only a handoff pause carries a handoff cause.", nameof(handoff));
        }

        EnsureOpenForAutomation();
        switch (AutomationStatus)
        {
            case ConversationAutomationStatus.Paused:
                throw new MessagingDomainException(MessagingErrorCodes.AutomationAlreadyPaused, "The assistant is already paused.");
            case ConversationAutomationStatus.None:
                throw new MessagingDomainException(MessagingErrorCodes.AutomationNotAvailable, "The conversation has no assistant to pause.");
        }

        MarkPaused(actorMemberId, reason, now);
        Touch(now);
        return new ConversationEvent(ConversationEventType.AutomationPaused, Actor: actorMemberId, PauseReason: reason, Handoff: handoff);
    }

    /// <summary>Spec 2026-10-25 D-1, D-7, D-8: el traspaso del asistente. Mismas comprobaciones y orden que <see cref="PauseAutomation"/>.
    /// Pausa sin actor y, si la causa trae un asesor, le asigna la conversación con los campos directos: <c>TransferTo</c> exige un actor y
    /// escribe <c>Transferred</c>, y el hilo no debe decir «Alguien transfirió…» (D-8). Con el asistente activo la conversación no tiene
    /// asesor (tomarla pausa el bot y <see cref="ResumeAutomation"/> la libera), así que no hay asignado previo que comparar.</summary>
    public ConversationEvent HandOffAutomation(AutomationHandoff handoff, DateTimeOffset now)
    {
        EnsureOpenForAutomation();
        switch (AutomationStatus)
        {
            case ConversationAutomationStatus.Paused:
                throw new MessagingDomainException(MessagingErrorCodes.AutomationAlreadyPaused, "The assistant is already paused.");
            case ConversationAutomationStatus.None:
                throw new MessagingDomainException(MessagingErrorCodes.AutomationNotAvailable, "The conversation has no assistant to pause.");
        }

        MarkPaused(null, Domain.AutomationPauseReason.Handoff, now);
        if (handoff.AssignedMemberId is { } member)
        {
            AssignedMemberId = member;
            AssignedAt = now;
        }

        Touch(now);
        return new ConversationEvent(ConversationEventType.AutomationPaused, Actor: null, PauseReason: Domain.AutomationPauseReason.Handoff, Handoff: handoff);
    }

    /// <summary>Reactiva un asistente pausado. Si hay un asesor asignado se le quita la conversación primero (evento
    /// <c>Released</c>); el orden de las comprobaciones es: abierta, hay asistente, no está activo.</summary>
    public IReadOnlyList<ConversationEvent> ResumeAutomation(Guid actorMemberId, DateTimeOffset now)
    {
        EnsureOpenForAutomation();
        switch (AutomationStatus)
        {
            case ConversationAutomationStatus.None:
                throw new MessagingDomainException(MessagingErrorCodes.AutomationNotAvailable, "The conversation has no assistant to resume.");
            case ConversationAutomationStatus.Active:
                throw new MessagingDomainException(MessagingErrorCodes.AutomationAlreadyActive, "The assistant is already attending the conversation.");
        }

        var events = new List<ConversationEvent>(2);
        if (AssignedMemberId is { } previous)
        {
            AssignedMemberId = null;
            AssignedAt = null;
            events.Add(new ConversationEvent(ConversationEventType.Released, Actor: actorMemberId, Previous: previous));
        }

        AutomationStatus = ConversationAutomationStatus.Active;
        AutomationPausedAt = null;
        AutomationPausedByMemberId = null;
        AutomationPauseReason = null;
        AutomationResumedAt = now;
        Touch(now);
        events.Add(new ConversationEvent(ConversationEventType.AutomationResumed, Actor: actorMemberId));
        return events;
    }

    private IReadOnlyList<ConversationEvent> WithAutomationPause(ConversationEvent change, Guid actorMemberId, AutomationPauseReason reason, DateTimeOffset now)
    {
        if (AutomationStatus != ConversationAutomationStatus.Active)
        {
            return [change];
        }

        // La versión ya subió en Assign: pausar va en el mismo cambio.
        MarkPaused(actorMemberId, reason, now);
        return [change, new ConversationEvent(ConversationEventType.AutomationPaused, Actor: actorMemberId, PauseReason: reason)];
    }

    private void MarkPaused(Guid? actorMemberId, AutomationPauseReason reason, DateTimeOffset now)
    {
        AutomationStatus = ConversationAutomationStatus.Paused;
        AutomationPausedAt = now;
        AutomationPausedByMemberId = actorMemberId;
        AutomationPauseReason = reason;
    }

    /// <summary>Spec 2026-10-26 §3.3: la regla de «se puede bloquear» se pregunta antes de llamar a Meta. No toca estado ni versión.
    /// Bloquear exige la ventana de 24 horas abierta: la API de bloqueo sólo acepta números que escribieron en ese lapso.</summary>
    public void EnsureCanBlock(DateTimeOffset now)
    {
        if (Channel == ConversationChannel.Instagram || WaId is null)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.BlockUnsupportedChannel, "Only a WhatsApp conversation with a phone number can block its contact.");
        }

        if (!IsWindowOpen(now))
        {
            throw new MessagingDomainException(MessagingErrorCodes.BlockWindowClosed, "The customer window is closed.");
        }
    }

    /// <summary>Se llama cuando Meta ya aceptó el bloqueo, así que no revisa la ventana (<see cref="EnsureCanBlock"/> va antes).
    /// <c>null</c> si ya estaba bloqueada.</summary>
    public ConversationEvent? Block(Guid memberId, DateTimeOffset now)
    {
        if (IsBlocked)
        {
            return null;
        }

        BlockedWaId = WaId ?? throw new InvalidOperationException("A conversation without a wa_id cannot be blocked; call EnsureCanBlock first.");
        BlockedAt = now;
        BlockedByMemberId = memberId;
        Touch(now);
        return new ConversationEvent(ConversationEventType.ContactBlocked, Actor: memberId);
    }

    /// <summary><c>null</c> si no estaba bloqueada. El actor viaja al evento del hilo, como en <see cref="PauseAutomation"/>.</summary>
    public ConversationEvent? Unblock(Guid memberId, DateTimeOffset now)
    {
        if (!IsBlocked)
        {
            return null;
        }

        BlockedAt = null;
        BlockedByMemberId = null;
        BlockedWaId = null;
        Touch(now);
        return new ConversationEvent(ConversationEventType.ContactUnblocked, Actor: memberId);
    }

    private void ClearAutomation()
    {
        AutomationStatus = ConversationAutomationStatus.None;
        AutomationStartedAt = null;
        AutomationPausedAt = null;
        AutomationPausedByMemberId = null;
        AutomationPauseReason = null;
        AutomationChatbotId = null;
        AutomationStep = null;
        AutomationResumedAt = null;
    }

    private void EnsureOpenForAutomation()
    {
        if (Status != ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ConversationNotOpen, "The conversation is not open.");
        }
    }

    private void Assign(Guid memberId, DateTimeOffset now)
    {
        AssignedMemberId = memberId;
        AssignedAt = now;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
