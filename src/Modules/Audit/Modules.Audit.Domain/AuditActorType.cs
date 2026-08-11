namespace Modules.Audit.Domain;

// El tipo de sujeto que ejecutó una acción auditada (contrato de capacidad: Human,
// System, Integration, AiAgent). Se persiste como string.
public enum AuditActorType
{
    Human = 1,
    System = 2,
    Integration = 3,
    AiAgent = 4
}
