using System.Net;
using System.Net.Http.Json;
using Modules.Tenancy.Application;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El PDF de la cotización, de punta a punta con el logo del tenant (spec 2026-09-19): exportar,
/// asignar un logo por Tenancy, y volver a exportar. `QuotationExportApiTests.cs` en esta misma
/// carpeta prueba la exportación **asíncrona a Excel** (`POST /export`), un endpoint distinto sin
/// relación con este — este archivo es el primero que ejercita `POST /{quotationId}/pdf` por
/// integración (antes sólo tenía cobertura unitaria, `ExportQuotationPdfHandlerTests`).
/// </summary>
public sealed class QuotationPdfExportApiTests
{
    [Fact]
    public async Task ExportAfterAssigningALogoRegenerates()
    {
        await using var database = await StartDatabaseAsync();
        // `publicPaymentProofLinks: true` -- el mismo interruptor que usan los comprobantes
        // públicos configura acá el bucket público del logo (`FilePublication.PublishAsync`
        // rechaza con `storage.public.not_configured` si `IPublicObjectStorage.IsConfigured` es
        // `false`, que es el default de esta factoría).
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, TenancyPermissions.SettingsRead, TenancyPermissions.SettingsUpdate]);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, customerId);

        var firstExport = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstExport.StatusCode);
        var first = await firstExport.Content.ReadFromJsonAsync<ExportDto>(TestContext.Current.CancellationToken);

        var fileId = await UploadAndCompleteTenantFileAsync(client, tenantId, factory);
        var etag = await GetSettingsEtagAsync(client, tenantId);
        var putLogo = await client.SendAsync(
            PutLogoRequest(tenantId, etag, fileId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, putLogo.StatusCode);

        var secondExport = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, secondExport.StatusCode);
        var second = await secondExport.Content.ReadFromJsonAsync<ExportDto>(TestContext.Current.CancellationToken);

        Assert.NotEqual(first!.GeneratedAt, second!.GeneratedAt);
    }

    /// <summary>Sube el logo del tenant (mismo pipeline de Storage que ya ejerce
    /// <c>CreateAvailableFileAsync</c>, pero con <c>ownerId == tenantId</c>: <c>TenantLogoStorage</c>
    /// exige esa igualdad para <c>FileOwnerType.Tenant</c>, si no responde
    /// <c>tenancy.logo.not_owned</c>). El contenido es un PNG real: uno de ceros llega quarantined
    /// por el decode de ImageSharp en <c>CompleteUploadHandler</c> (mismo hallazgo que
    /// <c>TenantLogoApiTests</c>).</summary>
    private static async Task<Guid> UploadAndCompleteTenantFileAsync(
        HttpClient client, Guid tenantId, QepApiFactory factory)
    {
        using var image = new Image<Rgba32>(16, 16, Color.CornflowerBlue);
        await using var png = new MemoryStream();
        await image.SaveAsPngAsync(png, TestContext.Current.CancellationToken);
        var content = png.ToArray();

        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = tenantId,
                ownerType = "Tenant",
                name = "logo.png",
                mimeType = "image/png",
                sizeBytes = content.Length,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = (await sessionResponse.Content.ReadFromJsonAsync<UploadSessionResponseDto>(
            TestContext.Current.CancellationToken))!;

        factory.ObjectStorage.Upload(session.StorageKey, content);

        var completeResponse = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{session.FileResourceId}/complete",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        return session.FileResourceId;
    }

    private static async Task<string> GetSettingsEtagAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/settings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private static HttpRequestMessage PutLogoRequest(Guid tenantId, string etag, Guid fileId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/tenants/{tenantId}/settings/logo")
        {
            Content = JsonContent.Create(new { fileId }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    private sealed record ExportDto(string Url, DateTimeOffset GeneratedAt);
}
