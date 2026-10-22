namespace Modules.Automations.Application;

/// <summary>
/// Spec 2026-10-21 D-C10: de núcleo (<c>RequiredModules</c> vacío), como los de Pipeline. Con <c>[automations]</c>, la máscara de
/// entitlements quitaría el permiso y el 403 saldría <c>authorization.denied</c>, sin el <c>tenancy.module_not_enabled</c> que el
/// contrato pide; por eso cada handler llama a <c>TenantModuleGuard</c>. Cada uno necesita su política en <c>AddAuthorization</c>:
/// sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es 500. De fábrica sólo en <c>admin</c>.
/// </summary>
public static class AutomationsPermissions
{
    /// <summary>Ver los chatbots, sus plantillas y las conexiones que atienden.</summary>
    public const string ChatbotRead = "automations.chatbot.read";

    /// <summary>Crear, editar, publicar, pausar y borrar chatbots y sus plantillas.</summary>
    public const string ChatbotManage = "automations.chatbot.manage";
}
