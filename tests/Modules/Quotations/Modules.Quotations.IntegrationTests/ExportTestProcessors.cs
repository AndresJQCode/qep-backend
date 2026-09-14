using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.IntegrationTests;

// Procesadores de mentira para probar la cola y el runner contra Postgres sin armar un Excel:
// lo que se verifica acá es la toma, el cierre y los eventos. El Excel real lo cubren
// QuotationExportApiTests y OrderExportApiTests.

internal sealed class SucceedingExportProcessor(ExportJobKind kind) : IExportJobProcessor
{
    public ExportJobKind Kind { get; } = kind;

    public Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken) =>
        Task.FromResult(new ExportJobResult(
            "cotizaciones-2026-09-12-1530.xlsx",
            3,
            $"https://r2.test/exports/tenants/{job.TenantId:N}/jobs/{job.Id:N}.xlsx",
            DateTimeOffset.UtcNow.AddHours(24)));
}

internal sealed class FailingExportProcessor(ExportJobKind kind, Exception failure) : IExportJobProcessor
{
    public ExportJobKind Kind { get; } = kind;

    public Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken) =>
        Task.FromException<ExportJobResult>(failure);
}

/// <summary>Deja meter una acción a mitad del procesamiento (fix round 1, hallazgo del revisor
/// sobre Task 4): <see cref="OnProcessing"/> se fija después de construir la factory, así que
/// puede cerrar sobre ella para forzar una carrera determinística —vencer el lease y reclamarlo
/// desde otro scope— sin ningún sleep. Termina como <see cref="SucceedingExportProcessor"/> para
/// que el runner llegue al mismo camino de cierre y sea ese guardado el que choque con el
/// intento ya adelantado por la carrera.</summary>
internal sealed class CallbackExportProcessor(ExportJobKind kind) : IExportJobProcessor
{
    public ExportJobKind Kind { get; } = kind;

    public Func<ExportJob, CancellationToken, Task>? OnProcessing { get; set; }

    public async Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        if (OnProcessing is not null)
        {
            await OnProcessing(job, cancellationToken);
        }

        return new ExportJobResult(
            "cotizaciones-2026-09-12-1530.xlsx",
            3,
            $"https://r2.test/exports/tenants/{job.TenantId:N}/jobs/{job.Id:N}.xlsx",
            DateTimeOffset.UtcNow.AddHours(24));
    }
}
