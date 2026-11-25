using System.Collections.Concurrent;
using Modules.Messaging.Application;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-11-17: el puerto de la imagen del encabezado en memoria, para que las pruebas de Messaging no dependan de Storage ni de R2.
/// Cada archivo recuerda su estado y si se publicó o se liberó; <see cref="PublishAsync"/> devuelve <c>https://public.test/&lt;fileId&gt;.png</c>.
/// <see cref="PublishFailure"/> y <see cref="ReleaseFailure"/> hacen fallar esas operaciones para probar la compensación.
/// </summary>
public sealed class FakeTemplateImageStore : IMessagingTemplateImageStore
{
    public sealed class StoredImage
    {
        public required Guid TenantId { get; init; }

        public TemplateImageState State { get; set; } = TemplateImageState.NotUploaded;

        public required string MimeType { get; set; }

        public required long Size { get; set; }

        public byte[] Bytes { get; set; } = [];

        /// <summary>Cuándo se abrió la sesión. El host de integración usa el reloj real, así que «recién subida» pasa la revisión de
        /// <c>TemplateHeaderImagePolicy.MaxUploadAge</c>.</summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public bool Published { get; set; }

        public bool Released { get; set; }
    }

    public ConcurrentDictionary<Guid, StoredImage> Files { get; } = new();

    /// <summary>Si no es <c>null</c>, <see cref="PublishAsync"/> la lanza.</summary>
    public Exception? PublishFailure { get; set; }

    /// <summary>Si no es <c>null</c>, <see cref="ReleaseAsync"/> la lanza.</summary>
    public Exception? ReleaseFailure { get; set; }

    /// <summary>Deja un archivo ya subido (por defecto <see cref="TemplateImageState.Available"/>), como si el navegador hubiera hecho el PUT.</summary>
    public Guid Seed(
        Guid tenantId, byte[] bytes, string mimeType = "image/png", TemplateImageState state = TemplateImageState.Available, long? size = null)
    {
        var fileId = Guid.CreateVersion7();
        Files[fileId] = new StoredImage
        {
            TenantId = tenantId,
            State = state,
            MimeType = mimeType,
            Size = size ?? bytes.LongLength,
            Bytes = bytes,
        };
        return fileId;
    }

    public Task<TemplateImageUploadSession> OpenUploadAsync(
        Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken)
    {
        var fileId = Guid.CreateVersion7();
        var trimmed = mimeType.Trim();
        Files[fileId] = new StoredImage { TenantId = tenantId, MimeType = trimmed, Size = sizeBytes };
        return Task.FromResult(new TemplateImageUploadSession(
            fileId,
            new Uri($"https://r2.test/staging/{fileId:N}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = trimmed, ["If-None-Match"] = "*" }));
    }

    public Task<TemplateImageFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(tenantId, fileId) is { Released: false } file
            ? new TemplateImageFile(file.State, file.State is TemplateImageState.Available ? file.MimeType : null, file.Size, file.CreatedAt)
            : new TemplateImageFile(TemplateImageState.NotFound, null, 0, default));

    public Task<byte[]> ReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Find(tenantId, fileId) is { State: TemplateImageState.Available, Released: false } file
            ? Task.FromResult(file.Bytes)
            : throw new InvalidOperationException($"The template image '{fileId}' is not available.");

    public Task<string> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        if (PublishFailure is { } failure)
        {
            throw failure;
        }

        var file = Find(tenantId, fileId) ?? throw new InvalidOperationException($"The template image '{fileId}' was not found.");
        file.Published = true;
        return Task.FromResult($"https://public.test/{fileId}.png");
    }

    public Task ReleaseAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        if (ReleaseFailure is { } failure)
        {
            throw failure;
        }

        if (Find(tenantId, fileId) is { } file)
        {
            file.Published = false;
            file.Released = true;
        }

        return Task.CompletedTask;
    }

    private StoredImage? Find(Guid tenantId, Guid fileId) =>
        Files.TryGetValue(fileId, out var file) && file.TenantId == tenantId ? file : null;
}
