namespace Bootstrapper.Seeding;

/// <summary>
/// La carga sintética para medir la exportación (spec 2026-09-13, A10): un tenant propio,
/// <c>carga-export</c>, con <see cref="Quotations"/> cotizaciones, sembrado por ExportLoadSeedWorker
/// después del arranque. No depende de <see cref="SeedOptions.Enabled"/>: se prende sólo para medir y
/// se apaga después.
/// </summary>
public sealed class ExportLoadSeedOptions
{
    /// <summary>Cuántas cotizaciones sembrar. 0 —el valor por defecto— la apaga.</summary>
    public int Quotations { get; set; }
}
