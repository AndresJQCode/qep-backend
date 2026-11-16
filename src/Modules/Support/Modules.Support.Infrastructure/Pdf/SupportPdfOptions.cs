namespace Modules.Support.Infrastructure.Pdf;

/// <summary>
/// Fase 4b (H12, spec §13.5): cómo llega Soporte a <c>qcode-pdf</c>. Sección y clave propias, nunca <c>Quotations:Pdf</c>: el servicio
/// resuelve el cliente por el hash de la clave, así que compartirla mezclaría el tráfico de los dos en sus logs, y rotar una obligaría a
/// tocar la otra. La URL va en el ConfigMap; la clave, en el Secret (<c>SUPPORT_PDF_API_KEY</c> del grupo <c>Backend-prod</c>).
/// </summary>
public sealed class SupportPdfOptions
{
    public const string SectionName = "Support:Pdf";

    public string BaseUrl { get; init; } = "https://qcode-pdf.qcode.co";

    public string ApiKey { get; init; } = string.Empty;

    /// <summary><c>qcode-pdf</c> corta la compilación a los 15 s; sin tope propio, el <c>HttpClient</c> esperaría 100.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}
