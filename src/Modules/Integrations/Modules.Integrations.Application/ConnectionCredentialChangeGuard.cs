namespace Modules.Integrations.Application;

/// <summary>
/// Lo que el módulo de un proveedor guarda a partir de una conexión (en Siigo, la configuración del asistente con ids de la cuenta) se
/// vuelve inválido si la edición pone credenciales de <b>otra cuenta</b>. Integrations no ve ese módulo (<c>IntegrationsLayerTests</c>):
/// este puerto lo cablea el composition root, y para un proveedor sin nada que cuidar no hace nada.
/// </summary>
public interface IConnectionCredentialChangeGuard
{
    /// <summary>
    /// Se llama con la prueba de credenciales ya pasada y antes de guardar nada. <paramref name="fields"/> y <paramref name="secrets"/>
    /// son los valores que se van a guardar (los secretos, ya mezclados con los guardados). <c>true</c>: la edición cambia de cuenta,
    /// <paramref name="confirmed"/> la autorizó y hay que llamar a <see cref="ResetConfigurationAsync"/> en la misma transacción que
    /// el guardado. <c>false</c>: no hay nada que hacer. Si cambia de cuenta y no está confirmada, lanza el 409 del proveedor; una falla
    /// transitoria del proveedor sube tal cual.
    /// </summary>
    Task<bool> MustResetConfigurationAsync(
        string providerKey,
        Guid tenantId,
        Guid connectionId,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>Borra lo que el proveedor guardaba de la cuenta anterior. Exige la transacción abierta por
    /// <see cref="IIntegrationsUnitOfWork.BeginTransactionAsync"/>, que el guardado de la conexión confirma.</summary>
    Task ResetConfigurationAsync(string providerKey, Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}
