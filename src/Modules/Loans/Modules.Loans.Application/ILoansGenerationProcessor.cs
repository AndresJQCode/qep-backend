namespace Modules.Loans.Application;

/// <summary>
/// La corrida diaria de un tenant: convierte en cuota cada período que ya cerró. No manda nada a nadie. Idempotente por día:
/// la deduplican <c>tenant_runs</c> y el índice único de la cuota, así que dos réplicas pueden llamarla a la vez.
/// </summary>
public interface ILoansGenerationProcessor
{
    Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}
