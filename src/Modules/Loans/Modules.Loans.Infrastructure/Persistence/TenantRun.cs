namespace Modules.Loans.Infrastructure.Persistence;

/// <summary>
/// La fila <c>loans.tenant_runs(tenant, día)</c>: el reclamo diario de la generación de cuotas, con el mismo modelo que la
/// de Contratos de servicio. Hoy sólo existe la tabla; el worker que la usa llega en su propia tarea.
/// </summary>
internal sealed class TenantRun
{
    public Guid TenantId { get; set; }

    public DateOnly RunDate { get; set; }

    public DateTimeOffset ClaimedUntil { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
