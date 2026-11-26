namespace Modules.Support.Domain;

/// <summary>Por dónde entró el ticket (plan D8). La fase 1 sólo crea <see cref="Manual"/>; el CHECK de la base lista los tres
/// desde ya para que las fases 2 y 3 no migren el enum.</summary>
public enum SupportTicketIntake
{
    Manual,
    Conversation,
    PublicForm,

    /// <summary>Spec 2026-11-22: una tarea interna entre miembros. Va al final: se persiste por nombre.</summary>
    Internal,
}
