using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// D9: el archivo va bajo `exports/` —el prefijo de la regla de lifecycle que ya existe en el
/// bucket (D13)— con el id del job en la clave. Un reintento pisa el mismo objeto en vez de dejar
/// basura, y el enlace firmado vence a las `Storage:ExportUrlHours` horas, la misma opción que el
/// export de clientes.
/// </summary>
public sealed class ExportFileStorageTests
{
    [Fact]
    public async Task UploadsUnderTheExportsPrefixWithTheJobIdAndSignsForExportUrlHours()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        // 48 y no el default de 24: si el adaptador fijara la vigencia a mano en vez de leer
        // Storage:ExportUrlHours, esta prueba lo vería.
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseSetting("Storage:ExportUrlHours", "48"));
        var tenantId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        byte[] content = [0x50, 0x4B, 0x03, 0x04];
        var path = await WriteTempFileAsync(content);

        try
        {
            var upload = await UploadAsync(factory, tenantId, jobId, path);

            // Bajo `exports/`: fuera de ese prefijo la regla `expire-exports` no lo ve y el objeto
            // queda para siempre (README § Reportes exportados).
            Assert.StartsWith("https://r2.test/exports/", upload.DownloadUrl, StringComparison.Ordinal);
            var key = $"exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx";
            Assert.Equal($"https://r2.test/{key}", upload.DownloadUrl);
            Assert.Equal(content, await baseFactory.ObjectStorage.DownloadAsync(key, TestContext.Current.CancellationToken));
            Assert.InRange(
                upload.ExpiresAt,
                DateTimeOffset.UtcNow.AddHours(47),
                DateTimeOffset.UtcNow.AddHours(49));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ARetryOfTheSameJobOverwritesTheSameObject()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var first = await WriteTempFileAsync([0x01]);
        byte[] retried = [0x02, 0x03];
        var second = await WriteTempFileAsync(retried);

        try
        {
            var firstUpload = await UploadAsync(factory, tenantId, jobId, first);
            var secondUpload = await UploadAsync(factory, tenantId, jobId, second);

            Assert.Equal(firstUpload.DownloadUrl, secondUpload.DownloadUrl);
            Assert.Equal(
                retried,
                await factory.ObjectStorage.DownloadAsync(
                    $"exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx", TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    private static async Task<ExportFileUpload> UploadAsync(
        WebApplicationFactory<Program> factory, Guid tenantId, Guid jobId, string path)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExportFileStorage>().UploadAsync(
            tenantId, jobId, "cotizaciones-2026-09-12-1530.xlsx", path, TestContext.Current.CancellationToken);
    }

    private static async Task<string> WriteTempFileAsync(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qep-storage-test-{Guid.NewGuid():N}.xlsx");
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
