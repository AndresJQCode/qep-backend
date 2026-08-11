namespace Modules.Audit.Infrastructure;

// Binding fuertemente tipado de la sección "Audit" de appsettings. Las ventanas de
// retención siguen el modelo de capacidad aprobado (7 años para seguridad/administración,
// 2 años para operativo). Las ventanas se modelan acá; el purgado automático queda pendiente.
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    // ~7 años.
    public int SecurityRetentionDays { get; init; } = 2555;

    // 2 años.
    public int OperationalRetentionDays { get; init; } = 730;
}
