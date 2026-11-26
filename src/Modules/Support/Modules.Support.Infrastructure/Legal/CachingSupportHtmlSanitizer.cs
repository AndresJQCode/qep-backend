using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Legal;

/// <summary>
/// The sanitizer's output cached by the SHA-256 of its input (final review S1). Sanitizing is a pure function, and the portal sanitizes
/// the same three stored documents on every <c>GET /form</c> and every filing, and the console every time it reads one. Keyed by the
/// content and not by the version id: a row changed by hand has another hash, so it is sanitized again instead of serving what the
/// cache saw before (the reason <c>SupportServedLegal</c> re-sanitizes at all). A rejection is not cached: it throws every time.
/// <para>
/// Bounded by characters, not entries: a document holds up to 60 000, so a cache of entries could grow to hundreds of megabytes. Each
/// pod has its own (<c>MultiReplicaInventory</c>, <c>PerPodByDesign</c>): two pods give the same output for the same input.
/// </para>
/// </summary>
internal sealed class CachingSupportHtmlSanitizer(ISupportHtmlSanitizer inner) : ISupportHtmlSanitizer, IDisposable
{
    /// <summary>About 16 MB of UTF-16 between key and output: dozens of tenants' three documents at their largest.</summary>
    internal const long SizeLimitChars = 8_000_000;

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = SizeLimitChars });

    public SanitizedHtml Sanitize(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
        if (cache.TryGetValue(key, out SanitizedHtml? cached) && cached is not null)
        {
            return cached;
        }

        var sanitized = inner.Sanitize(html);
        cache.Set(key, sanitized, new MemoryCacheEntryOptions { Size = sanitized.Html.Length + key.Length });
        return sanitized;
    }

    public void Dispose() => cache.Dispose();
}
