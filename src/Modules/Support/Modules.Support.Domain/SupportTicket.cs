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

    /// <summary>Fase 4a (plan G11): el motivo de la prórroga, recortado.</summary>
    public const int ExtensionReasonMaxLength = 1000;

    private readonly List<SupportTicketEvent> events = [];

    // EF Core materializa por acá; CreateManual, CreateFromConversation y CreateFromPublicForm son los únicos puntos de entrada de
    // código.
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

    /// <summary>La elige quien radica a mano o desde una conversación; el formulario público entra siempre en
    /// <see cref="SupportTicketPriority.Medium"/>. No toca el reloj.</summary>
    public SupportTicketPriority Priority { get; private set; }

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

    /// <summary>Fase 4a (plan G9): los días hábiles de la prórroga única; 0 sin prórroga. Se guardan como días y no como fecha para
    /// que entren en <see cref="DueOnFor"/> igual que las pausas: una pausa posterior o un cambio de tipo la siguen contando.</summary>
    public int ExtensionBusinessDays { get; private set; }

    public string? ExtensionReason { get; private set; }

    public DateTimeOffset? ExtendedAt { get; private set; }

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

    /// <summary>Fase 3 (spec §9.4): radicado desde el formulario público sin datos personales. No tiene cliente, ni foto del
    /// solicitante, ni consentimiento, y se responde sólo por <see cref="SupportReplyChannel.Portal"/>.</summary>
    public bool IsAnonymous { get; private set; }

    /// <summary>Fase 3 (plan F14): la versión del formulario con que se radicó. Sólo la tienen los tickets del formulario público.</summary>
    public Guid? FormVersionId { get; private set; }

    public int LastEventOrdinal { get; private set; }

    /// <summary>Los eventos de esta instancia: al crear, el <see cref="SupportTicketEventKind.Created"/>; cargado de la base, sólo
    /// los que se agreguen después (el repositorio no los carga).</summary>
    public IReadOnlyList<SupportTicketEvent> Events => events;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Plan D6: un ticket manual necesita cliente o solicitante con nombre. Fase 4a (plan G2): con
    /// <paramref name="anonymous"/> es el anónimo del alta manual, como el del formulario público: sin cliente ni foto del solicitante,
    /// y se responde sólo por <see cref="SupportReplyChannel.Portal"/>. El validador lo ataja antes; el dominio no confía.</summary>
    public static SupportTicket CreateManual(
        SupportTicketId id,
        Guid tenantId,
        long number,
        SupportTicketType type,
        string subject,
        string description,
        SupportTicketPriority priority,
        ManualIntakeMedium medium,
        SupportReplyChannel replyChannel,
        Guid? customerId,
        TicketRequester? requester,
        Guid? assigneeMemberId,
        Guid actorMemberId,
        DateTimeOffset now,
        SupportClockContext clock,
        bool anonymous = false)
    {
        if (!anonymous)
        {
            return Start(
                id, tenantId, number, type, subject, description, priority, SupportTicketIntake.Manual, medium, replyChannel,
                conversation: null, customerId, requester, assigneeMemberId, actorMemberId, now, clock);
        }

        if (customerId is not null)
        {
            throw new ArgumentException("An anonymous ticket cannot be linked to a customer.", nameof(customerId));
        }

        if (requester is not null)
        {
            throw new ArgumentException("An anonymous ticket has no requester.", nameof(requester));
        }

        // El canal se fija: un anónimo no tiene otro (spec §9.4).
        var ticket = Start(
            id, tenantId, number, type, subject, description, priority, SupportTicketIntake.Manual, medium, SupportReplyChannel.Portal,
            conversation: null, customerId: null, requester: null, assigneeMemberId, actorMemberId, now, clock, anonymous: true);
        ticket.IsAnonymous = true;
        return ticket;
    }

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
        SupportTicketPriority priority,
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
            id, tenantId, number, type, subject, description, priority, SupportTicketIntake.Conversation, medium: null,
            SupportReplyChannel.Thread, conversation, customerId, requester, assigneeMemberId, actorMemberId, now, clock);
    }

    /// <summary>
    /// Fase 3 (spec §9.3 y §9.4, plan F22): el ticket que radica un solicitante en el formulario público. No tiene actor (el evento
    /// <see cref="SupportTicketEventKind.Created"/> va con actor null, el sistema) ni responsable. Con <paramref name="requester"/>
    /// null es anónimo: sin cliente ni foto, y se responde por <see cref="SupportReplyChannel.Portal"/>; si no, por correo. La regla
    /// del nombre de las otras entradas (plan D6) no aplica: los campos los exige el validador según <see cref="SupportFormFields"/>,
    /// y un NIT sin razón social se muestra como «NIT &lt;número&gt;».
    /// </summary>
    public static SupportTicket CreateFromPublicForm(
        SupportTicketId id,
        Guid tenantId,
        long number,
        SupportTicketType type,
        string subject,
        string description,
        TicketRequester? requester,
        Guid? customerId,
        Guid formVersionId,
        DateTimeOffset now,
        SupportClockContext clock)
    {
        if (formVersionId == Guid.Empty)
        {
            throw new ArgumentException("A ticket from the public form needs its form version.", nameof(formVersionId));
        }

        var anonymous = requester is null;
        if (anonymous && customerId is not null)
        {
            throw new ArgumentException("An anonymous ticket cannot be linked to a customer.", nameof(customerId));
        }

        if (!anonymous && string.IsNullOrWhiteSpace(requester!.Email))
        {
            throw new ArgumentException("A named ticket from the public form needs an email to reply to.", nameof(requester));
        }

        var ticket = Start(
            id, tenantId, number, type, subject, description, SupportTicketPriority.Medium, SupportTicketIntake.PublicForm,
            medium: null, anonymous ? SupportReplyChannel.Portal : SupportReplyChannel.Email, conversation: null, customerId, requester,
            assigneeMemberId: null, actorMemberId: null, now, clock);
        ticket.IsAnonymous = anonymous;
        ticket.FormVersionId = formVersionId;
        return ticket;
    }

    // El alta común de las tres entradas. Es un método y no un constructor con parámetros para que EF siga materializando por el
    // constructor vacío. Sin actor sólo entra el formulario público. Con anonymous no se exige solicitante con nombre.
    private static SupportTicket Start(
        SupportTicketId id,
        Guid tenantId,
        long number,
        SupportTicketType type,
        string subject,
        string description,
        SupportTicketPriority priority,
        SupportTicketIntake intake,
        ManualIntakeMedium? medium,
        SupportReplyChannel replyChannel,
        SupportConversationRef? conversation,
        Guid? customerId,
        TicketRequester? requester,
        Guid? assigneeMemberId,
        Guid? actorMemberId,
        DateTimeOffset now,
        SupportClockContext clock,
        bool anonymous = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        EnsureSameTenant(tenantId, type);
        conversation?.EnsureValid();
        // Un anónimo (formulario público o alta manual, plan G2) no tiene a quién nombrar.
        if (intake is not SupportTicketIntake.PublicForm && !anonymous && customerId is null && requester is not { HasName: true })
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
            Priority = priority,
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

            // Si el asesor ya lo había apagado, se conserva esa hora: lo que llegó con el interruptor apagado nunca entró a la ventana.
            if (FollowThread)
            {
                FollowThread = false;
                FollowOffAt = now;
            }
        }

        AddEvent(SupportTicketEventKind.StatusChanged, actorMemberId, now, new { from = from.ToString(), to = target.ToString() });
    }

    /// <summary>No cambia el estado. Resuelto o cerrado se rechaza (R9).</summary>
    public void Assign(Guid? assigneeMemberId, Guid actorMemberId, DateTimeOffset now)
    {
        EnsureEditable();
        if (assigneeMemberId == AssigneeMemberId)
        {
            return;
        }

        var from = AssigneeMemberId;
        AssigneeMemberId = assigneeMemberId;
        AddEvent(SupportTicketEventKind.AssigneeChanged, actorMemberId, now, new { from, to = assigneeMemberId });
    }

    /// <summary>Recalcula el vencimiento desde la radicación con el término nuevo más las pausas ya contadas (spec §7.2). Resuelto o
    /// cerrado se rechaza (R9).</summary>
    public void ChangeType(SupportTicketType type, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(clock);
        EnsureSameTenant(TenantId, type);
        EnsureEditable();
        if (type.Id == TypeId)
        {
            return;
        }

        var from = new { id = TypeId.Value, name = TypeName };
        CopyType(type, clock);
        AddEvent(SupportTicketEventKind.TypeChanged, actorMemberId, now, new { from, to = new { id = TypeId.Value, name = TypeName } });
    }

    /// <summary>Spec 2026-11-15 §5.1: no toca el vencimiento, el término, las pausas ni el reloj. La misma prioridad no hace nada.
    /// Resuelto o cerrado se rechaza (R9), aunque el valor sea el mismo.</summary>
    public void ChangePriority(SupportTicketPriority priority, Guid actorMemberId, DateTimeOffset now)
    {
        EnsureEditable();
        if (priority == Priority)
        {
            return;
        }

        var from = Priority;
        Priority = priority;
        AddEvent(SupportTicketEventKind.PriorityChanged, actorMemberId, now, new { from = from.ToString(), to = priority.ToString() });
    }

    /// <summary>Desvincular deja la foto del solicitante; si no hay foto con nombre, el ticket quedaría sin nadie y se rechaza
    /// (pre-flight R11). Resuelto o cerrado se rechaza (R9); la fusión de clientes mueve tickets en cualquier estado, pero por SQL y
    /// sin pasar por acá (<c>CustomerMergeReferences</c>). Un anónimo no recibe cliente (spec §9.4): quien radicó sin datos no aparece
    /// en la ficha de nadie.</summary>
    public void LinkCustomer(Guid? customerId, Guid actorMemberId, DateTimeOffset now)
    {
        EnsureEditable();
        if (IsAnonymous && customerId is not null)
        {
            throw new SupportDomainException(
                SupportErrorCodes.AnonymousHasNoCustomer, "An anonymous ticket cannot be linked to a customer.");
        }

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
    /// hilo que seguir. Cerrado, 422 <c>support.ticket.not_editable</c> aunque el valor sea el mismo (R9 extendida); resuelto, sí.</summary>
    public void SetFollowThread(bool follow, Guid actorMemberId, DateTimeOffset now)
    {
        EnsureNotClosed();
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
    /// a seguir el hilo desde ya, salvo que esté cerrado (revisión final, M3): un cerrado no sigue el hilo, así que la ventana nace
    /// apagada, con <see cref="FollowOffAt"/> en este instante. Un ticket que ya tiene conversación no la cambia: quien llama lo revisa
    /// antes.</summary>
    public void LinkConversation(SupportConversationRef conversation, Guid actorMemberId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.EnsureValid();
        if (ConversationId is not null)
        {
            throw new InvalidOperationException($"The ticket '{Id}' already has a conversation.");
        }

        var closed = Status is SupportTicketStatus.Closed;
        ConversationId = conversation.Id;
        ConversationChannel = conversation.Channel;
        FollowThread = !closed;
        FollowSince = now;
        FollowOffAt = closed ? now : null;
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

    /// <summary>Fijar, soltar, clasificar desde «sin clasificar» o «De ninguno» (plan E7 y E8). Null es el sistema. Fijar y soltar en un
    /// ticket cerrado es 422 <c>support.ticket.not_editable</c> (R9 extendida); resuelto, sí. Es la única entrada de dominio de esas dos
    /// acciones: el handler cambia el vínculo y llama acá antes de guardar, así un rechazo no deja nada escrito.</summary>
    public void RecordMessageEvent(SupportTicketEventKind kind, Guid messageId, Guid? actorMemberId, DateTimeOffset now)
    {
        if (kind is not (SupportTicketEventKind.MessagePinned or SupportTicketEventKind.MessageReleased
            or SupportTicketEventKind.MessageClassified or SupportTicketEventKind.MessageDismissed))
        {
            throw new ArgumentException($"'{kind}' is not a message event.", nameof(kind));
        }

        if (kind is SupportTicketEventKind.MessagePinned or SupportTicketEventKind.MessageReleased)
        {
            EnsureNotClosed();
        }

        AddEvent(kind, actorMemberId, now, new { messageId });
    }

    /// <summary>Block E (E-2): la guarda de fijar y soltar, para que el handler la pida <b>antes</b> de su no-op (fijar lo ya fijado,
    /// soltar lo ya soltado). Cerrado responde 422 <c>support.ticket.not_editable</c> aunque nada fuera a cambiar, igual que
    /// <see cref="SetFollowThread"/> con el mismo valor; resuelto, sí.</summary>
    public void EnsureMessagesCanChange() => EnsureNotClosed();

    /// <summary>Anota una respuesta en la traza con su canal, cómo terminó (<c>status</c>, y <c>failureCode</c> si falló) y, si salió
    /// por Mensajería, su mensaje (plan E16). Se llama después de marcarla.</summary>
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
                status = reply.Status.ToString(),
                failureCode = reply.FailureCode,
            });
    }

    /// <summary>
    /// Fase 4a (spec §7.2, plan G9 a G11): la prórroga única del plazo, con su motivo. Las guardas van en el orden de
    /// <see cref="ExtensionRejection"/> y, al final, el tope: la prórroga no pasa del término inicial (el plazo total no pasa del
    /// doble). «Hoy» es la fecha local del tenant; el día del vencimiento todavía se puede, porque el plazo vence al final de ese día.
    /// El evento <see cref="SupportTicketEventKind.Extended"/> lleva los días, el motivo y el vencimiento de antes y de después.
    /// </summary>
    public void Extend(int businessDays, string reason, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(businessDays);
        var trimmed = Text(reason, ExtensionReasonMaxLength, nameof(reason));

        if (ExtensionRejection(clock.LocalDate(now)) is { } rejection)
        {
            throw rejection;
        }

        if (businessDays > TermBusinessDays)
        {
            throw new SupportDomainException(
                SupportErrorCodes.ExtensionTooLong,
                $"An extension of {businessDays} business days is longer than the term of {TermBusinessDays}.");
        }

        var fromDueOn = DueOn;
        ExtensionBusinessDays = businessDays;
        ExtensionReason = trimmed;
        ExtendedAt = now;
        DueOn = DueOnFor(TermBusinessDays, clock);
        AddEvent(
            SupportTicketEventKind.Extended, actorMemberId, now,
            new { businessDays, reason = trimmed, fromDueOn, toDueOn = DueOn });
    }

    /// <summary>El tope de la prórroga si hoy se puede prorrogar (el término inicial), o null si alguna guarda de
    /// <see cref="Extend"/> la rechaza: la pantalla muestra u oculta «Prorrogar» sin repetir las reglas.</summary>
    public int? MaxExtensionBusinessDays(DateOnly today) =>
        ExtensionRejection(today) is null ? TermBusinessDays : null;

    // Las guardas de la prórroga en el orden del plan G10, sin el tope (depende de los días pedidos). Null si hoy se puede.
    private SupportDomainException? ExtensionRejection(DateOnly today)
    {
        if (Status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed)
        {
            return new SupportDomainException(SupportErrorCodes.NotEditable, $"A ticket in status '{Status}' cannot be extended.");
        }

        if (ExtendedAt is not null)
        {
            return new SupportDomainException(SupportErrorCodes.ExtensionAlreadyUsed, "The ticket already used its extension.");
        }

        if (TermBusinessDays is null || DueOn is not { } dueOn)
        {
            return new SupportDomainException(SupportErrorCodes.ExtensionNoTerm, "A ticket without a term cannot be extended.");
        }

        if (SupportTicketStatuses.StopsTheClock(Status))
        {
            return new SupportDomainException(
                SupportErrorCodes.ExtensionClockStopped, "A ticket cannot be extended while its clock is stopped.");
        }

        if (today > dueOn)
        {
            return new SupportDomainException(
                SupportErrorCodes.ExtensionOverdue, $"The ticket was due on {dueOn:yyyy-MM-dd} and can no longer be extended.");
        }

        return null;
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

    // El único cálculo del vencimiento (spec §7.2): término + prórroga + pausas, desde la radicación. Un camino nuevo que lo recalcule
    // pasa por acá o pierde la prórroga (plan G9, G12).
    private DateOnly? DueOnFor(int? term, SupportClockContext clock) =>
        term is { } days
            ? SupportBusinessDays.Add(clock.Calendar, ReceivedOn, days + ExtensionBusinessDays + PausedBusinessDays)
            : null;

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

    /// <summary>R9 [owner 2026-11-15]: resuelto o cerrado, el ticket sólo cambia de estado por la tabla de transiciones (reabrir).
    /// Se rechaza aunque el valor sea el mismo, para que la respuesta no dependa de lo que se mande.</summary>
    private void EnsureEditable()
    {
        if (Status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed)
        {
            throw new SupportDomainException(
                SupportErrorCodes.NotEditable, $"A ticket in status '{Status}' cannot be reassigned, retyped or relinked.");
        }
    }

    /// <summary>R9 extendida [owner 2026-11-15]: fijar o soltar un mensaje y «Seguir el hilo» se permiten resuelto, no cerrado. Responder
    /// no pasa por acá (se permite en cualquier estado), ni los caminos del sistema: el worker del hilo vincula con su propio repositorio y
    /// la fusión y el borrado de clientes son SQL crudo.</summary>
    private void EnsureNotClosed()
    {
        if (Status is SupportTicketStatus.Closed)
        {
            throw new SupportDomainException(
                SupportErrorCodes.NotEditable, "A closed ticket cannot pin or release messages nor change whether it follows its thread.");
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
