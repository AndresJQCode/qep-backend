using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Lo que comparten los cinco handlers de gestión (editar, cancelar, «Enviar ya», reintentar, descartar). Spec 2026-11-09 D3: pide el
/// permiso de responder, módulo y membresía activa, y que la conversación exista; <b>no</b> pide ser el asignado ni el autor. Después
/// toma el candado de la fila (<c>null</c> es 404 con <c>messaging.scheduled.not_found</c>, también si el id es de otra conversación).
/// El orden que cada handler sigue desde ahí: estado (409) → versión (412) → transición → auditoría en la misma transacción → commit →
/// aviso. Nadie carga la fila con tracking antes de bloquearla (<c>LockAsync</c> la trae con <c>FromSql</c> al tracker).
/// </summary>
internal sealed class ScheduledMessageManager(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier)
{
    public DateTimeOffset Now => clock.UtcNow;

    public Guid SubjectId => executionContext.SubjectId;

    public async Task<ManagedScheduledMessage> OpenAsync(Guid tenantId, Guid conversationId, Guid scheduledMessageId, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await conversations.FindAsync(tenantId, conversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(conversationId);
        var member = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");
        var locked = await scheduled.LockAsync(tenantId, conversationId, scheduledMessageId, cancellationToken)
            ?? throw new ResourceNotFoundException(MessagingErrorCodes.ScheduledNotFound, $"Scheduled message '{scheduledMessageId}' was not found.");
        return new ManagedScheduledMessage(conversation, member, locked);
    }

    public static ResourceConflictException NotEditable() =>
        new(MessagingErrorCodes.ScheduledNotEditable, "Only a scheduled message that has not started sending can be edited, cancelled or sent now.");

    public static ResourceConflictException NotRetriable() =>
        new(MessagingErrorCodes.ScheduledNotRetriable, "Only a failed scheduled message can be retried or discarded.");

    public static void EnsureVersion(ScheduledMessage message, long? expectedVersion)
    {
        if (message.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The scheduled message changed since it was loaded.");
        }
    }

    /// <summary>Ya commiteado, el otro asesor del hilo tiene algo que leer. Un fallo del aviso no deshace el cambio.</summary>
    public async Task NotifyAsync(Guid tenantId, Guid conversationId)
    {
        try
        {
            await notifier.ConversationChangedAsync(tenantId, conversationId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Application no tiene logger; el polling del frontend cubre el aviso perdido.
            _ = exception;
        }
    }
}

internal sealed record ManagedScheduledMessage(ConversationRow Conversation, Guid Member, IScheduledMessageLock Lock);

/// <summary>Spec 2026-11-09 «Contrato HTTP»: <c>PUT …/scheduled-message/{id}</c>. Un campo omitido o <c>null</c> conserva lo guardado;
/// <c>caption: ""</c> lo borra; <c>scheduledAt</c> es opcional y sólo se revalida si difiere de la guardada.</summary>
public sealed record UpdateScheduledMessageCommand(
    Guid TenantId, Guid ConversationId, Guid ScheduledMessageId, long? ExpectedVersion, DateTimeOffset? ScheduledAt, string? Text, string? Caption)
    : ICommand<ScheduledMessageDto>;

public sealed record CancelScheduledMessageCommand(Guid TenantId, Guid ConversationId, Guid ScheduledMessageId, long? ExpectedVersion) : ICommand<bool>;

public sealed record SendScheduledMessageNowCommand(Guid TenantId, Guid ConversationId, Guid ScheduledMessageId, long? ExpectedVersion)
    : ICommand<ScheduledMessageDto>;

public sealed record RetryScheduledMessageCommand(Guid TenantId, Guid ConversationId, Guid ScheduledMessageId, long? ExpectedVersion)
    : ICommand<ScheduledMessageDto>;

public sealed record DiscardScheduledMessageCommand(Guid TenantId, Guid ConversationId, Guid ScheduledMessageId, long? ExpectedVersion) : ICommand<bool>;

/// <summary>La forma del cuerpo. Lo que depende del tipo guardado (texto en un adjunto, pie en un audio) lo decide el handler, que es
/// quien ve la fila; aquí sólo los topes, midiendo <c>{saludo}</c> como el más largo (<see cref="ScheduledText.MeasuredLength"/>).</summary>
public sealed class UpdateScheduledMessageValidator : AbstractValidator<UpdateScheduledMessageCommand>
{
    public UpdateScheduledMessageValidator()
    {
        RuleFor(command => command.ExpectedVersion).Must(version => version is > 0).OverridePropertyName("expectedVersion").WithMessage("Falta la versión que estás editando.");
        RuleFor(command => command.Text)
            .Must(text => !SendMessageValidator.HasForbiddenControl(text!) && ScheduledText.MeasuredLength(text!.Trim()) <= SendMessageValidator.TextMaxLength)
            .When(command => command.Text is not null)
            .WithMessage("Escribe un mensaje de hasta 4096 caracteres.")
            .OverridePropertyName("text");
        RuleFor(command => command.Caption)
            .Must(caption => ScheduledText.MeasuredLength(caption!.Trim()) <= SendMediaMessageValidator.MaxCaptionLength)
            .When(command => command.Caption is not null)
            .WithMessage("El texto que acompaña al archivo puede tener hasta 1024 caracteres.")
            .OverridePropertyName("caption");
    }
}

public sealed class CancelScheduledMessageValidator : AbstractValidator<CancelScheduledMessageCommand>
{
    public CancelScheduledMessageValidator() =>
        RuleFor(command => command.ExpectedVersion).Must(version => version is > 0).OverridePropertyName("expectedVersion").WithMessage("Falta la versión que estás viendo.");
}

public sealed class SendScheduledMessageNowValidator : AbstractValidator<SendScheduledMessageNowCommand>
{
    public SendScheduledMessageNowValidator() =>
        RuleFor(command => command.ExpectedVersion).Must(version => version is > 0).OverridePropertyName("expectedVersion").WithMessage("Falta la versión que estás viendo.");
}

public sealed class RetryScheduledMessageValidator : AbstractValidator<RetryScheduledMessageCommand>
{
    public RetryScheduledMessageValidator() =>
        RuleFor(command => command.ExpectedVersion).Must(version => version is > 0).OverridePropertyName("expectedVersion").WithMessage("Falta la versión que estás viendo.");
}

public sealed class DiscardScheduledMessageValidator : AbstractValidator<DiscardScheduledMessageCommand>
{
    public DiscardScheduledMessageValidator() =>
        RuleFor(command => command.ExpectedVersion).Must(version => version is > 0).OverridePropertyName("expectedVersion").WithMessage("Falta la versión que estás viendo.");
}

/// <summary>Editar: estado (409) → versión (412) → reglas que dependen del tipo guardado (422) → hora (sólo si cambió) → <c>Edit</c>. Una
/// edición que no cambia nada responde 200 sin commit ni auditoría: soltar el candado sin confirmar es rollback.</summary>
public sealed class UpdateScheduledMessageHandler(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IMessagingAuditRecorder audit,
    ScheduledMessageMapper mapper,
    IValidator<UpdateScheduledMessageCommand> validator)
    : ICommandHandler<UpdateScheduledMessageCommand, ScheduledMessageDto>
{
    public async Task<ScheduledMessageDto> HandleAsync(UpdateScheduledMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var manager = new ScheduledMessageManager(scheduled, conversations, membershipDirectory, tenantModules, executionContext, clock, notifier);
        var managed = await manager.OpenAsync(command.TenantId, command.ConversationId, command.ScheduledMessageId, cancellationToken);
        await using var locked = managed.Lock;
        var message = locked.Message;
        if (!message.CanEdit)
        {
            throw ScheduledMessageManager.NotEditable();
        }

        ScheduledMessageManager.EnsureVersion(message, command.ExpectedVersion);
        ValidateAgainstStoredKind(command, message, managed.Conversation);

        var now = manager.Now;
        if (command.ScheduledAt is { } at && at != message.ScheduledAt)
        {
            ScheduledMessage.EnsureSchedulable(at, managed.Conversation.LastInboundAt, now);
        }

        var edit = message.Edit(
            command.ScheduledAt, command.Text, textProvided: command.Text is not null, command.Caption, captionProvided: command.Caption is not null,
            managed.Member, now);
        if (edit.ChangedFields.Count == 0)
        {
            return await mapper.ToDtoAsync(command.TenantId, message, managed.Conversation, now, cancellationToken);
        }

        audit.RecordScheduledMessage(
            command.TenantId, manager.SubjectId, bySystem: false, MessagingAuditActions.ScheduledMessageUpdated, message.Id, now, edit.ChangedFields);
        await locked.CommitAsync(cancellationToken);
        await manager.NotifyAsync(command.TenantId, command.ConversationId);
        return await mapper.ToDtoAsync(command.TenantId, message, managed.Conversation, now, cancellationToken);
    }

    // Las claves son las del JSON (errors.text, errors.caption). El mensaje del texto vacío es el de SendMessageValidator.
    private static void ValidateAgainstStoredKind(UpdateScheduledMessageCommand command, ScheduledMessage message, ConversationRow conversation)
    {
        var failures = new List<ValidationFailure>(2);
        if (command.Text is { } text)
        {
            if (message.Kind != MessageKind.Text)
            {
                failures.Add(new ValidationFailure("text", "Un mensaje con archivo no lleva texto: edita su texto de acompañamiento."));
            }
            else if (text.Trim().Length == 0)
            {
                failures.Add(new ValidationFailure("text", "Escribe un mensaje de hasta 4096 caracteres."));
            }
            else if (conversation.Channel == ConversationChannel.Instagram
                && ScheduledText.MeasuredLength(text.Trim()) > SendMessageValidator.InstagramTextMaxLength)
            {
                failures.Add(new ValidationFailure("text", "Escribe un mensaje de hasta 1000 caracteres: es el máximo de Instagram."));
            }
        }

        if (command.Caption is not null)
        {
            if (message.Kind == MessageKind.Text)
            {
                failures.Add(new ValidationFailure("caption", "Un mensaje de texto no lleva texto de acompañamiento."));
            }
            else if (message.Kind == MessageKind.Audio)
            {
                failures.Add(new ValidationFailure("caption", "Los audios se envían sin texto."));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }
}

public sealed class CancelScheduledMessageHandler(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IMessagingAuditRecorder audit,
    IMessagingMediaStore store,
    IValidator<CancelScheduledMessageCommand> validator)
    : ICommandHandler<CancelScheduledMessageCommand, bool>
{
    public async Task<bool> HandleAsync(CancelScheduledMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var manager = new ScheduledMessageManager(scheduled, conversations, membershipDirectory, tenantModules, executionContext, clock, notifier);
        var managed = await manager.OpenAsync(command.TenantId, command.ConversationId, command.ScheduledMessageId, cancellationToken);
        await using var locked = managed.Lock;
        var message = locked.Message;
        if (!message.CanCancel)
        {
            throw ScheduledMessageManager.NotEditable();
        }

        ScheduledMessageManager.EnsureVersion(message, command.ExpectedVersion);
        var now = manager.Now;
        message.Cancel(managed.Member, now);
        audit.RecordScheduledMessage(command.TenantId, manager.SubjectId, bySystem: false, MessagingAuditActions.ScheduledMessageCancelled, message.Id, now);
        await locked.CommitAsync(cancellationToken);
        await manager.NotifyAsync(command.TenantId, command.ConversationId);
        if (message.MediaStorageKey is { } key)
        {
            await ScheduledMessageWriter.DiscardAsync(store, key);
        }

        return true;
    }
}

/// <summary>«Enviar ya»: sólo adelanta la hora a ahora y deja el resto al ejecutor del próximo ciclo. No reclama la fila ni asume un
/// <c>LockedUntil</c>: el arrendamiento real lo pone quien la reclama para enviarla.</summary>
public sealed class SendScheduledMessageNowHandler(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IMessagingAuditRecorder audit,
    ScheduledMessageMapper mapper,
    IValidator<SendScheduledMessageNowCommand> validator)
    : ICommandHandler<SendScheduledMessageNowCommand, ScheduledMessageDto>
{
    public async Task<ScheduledMessageDto> HandleAsync(SendScheduledMessageNowCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var manager = new ScheduledMessageManager(scheduled, conversations, membershipDirectory, tenantModules, executionContext, clock, notifier);
        var managed = await manager.OpenAsync(command.TenantId, command.ConversationId, command.ScheduledMessageId, cancellationToken);
        await using var locked = managed.Lock;
        var message = locked.Message;
        if (!message.CanSendNow)
        {
            throw ScheduledMessageManager.NotEditable();
        }

        ScheduledMessageManager.EnsureVersion(message, command.ExpectedVersion);
        var now = manager.Now;
        message.SendNow(now);
        audit.RecordScheduledMessage(command.TenantId, manager.SubjectId, bySystem: false, MessagingAuditActions.ScheduledMessageSendNow, message.Id, now);
        await locked.CommitAsync(cancellationToken);
        await manager.NotifyAsync(command.TenantId, command.ConversationId);
        return await mapper.ToDtoAsync(command.TenantId, message, managed.Conversation, now, cancellationToken);
    }
}

/// <summary>Reintentar un <c>Failed</c>: estado (409) → versión (412) → contacto bloqueado (<c>messaging.contact.blocked</c>, el mismo
/// código que el envío normal) → ventana con el margen (<c>outside_window</c>) → vuelve a la cola para salir ya.</summary>
public sealed class RetryScheduledMessageHandler(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IMessagingAuditRecorder audit,
    ScheduledMessageMapper mapper,
    IValidator<RetryScheduledMessageCommand> validator)
    : ICommandHandler<RetryScheduledMessageCommand, ScheduledMessageDto>
{
    public async Task<ScheduledMessageDto> HandleAsync(RetryScheduledMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var manager = new ScheduledMessageManager(scheduled, conversations, membershipDirectory, tenantModules, executionContext, clock, notifier);
        var managed = await manager.OpenAsync(command.TenantId, command.ConversationId, command.ScheduledMessageId, cancellationToken);
        await using var locked = managed.Lock;
        var message = locked.Message;
        if (!message.CanRetry)
        {
            throw ScheduledMessageManager.NotRetriable();
        }

        ScheduledMessageManager.EnsureVersion(message, command.ExpectedVersion);
        var now = manager.Now;
        if (managed.Conversation.BlockedAt is not null)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ContactBlocked, "The contact is blocked; unblock it to write.");
        }

        if (!ScheduledMessage.IsWindowOpenForSending(managed.Conversation.LastInboundAt, now))
        {
            throw new MessagingDomainException(MessagingErrorCodes.ScheduledOutsideWindow, "The 24-hour customer service window is closed.");
        }

        message.Retry(now);
        audit.RecordScheduledMessage(command.TenantId, manager.SubjectId, bySystem: false, MessagingAuditActions.ScheduledMessageRetried, message.Id, now);
        await locked.CommitAsync(cancellationToken);
        await manager.NotifyAsync(command.TenantId, command.ConversationId);
        return await mapper.ToDtoAsync(command.TenantId, message, managed.Conversation, now, cancellationToken);
    }
}

/// <summary>Descartar un <c>Failed</c>: lo cierra y, en la misma transacción, quema el reintento del mensaje <c>Failed</c> que dejó
/// (<see cref="IScheduledMessageLock.CommitDiscardAsync"/>), para que el hilo no reenvíe su <c>clientId</c>.</summary>
public sealed class DiscardScheduledMessageHandler(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IMessagingAuditRecorder audit,
    IMessagingMediaStore store,
    IValidator<DiscardScheduledMessageCommand> validator)
    : ICommandHandler<DiscardScheduledMessageCommand, bool>
{
    public async Task<bool> HandleAsync(DiscardScheduledMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var manager = new ScheduledMessageManager(scheduled, conversations, membershipDirectory, tenantModules, executionContext, clock, notifier);
        var managed = await manager.OpenAsync(command.TenantId, command.ConversationId, command.ScheduledMessageId, cancellationToken);
        await using var locked = managed.Lock;
        var message = locked.Message;
        if (!message.CanDiscard)
        {
            throw ScheduledMessageManager.NotRetriable();
        }

        ScheduledMessageManager.EnsureVersion(message, command.ExpectedVersion);
        var now = manager.Now;
        message.Discard(managed.Member, now);
        audit.RecordScheduledMessage(command.TenantId, manager.SubjectId, bySystem: false, MessagingAuditActions.ScheduledMessageDiscarded, message.Id, now);
        await locked.CommitDiscardAsync(cancellationToken);
        await manager.NotifyAsync(command.TenantId, command.ConversationId);
        if (message.MediaStorageKey is { } key)
        {
            await ScheduledMessageWriter.DiscardAsync(store, key);
        }

        return true;
    }
}
