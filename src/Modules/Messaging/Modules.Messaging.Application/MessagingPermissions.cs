namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-09, decisión 5: de núcleo (<c>RequiredModules</c> vacío) y en <c>admin</c> y <c>advisor</c>.
/// Si llevaran <c>[messaging]</c>, la máscara quitaría el permiso y el 403 saldría como
/// <c>authorization.denied</c>, sin el código <c>tenancy.module_not_enabled</c> que el contrato pide; por
/// eso cada handler llama <c>TenantModuleGuard.EnsureEnabledAsync</c>. Cada uno necesita su política en
/// <c>AddAuthorization</c>: sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es 500.
/// </summary>
public static class MessagingPermissions
{
    public const string ConversationRead = "messaging.conversation.read";
    public const string ConversationManage = "messaging.conversation.manage";
}
