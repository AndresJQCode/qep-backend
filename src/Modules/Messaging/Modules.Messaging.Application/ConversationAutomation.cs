using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

public sealed record PauseAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ResumeAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record StartAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ContinueAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

/// <summary>Spec 2026-10-31: una persona inicia el asistente desde el hilo. Mismo orden que pausar (tenant y permiso, 404, membresía,
/// If-Match). El agregado se carga antes del candado a propósito: cualquier cambio en el medio, también el <c>version + 1</c> por SQL
/// de la ingesta, choca al guardar y sale 412; no hace falta reintentar. La fila manual de la cola, el evento y la auditoría salen en
/// un solo guardado, y la unidad corre en el worker.</summary>
public sealed class StartAutomationHandler(ConversationAssignmentSteps steps, IAutomationManualStart automations, IAutomationInbox inbox)
    : ICommandHandler<StartAutomationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(StartAutomationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        ConversationSummary? summary = null;
        await automations.StartAsync(
            command.TenantId,
            command.ConversationId,
            async (chatbotId, ct) =>
            {
                var start = conversation.StartAutomationByMember(member, chatbotId, now);
                inbox.EnqueueManualStart(command.TenantId, conversation.ConnectionId, conversation.Id, now);
                summary = await steps.CommitAsync(command.TenantId, conversation, [start], now, ct);
            },
            cancellationToken);
        return summary!;
    }
}

/// <summary>Spec 2026-11-01 D-8: el asesor devuelve una pausa manual y el asistente sigue en el paso pendiente cuando el cliente escriba.
/// Tiene la forma de <see cref="StartAutomationHandler"/>, con la que comparte el porqué del orden, pero no encola nada: ninguna corrida
/// arranca ni avanza, así que no sale ningún mensaje. El evento y la auditoría van en la misma transacción que el cambio.</summary>
public sealed class ContinueAutomationHandler(ConversationAssignmentSteps steps, IAutomationManualStart automations)
    : ICommandHandler<ContinueAutomationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ContinueAutomationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        ConversationSummary? summary = null;
        await automations.ContinueAsync(
            command.TenantId,
            command.ConversationId,
            async ct =>
            {
                var change = conversation.ContinueAutomation(member, now);
                summary = await steps.CommitAsync(command.TenantId, conversation, [change], now, ct);
            },
            cancellationToken);
        return summary!;
    }
}

/// <summary>Spec 2026-10-21 §4.4: pausar a mano el asistente. Mismo orden que tomar (tenant y permiso → 404 → membresía → If-Match),
/// y el evento y la auditoría salen en la misma transacción que el cambio.</summary>
public sealed class PauseAutomationHandler(ConversationAssignmentSteps steps) : ICommandHandler<PauseAutomationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(PauseAutomationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        var pause = conversation.PauseAutomation(member, AutomationPauseReason.Manual, now);
        return await steps.CommitAsync(command.TenantId, conversation, [pause], now, cancellationToken);
    }
}

/// <summary>Spec 2026-10-21 §4.4: devolverle la conversación al asistente. Si un asesor la tiene, la libera primero (dos eventos).</summary>
public sealed class ResumeAutomationHandler(ConversationAssignmentSteps steps, IAutomationInbox inbox) : ICommandHandler<ResumeAutomationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ResumeAutomationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        var before = conversation.AutomationStatus;
        var changes = conversation.ResumeAutomation(member, now);
        // D-6: la corrida vieja se cierra con la señal; el asistente arranca de nuevo con el siguiente entrante.
        inbox.EnqueueIfAssisted(conversation, before, changes.Count, now);
        return await steps.CommitAsync(command.TenantId, conversation, changes, now, cancellationToken);
    }
}
