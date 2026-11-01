namespace Modules.Loans.Application;

/// <summary>La sección <c>Loans</c> de appsettings. Se valida al arrancar, como <c>ServiceContractsOptions</c>.</summary>
public sealed class LoansOptions
{
    public const string SectionName = "Loans";

    /// <summary>
    /// Cada cuánto despierta el worker. Corre más de una vez al día a propósito: el trabajo del día se deduplica por
    /// <c>tenant_runs</c>, y un tick frecuente arranca pronto después de las 00:00 locales de cada huso.
    /// </summary>
    public int SweepMinutes { get; init; } = 15;

    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (SweepMinutes <= 0)
        {
            failures.Add("Loans:SweepMinutes must be greater than zero.");
        }

        return failures;
    }
}
