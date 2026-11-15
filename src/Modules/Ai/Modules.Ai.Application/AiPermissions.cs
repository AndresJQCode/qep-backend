namespace Modules.Ai.Application;

/// <summary>
/// Spec 2026-10-29 §1: de núcleo (<c>RequiredModules</c> vacío), como los de Automations. Con <c>[ai]</c>, la máscara de entitlements
/// quitaría el permiso y el 403 saldría <c>authorization.denied</c>, sin el <c>tenancy.module_not_enabled</c> que el contrato pide; por
/// eso cada handler llama a <c>TenantModuleGuard</c>. Cada uno necesita su política en <c>AddAuthorization</c>: sin ella
/// <c>RequireAuthorization</c> no resuelve y el síntoma es 500. De fábrica sólo en <c>admin</c>: un agente habla con los clientes en
/// nombre de la empresa.
/// </summary>
public static class AiPermissions
{
    /// <summary>Ver los agentes, sus modelos, su consumo y el estado de las claves de los proveedores.</summary>
    public const string AgentRead = "ai.agent.read";

    /// <summary>Crear, editar, probar, publicar, pausar y borrar agentes.</summary>
    public const string AgentManage = "ai.agent.manage";
}
