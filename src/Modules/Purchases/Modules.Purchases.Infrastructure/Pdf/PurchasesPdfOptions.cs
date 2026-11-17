namespace Modules.Purchases.Infrastructure.Pdf;

/// <summary>
/// Cómo llega Compras a <c>qcode-pdf</c> (copia de <c>SupportPdfOptions</c>). Sección y clave propias, nunca <c>Quotations:Pdf</c> ni
/// <c>Support:Pdf</c>: el servicio resuelve el cliente por el hash de la clave, así que compartirla mezclaría el tráfico en sus logs, y
/// rotar una obligaría a tocar las otras. La URL va en el ConfigMap; la clave, en el Secret (<c>PURCHASES_PDF_API_KEY</c> del grupo
/// <c>Backend-prod</c>).
/// </summary>
public sealed class PurchasesPdfOptions
{
    public const string SectionName = "Purchases:Pdf";

    public string BaseUrl { get; init; } = "https://qcode-pdf.qcode.co";

    public string ApiKey { get; init; } = string.Empty;

    /// <summary><c>qcode-pdf</c> corta la compilación a los 15 s; sin tope propio, el <c>HttpClient</c> esperaría 100.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}
