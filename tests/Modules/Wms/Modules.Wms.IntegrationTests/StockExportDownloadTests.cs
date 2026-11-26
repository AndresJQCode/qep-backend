using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>
/// I-3: el <c>fileId</c> del export se consulta y se descarga por el flujo ya existente de Storage (§24.3, S14). Corre el adaptador real
/// (<c>WmsExportStorage</c>) y el endpoint real <c>POST /files/{id}/download-url</c>; lo único sustituido es el bucket.
/// </summary>
public sealed class StockExportDownloadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class InMemoryObjectStorage : IObjectStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);

        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(objects.TryGetValue(key, out var content)
                ? new StoredObject(content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)))
                : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken)
        {
            objects[destinationKey] = objects[sourceKey].ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => Task.FromResult(objects[key].ToArray());

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
        {
            objects[key] = content.ToArray();
            return Task.CompletedTask;
        }

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            objects[key] = buffer.ToArray();
        }

        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(objects.TryGetValue(key, out var bytes)
                ? new StoredObjectStream(new MemoryStream(bytes), "application/octet-stream", bytes.LongLength)
                : null);

        public bool Has(string key) => objects.ContainsKey(key);
    }

    private static async Task<(Testcontainers.PostgreSql.PostgreSqlContainer Database, QepApiFactory Factory, InMemoryObjectStorage Bucket, LedgerSetup S, StockExportAcceptedDto Export)>
        ExportedAsync()
    {
        var bucket = new InMemoryObjectStorage();
        var (database, factory) = await OpenAsync(configureServices: services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage>(bucket);
        });
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10m);
        var response = await s.Manager.PostAsync($"{WmsUrl()}/stock/export", content: null, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await BodyAsync(response));
        var export = (await response.Content.ReadFromJsonAsync<StockExportAcceptedDto>(Ct))!;
        return (database, factory, bucket, s, export);
    }

    private static Task<HttpResponseMessage> DownloadUrlAsync(HttpClient client, Guid fileId) =>
        client.PostAsync($"/api/v1/tenants/{TenantId}/files/{fileId}/download-url", content: null, Ct);

    [Fact]
    public async Task TheExportFileIdIsDownloadableThroughStorageAndBlockedWithTheModuleOff()
    {
        var (database, factory, bucket, _, export) = await ExportedAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateClient(factory, SubjectId, TenantId, "storage.file.read", WmsPermissions.StockExport);

        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                database,
                "SELECT count(*) FROM storage.file_resources WHERE id = @id AND owner_type = 'WmsExport' AND status = 'Available'",
                ("id", export.FileId)));
        Assert.True(bucket.Has($"exports/tenants/{Guid.Parse(TenantId):N}/wms/{export.FileId:N}.xlsx"));

        var ok = await DownloadUrlAsync(reader, export.FileId);

        Assert.True(ok.StatusCode == HttpStatusCode.OK, await BodyAsync(ok));
        Assert.Contains($"{export.FileId:N}.xlsx", await BodyAsync(ok), StringComparison.Ordinal);

        // S14: con wms apagado el archivo deja de poder descargarse, aunque el permiso de Storage sea núcleo.
        await ExecuteAsync(
            database,
            "UPDATE tenancy.tenant_modules SET status = 'inactive', status_changed_at = now() WHERE tenant_id = @t AND module_key = 'wms'",
            ("t", Guid.Parse(TenantId)));

        var blocked = await DownloadUrlAsync(reader, export.FileId);

        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", await CodeOfAsync(blocked));
    }

    // Storage no referencia a Wms: su tabla lleva el permiso como literal, y este es el único lugar que ve los dos lados.
    [Fact]
    public void StorageRequiresTheWmsExportPermissionByItsLiteral() =>
        Assert.Equal(WmsPermissions.StockExport, FileOwnerModules.RequiredPermissionOf(Modules.Storage.Domain.FileOwnerType.WmsExport));

    // I-2: storage.file.read es núcleo y lo tiene cualquier miembro del tenant; el export trae saldos de toda la bodega, así que
    // descargarlo o listarlo exige además wms.stock.export. Mismo tenant, rol sin ese permiso.
    [Fact]
    public async Task ASameTenantCallerWithoutTheExportPermissionCannotDownloadOrListTheExport()
    {
        var (database, factory, _, _, export) = await ExportedAsync();
        await using var _ = database;
        using var __ = factory;
        using var withPermission = CreateClient(factory, SubjectId, TenantId, "storage.file.read", WmsPermissions.StockExport);
        using var withoutPermission = CreateClient(factory, SubjectId, TenantId, "storage.file.read");

        var denied = await DownloadUrlAsync(withoutPermission, export.FileId);
        var listedWithout = await withoutPermission.GetAsync($"/api/v1/tenants/{TenantId}/files?pageSize=100", Ct);
        var listedWith = await withPermission.GetAsync($"/api/v1/tenants/{TenantId}/files?pageSize=100", Ct);
        var allowed = await DownloadUrlAsync(withPermission, export.FileId);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(denied));
        Assert.Equal(HttpStatusCode.OK, listedWithout.StatusCode);
        Assert.DoesNotContain(export.FileId.ToString(), await BodyAsync(listedWithout), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(export.FileId.ToString(), await BodyAsync(listedWith), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task AnExpiredExportIsAGracefulErrorAndNotADeadLink()
    {
        var (database, factory, _, _, export) = await ExportedAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateClient(factory, SubjectId, TenantId, "storage.file.read", WmsPermissions.StockExport);
        await ExecuteAsync(
            database, "UPDATE storage.file_resources SET created_at = now() - interval '3 days' WHERE id = @id", ("id", export.FileId));

        var response = await DownloadUrlAsync(reader, export.FileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("storage.file.expired", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnotherTenantCannotResolveTheExportFileId()
    {
        var (database, factory, _, _, export) = await ExportedAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, OtherTenantId);
        using var outsider = CreateClient(factory, OtherSubjectId, OtherTenantId, "storage.file.read", WmsPermissions.StockExport);

        var response = await outsider.PostAsync($"/api/v1/tenants/{OtherTenantId}/files/{export.FileId}/download-url", content: null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
