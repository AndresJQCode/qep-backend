using Modules.Integrations.Application;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-29 §5 (P-8): el estado de la importación del historial se lee con el permiso de leer la conexión y se reintenta con el
/// de editarla. Messaging no referencia Integrations, así que copia los textos en <see cref="MessagingConnectionPermissions"/>. Si
/// Integrations renombra uno, la política de la ruta de Messaging deja de existir y el síntoma es 500, no 403. Este proyecto ve los dos.
/// </summary>
public sealed class MessagingConnectionPermissionsParityTests
{
    [Fact]
    public void PermissionsMatchIntegrations()
    {
        Assert.Equal(IntegrationsPermissions.ConnectionRead, MessagingConnectionPermissions.Read);
        Assert.Equal(IntegrationsPermissions.ConnectionManage, MessagingConnectionPermissions.Manage);
    }
}
