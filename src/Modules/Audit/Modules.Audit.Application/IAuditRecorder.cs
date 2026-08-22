using Modules.Audit.Domain;

namespace Modules.Audit.Application;

/// <summary>
/// El contrato transversal de auditoría (contrato de capacidad: <c>Audit | todos |
/// IAuditRecorder + audit outbox</c>). Los servicios de aplicación de cualquier módulo
/// registran por acá sus acciones auditadas. La implementación atómica (ADR 0019) acumula
/// la entrada en la unidad de trabajo del propio llamador, así que la fila commitea o
/// revierte junto con el cambio de negocio — una falla de auditoría bloquea una acción de
/// seguridad. La auditoría operativa y eventual va por el outbox y la proyecta Audit.
/// </summary>
public interface IAuditRecorder
{
    void Record(
        Guid? tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human,
        string source = "");
}
