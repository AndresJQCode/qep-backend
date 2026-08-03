namespace Modules.Audit.Domain;

// The kind of subject that performed an audited action (capability contract: Human,
// System, Integration, AiAgent). Persisted as a string.
public enum AuditActorType
{
    Human = 1,
    System = 2,
    Integration = 3,
    AiAgent = 4
}
