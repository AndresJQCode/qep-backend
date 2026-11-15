using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// La superficie del grupo anónimo <c>/api/v1/public/support/{tenantSlug}</c> (plan fase 3, Task 4; F3 a F6, Review Focus 3): el
/// mismo 404 para todo tenant que no se sirve, <c>no-store</c> en toda respuesta, la cookie de sesión ignorada con la autenticación
/// real, la política CORS propia sin credenciales, el tope del cuerpo y los limitadores por IP del visitante. Host propio con la
/// autenticación real; cada prueba siembra su tenant y llega desde su propia IP.
/// </summary>
public sealed class SupportPublicSurfaceApiTests(SupportPublicRealAuthHost host) : IClassFixture<SupportPublicRealAuthHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object LookupBody = new { number = "SOP-000001", pin = "123456", turnstileToken = FakeSupportCaptcha.ValidToken };

    [Fact]
    public async Task UnknownSlugInactiveTenantModuleOffAndUnpublishedAreTheSame404()
    {
        var factory = await host.StartTestAsync();
        var inactive = await SeedPublishedTenantAsync(factory, NewTenantId());
        await SuspendAsync(factory, inactive);
        var moduleOff = await SeedPublishedTenantAsync(
            factory, NewTenantId(), modules: TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Support).ToList());
        var unpublished = await SeedPublishedTenantAsync(factory, NewTenantId(), published: false);
        var control = await SeedPublishedTenantAsync(factory, NewTenantId());
        using var client = VisitorClient(factory);

        // El control: el mismo formulario, con el tenant activo, el módulo prendido y publicado, sí se sirve.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PublicUrl(control, "/form"), Ct)).StatusCode);

        var responses = new List<HttpResponseMessage>();
        foreach (var slug in new[] { "no-existe-este-slug", inactive, moduleOff, unpublished })
        {
            responses.Add(await client.GetAsync(PublicUrl(slug, "/form"), Ct));
        }

        var bodies = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("support.public.not_found", await CodeOfAsync(response));
            bodies.Add(WithoutTraceId(await JsonAsync(response)));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryPublicResponseIsNoStoreEvenOn404And413And429()
    {
        var factory = await host.StartTestAsync();
        using var client = VisitorClient(factory);

        var notFound = await client.GetAsync(PublicUrl("no-existe-este-slug", "/form"), Ct);
        var tooLarge = await PostRawAsync(client, PublicUrl("no-existe-este-slug", "/tickets/lookup"), OversizedBody());
        HttpResponseMessage last = null!;
        for (var i = 0; i < 11; i++)
        {
            last = await PostJsonAsync(client, PublicUrl("no-existe-este-slug", "/tickets/lookup"), LookupBody);
        }

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        foreach (var response in new[] { notFound, tooLarge, last })
        {
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
    }

    // Review Focus 3: con la autenticación real (la defensa CSRF corre), un POST del grupo con la cookie de sesión y sin X-Qep-Client
    // no da 403 y responde exactamente lo mismo que sin la cookie: el grupo es anónimo y no la lee. Precedente
    // PublicOrderRequestApiTests.ThePublicRoutesIgnoreTheSessionCookie.
    [Fact]
    public async Task ThePublicGroupIgnoresTheSessionCookieWithRealAuth()
    {
        var factory = await host.StartTestAsync();
        var slug = await SeedPublishedTenantAsync(factory, NewTenantId());
        using var client = VisitorClient(factory);

        var plainForm = await client.GetAsync(PublicUrl(slug, "/form"), Ct);
        using var formWithCookie = new HttpRequestMessage(HttpMethod.Get, PublicUrl(slug, "/form"));
        formWithCookie.Headers.Add("Cookie", "qep_session=una-sesion-de-asesor");
        var cookieForm = await client.SendAsync(formWithCookie, Ct);

        var plainLookup = await PostJsonAsync(client, PublicUrl(slug, "/tickets/lookup"), LookupBody);
        using var lookupWithCookie = new HttpRequestMessage(HttpMethod.Post, PublicUrl(slug, "/tickets/lookup"))
        {
            Content = JsonContent(LookupBody),
        };
        lookupWithCookie.Headers.Add("Cookie", "qep_session=una-sesion-de-asesor");
        var cookieLookup = await client.SendAsync(lookupWithCookie, Ct);

        Assert.NotEqual(HttpStatusCode.Forbidden, cookieLookup.StatusCode);
        Assert.Equal(plainForm.StatusCode, cookieForm.StatusCode);
        Assert.Equal(WithoutTraceId(await JsonAsync(plainForm)), WithoutTraceId(await JsonAsync(cookieForm)));
        Assert.Equal(plainLookup.StatusCode, cookieLookup.StatusCode);
        Assert.Equal(WithoutTraceId(await JsonAsync(plainLookup)), WithoutTraceId(await JsonAsync(cookieLookup)));
    }

    // Review Focus 3: el origen de la página recibe Access-Control-Allow-Origin y nunca Allow-Credentials.
    [Fact]
    public async Task ThePublicPreflightAllowsTheSupportOriginWithoutCredentials()
    {
        var factory = await host.StartTestAsync();
        using var client = VisitorClient(factory);

        var response = await PreflightAsync(client, SupportOrigin, PublicUrl("mi-empresa", "/tickets/lookup"), "POST");

        Assert.Equal(SupportOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        Assert.Contains("POST", Header(response, "Access-Control-Allow-Methods") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // Review Focus 3: ese mismo origen no recibe nada en una ruta de la consola, donde la política es la de la SPA con credenciales.
    [Fact]
    public async Task TheSupportOriginGetsNoCorsOnTenantRoutes()
    {
        var factory = await host.StartTestAsync();
        using var client = VisitorClient(factory);

        var response = await PreflightAsync(client, SupportOrigin, $"{SupportUrl(NewTenantId())}/tickets", "POST");

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task TheConsoleOriginGetsNoCorsOnThePublicGroup()
    {
        var factory = await host.StartTestAsync();
        using var client = VisitorClient(factory);

        var response = await PreflightAsync(client, ConsoleOrigin, PublicUrl("mi-empresa", "/tickets/lookup"), "POST");
        var control = await PreflightAsync(client, ConsoleOrigin, $"{SupportUrl(NewTenantId())}/tickets", "POST");

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        // El control: la consola sí recibe CORS en sus rutas, así que la ausencia de arriba es de la política del grupo.
        Assert.Equal(ConsoleOrigin, Header(control, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ABodyOver64KbIs413()
    {
        var factory = await host.StartTestAsync();
        using var client = VisitorClient(factory);

        var response = await PostRawAsync(client, PublicUrl("mi-empresa", "/tickets/lookup"), OversizedBody());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("support.public.body_too_large", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AMalformedJsonBodyIs400()
    {
        var factory = await host.StartTestAsync();
        using var client = VisitorClient(factory);

        var malformed = await PostRawAsync(client, PublicUrl("mi-empresa", "/tickets/lookup"), "{ \"number\": ");
        var nullBody = await PostRawAsync(client, PublicUrl("mi-empresa", "/tickets/lookup"), "null");

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("support.public.body_invalid", await CodeOfAsync(malformed));
        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
        Assert.Equal("support.public.body_invalid", await CodeOfAsync(nullBody));
    }

    // Plan F6: la consulta admite 10 por minuto por IP del visitante (la de X-Real-IP detrás del ingress de confianza). La undécima es
    // 429 y otra IP sigue con su cupo entero.
    [Fact]
    public async Task TheLookupLimiterIs429OnTheEleventhAndBucketsArePerIp()
    {
        var factory = await host.StartTestAsync();
        using var first = VisitorClient(factory);
        using var second = VisitorClient(factory);
        var url = PublicUrl("no-existe-este-slug", "/tickets/lookup");

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await PostJsonAsync(first, url, LookupBody)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostJsonAsync(first, url, LookupBody)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostJsonAsync(second, url, LookupBody)).StatusCode);
    }

    private static async Task SuspendAsync(QepApiFactory factory, string slug)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await tenancy.Tenants.SingleAsync(value => value.Slug == slug, Ct);
        tenant.Suspend(ChangeReason.Nonpayment, DateTimeOffset.UtcNow);
        await tenancy.SaveChangesAsync(Ct);
    }

    private static async Task<HttpResponseMessage> PreflightAsync(HttpClient client, string origin, string url, string method)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string url, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content, Ct);
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

    // Un texto de 70 000 caracteres dentro de un JSON válido: lo que corta es el tamaño, no la forma.
    private static string OversizedBody() =>
        JsonSerializer.Serialize(new { number = new string('1', 70_000), pin = "123456", turnstileToken = FakeSupportCaptcha.ValidToken });

    // El cuerpo del problema es idéntico salvo el traceId, que cambia en cada request.
    private static string WithoutTraceId(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .ToDictionary(property => property.Name, property => property.Value.ToString()));
}
