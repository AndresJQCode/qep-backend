using Modules.Integrations.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-24 D-T10 y §3.4: el motivo por el que una conexión no se puede usar, como el código <c>snake_case</c> que viaja al hilo
/// del traspaso. Vive en Bootstrapper porque Automations y Messaging no referencian Integrations: un solo traductor evita dos copias de la
/// misma tabla. Los módulos sólo emiten los suyos (<c>not_chosen</c>, <c>auth_header_reserved</c>, <c>credentials_rejected</c>).
/// </summary>
internal static class ConnectionReasonCodes
{
    /// <summary>La conexión es de otro proveedor (un bloque importado con el id de una conexión de WhatsApp).</summary>
    public const string WrongProvider = "wrong_provider";

    /// <summary>Le falta un campo o secreto requerido (URL base, clave, número, token).</summary>
    public const string Incomplete = "incomplete";

    public static string Of(ConnectionUnavailableReason reason) => reason switch
    {
        ConnectionUnavailableReason.NotFound => "not_found",
        ConnectionUnavailableReason.Paused => "paused",
        ConnectionUnavailableReason.NeedsAttention => "needs_attention",
        ConnectionUnavailableReason.ProviderHidden => "provider_hidden",
        ConnectionUnavailableReason.ProviderDisabled => "provider_disabled",
        ConnectionUnavailableReason.SecretUnreadable => "secret_unreadable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Every reason needs its code (ConnectionReasonCodesTests)."),
    };
}
