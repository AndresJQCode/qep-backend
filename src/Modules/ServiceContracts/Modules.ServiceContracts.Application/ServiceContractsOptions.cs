namespace Modules.ServiceContracts.Application;

/// <summary>La sección <c>Billing</c> de appsettings (spec §6). Se valida al arrancar, como <c>QuotationsOptions</c>.</summary>
public sealed class ServiceContractsOptions
{
    /// <summary>
    /// Sigue siendo <c>Billing</c> aunque el módulo se llame <c>ServiceContracts</c>: la sección vive en <c>appsettings.json</c>,
    /// en los user-secrets de cada developer y en el ConfigMap de producción, y renombrarla apagaría el worker sin error.
    /// </summary>
    public const string SectionName = "Billing";

    /// <summary>Cada cuánto despierta el worker. Corre más de una vez al día a propósito: el trabajo del día se deduplica por
    /// <c>tenant_runs</c>, y un tick frecuente arranca pronto después de <see cref="SendHourLocal"/> en cada huso.</summary>
    public int SweepMinutes { get; init; } = 15;

    /// <summary>La hora local del tenant desde la que se manda lo del día: nadie recibe un cobro a medianoche.</summary>
    public int SendHourLocal { get; init; } = 8;

    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (SweepMinutes <= 0)
        {
            failures.Add("Billing:SweepMinutes must be greater than zero.");
        }

        if (SendHourLocal is < 0 or > 23)
        {
            failures.Add("Billing:SendHourLocal must be an hour between 0 and 23.");
        }

        return failures;
    }
}
