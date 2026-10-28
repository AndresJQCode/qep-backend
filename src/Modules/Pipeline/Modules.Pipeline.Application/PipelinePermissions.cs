namespace Modules.Pipeline.Application;

/// <summary>
/// Spec 2026-10-13 D23 y P1 del plan: de núcleo (<c>RequiredModules</c> vacío), como los de Messaging. Con
/// <c>[pipeline]</c>, la máscara de entitlements quitaría el permiso y el 403 saldría <c>authorization.denied</c>, sin el
/// <c>tenancy.module_not_enabled</c> que el contrato pide; por eso cada handler llama a <c>TenantModuleGuard</c>. Cada uno
/// necesita su política en <c>AddAuthorization</c>: sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es 500.
/// </summary>
public static class PipelinePermissions
{
    /// <summary>Ver todos los negocios del tenant, el tablero, el triage y los ajustes. El filtro por dueño es foco, no
    /// seguridad (D23).</summary>
    public const string DealRead = "pipeline.deal.read";

    /// <summary>Crear, editar, mover, ganar, perder y reabrir negocios; enlazar cotizaciones; decidir el triage.</summary>
    public const string DealManage = "pipeline.deal.manage";

    /// <summary>Etapas y catálogos de motivos: cambian el trabajo de todos los asesores (D32).</summary>
    public const string SettingsManage = "pipeline.settings.manage";
}
