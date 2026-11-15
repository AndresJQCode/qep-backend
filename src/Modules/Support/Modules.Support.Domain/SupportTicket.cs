using System.Text.Json;

namespace Modules.Support.Domain;

/// <summary>
/// Un ticket de soporte (spec 2026-11-14). Copia del tipo el nombre, el término y si es legal, así que editar el tipo después no
/// mueve el ticket. El plazo cuenta en días hábiles desde el día siguiente a la fecha local de radicación (§7.2): se detiene
/// mientras el estado lo detiene (<see cref="SupportTicketStatuses.StopsTheClock"/>) y al reanudar el vencimiento se corre por los
/// días hábiles estrictamente entre la fecha de la pausa y la de la reanudación (plan D3). Toda mutación real agrega un evento con
/// el ordinal siguiente y sube <see cref="Version"/>; un no-op no hace ninguna de las dos.
/// </summary>
public sealed class SupportTicket
{
    public const int SubjectMaxLength = 200;

    public const int DescriptionMaxLength = 5000;

    private readonly List<SupportTicketEvent> events = [];

    // EF Core materializa por acá; CreateManual y CreateFromConversation son los únicos puntos de entrada de código.
    private SupportTicket()
    {
        TypeName = string.Empty;
        Subject = string.Empty;
        Description = string.Empty;
    }

    public SupportTicketId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public long Number { get; private set; }

    public string DisplayNumber => SupportTicketNumber.Format(Number);

    public SupportTicketTypeId TypeId { get; private set; }

    public string TypeName { get; private set; }

    public int? TermBusinessDays { get; private set; }

    public bool TermIsLegal { get; private set; }

    public bool RequiresFormalResponse { get; private set; }

    public string Subject { get; private set; }

    public string Description { get; private set; }

    public SupportTicketIntake Intake { get; private set; }

    public ManualIntakeMedium? ManualMedium { get; private set; }

    public SupportReplyChannel ReplyChannel { get; private set; }

    public SupportTicketStatus Status { get; private set; }

    public Guid? AssigneeMemberId { get; private set; }

    /// <summary>FK blanda a Customers: Guid y no un id tipado, como en el resto de módulos.</summary>
    public Guid? CustomerId { get; private set; }

    public TicketRequester? Requester { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>La fecha local de radicación en la zona del tenant: desde el día hábil siguiente cuenta el plazo.</summary>
    public DateOnly ReceivedOn { get; private set; }

    /// <summary>Se recalcula sólo al crear, al reanudar y al cambiar el tipo (plan D4). Detenido, conserva el último valor.</summary>
    public DateOnly? DueOn { get; private set; }

    /// <summary>Desde cuándo está detenido el reloj; una cadena de detenciones conserva el inicio de la primera.</summary>
    public DateTimeOffset? PausedSince { get; private set; }

    public int PausedBusinessDays { get; private set; }

    /// <summary>Cuántas veces entró a <see cref="SupportTicketStatus.Resolved"/>: reabrir y resolver otra vez es otro ciclo.</summary>
    public int ResolutionCount { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>La conversación de Mensajería del ticket (spec §8.1): con la que nació o la que vinculó una plantilla (plan E14).
    /// Una vez puesta no cambia.</summary>
    public Guid? ConversationId { get; private set; }

    /// <summary>El canal de la conversación por nombre: <c>WhatsApp</c> o <c>Instagram</c>.</summary>
    public string? ConversationChannel { get; private set; }

    /// <summary>«Seguir el hilo» (spec §8.2): si los entrantes de la conversación se vinculan solos.</summary>
    public bool FollowThread { get; private set; }

    /// <summary>Desde cuándo sigue el hilo: al crear, al vincular la conversación o al prender de nuevo (plan E4). Lo que llegó
    /// antes no se vincula solo.</summary>
    public DateTimeOffset? FollowSince { get; private set; }

    /// <summary>Cuándo dejó de seguirlo: al apagar o al cerrar. Null mientras lo sigue.</summary>
    public DateTimeOffset? FollowOffAt { get; private set; }

    public int LastEventOrdinal { get; private set; }

    /// <summary>Los eventos de esta instancia: al crear, el <see cref="SupportTicketEventKind.Created"/>; cargado de la base, sólo
    /// los que se agreguen después (el repositorio no los carga).</summary>
    public IReadOnlyList<SupportTicketEvent> Events => events;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Plan D6: un ticket manual necesita cliente o solicitante con nombre.</summary>
    public static SupportTicket CreateManual(
        SupportTicketId id,
        Guid tenantId,
        long number,
        SupportTicketType type,
        string subject,
        string description,
        ManualIntakeMedium medium,
        SupportReplyChannel replyChannel,
        Guid? customerId,
        TicketRequester? requester,
        Guid? assigneeMemberId,
        Guid actorMemberId,
        DateTimeOffset now,
        SupportClockContext clock) =>
        Start(
            id, tenantId, number, type, subject, description, SupportTicketIntake.Manual, medium, replyChannel, conversation: null,
            customerId, requester, assigneeMemberId, actorMemberId, now, clock);

    /// <summary>Spec §8.1: el ticket que abre un asesor desde una conversación. Entra por
    /// <see cref="SupportTicketIntake.Conversation"/>, se responde por el hilo y lo sigue desde ya. La regla del solicitante es la
    /// del manual (plan D6).</summary>
    public static SupportTicket CreateFromConversation(
        SupportTicketId id,
        Guid tenantId,
        long number,
        SupportTicketType type,
        string subject,
        string description,
        SupportConversationRef conversation,
        Guid? customerId,
        TicketRequester? requester,
        Guid? assigneeMemberId,
        Guid actorMemberId,
        DateTimeOffset now,
        SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return Start(
            id, tenantId, number, type, subject, description, SupportTicketIntake.Conversation, medium: null,
            SupportReplyChannel.Thread, conversation, customerId, requester, assigneeMemberId, actorMemberId, now, clock);
    }

    // El alta común de las dos entradas. Es un método y no un constructor con parámetros para que EF siga materializando por el
    // constructor vacío.
    private static SupportTicket Start(
        SupportTicketId id,
        Guid tenantId,
        long number,
        SupportTicketType type,
        string subject,
        string description,
        SupportTicketIntake intake,
        ManualIntakeMedium? medium,
        SupportReplyChannel replyChannel,
        SupportConversationRef? conversation,
        Guid? customerId,
        TicketRequester? requester,
        Guid? assigneeMemberId,
        Guid actorMemberId,
        DateTimeOffset now,
        SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        EnsureSameTenant(tenantId, type);
        conversation?.EnsureValid();
        if (customerId is null && requester is not { HasName: true })
        {
            throw RequesterRequired();
        }

        var ticket = new SupportTicket
        {
            Id = id,
            TenantId = tenantId,
            Number = number,
            Subject = Text(subject, SubjectMaxLength, nameof(subject)),
            Description = Text(description, DescriptionMaxLength, nameof(description)),
            Intake = intake,
            ManualMedium = medium,
            ReplyChannel = replyChannel,
            Status = SupportTicketStatus.New,
            AssigneeMemberId = assigneeMemberId,
            CustomerId = customerId,
            Requester = requester,
            ConversationId = conversation?.Id,
            ConversationChannel = conversation?.Channel,
            FollowThread = conversation is not null,
            FollowSince = conversation is not null ? now : null,
            ReceivedAt = now,
            ReceivedOn = clock.LocalDate(now),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        ticket.CopyType(type, clock);
        ticket.AddEvent(SupportTicketEventKind.Created, actorMemberId, now, new { to = SupportTicketStatus.New.ToString() });
        return ticket;
    }

    /// <summary><paramref name="actorMemberId"/> null es el sistema: el seguimiento del hilo que reabre un ticket que esperaba al
    /// cliente (plan E6). Al cerrar deja de seguir el hilo: un ticket cerrado no captura mensajes ni cuenta como candidato.</summary>
    public void ChangeStatus(SupportTicketStatus target, Guid? actorMemberId, DateTimeOffset now, SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!SupportTicketStatuses.AllowedFrom(Status).Contains(target))
        {
            throw new SupportDomainException(
                SupportErrorCodes.TransitionInvalid, $"A ticket cannot go from '{Status}' to '{target}'.");
        }

        var from = Status;
        var wasStopped = SupportTicketStatuses.StopsTheClock(from);
        var stops = SupportTicketStatuses.StopsTheClock(target);
        if (!wasStopped && stops)
        {
            PausedSince = now;
        }
        else if (wasStopped && !stops && PausedSince is { } pausedSince)
        {
            PausedBusinessDays += SupportBusinessDays.CountStrictlyBetween(
                clock.Calendar, clock.LocalDate(pausedSince), clock.LocalDate(now));
            PausedSince = null;
            DueOn = DueOnFor(TermBusinessDays, clock);
        }

        Status = target;
        if (target is SupportTicketStatus.Resolved)
        {
            ResolutionCount++;
            ResolvedAt = now;
        }
        else if (target is SupportTicketStatus.Closed)
        {
            ClosedAt = now;
            if (FollowThread)
            {
                FollowThread = false;
                FollowOffAt = now;
            }
        }

        AddEvent(SupportTicketEventKind.StatusChanged, actorMemberId, now, new { from = from.ToString(), to = target.ToString() });
    }

    /// <summary>Pre-flight R9: se permite en cualquier estado, también resuelto o cerrado; no cambia el estado.</summary>
    public void Assign(Guid? assigneeMemberId, Guid actorMemberId, DateTimeOffset now)
    {
        if (assigneeMemberId == AssigneeMemberId)
        {
            return;
        }

        var from = AssigneeMemberId;
        AssigneeMemberId = assigneeMemberId;
        AddEvent(SupportTicketEventKind.AssigneeChanged, actorMemberId, now, new { from, to = assigneeMemberId });
    }

    /// <summary>Recalcula el vencimiento desde la radicación con el término nuevo más las pausas ya contadas (spec §7.2).</summary>
    public void ChangeType(SupportTicketType type, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(clock);
        EnsureSameTenant(TenantId, type);
        if (type.Id == TypeId)
        {
            return;
        }

        var from = new { id = TypeId.Value, name = TypeName };
        CopyType(type, clock);
        AddEvent(SupportTicketEventKind.TypeChanged, actorMemberId, now, new { from, to = new { id = TypeId.Value, name = TypeName } });
    }

    /// <summary>Desvincular deja la foto del solicitante; si no hay foto con nombre, el ticket quedaría sin nadie y se rechaza
    /// (pre-flight R11).</summary>
    public void LinkCustomer(Guid? customerId, Guid actorMemberId, DateTimeOffset now)
    {
        if (customerId == CustomerId)
        {
            return;
        }

        if (customerId is null && Requester is not { HasName: true })
        {
            throw RequesterRequired();
        }

        var from = CustomerId;
        CustomerId = customerId;
        AddEvent(SupportTicketEventKind.CustomerChanged, actorMemberId, now, new { from, to = customerId });
    }

    /// <summary>«Seguir el hilo» (spec §8.2). Apagar sella <see cref="FollowOffAt"/>; prender abre una ventana nueva desde ahora,
    /// así lo que llegó con el interruptor apagado no se vincula (plan E4). El mismo valor no hace nada. Sin conversación no hay
    /// hilo que seguir.</summary>
    public void SetFollowThread(bool follow, Guid actorMemberId, DateTimeOffset now)
    {
        if (follow == FollowThread)
        {
            return;
        }

        if (follow && ConversationId is null)
        {
            throw new SupportDomainException(SupportErrorCodes.NoConversation, "The ticket has no conversation to follow.");
        }

        FollowThread = follow;
        if (follow)
        {
            FollowSince = now;
            FollowOffAt = null;
        }
        else
        {
            FollowOffAt = now;
        }

        AddEvent(SupportTicketEventKind.FollowThreadChanged, actorMemberId, now, new { follow, at = now });
    }

    /// <summary>La conversación que crea o encuentra una respuesta por plantilla a un ticket sin conversación (plan E14). Empieza
    /// a seguir el hilo desde ya. Un ticket que ya tiene conversación no la cambia: quien llama lo revisa antes.</summary>
    public void LinkConversation(SupportConversationRef conversation, Guid actorMemberId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.EnsureValid();
        if (ConversationId is not null)
        {
            throw new InvalidOperationException($"The ticket '{Id}' already has a conversation.");
        }

        ConversationId = conversation.Id;
        ConversationChannel = conversation.Channel;
        FollowThread = true;
        FollowSince = now;
        FollowOffAt = null;
        AddEvent(
            SupportTicketEventKind.ConversationLinked, actorMemberId, now,
            new { conversationId = conversation.Id, channel = conversation.Channel });
    }

    /// <summary>Si un entrante de su conversación con esa hora le corresponde (plan E4 y E5): no cerrado y dentro de la ventana
    /// <c>[FollowSince, FollowOffAt)</c>. Se mide con la hora del mensaje, no con la de llegada: un reintento de Meta horas
    /// después con una hora anterior al ticket no entra.</summary>
    public bool CoversInbound(DateTimeOffset occurredAt) =>
        Status is not SupportTicketStatus.Closed
        && ConversationId is not null
        && FollowSince is { } since
        && since <= occurredAt
        && (FollowOffAt is not { } off || occurredAt < off);

    /// <summary>Fijar, soltar, clasificar desde «sin clasificar» o «De ninguno» (plan E7 y E8). Null es el sistema.</summary>
    public void RecordMessageEvent(SupportTicketEventKind kind, Guid messageId, Guid? actorMemberId, DateTimeOffset now)
    {
        if (kind is not (SupportTicketEventKind.MessagePinned or SupportTicketEventKind.MessageReleased
            or SupportTicketEventKind.MessageClassified or SupportTicketEventKind.MessageDismissed))
        {
            throw new ArgumentException($"'{kind}' is not a message event.", nameof(kind));
        }

        AddEvent(kind, actorMemberId, now, new { messageId });
    }

    /// <summary>Anota una respuesta en la traza con su canal y, si salió por Mensajería, su mensaje (plan E16).</summary>
    public void RecordReply(SupportTicketReply reply, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.TicketId != Id || reply.TenantId != TenantId)
        {
            throw new ArgumentException("The reply belongs to another ticket.", nameof(reply));
        }

        AddEvent(
            SupportTicketEventKind.ReplySent,
            reply.AuthorMemberId,
            now,
            new
            {
                replyId = reply.Id,
                channel = reply.Channel.ToString(),
                delivery = reply.Delivery.ToString(),
                messageId = reply.MessageId,
            });
    }

    private void CopyType(SupportTicketType type, SupportClockContext clock)
    {
        TypeId = type.Id;
        TypeName = type.Name;
        TermBusinessDays = type.TermBusinessDays;
        TermIsLegal = type.TermIsLegal;
        RequiresFormalResponse = type.RequiresFormalResponse;
        DueOn = DueOnFor(type.TermBusinessDays, clock);
    }

    private DateOnly? DueOnFor(int? term, SupportClockContext clock) =>
        term is { } days ? SupportBusinessDays.Add(clock.Calendar, ReceivedOn, days + PausedBusinessDays) : null;

    private void AddEvent(SupportTicketEventKind kind, Guid? actorMemberId, DateTimeOffset now, object details)
    {
        LastEventOrdinal++;
        events.Add(SupportTicketEvent.Create(
            TenantId, Id, LastEventOrdinal, kind, actorMemberId, now, JsonSerializer.Serialize(details)));
        if (kind is not SupportTicketEventKind.Created)
        {
            UpdatedAt = now;
            Version++;
        }
    }

    private static void EnsureSameTenant(Guid tenantId, SupportTicketType type)
    {
        if (type.TenantId != tenantId)
        {
            throw new ArgumentException("The ticket type belongs to another tenant.", nameof(type));
        }
    }

    private static string Text(string value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            throw new ArgumentException($"The text must have between 1 and {maxLength} characters.", name);
        }

        return trimmed;
    }

    private static SupportDomainException RequesterRequired() =>
        new(SupportErrorCodes.RequesterRequired, "A ticket needs a linked customer or a requester with a name.");
}
