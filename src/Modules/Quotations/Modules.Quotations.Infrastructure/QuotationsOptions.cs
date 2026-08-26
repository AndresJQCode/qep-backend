namespace Modules.Quotations.Infrastructure;

// Binding fuertemente tipado de la sección "Quotations" de appsettings, mismo criterio que
// StorageOptions.
public sealed class QuotationsOptions
{
    public const string SectionName = "Quotations";

    /// <summary>Período del barrido de vencimiento (US-19). El documento habla de un job
    /// "diario"; se expone como intervalo configurable y no como cron fijo, mismo criterio que
    /// Storage:StagingCleanupMinutes — cada tick es una sola consulta sobre una columna indexada,
    /// así que revisarlo más seguido que una vez al día no tiene costo real y deja la cotización
    /// vencida visible antes.</summary>
    public int ExpirationSweepMinutes { get; init; } = 60;
}
