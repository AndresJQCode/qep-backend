using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>El staging del adjunto del asesor en memoria. La URL firmada es determinista (<see cref="UrlFor"/>) y la expiración
/// es la hora de la prueba más cinco minutos.</summary>
internal sealed class FakeUploadStaging : IMessagingUploadStaging
{
    private static readonly DateTimeOffset Base = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);

    public List<(string Key, string ContentType)> Signed { get; } = [];

    public static Uri UrlFor(string key) => new($"https://r2.test/{key}");

    public Task<MessagingUploadUrl> CreateUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken)
    {
        Signed.Add((key, contentType));
        return Task.FromResult(new MessagingUploadUrl(UrlFor(key), Base.AddMinutes(5)));
    }

    public Task<StagedUpload?> StatAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task PromoteAsync(string stagingKey, string finalKey, string eTag, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
