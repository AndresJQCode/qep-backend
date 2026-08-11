using Microsoft.Extensions.Options;

namespace Modules.Storage.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart). R2 es el único proveedor de runtime, así que
// todas las credenciales requeridas tienen que estar presentes en todos los entornos.
internal sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var failures = new List<string>();

        if (options.PresignedUrlMinutes <= 0)
        {
            failures.Add("Storage:PresignedUrlMinutes must be greater than zero.");
        }

        if (options.StagingRetentionHours <= 0)
        {
            failures.Add("Storage:StagingRetentionHours must be greater than zero.");
        }

        if (options.StagingCleanupMinutes <= 0)
        {
            failures.Add("Storage:StagingCleanupMinutes must be greater than zero.");
        }

        if (options.ClamAv.Enabled && string.IsNullOrWhiteSpace(options.ClamAv.Host))
        {
            failures.Add("Storage:ClamAv:Host is required when malware scanning is enabled.");
        }

        if (options.ClamAv.Port is < 1 or > 65535)
        {
            failures.Add("Storage:ClamAv:Port must be between 1 and 65535.");
        }

        if (options.ClamAv.TimeoutSeconds <= 0)
        {
            failures.Add("Storage:ClamAv:TimeoutSeconds must be greater than zero.");
        }

        var r2 = options.R2;
        if (string.IsNullOrWhiteSpace(r2.AccessKeyId))
        {
            failures.Add("Storage:R2:AccessKeyId is required.");
        }

        if (string.IsNullOrWhiteSpace(r2.SecretAccessKey))
        {
            failures.Add("Storage:R2:SecretAccessKey is required.");
        }

        if (string.IsNullOrWhiteSpace(r2.Bucket))
        {
            failures.Add("Storage:R2:Bucket is required.");
        }

        var hasPublicBucket = !string.IsNullOrWhiteSpace(r2.PublicBucket);
        var hasPublicBaseUrl = !string.IsNullOrWhiteSpace(r2.PublicBaseUrl);
        if (hasPublicBucket != hasPublicBaseUrl)
        {
            failures.Add("Storage:R2:PublicBucket and Storage:R2:PublicBaseUrl must be configured together.");
        }
        else if (hasPublicBaseUrl &&
                 (!Uri.TryCreate(r2.PublicBaseUrl, UriKind.Absolute, out var publicUri) ||
                  publicUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Storage:R2:PublicBaseUrl must be an absolute HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(r2.Endpoint) && string.IsNullOrWhiteSpace(r2.AccountId))
        {
            failures.Add("Storage:R2:Endpoint or Storage:R2:AccountId is required.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
