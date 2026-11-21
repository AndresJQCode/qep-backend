using System.Collections.Concurrent;
using Modules.Hr.Application;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// El puerto del PDF en memoria (calco de <c>FakeTemplateImageStore</c>): las pruebas de Hr no dependen de Storage ni de R2. Un id no
/// sembrado completa como <see cref="HrDocumentState.NotFound"/>; la URL de descarga es <c>https://files.test/{fileId}</c>.
/// </summary>
internal sealed class FakeHrDocumentStore : IHrDocumentStore
{
    private sealed record Seeded(HrDocumentState State, string FileName);

    private readonly ConcurrentDictionary<Guid, Seeded> _files = new();

    /// <summary>Cada sesión que se abrió: lo que el caso de uso rechaza sin llamar al puerto no queda acá.</summary>
    public ConcurrentQueue<(Guid TenantId, string FileName, string MimeType, long SizeBytes)> Opens { get; } = new();

    /// <summary>Deja un archivo en el estado que tendría al completar la subida.</summary>
    public Guid Seed(Guid fileId, HrDocumentState state, string fileName = "contrato.pdf")
    {
        _files[fileId] = new Seeded(state, fileName);
        return fileId;
    }

    public Guid Seed(HrDocumentState state = HrDocumentState.Available, string fileName = "contrato.pdf") =>
        Seed(Guid.CreateVersion7(), state, fileName);

    public Task<HrUploadSession> OpenUploadAsync(
        Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken)
    {
        Opens.Enqueue((tenantId, fileName, mimeType, sizeBytes));
        var fileId = Guid.CreateVersion7();
        return Task.FromResult(new HrUploadSession(
            fileId,
            new Uri($"https://r2.test/staging/{fileId:N}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = mimeType, ["If-None-Match"] = "*" }));
    }

    public Task<HrDocumentFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult(_files.TryGetValue(fileId, out var file)
            ? new HrDocumentFile(file.State, file.State is HrDocumentState.Available ? file.FileName : null)
            : new HrDocumentFile(HrDocumentState.NotFound, null));

    public Task<Uri?> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult<Uri?>(_files.TryGetValue(fileId, out var file) && file.State is HrDocumentState.Available
            ? new Uri($"https://files.test/{fileId}")
            : null);
}
