using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modules.Store.Application;
using Npgsql;
using static Modules.Store.IntegrationTests.PublicStoreFixture;

namespace Modules.Store.IntegrationTests;

/// <summary>El verificador de Turnstile controlable por prueba: nunca sale a Cloudflare.</summary>
internal sealed class FakeCaptchaVerifier : ICaptchaVerifier
{
    private int _calls;

    public bool Passes { get; set; } = true;

    public int Calls => _calls;

    public string? LastHostname { get; private set; }

    public Task<bool> VerifyAsync(string token, string remoteIp, string expectedHostname, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        LastHostname = expectedHostname;
        return Task.FromResult(Passes);
    }
}

/// <summary>
/// El envío de solicitudes de punta a punta, con la API real, Postgres y las rutas públicas en su orden de middleware real (limitador,
/// después la clave). Necesitan Docker; se corren una vez en la tanda final.
/// </summary>
public sealed class PublicOrderRequestApiTests
{
    private static readonly string[] ColombiaAndSpain = ["CO", "ES"];

    private static int _ipCounter;

    private sealed record Rig(
        PublicStoreFixture Fixture, FakeCaptchaVerifier Captcha, Guid Variant, Guid Department, string DepartmentName, Guid City, string CityName)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    private static string NextIp() => $"203.0.113.{(Interlocked.Increment(ref _ipCounter) % 250) + 1}";

    private static async Task<Rig> OpenRigAsync(
        Action<StoreSettingsFixture, Dictionary<string, object?>>? tweak = null,
        Action<IServiceCollection>? configure = null,
        bool eurPrice = false,
        FakeCaptchaVerifier? captcha = null)
    {
        captcha ??= new FakeCaptchaVerifier();
        var fixture = await OpenAsync(
            tweak,
            configureServices: services =>
            {
                services.RemoveAll<ICaptchaVerifier>();
                services.AddSingleton<ICaptchaVerifier>(captcha);
                configure?.Invoke(services);
            });
        var (_, variant) = await fixture.CreateProductAsync("Camiseta", "CAM-S", Prices(10_000m, eurPrice ? 4m : null));

        var departments = await fixture.GetJsonAsync("/geography/departments");
        foreach (var department in departments["items"]!.AsArray())
        {
            var departmentId = (Guid)department!["id"]!;
            var cities = await fixture.GetJsonAsync($"/geography/departments/{departmentId}/cities");
            var first = cities["items"]!.AsArray().FirstOrDefault();
            if (first is not null)
            {
                return new Rig(fixture, captcha, variant, departmentId, (string)department["name"]!, (Guid)first["id"]!, (string)first["name"]!);
            }
        }

        throw new InvalidOperationException("The geography seed has no city.");
    }

    private static Dictionary<string, object?> Billing(Rig rig, Action<Dictionary<string, object?>>? tweak = null)
    {
        var billing = new Dictionary<string, object?>
        {
            ["name"] = "Ana Pérez",
            ["identificationType"] = "CC",
            ["identificationNumber"] = "123456",
            ["phone"] = "3001234567",
            ["email"] = "ana@test.co",
            ["address"] = "Calle 1 # 2-3",
            ["country"] = "CO",
            ["departmentId"] = rig.Department,
            ["cityId"] = rig.City,
            ["cityName"] = null,
        };
        tweak?.Invoke(billing);
        return billing;
    }

    private static Dictionary<string, object?> Shipping(Rig rig) => new()
    {
        ["recipientName"] = "Luis Gómez",
        ["phone"] = "3007654321",
        ["address"] = "Carrera 9 # 8-7",
        ["country"] = "CO",
        ["departmentId"] = rig.Department,
        ["cityId"] = rig.City,
        ["cityName"] = null,
    };

    private static Dictionary<string, object?> Body(Rig rig, Action<Dictionary<string, object?>>? tweak = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["clientRequestId"] = Guid.CreateVersion7(),
            ["turnstileToken"] = "token",
            ["currency"] = "COP",
            ["billing"] = Billing(rig),
            ["shipping"] = null,
            ["note"] = "Entregar en portería",
            ["acceptsDataPolicy"] = true,
            ["lines"] = new[] { new { variantId = rig.Variant, quantity = 2 } },
        };
        tweak?.Invoke(body);
        return body;
    }

    // Async a propósito: con el using, devolver la tarea sin esperarla liberaba el request (y su contenido) antes de que el servidor de
    // pruebas terminara de leerlo, y salía ObjectDisposedException.
    private static async Task<HttpResponseMessage> SubmitAsync(
        Rig rig, object body, string? visitorIp = "", HttpClient? client = null, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/order-requests")) { Content = JsonContent.Create(body) };
        var ip = visitorIp == string.Empty ? NextIp() : visitorIp;
        if (ip is not null)
        {
            request.Headers.Add("X-Qep-Visitor-Ip", ip);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await (client ?? rig.Fixture.Storefront).SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (string)(await PublicStoreFixture.JsonAsync(response))["code"]!;

    private static async Task AssertRejectedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
    }

    private static async Task<long> CountAsync(Rig rig, string table)
    {
        var connectionString = rig.Fixture.Settings.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static Task<long> RequestsAsync(Rig rig) => CountAsync(rig, "store.order_requests");

    private static async Task<Modules.Store.Domain.StoreOrderRequest> LoadAsync(Rig rig, Guid clientRequestId)
    {
        await using var scope = rig.Fixture.Settings.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStoreOrderRequestRepository>();
        return (await repository.FindByClientRequestIdAsync(Tenant, clientRequestId, TestContext.Current.CancellationToken))!;
    }

    private static async Task AssertNextNumberIsFirstAsync(Rig rig)
    {
        rig.Captcha.Passes = true;
        var response = await SubmitAsync(rig, Body(rig));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("T-0001", (string)(await PublicStoreFixture.JsonAsync(response))["number"]!);
    }

    [Fact]
    public async Task ASubmissionFreezesTheRepricedLinesAndConsent()
    {
        await using var rig = await OpenRigAsync();
        var body = Body(rig);
        var visitorIp = "198.51.100.23";

        var response = await SubmitAsync(rig, body, visitorIp);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await PublicStoreFixture.JsonAsync(response);
        Assert.Equal("T-0001", (string)receipt["number"]!);
        Assert.Equal(20_000m, (decimal)receipt["total"]!);
        Assert.Equal("COP", (string)receipt["currency"]!);
        var saved = await LoadAsync(rig, (Guid)body["clientRequestId"]!);
        var line = Assert.Single(saved.Lines);
        Assert.Equal(rig.Variant, line.VariantId);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(10_000m, line.UnitPrice);
        Assert.Equal(20_000m, line.LineTotal);
        Assert.Equal(20_000m, saved.Total);
        Assert.Equal(visitorIp, saved.Consent.Ip);
        Assert.Equal("https://demo.test/politica", saved.Consent.PolicyUrl);
        Assert.NotEqual(default, saved.Consent.PolicySetAt);
        Assert.Equal("+573001234567", saved.Billing.PhoneE164);
    }

    [Fact]
    public async Task AStoreWithoutPolicyFreezesTheConsentWithoutPolicy()
    {
        await using var rig = await OpenRigAsync((_, settings) => settings["dataPolicyUrl"] = null);
        var body = Body(rig);
        var visitorIp = "198.51.100.24";

        var response = await SubmitAsync(rig, body, visitorIp);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await PublicStoreFixture.JsonAsync(response);
        Assert.Equal("T-0001", (string)receipt["number"]!);
        var saved = await LoadAsync(rig, (Guid)body["clientRequestId"]!);
        Assert.Equal(visitorIp, saved.Consent.Ip);
        Assert.Null(saved.Consent.PolicyUrl);
        DateTimeOffset? policySetAt = saved.Consent.PolicySetAt;
        Assert.Null(policySetAt);
    }

    [Fact]
    public async Task WithoutPolicyTheConsentIsStillRequired()
    {
        await using var rig = await OpenRigAsync((_, settings) => settings["dataPolicyUrl"] = null);

        var response = await SubmitAsync(rig, Body(rig, body => body["acceptsDataPolicy"] = false));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.consent_required");
        Assert.Equal(0, await RequestsAsync(rig));
    }

    [Fact]
    public async Task TheSameClientRequestIdReturnsTheSameReceipt()
    {
        await using var rig = await OpenRigAsync();
        var body = Body(rig);

        var first = await PublicStoreFixture.JsonAsync(await SubmitAsync(rig, body));
        var retry = await SubmitAsync(rig, body);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var second = await PublicStoreFixture.JsonAsync(retry);
        Assert.Equal((string)first["number"]!, (string)second["number"]!);
        Assert.Equal((Guid)first["id"]!, (Guid)second["id"]!);
        Assert.Equal(1, await RequestsAsync(rig));
    }

    [Fact]
    public async Task TwoSimultaneousSubmissionsOfTheSameIdCreateOneRequest()
    {
        await using var rig = await OpenRigAsync();
        var body = Body(rig);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SubmitAsync(rig, body)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var numbers = new List<string>();
        foreach (var response in responses)
        {
            numbers.Add((string)(await PublicStoreFixture.JsonAsync(response))["number"]!);
        }

        Assert.Single(numbers.Distinct());
        Assert.Equal(1, await RequestsAsync(rig));
    }

    [Fact]
    public async Task AFailedCaptchaWritesNothingAndConsumesNoNumber()
    {
        await using var rig = await OpenRigAsync();
        rig.Captcha.Passes = false;

        await AssertRejectedAsync(await SubmitAsync(rig, Body(rig)), HttpStatusCode.UnprocessableEntity, "store.order_request.captcha_invalid");

        Assert.Equal(0, await RequestsAsync(rig));
        Assert.Equal("tienda-demo.lulocrm.com", rig.Captcha.LastHostname);
        await AssertNextNumberIsFirstAsync(rig);
    }

    [Fact]
    public async Task TurnstileEnabledWithoutAKeyFailsClosed()
    {
        // El verificador real, sin llave: el envío público no pasa y no sale ninguna llamada a Cloudflare.
        await using var rig = await OpenRigAsync(
            configure: services =>
            {
                services.RemoveAll<ICaptchaVerifier>();
                services.AddScoped<ICaptchaVerifier>(provider => new Infrastructure.Captcha.CloudflareTurnstileVerifier(
                    provider.GetRequiredService<IHttpClientFactory>(),
                    new StoreOptions { Turnstile = new TurnstileOptions { Enabled = true, SecretKey = "   " } },
                    enforceHostname: true,
                    provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Infrastructure.Captcha.CloudflareTurnstileVerifier>>()));
            });

        await AssertRejectedAsync(await SubmitAsync(rig, Body(rig)), HttpStatusCode.ServiceUnavailable, "store.captcha.unavailable");

        Assert.Equal(0, await RequestsAsync(rig));
    }

    [Fact]
    public async Task WithoutConsentIs422()
    {
        await using var rig = await OpenRigAsync();

        var response = await SubmitAsync(rig, Body(rig, body => body["acceptsDataPolicy"] = false));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.consent_required");
        Assert.Equal(0, rig.Captcha.Calls);
        Assert.Equal(0, await RequestsAsync(rig));
    }

    [Fact]
    public async Task APhoneWithoutCountryCodeThatDoesNotNormalizeIs422()
    {
        await using var rig = await OpenRigAsync();

        var response = await SubmitAsync(rig, Body(rig, body => body["billing"] = Billing(rig, b => b["phone"] = "12345")));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.phone_invalid");
        Assert.Equal(0, rig.Captcha.Calls);
    }

    [Fact]
    public async Task ASubmissionWithAStaleLineIs422()
    {
        await using var rig = await OpenRigAsync();
        var (_, hidden) = await rig.Fixture.CreateProductAsync("Oculto", "OCU-1", Prices(5_000m), visible: false);

        var response = await SubmitAsync(
            rig, Body(rig, body => body["lines"] = new[] { new { variantId = hidden, quantity = 1 } }));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.line_unavailable");
        Assert.Equal(0, await RequestsAsync(rig));
        await AssertNextNumberIsFirstAsync(rig);
    }

    [Fact]
    public async Task ThePublicEndpointNeverCreatesACustomer()
    {
        await using var rig = await OpenRigAsync();
        var before = await CountAsync(rig, "customers.customers");

        var response = await SubmitAsync(rig, Body(rig, body => body["shipping"] = Shipping(rig)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(before, await CountAsync(rig, "customers.customers"));
    }

    [Fact]
    public async Task TheDailyCapIs429()
    {
        await using var rig = await OpenRigAsync(configure: services => services.PostConfigure<StoreOptions>(options => options.DailyRequestCap = 2));

        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(rig, Body(rig))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(rig, Body(rig))).StatusCode);

        await AssertRejectedAsync(await SubmitAsync(rig, Body(rig)), HttpStatusCode.TooManyRequests, "store.order_request.daily_cap");
        Assert.Equal(2, await RequestsAsync(rig));
    }

    [Fact]
    public async Task ABodyOver64KbIs413()
    {
        await using var rig = await OpenRigAsync();

        // El resto del cuerpo es válido (una propiedad desconocida no la mira el validador): lo único que falla es el tamaño, así que
        // sin el tope el envío saldría 201. No depende de que el servidor de pruebas implemente el límite de Kestrel.
        var response = await SubmitAsync(rig, Body(rig, body => body["padding"] = new string('a', 70_000)));

        await AssertRejectedAsync(response, HttpStatusCode.RequestEntityTooLarge, "store.public.body_too_large");
        Assert.Equal(0, await RequestsAsync(rig));
        Assert.Equal(0, rig.Captcha.Calls);
    }

    [Fact]
    public async Task ASubmissionStoresItsCurrency()
    {
        await using var rig = await OpenRigAsync(PublicStoreFixture.TwoCurrencies, eurPrice: true);
        var body = Body(rig, b => b["currency"] = "eur");

        var response = await SubmitAsync(rig, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await PublicStoreFixture.JsonAsync(response);
        Assert.Equal("EUR", (string)receipt["currency"]!);
        Assert.Equal(8m, (decimal)receipt["total"]!);
        var saved = await LoadAsync(rig, (Guid)body["clientRequestId"]!);
        Assert.Equal("EUR", saved.Currency);
        Assert.Equal(4m, Assert.Single(saved.Lines).UnitPrice);
    }

    [Fact]
    public async Task ARetryNamingAnotherCurrencyGetsTheFirstReceiptInItsOwnCurrency()
    {
        await using var rig = await OpenRigAsync(PublicStoreFixture.TwoCurrencies, eurPrice: true);
        var body = Body(rig, b => b["currency"] = "EUR");
        await SubmitAsync(rig, body);

        body["currency"] = "COP";
        var retry = await PublicStoreFixture.JsonAsync(await SubmitAsync(rig, body));

        Assert.Equal("EUR", (string)retry["currency"]!);
        Assert.Equal(1, await RequestsAsync(rig));
    }

    [Fact]
    public async Task ASubmissionInACurrencyOutsideTheStoreWritesNothing()
    {
        await using var rig = await OpenRigAsync();

        var response = await SubmitAsync(rig, Body(rig, body => body["currency"] = "USD"));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.public.currency_invalid");
        Assert.Equal(0, rig.Captcha.Calls);
        Assert.Equal(0, await RequestsAsync(rig));
        await AssertNextNumberIsFirstAsync(rig);
    }

    [Fact]
    public async Task AMissingCurrencyIs422WithTheStoreCode()
    {
        await using var rig = await OpenRigAsync();

        var response = await SubmitAsync(rig, Body(rig, body => body.Remove("currency")));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.public.currency_invalid");
    }

    [Fact]
    public async Task ASubmissionFreezesBillingAndShipping()
    {
        await using var rig = await OpenRigAsync();
        var withShipping = Body(rig, body => body["shipping"] = Shipping(rig));
        var sameData = Body(rig);

        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(rig, withShipping)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(rig, sameData)).StatusCode);

        var saved = await LoadAsync(rig, (Guid)withShipping["clientRequestId"]!);
        Assert.Equal(rig.DepartmentName, saved.Billing.Place.DepartmentName);
        Assert.Equal(rig.CityName, saved.Billing.Place.CityName);
        Assert.Equal("CO", saved.Billing.Place.Country);
        var shipping = saved.Shipping!;
        Assert.Equal("Luis Gómez", shipping.RecipientName);
        Assert.Equal("+573007654321", shipping.PhoneE164);
        Assert.Equal(rig.CityName, shipping.Place.CityName);
        Assert.Null((await LoadAsync(rig, (Guid)sameData["clientRequestId"]!)).Shipping);
    }

    [Fact]
    public async Task ABillingCountryTheStoreDoesNotSellToIs422()
    {
        await using var rig = await OpenRigAsync();

        var response = await SubmitAsync(
            rig, Body(rig, body => body["billing"] = Billing(rig, b => { b["identificationType"] = "PASAPORTE"; b["country"] = "ES"; b["departmentId"] = null; b["cityId"] = null; b["cityName"] = "Madrid"; })));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.country_not_sold");
        Assert.Equal(0, rig.Captcha.Calls);
    }

    [Fact]
    public async Task AShippingCountryTheStoreDoesNotSellToIs422()
    {
        await using var rig = await OpenRigAsync();
        var shipping = Shipping(rig);
        shipping["country"] = "ES";
        shipping["departmentId"] = null;
        shipping["cityId"] = null;
        shipping["cityName"] = "Madrid";

        var response = await SubmitAsync(rig, Body(rig, body => body["shipping"] = shipping));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.country_not_sold");
        Assert.Equal(0, rig.Captcha.Calls);
    }

    [Fact]
    public async Task ACityOfAnotherDepartmentIs422()
    {
        await using var rig = await OpenRigAsync();
        var departments = await rig.Fixture.GetJsonAsync("/geography/departments");
        var other = departments["items"]!.AsArray().Select(item => (Guid)item!["id"]!).First(id => id != rig.Department);

        var response = await SubmitAsync(
            rig, Body(rig, body => body["billing"] = Billing(rig, b => b["departmentId"] = other)));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.city_invalid");
        Assert.Equal(0, rig.Captcha.Calls);
    }

    [Fact]
    public async Task AnUnknownCityIs422()
    {
        await using var rig = await OpenRigAsync();

        var response = await SubmitAsync(
            rig, Body(rig, body => body["billing"] = Billing(rig, b => b["cityId"] = Guid.CreateVersion7())));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.city_invalid");
    }

    [Fact]
    public async Task AForeignBlockWithDivipolaIdsIs422()
    {
        await using var rig = await OpenRigAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);

        var response = await SubmitAsync(
            rig, Body(rig, body => body["billing"] = Billing(rig, b => { b["identificationType"] = "PASAPORTE"; b["country"] = "ES"; b["phone"] = "612345678"; })));

        await AssertRejectedAsync(response, HttpStatusCode.UnprocessableEntity, "store.order_request.city_invalid");
        Assert.Equal(0, rig.Captcha.Calls);
    }

    [Fact]
    public async Task AForeignBillingWithACityTextIsAccepted()
    {
        await using var rig = await OpenRigAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
        var body = Body(rig, b => b["billing"] = Billing(
            rig, billing => { billing["identificationType"] = "PASAPORTE"; billing["country"] = "ES"; billing["departmentId"] = null; billing["cityId"] = null; billing["cityName"] = "Madrid"; billing["phone"] = "612345678"; }));

        var response = await SubmitAsync(rig, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await LoadAsync(rig, (Guid)body["clientRequestId"]!);
        Assert.Equal("Madrid", saved.Billing.Place.CityName);
        Assert.Null(saved.Billing.Place.CityId);
        Assert.Null(saved.Billing.Place.DepartmentId);
        Assert.Equal("+34612345678", saved.Billing.PhoneE164);
    }

    [Fact]
    public async Task ASpanishNifIsAccepted()
    {
        await using var rig = await OpenRigAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
        var body = Body(rig, b => b["billing"] = Billing(
            rig, billing => { billing["identificationType"] = "NIF"; billing["country"] = "ES"; billing["departmentId"] = null; billing["cityId"] = null; billing["cityName"] = "Madrid"; billing["phone"] = "612345678"; }));

        var response = await SubmitAsync(rig, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("NIF", (await LoadAsync(rig, (Guid)body["clientRequestId"]!)).Billing.IdentificationType);
    }

    [Fact]
    public async Task AColombianTypeForSpainIs422AndWritesNothing()
    {
        await using var rig = await OpenRigAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);

        var response = await SubmitAsync(
            rig, Body(rig, body => body["billing"] = Billing(
                rig, billing => { billing["identificationType"] = "CC"; billing["country"] = "ES"; billing["departmentId"] = null; billing["cityId"] = null; billing["cityName"] = "Madrid"; billing["phone"] = "612345678"; })));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.Equal("validation.failed", (string)json["code"]!);
        Assert.NotNull(json["errors"]!["Billing.IdentificationType"]);
        Assert.Equal(0, rig.Captcha.Calls);
        Assert.Equal(0, await RequestsAsync(rig));
        await AssertNextNumberIsFirstAsync(rig);
    }

    [Fact]
    public async Task ASubmissionWithoutEmailIs422WithTheField()
    {
        await using var rig = await OpenRigAsync();

        var response = await SubmitAsync(rig, Body(rig, body => body["billing"] = Billing(rig, b => b.Remove("email"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.Equal("validation.failed", (string)json["code"]!);
        Assert.NotNull(json["errors"]!["Billing.Email"]);
        Assert.Equal(0, rig.Captcha.Calls);
        Assert.Equal(0, await RequestsAsync(rig));
    }

    [Fact]
    public async Task AMissingVisitorIpIs422AndDoesNotDrainOtherVisitors()
    {
        await using var rig = await OpenRigAsync();

        // Sin cabecera todos caen en la cubeta del par directo (el servidor del storefront), que ningún visitante real usa: se agota
        // con 5 y el sexto es 429. El cupo es 5 cada 10 minutos, así que con menos de 6 el test no distinguiría una cubeta compartida.
        for (var i = 0; i < 5; i++)
        {
            await AssertRejectedAsync(
                await SubmitAsync(rig, Body(rig), visitorIp: null), HttpStatusCode.UnprocessableEntity, "store.public.visitor_ip_required");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SubmitAsync(rig, Body(rig), visitorIp: null)).StatusCode);

        // Un visitante que sí declara su IP tiene cubeta propia y sigue pudiendo enviar.
        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(rig, Body(rig))).StatusCode);
        Assert.Equal(1, rig.Captcha.Calls);
    }

    [Fact]
    public async Task ASubmissionWithoutTheKeyIs401()
    {
        await using var rig = await OpenRigAsync();
        using var anonymous = rig.Fixture.Settings.Factory.CreateClient();

        var response = await SubmitAsync(rig, Body(rig), client: anonymous);

        await AssertRejectedAsync(response, HttpStatusCode.Unauthorized, "store.public.client_unauthorized");
        Assert.Equal(0, await RequestsAsync(rig));
    }

    [Fact]
    public async Task TheSubmitLimiterAnswers429WithRetryAfterAndBucketsAreIsolatedPerVisitor()
    {
        await using var rig = await OpenRigAsync();
        const string visitor = "198.51.100.77";

        // Un cuerpo vacío es un 422, pero gasta el cupo igual: la cuenta es por visitante, antes de cualquier validación.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubmitAsync(rig, new { }, visitor)).StatusCode);
        }

        var limited = await SubmitAsync(rig, new { }, visitor);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        // Otro visitante, por el mismo servidor del storefront, tiene su propia cubeta.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubmitAsync(rig, new { }, "198.51.100.78")).StatusCode);
    }

    [Fact]
    public async Task TheVisitorIpHeaderIsIgnoredWithoutTheKey()
    {
        await using var rig = await OpenRigAsync();
        using var anonymous = rig.Fixture.Settings.Factory.CreateClient();

        // Sin clave el header no se cree: cada intento cae en la cubeta del par directo, así que rotar la IP declarada no la evita.
        // El limitador corre antes del filtro de la clave: 401 hasta agotar el cupo y 429 después.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SubmitAsync(rig, new { }, NextIp(), anonymous)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SubmitAsync(rig, new { }, NextIp(), anonymous)).StatusCode);
    }

    [Fact]
    public async Task ThePublicRoutesIgnoreTheSessionCookie()
    {
        await using var rig = await OpenRigAsync();
        using var withCookie = new HttpRequestMessage(HttpMethod.Get, Url("/"));
        withCookie.Headers.Add("Cookie", "qep_session=not-a-real-session");

        var plain = await rig.Fixture.GetAsync("/");
        var response = await rig.Fixture.Storefront.SendAsync(withCookie, TestContext.Current.CancellationToken);

        // El resultado es el mismo con o sin cookie: la ruta pública no la lee. La autenticación global sí valida una cookie presente,
        // y por eso se afirma el resultado y no la ausencia de esa validación.
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(plain.StatusCode, response.StatusCode);
        Assert.Equal(await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // El envío también lleva la cookie: la ruta no la lee, y el recibo es el de siempre.
        var submit = await SubmitAsync(rig, Body(rig), cookie: "qep_session=not-a-real-session");
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        Assert.Equal("T-0001", (string)(await PublicStoreFixture.JsonAsync(submit))["number"]!);
    }
}
