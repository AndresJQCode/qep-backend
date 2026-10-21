using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

public sealed record PauseAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ResumeAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

/// <summary>Spec 2026-10-21 §4.3: pausar a mano el asistente. Mismo orden que tomar (tenant y permiso → 404 → membresía → If-Match),
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

/// <summary>Spec 2026-10-21 §4.3: devolverle la conversación al asistente. Si un asesor la tiene, la libera primero (dos eventos).</summary>
public sealed class ResumeAutomationHandler(ConversationAssignmentSteps steps) : ICommandHandler<ResumeAutomationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ResumeAutomationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        return await steps.CommitAsync(command.TenantId, conversation, conversation.ResumeAutomation(member, now), now, cancellationToken);
    }
}
