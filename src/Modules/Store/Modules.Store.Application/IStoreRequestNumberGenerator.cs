namespace Modules.Store.Application;

public interface IStoreRequestNumberGenerator
{
    /// <summary>El siguiente consecutivo del tenant, desde 1. Corre en la transacción del llamador.</summary>
    Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken);
}
