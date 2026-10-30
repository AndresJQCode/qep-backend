namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-29 §5 (P-8): la importación del historial es de la pantalla de la conexión, así que se lee con el permiso de leer la
/// conexión y se reintenta con el de editarla. Messaging no referencia Integrations: los textos se copian y sus políticas ya existen
/// (las registra Integrations en <c>AddAuthorization</c>).
/// </summary>
public static class MessagingConnectionPermissions
{
    // Mismo texto que IntegrationsPermissions (P-8); lo amarra MessagingConnectionPermissionsParityTests.
    public const string Read = "integrations.connection.read";
    public const string Manage = "integrations.connection.manage";
}
