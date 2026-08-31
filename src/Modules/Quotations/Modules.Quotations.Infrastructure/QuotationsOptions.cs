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

    public WhatsAppOptions WhatsApp { get; init; } = new();
}

/// <summary>
/// Credenciales de Zenvia (envío de la cotización por WhatsApp, ver `ZenviaWhatsAppSender`).
/// A diferencia de `ExpirationSweepMinutes`, deliberadamente **no** se valida con
/// `ValidateOnStart`: `QuotationsInfrastructureExtensions.AddWhatsAppSender` registra
/// `ZenviaWhatsAppSender` sólo cuando las tres están presentes y cae a `LogWhatsAppSender`
/// (no-op) en su ausencia, mismo criterio que `Notifications:EmailProvider` con Infobip — así
/// ningún `WebApplicationFactory` de las pruebas de integración necesita configurar esto para
/// que "Enviar" les siga funcionando. "De momento" a pedido del owner, se ajusta más adelante.
/// </summary>
public sealed class WhatsAppOptions
{
    public string ApiToken { get; init; } = string.Empty;

    public string FromNumber { get; init; } = string.Empty;

    /// <summary>Id de la plantilla en Zenvia. No es un secreto — es un id de configuración,
    /// mismo criterio que `Infobip:SenderEmail` — así que vive en `appsettings.json` en vez de
    /// en `user-secrets`, a menos que el owner decida lo contrario más adelante.</summary>
    public string TemplateId { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = "https://api.zenvia.com";
}
