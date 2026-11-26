using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Store.Application;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>El pedido que crea el checkout aparece en la pantalla de pedidos con su origen. Necesitan Docker.</summary>
public sealed class StoreOrderDetailApiTests
{
    [Fact]
    public async Task TheOrdersListFiltersByStoreSource()
    {
        await using var fixture = await PublicStoreFixture.OpenAsync(configureServices: services =>
        {
            services.RemoveAll<ICaptchaVerifier>();
            services.AddSingleton<ICaptchaVerifier>(new FakeCaptchaVerifier());
        });
        var orderId = await SubmitAsync(fixture);
        using var reader = fixture.Settings.ClientFor(TenantId, "quotations.order.read");

        var store = await ListAsync(reader, "Store");
        var manual = await ListAsync(reader, "Manual");

        var item = Assert.Single(store["items"]!.AsArray())!;
        Assert.Equal(orderId, (Guid)item["id"]!);
        Assert.Equal("Store", (string)item["source"]!);
        Assert.Empty(manual["items"]!.AsArray());
    }

    [Fact]
    public async Task TheStoreOrderDetailCarriesNoStoreRequest()
    {
        // La bandeja se quitó (spec 2026-11-09): el detalle de un pedido de la tienda es el de cualquier pedido.
        await using var fixture = await PublicStoreFixture.OpenAsync(configureServices: services =>
        {
            services.RemoveAll<ICaptchaVerifier>();
            services.AddSingleton<ICaptchaVerifier>(new FakeCaptchaVerifier());
        });
        var orderId = await SubmitAsync(fixture);
        using var reader = fixture.Settings.ClientFor(TenantId, "quotations.order.read");

        var response = await reader.GetAsync(
            $"/api/v1/tenants/{TenantId}/orders/{orderId}", TestContext.Current.CancellationToken);
        var detail = (await PublicStoreFixture.JsonAsync(response)).AsObject();

        Assert.True(response.IsSuccessStatusCode, detail.ToJsonString());
        Assert.Equal("Store", (string)detail["order"]!["source"]!);
        Assert.False(detail.ContainsKey("storeRequest"));
    }

    // Un envío público mínimo: la primera ciudad sembrada de DIVIPOLA y un producto visible.
    private static async Task<Guid> SubmitAsync(PublicStoreFixture fixture)
    {
        var (_, variant) = await fixture.CreateProductAsync("Camiseta", "CAM-S", PublicStoreFixture.Prices(10_000m));
        var (department, city) = await FirstCityAsync(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicStoreFixture.Url("/order-requests"))
        {
            Content = JsonContent.Create(new
            {
                clientRequestId = Guid.CreateVersion7(),
                turnstileToken = "token",
                currency = "COP",
                billing = new
                {
                    name = "Ana Pérez",
                    identificationType = "CC",
                    identificationNumber = "123456",
                    phone = "3001234567",
                    email = "ana@test.co",
                    address = "Calle 1 # 2-3",
                    country = "CO",
                    departmentId = department,
                    cityId = city,
                    cityName = (string?)null,
                },
                shipping = (object?)null,
                note = (string?)null,
                acceptsDataPolicy = true,
                lines = new[] { new { variantId = variant, quantity = 1 } },
            }),
        };
        request.Headers.Add("X-Qep-Visitor-Ip", "203.0.113.10");
        var response = await fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, json.ToJsonString());
        return (Guid)json["id"]!;
    }

    private static async Task<(Guid Department, Guid City)> FirstCityAsync(PublicStoreFixture fixture)
    {
        var departments = await fixture.GetJsonAsync("/geography/departments");
        foreach (var department in departments["items"]!.AsArray())
        {
            var departmentId = (Guid)department!["id"]!;
            var cities = await fixture.GetJsonAsync($"/geography/departments/{departmentId}/cities");
            if (cities["items"]!.AsArray().FirstOrDefault() is { } first)
            {
                return (departmentId, (Guid)first["id"]!);
            }
        }

        throw new InvalidOperationException("The geography seed has no city.");
    }

    private static async Task<JsonNode> ListAsync(HttpClient reader, string source)
    {
        var response = await reader.GetAsync(
            $"/api/v1/tenants/{TenantId}/orders?source={source}", TestContext.Current.CancellationToken);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.IsSuccessStatusCode, json.ToJsonString());
        return json;
    }
}
