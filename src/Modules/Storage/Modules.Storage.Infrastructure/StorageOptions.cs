namespace Modules.Storage.Infrastructure;

// Binding fuertemente tipado de la sección "Storage" de appsettings. Cloudflare R2 es el
// único proveedor de runtime (ADR 0020). Las credenciales son secretos por entorno.
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public int PresignedUrlMinutes { get; init; } = 5;

    // Vida del enlace de descarga de un reporte generado. Es propia y no PresignedUrlMinutes
    // porque los públicos son opuestos: aquellas URLs las consume un navegador que ya está en
    // pantalla, y ésta viaja por correo hasta una bandeja de entrada que puede tardar horas en
    // abrirse. El techo lo pone SigV4, que no firma más de 7 días (168 h).
    public int ExportUrlHours { get; init; } = 24;

    public int StagingRetentionHours { get; init; } = 24;

    public int StagingCleanupMinutes { get; init; } = 60;

    public PaymentProofOrphanCleanupOptions PaymentProofOrphanCleanup { get; init; } = new();

    public R2Options R2 { get; init; } = new();

    public ClamAvOptions ClamAv { get; init; } = new();
}

// Spec 2026-09-16, D12: la reconciliación de payment-proofs/ en el bucket público. Borra objetos cuya
// URL puede estar en un Excel ya enviado, así que arranca en modo solo-registrar y DryRun se apaga a
// mano, después de revisar en los logs de producción que lo marcado como huérfano realmente lo es.
public sealed class PaymentProofOrphanCleanupOptions
{
    // Al adjuntar se copia antes de guardar el pedido (D9): un objeto recién copiado todavía no tiene
    // quién lo referencie, y no es huérfano.
    public int MinimumAgeHours { get; init; } = 24;

    public int IntervalHours { get; init; } = 24;

    public bool DryRun { get; init; } = true;
}

public sealed class ClamAvOptions
{
    public bool Enabled { get; init; }

    public string Host { get; init; } = "clamav";

    public int Port { get; init; } = 3310;

    public int TimeoutSeconds { get; init; } = 30;
}

public sealed class R2Options
{
    public string AccountId { get; init; } = string.Empty;

    public string AccessKeyId { get; init; } = string.Empty;

    public string SecretAccessKey { get; init; } = string.Empty;

    public string Bucket { get; init; } = string.Empty;

    public string PublicBucket { get; init; } = string.Empty;

    // Dominio público propio u origen R2.dev, por ejemplo https://assets.example.com.
    public string PublicBaseUrl { get; init; } = string.Empty;

    // Opcional; cuando está vacío se deriva de AccountId como
    // https://{AccountId}.r2.cloudflarestorage.com.
    public string Endpoint { get; init; } = string.Empty;
}
