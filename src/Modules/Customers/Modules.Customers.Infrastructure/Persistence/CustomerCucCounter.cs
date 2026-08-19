namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>
/// El consecutivo del CUC de un tenant. Una fila por tenant, y solo la toca
/// <see cref="CucGenerator"/>.
///
/// Vive en Infrastructure y no en Domain a proposito: no es una regla de negocio, es el mecanismo
/// con el que la base serializa la emision. El dominio recibe el CUC ya formado y solo comprueba
/// que llegue.
/// </summary>
internal sealed class CustomerCucCounter
{
    public Guid TenantId { get; init; }

    /// <summary>El proximo numero a emitir. Arranca en 1.</summary>
    public long NextValue { get; init; }
}
