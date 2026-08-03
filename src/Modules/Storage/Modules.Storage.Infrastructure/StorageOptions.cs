namespace Modules.Storage.Infrastructure;

// Strongly-typed binding of the "Storage" appsettings section. Cloudflare R2 is the only
// runtime provider (ADR 0020). Credentials are per-environment secrets, never committed.
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

    // Public custom domain or R2.dev origin, e.g. https://assets.example.com.
    public string PublicBaseUrl { get; init; } = string.Empty;

    // Optional; when empty it is derived from AccountId as
    // https://{AccountId}.r2.cloudflarestorage.com.
    public string Endpoint { get; init; } = string.Empty;
}
