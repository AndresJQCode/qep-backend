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

    /// <summary>Spec 2026-10-11 §6.4 y decisión 16: crear, lanzar y cancelar campañas. De núcleo y de fábrica sólo en <c>admin</c>:
    /// una campaña llega a miles de personas en nombre de la empresa y el costo es por mensaje.</summary>
    public const string CampaignManage = "messaging.campaign.manage";

    /// <summary>Spec 2026-10-12 §3.5 y D-Q6: crear, editar y borrar las respuestas rápidas del tenant. Leerlas no lo pide (basta
    /// <see cref="ConversationRead"/>). De núcleo y de fábrica sólo en <c>admin</c>, como <see cref="CampaignManage"/>.</summary>
    public const string QuickReplyManage = "messaging.quick_reply.manage";

    /// <summary>Spec 2026-10-14 D-T5: crear, aprovisionar, sincronizar y borrar las plantillas de WhatsApp de una conexión. Leer el
    /// catálogo de envío no lo pide (basta <see cref="ConversationRead"/>). De núcleo y de fábrica sólo en <c>admin</c>: lo que la empresa
    /// manda en su nombre y lo que Meta cobra lo decide quien administra, como <see cref="CampaignManage"/>.</summary>
    public const string TemplateManage = "messaging.template.manage";

    /// <summary>Spec 2026-10-15 D-R8: renombrar y archivar los motivos de resolución del tenant. Crearlos en línea al resolver no lo pide
    /// (basta <see cref="ConversationManage"/>), y leerlos tampoco (<see cref="ConversationRead"/>). De núcleo y de fábrica en <c>admin</c>
    /// y <c>advisor</c> (decisión del owner).</summary>
    public const string ResolutionReasonManage = "messaging.resolution_reason.manage";
}
