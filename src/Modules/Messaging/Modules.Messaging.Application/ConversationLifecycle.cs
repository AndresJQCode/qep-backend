using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record MarkConversationReadCommand(Guid TenantId, Guid ConversationId) : ICommand<bool>;

/// <summary>Spec 2026-10-14 §2: «no leída» es un marcador, no un conteo. Sube <c>unread_count</c> a 1 sólo si
/// estaba en 0; con no leídos reales no toca nada. Sin acuse a Meta: un read enviado no se retracta.</summary>
public sealed record MarkConversationUnreadCommand(Guid TenantId, Guid ConversationId) : ICommand<bool>;

/// <summary>Spec 2026-10-14 §7: «desde este mensaje». El contador pasa al mayor entre el actual y los entrantes
/// desde ese mensaje: lo que tendría si acabaran de llegar. Sin modelo nuevo y sin acuse a Meta.</summary>
public sealed record MarkUnreadFromMessageCommand(Guid TenantId, Guid ConversationId, Guid MessageId) : ICommand<bool>;

/// <summary>Spec 2026-10-15 §3.3: el motivo es opcional. <see cref="ReasonId"/> elige uno vigente; <see cref="ReasonName"/> busca o crea
/// por nombre (D-R4). Los dos a la vez es 422; los dos ausentes, o el nombre en blanco, es resolver sin motivo como antes.</summary>
public sealed record ResolveConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion, Guid? ReasonId = null, string? ReasonName = null)
    : ICommand<ConversationSummary>;

/// <summary>Spec 2026-10-15 §3.3: el validador da el campo (<c>errors.reasonName</c>) que el dominio no puede dar.</summary>
public sealed class ResolveConversationValidator : AbstractValidator<ResolveConversationCommand>
{
    public ResolveConversationValidator()
    {
        RuleFor(command => command.ReasonName)
            .Must(name => ResolutionReason.TrimName(name).Length <= ResolutionReason.NameMaxLength)
            .OverridePropertyName("reasonName").WithMessage(ResolutionReasonMessages.NameTooLong);
        RuleFor(command => command)
            .Must(command => command.ReasonId is null || string.IsNullOrWhiteSpace(command.ReasonName))
            .OverridePropertyName("reasonName").WithMessage(ResolutionReasonMessages.Both);
    }
}

public sealed record ReopenConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

/// <summary>§8.4: commit primero, por un UPDATE atómico sin token de concurrencia (un entrante que sube
/// <c>version</c> en medio no lo convierte en 412); después el acuse a Meta, best effort (5 s), sólo si había
/// no leídos, hay último entrante de menos de 30 días y la conexión está Active. Siempre 204.</summary>
public sealed class MarkConversationReadHandler(
    IConversationRepository repository,
    ReadReceipts readReceipts,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier)
    : ICommandHandler<MarkConversationReadCommand, bool>
{
    public async Task<bool> HandleAsync(MarkConversationReadCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var now = clock.UtcNow;
        var target = await repository.MarkReadAsync(command.TenantId, command.ConversationId, now, cancellationToken);
        if (target is null)
        {
            // Nada que marcar: ya estaba en 0 (204 sin acuse) o no es de este tenant (404).
            return await repository.ExistsAsync(command.TenantId, command.ConversationId, cancellationToken)
                ? false
                : throw MessagingNotFound.Conversation(command.ConversationId);
        }

        // Spec 2026-10-14: el contador bajó a 0 en la base; la lista de las otras pestañas lo relee.
        await notifier.ConversationChangedAsync(command.TenantId, command.ConversationId, cancellationToken);
        await readReceipts.SendAsync(command.TenantId, command.ConversationId, target, now, cancellationToken);
        return true;
    }
}

/// <summary>Spec 2026-10-14 §3: espejo de <see cref="MarkConversationReadHandler"/>. Un UPDATE atómico sin token
/// de concurrencia —la ingesta sube <c>version</c> por SQL y un If-Match lo volvería 412—, sin auditoría ni evento:
/// es estado de la bandeja, no una decisión sobre la conversación. Siempre 204 si existe en el tenant.</summary>
public sealed class MarkConversationUnreadHandler(
    IConversationRepository repository,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier)
    : ICommandHandler<MarkConversationUnreadCommand, bool>
{
    public async Task<bool> HandleAsync(MarkConversationUnreadCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        if (await repository.MarkUnreadAsync(command.TenantId, command.ConversationId, clock.UtcNow, cancellationToken))
        {
            await notifier.ConversationChangedAsync(command.TenantId, command.ConversationId, cancellationToken);
            return true;
        }

        // Nada que marcar: ya tenía no leídos (204) o no es de este tenant (404). Igual que el read.
        return await repository.ExistsAsync(command.TenantId, command.ConversationId, cancellationToken)
            ? false
            : throw MessagingNotFound.Conversation(command.ConversationId);
    }
}

/// <summary>Spec 2026-10-14 §7.3: mismo esquema que <see cref="MarkConversationUnreadHandler"/>; el 404 distingue el
/// mensaje de la conversación porque el formulario del hilo tiene que saber cuál de los dos ya no está.</summary>
public sealed class MarkUnreadFromMessageHandler(
    IConversationRepository repository,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier)
    : ICommandHandler<MarkUnreadFromMessageCommand, bool>
{
    public async Task<bool> HandleAsync(MarkUnreadFromMessageCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var outcome = await repository.MarkUnreadFromAsync(command.TenantId, command.ConversationId, command.MessageId, clock.UtcNow, cancellationToken);
        if (outcome == UnreadFromOutcome.Marked)
        {
            await notifier.ConversationChangedAsync(command.TenantId, command.ConversationId, cancellationToken);
        }

        return outcome switch
        {
            UnreadFromOutcome.Marked => true,
            UnreadFromOutcome.Unchanged => false,
            _ => await repository.ExistsAsync(command.TenantId, command.ConversationId, cancellationToken)
                ? throw MessagingNotFound.Message(command.MessageId)
                : throw MessagingNotFound.Conversation(command.ConversationId),
        };
    }
}

/// <summary>§8.5: por el agregado con If-Match (428 lo pone el endpoint; 412 lo pone el unit of work). Spec 2026-10-15 §3.3: el
/// motivo nuevo se agrega al mismo DbContext, así que entra en el mismo SaveChanges que la resolución, el evento y la auditoría: un
/// resolver que falla no deja motivos huérfanos. El orden importa: la versión y <c>EnsureCanResolve</c> (already_resolved) van antes de buscar el
/// motivo y de contar el tope, que sólo se cuenta si de verdad se crea.</summary>
public sealed class ResolveConversationHandler(
    IConversationRepository repository,
    IConversationQueries queries,
    IResolutionReasonRepository reasons,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    IConversationEvents events,
    ConversationSummaryBuilder summaries,
    CallerMembership caller,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IValidator<ResolveConversationCommand> validator)
    : ICommandHandler<ResolveConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ResolveConversationCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        conversation.EnsureCanResolve();

        var now = clock.UtcNow;
        var reason = await ResolutionReasonChoice.ChooseAsync(reasons, command.TenantId, command.ReasonId, command.ReasonName, cancellationToken);
        conversation.Resolve(now, reason?.Id);
        if (reason is { IsNew: true })
        {
            ResolutionReason.EnsureTenantCapacity(await reasons.CountActiveAsync(command.TenantId, cancellationToken));
            reasons.Add(ResolutionReason.Create(reason.Id, command.TenantId, reason.Name, now));
            audit.RecordResolutionReason(command.TenantId, executionContext.SubjectId, MessagingAuditActions.ResolutionReasonCreated, reason.Id, now);
        }

        // Spec 2026-10-10 §6.1.5 (P9): el evento lleva quién; sin membresía activa, actor null (sin 403 nuevo).
        var actor = await caller.FindAsync(command.TenantId, cancellationToken);
        events.Record(
            command.TenantId, conversation.ConnectionId, conversation.Id,
            new ConversationEvent(ConversationEventType.Resolved, Actor: actor, ReasonId: reason?.Id), now);
        audit.Record(command.TenantId, executionContext.SubjectId, MessagingAuditActions.Resolved, conversation.Id, now);
        // D-R11: dos asesores creando el mismo nombre nuevo a la vez → el segundo recibe 409 name_taken de la unidad de trabajo, y la
        // transacción entera se revierte (el If-Match sigue vigente). El cliente reintenta una vez y el segundo intento lo reutiliza.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await notifier.ConversationChangedAsync(command.TenantId, conversation.Id, cancellationToken);
        return (await summaries.BuildAsync(command.TenantId, [(await queries.FindAsync(command.TenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}

public sealed class ReopenConversationHandler(
    IConversationRepository repository,
    IConversationQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    IConversationEvents events,
    ConversationSummaryBuilder summaries,
    CallerMembership caller,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier)
    : ICommandHandler<ReopenConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ReopenConversationCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);

        var now = clock.UtcNow;
        conversation.Reopen(now);
        // Spec 2026-10-10 §6.1.5 (P9): el evento lleva quién; sin membresía activa, actor null (sin 403 nuevo).
        var actor = await caller.FindAsync(command.TenantId, cancellationToken);
        events.Record(command.TenantId, conversation.ConnectionId, conversation.Id, new ConversationEvent(ConversationEventType.Reopened, Actor: actor), now);
        audit.Record(command.TenantId, executionContext.SubjectId, MessagingAuditActions.Reopened, conversation.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await notifier.ConversationChangedAsync(command.TenantId, conversation.Id, cancellationToken);
        return (await summaries.BuildAsync(command.TenantId, [(await queries.FindAsync(command.TenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}

/// <summary>Copia de <c>ConcurrencyGuard</c> de Integrations: la versión del If-Match tiene que ser la vigente.</summary>
internal static class ConversationConcurrency
{
    public static void EnsureVersion(Conversation conversation, long expectedVersion)
    {
        if (conversation.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The conversation changed since it was loaded.");
        }
    }
}
