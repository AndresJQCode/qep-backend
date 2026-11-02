using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>RF-004: las etiquetas de ubicación por HTTP. El renderer se sustituye por uno falso: la prueba ejerce la ruta, el permiso
/// y la pertenencia de los ids a la bodega, no el servicio <c>qcode-pdf</c> (que pide una clave real).</summary>
public sealed class LocationLabelApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeLabelRenderer : IWmsLabelRenderer
    {
        public IReadOnlyList<LocationLabel>? Received { get; private set; }

        public Task<byte[]> RenderLocationLabelsAsync(IReadOnlyList<LocationLabel> labels, CancellationToken ct)
        {
            Received = labels;
            return Task.FromResult(Encoding.ASCII.GetBytes("%PDF-1.7"));
        }
    }

    private static HttpRequestMessage Write(string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Client", "web");
        return request;
    }

    private static async Task<Guid> CreateWarehouseAsync(HttpClient client, string code)
    {
        var response = await client.SendAsync(Write($"{WmsUrl()}/warehouses", new { code, name = "Bodega" }), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Dictionary<string, Guid>> LocationIdsAsync(HttpClient client, Guid warehouseId)
    {
        var response = await client.GetAsync($"{WmsUrl()}/warehouses/{warehouseId}/locations", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("code").GetString()!, item => item.GetProperty("id").GetGuid());
    }

    private static string LabelsUrl(Guid warehouseId) => $"{WmsUrl()}/warehouses/{warehouseId}/locations/labels";

    [Fact]
    public async Task LabelsForTheWarehousesLocationsComeBackAsAPdf()
    {
        var renderer = new FakeLabelRenderer();
        var (database, factory) = await OpenAsync(configureServices: services =>
        {
            services.RemoveAll<IWmsLabelRenderer>();
            services.AddSingleton<IWmsLabelRenderer>(renderer);
        });
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client, "BOG");
        var ids = await LocationIdsAsync(client, warehouseId);

        var response = await client.SendAsync(
            Write(LabelsUrl(warehouseId), new { locationIds = new[] { ids["RECEIVING"], ids["REC-01"] } }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync(Ct)));
        Assert.Equal("etiquetas-BOG.pdf", response.Content.Headers.ContentDisposition?.FileName);
        Assert.NotNull(renderer.Received);
        Assert.Equal(
            ["BOG/RECEIVING", "BOG/RECEIVING/REC-01"],
            renderer.Received.Select(label => label.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.All(renderer.Received, label => Assert.Equal("BOG", label.WarehouseCode));
    }

    [Fact]
    public async Task ALocationFromAnotherWarehouseIs422NotFound()
    {
        var renderer = new FakeLabelRenderer();
        var (database, factory) = await OpenAsync(configureServices: services =>
        {
            services.RemoveAll<IWmsLabelRenderer>();
            services.AddSingleton<IWmsLabelRenderer>(renderer);
        });
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var first = await CreateWarehouseAsync(client, "BOG");
        var second = await CreateWarehouseAsync(client, "MED");
        var foreign = (await LocationIdsAsync(client, second))["REC-01"];

        var response = await client.SendAsync(Write(LabelsUrl(first), new { locationIds = new[] { foreign } }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.not_found", await CodeOfAsync(response));
        Assert.Null(renderer.Received);
    }

    [Fact]
    public async Task AVirtualLocationHasNoLabelAndIs422VirtualImmutable()
    {
        var renderer = new FakeLabelRenderer();
        var (database, factory) = await OpenAsync(configureServices: services =>
        {
            services.RemoveAll<IWmsLabelRenderer>();
            services.AddSingleton<IWmsLabelRenderer>(renderer);
        });
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client, "BOG");
        var ids = await LocationIdsAsync(client, warehouseId);

        // Una virtual no se escanea (ToScannable la excluye): su etiqueta impresa nunca resolvería en la app.
        var response = await client.SendAsync(
            Write(LabelsUrl(warehouseId), new { locationIds = new[] { ids["REC-01"], ids["SUPPLIER"] } }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.virtual_immutable", await CodeOfAsync(response));
        Assert.Null(renderer.Received);
    }

    [Fact]
    public async Task AnEmptyListIs422ValidationFailed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client, "BOG");

        var response = await client.SendAsync(Write(LabelsUrl(warehouseId), new { locationIds = Array.Empty<Guid>() }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task WithoutTheManagePermissionIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(manager, "BOG");
        var ids = await LocationIdsAsync(manager, warehouseId);
        using var reader = CreateReader(factory);

        var response = await reader.SendAsync(Write(LabelsUrl(warehouseId), new { locationIds = new[] { ids["REC-01"] } }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
