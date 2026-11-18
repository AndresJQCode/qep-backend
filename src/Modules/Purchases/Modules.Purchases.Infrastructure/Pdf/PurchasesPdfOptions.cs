namespace Modules.Purchases.Infrastructure.Pdf;

/// <summary>
/// Cómo llega Compras a <c>qcode-pdf</c> (copia de <c>SupportPdfOptions</c>). Sección propia, para que el módulo no dependa de la
/// configuración de otro, pero la clave es una sola para todo el backend de QEP (decisión del owner, 2026-11-17): todas las claves
/// viven en el mismo pod, así que separarlas no aísla nada y cada variable más es otra forma de que el pod no arranque. En producción
/// la URL y la clave salen de los mismos tokens que cotizaciones (<c>QUOTATIONS_PDF_BASE_URL</c> en el ConfigMap,
/// <c>QUOTATIONS_PDF_API_KEY</c> en el Secret, grupo <c>Backend-prod</c>).
/// </summary>
public sealed class PurchasesPdfOptions
{
    public const string SectionName = "Purchases:Pdf";

    public string BaseUrl { get; init; } = "https://qcode-pdf.qcode.co";

    public string ApiKey { get; init; } = string.Empty;

    /// <summary><c>qcode-pdf</c> corta la compilación a los 15 s; sin tope propio, el <c>HttpClient</c> esperaría 100.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}
