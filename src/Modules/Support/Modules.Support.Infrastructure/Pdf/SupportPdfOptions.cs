namespace Modules.Support.Infrastructure.Pdf;

/// <summary>
/// Fase 4b (H12, spec §13.5): cómo llega Soporte a <c>qcode-pdf</c>. Sección propia, para que el módulo no dependa de la configuración
/// de cotizaciones, pero la clave es una sola para todo el backend de QEP (decisión del owner, 2026-11-17, que reemplaza la clave propia
/// de §13.5): todas viven en el mismo pod, así que separarlas no aísla nada. En producción la URL y la clave salen de los mismos tokens
/// que cotizaciones (<c>QUOTATIONS_PDF_BASE_URL</c> en el ConfigMap, <c>QUOTATIONS_PDF_API_KEY</c> en el Secret, grupo <c>Backend-prod</c>).
/// </summary>
public sealed class SupportPdfOptions
{
    public const string SectionName = "Support:Pdf";

    public string BaseUrl { get; init; } = "https://qcode-pdf.qcode.co";

    public string ApiKey { get; init; } = string.Empty;

    /// <summary><c>qcode-pdf</c> corta la compilación a los 15 s; sin tope propio, el <c>HttpClient</c> esperaría 100.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}
