namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>
/// Motivos de <c>qep.webhook.discarded</c>: un evento de Meta que llegó con firma válida y no se ingresó a propósito. Son contrato
/// con la alerta <c>QepWebhookDiscarding</c> de <c>migracion-k8s</c>, que los lee por nombre (sólo alerta con
/// <see cref="UnknownRoute"/> y <see cref="WithoutUserId"/>). Un descarte nuevo suma su motivo acá y en
/// <c>WebhookDiscardReasonsTests</c>, y decide si alerta. El texto de los logs no usa estas constantes: sigue con guion.
/// </summary>
internal static class WebhookDiscardReasons
{
    /// <summary>El número o la cuenta no tiene conexión en QEP.</summary>
    public const string UnknownRoute = "unknown_route";

    /// <summary>Un entrante de WhatsApp sin <c>from_user_id</c> válido.</summary>
    public const string WithoutUserId = "without_user_id";

    public const string Paused = "paused";

    public const string ModuleOff = "module_off";

    /// <summary>Un estado de WhatsApp de un mensaje que QEP no mandó (coexistencia): tráfico normal.</summary>
    public const string ForeignStatus = "foreign_status";

    /// <summary>Un campo del webhook de WhatsApp que QEP no atiende.</summary>
    public const string UnhandledField = "unhandled_field";

    /// <summary>Un visto o un borrado de Instagram sin mensaje en QEP.</summary>
    public const string ForeignEvent = "foreign_event";

    /// <summary>Un evento de Instagram que el parser ignora (<c>InstagramIgnoredEvent</c>).</summary>
    public const string Ignored = "ignored";

    public static IReadOnlyList<string> All { get; } =
        [UnknownRoute, WithoutUserId, Paused, ModuleOff, ForeignStatus, UnhandledField, ForeignEvent, Ignored];
}

/// <summary>Valores de la etiqueta <c>provider</c> de <c>qep.webhook.discarded</c>; contrato con las alertas.</summary>
internal static class WebhookDiscardProviders
{
    public const string WhatsApp = "whatsapp";

    public const string Instagram = "instagram";
}
