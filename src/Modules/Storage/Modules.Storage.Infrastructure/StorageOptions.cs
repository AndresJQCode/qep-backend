namespace Modules.Storage.Infrastructure;

// Binding fuertemente tipado de la sección "Storage" de appsettings. Cloudflare R2 es el
// único proveedor de runtime (ADR 0020). Las credenciales son secretos por entorno.
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public int PresignedUrlMinutes { get; init; } = 5;

    public int StagingRetentionHours { get; init; } = 24;

    public int StagingCleanupMinutes { get; init; } = 60;

    public R2Options R2 { get; init; } = new();

    public ClamAvOptions ClamAv { get; init; } = new();
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
