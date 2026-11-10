using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Buyers.Application;
using Modules.Buyers.Infrastructure.Signup;
using Modules.Store.Application;
using Npgsql;
using static Modules.Store.IntegrationTests.PublicStoreFixture;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// La tienda publicada para los pedidos del comprador: un producto (Camiseta a 10.000 COP), una ciudad para la facturación, quien
/// atiende en la consola y el checkout real para crear pedidos. Los workers de Buyers están apagados: las pruebas drenan a mano.
/// </summary>
internal sealed class BuyerOrdersRig : IAsyncDisposable
{
    public const string ConsolePermissions = "quotations.order.manage,quotations.order.read";

    private static int _ipCounter;

    private BuyerOrdersRig(PublicStoreFixture fixture, HttpClient console, Guid variant, Guid department, Guid city)
    {
        Fixture = fixture;
        Console = console;
        Variant = variant;
        Department = department;
        City = city;
    }

    public PublicStoreFixture Fixture { get; }

    /// <summary>Quien atiende en la consola: el sujeto del harness, con una membresía activa.</summary>
    public HttpClient Console { get; }

    public Guid Variant { get; }

    public Guid Department { get; }

    public Guid City { get; }

    public QepApiFactory Factory => Fixture.Settings.Factory;

    public static string NextIp() => $"192.0.2.{(Interlocked.Increment(ref _ipCounter) % 250) + 1}";

    public static async Task<BuyerOrdersRig> OpenAsync(Action<IServiceCollection>? configureServices = null)
    {
        var fixture = await PublicStoreFixture.OpenAsync(
            configureServices: services =>
            {
                services.RemoveAll<ICaptchaVerifier>();
                services.AddSingleton<ICaptchaVerifier>(new FakeCaptchaVerifier());
                services.RemoveAll<IBuyerCaptcha>();
                services.AddSingleton<IBuyerCaptcha>(new PassingBuyerCaptcha());
                // Los workers de Buyers sondean solos; las pruebas drenan a mano para ser deterministas.
                foreach (var descriptor in services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType?.Namespace?.StartsWith("Modules.Buyers", StringComparison.Ordinal) == true)
                    .ToList())
                {
                    services.Remove(descriptor);
                }

                configureServices?.Invoke(services);
            });
        var (_, variant) = await fixture.CreateProductAsync("Camiseta", "CAM-S", Prices(10_000m));

        var departments = await fixture.GetJsonAsync("/geography/departments");
        foreach (var department in departments["items"]!.AsArray())
        {
            var departmentId = (Guid)department!["id"]!;
            var cities = await fixture.GetJsonAsync($"/geography/departments/{departmentId}/cities");
            if (cities["items"]!.AsArray().FirstOrDefault() is not { } first)
            {
                continue;
            }

            await SeedCallerMembershipAsync(fixture.Settings.Factory, "Quien atiende");
            var console = CreateClient(fixture.Settings.Factory, SubjectId, StoreApiHarness.TenantId, ConsolePermissions);
            return new BuyerOrdersRig(fixture, console, variant, departmentId, (Guid)first["id"]!);
        }

        throw new InvalidOperationException("The geography seed has no city.");
    }

    // ---- Checkout -----------------------------------------------------------------------------------------------------------

    /// <summary>El cuerpo del checkout: 2 × Camiseta en COP, facturación en Colombia, sin envío. <paramref name="tweak"/> lo ajusta.</summary>
    public Dictionary<string, object?> CheckoutBody(string email = "ana@test.co", Action<Dictionary<string, object?>>? tweak = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["clientRequestId"] = Guid.CreateVersion7(),
            ["turnstileToken"] = "token",
            ["currency"] = "COP",
            ["billing"] = new Dictionary<string, object?>
            {
                ["name"] = "Ana Pérez",
                ["identificationType"] = "CC",
                ["identificationNumber"] = "123456",
                ["phone"] = "3001234567",
                ["email"] = email,
                ["address"] = "Calle 1 # 2-3",
                ["country"] = "CO",
                ["departmentId"] = Department,
                ["cityId"] = City,
                ["cityName"] = null,
            },
            ["shipping"] = null,
            ["note"] = null,
            ["acceptsDataPolicy"] = true,
            ["lines"] = new[] { new { variantId = Variant, quantity = 2 } },
        };
        tweak?.Invoke(body);
        return body;
    }

    public async Task<HttpResponseMessage> CheckoutAsync(object body, string? session = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/order-requests")) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Visitor-Ip", NextIp());
        if (session is not null)
        {
            request.Headers.Add("X-Qep-Buyer-Session", session);
        }

        return await Fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Un pedido por el checkout real; falla la prueba si no es 201. Devuelve el recibo.</summary>
    public async Task<JsonNode> CheckoutOkAsync(object? body = null, string? session = null)
    {
        var response = await CheckoutAsync(body ?? CheckoutBody(), session);
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, json.ToJsonString());
        return json;
    }

    /// <summary>Corre una pasada del worker de Buyers que consume las altas diferidas.</summary>
    public Task DrainBuyerSignupsAsync() =>
        ActivatorUtilities.CreateInstance<BuyerSignupRequestedWorker>(Factory.Services)
            .DrainAsync(TestContext.Current.CancellationToken);

    // ---- Consola ------------------------------------------------------------------------------------------------------------

    public static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == status, json.ToJsonString());
        Assert.Equal(code, (string)json["code"]!);
    }

    public async Task<JsonNode> OrderAsync(Guid orderId)
    {
        var response = await Console.GetAsync(
            $"/api/v1/tenants/{StoreApiHarness.TenantId}/orders/{orderId}", TestContext.Current.CancellationToken);
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
        return json;
    }

    // ---- Base ---------------------------------------------------------------------------------------------------------------

    public async Task<string?> TextAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = Command(connection, sql, parameters);
        return (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) switch
        {
            null or DBNull => null,
            var value => value.ToString(),
        };
    }

    public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Console.Dispose();
        await Fixture.DisposeAsync();
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private sealed class PassingBuyerCaptcha : IBuyerCaptcha
    {
        public Task<bool> VerifyAsync(string token, string visitorIp, string host, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
