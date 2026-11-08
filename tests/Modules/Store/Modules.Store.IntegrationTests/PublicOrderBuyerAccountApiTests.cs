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

namespace Modules.Store.IntegrationTests;

/// <summary>
/// El pedido conectado a las cuentas de comprador (D7): el alta viaja como mensaje durable del outbox que commitea con la solicitud,
/// y actualizar el perfil corre después del commit. Con la API real y Postgres; sólo Turnstile es falso. Necesitan Docker.
/// </summary>
public sealed class PublicOrderBuyerAccountApiTests
{
    private const string StoreName = "Café Origen";
    private const string Password = "contraseña-de-prueba-123";

    private static int _ipCounter;

    private static string NextIp() => $"198.51.100.{(Interlocked.Increment(ref _ipCounter) % 250) + 1}";

    internal sealed record Rig(PublicStoreFixture Fixture, Guid Variant, Guid Department, Guid City) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        private string ConnectionString =>
            Fixture.Settings.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;

        /// <summary>El <c>payload</c> crudo de cada mensaje de alta del outbox, en orden de llegada.</summary>
        public Task<IReadOnlyList<string>> SignupPayloadsAsync() =>
            TextsAsync(
                "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name ORDER BY occurred_at, id",
                StoreIntegrationEvents.BuyerSignupRequested);

        public async Task<IReadOnlyList<string>> TextsAsync(string sql, string? name = null)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            if (name is not null)
            {
                command.Parameters.AddWithValue("name", name);
            }

            var texts = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                texts.Add(reader.GetString(0));
            }

            return texts;
        }

        /// <summary>Corre una pasada del worker de Buyers que consume las altas (los de fondo están apagados).</summary>
        public Task DrainBuyerSignupsAsync() =>
            ActivatorUtilities.CreateInstance<BuyerSignupRequestedWorker>(Fixture.Settings.Factory.Services)
                .DrainAsync(TestContext.Current.CancellationToken);

        public Task<IReadOnlyList<string>> OutboxAsync(string eventName) =>
            TextsAsync("SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name ORDER BY occurred_at, id", eventName);

        public async Task<string?> ScalarTextAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            return (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToString();
        }

        public async Task<HttpResponseMessage> PostBuyerAsync(string path, object body, string? session = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url($"/buyer{path}")) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-Qep-Visitor-Ip", NextIp());
            if (session is not null)
            {
                request.Headers.Add("X-Qep-Buyer-Session", session);
            }

            return await Fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
        }

        /// <summary>Registra una cuenta por la ruta real y devuelve el token de verificación que el outbox lleva para ella.</summary>
        public async Task<string> RegisterAsync(string email, string password = "una-contrasena-larga")
        {
            var registered = await PostBuyerAsync(
                "/register", new { email, password, acceptsDataPolicy = true, turnstileToken = "ok", profile = (object?)null });
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
            return (await OutboxAsync(BuyerIntegrationEvents.EmailVerificationRequested))
                .Select(payload => JsonNode.Parse(payload)!)
                .Last(payload => (string?)payload["email"] == email)["token"]!.GetValue<string>();
        }

        /// <summary>Deja una cuenta verificada (registro y verificación por las rutas reales).</summary>
        public async Task RegisterVerifiedAsync(string email, string password = "una-contrasena-larga")
        {
            var token = await RegisterAsync(email, password);
            var verified = await PostBuyerAsync("/email/verify", new { token });
            Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
        }

        public async Task<long> CountAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }
    }

    private static async Task<Rig> OpenRigAsync(Action<IServiceCollection>? configure = null)
    {
        var fixture = await OpenAsync(
            (_, body) => body["name"] = StoreName,
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

                configure?.Invoke(services);
            });
        var (_, variant) = await fixture.CreateProductAsync("Camiseta", "CAM-S", Prices(10_000m));

        var departments = await fixture.GetJsonAsync("/geography/departments");
        foreach (var department in departments["items"]!.AsArray())
        {
            var departmentId = (Guid)department!["id"]!;
            var cities = await fixture.GetJsonAsync($"/geography/departments/{departmentId}/cities");
            var first = cities["items"]!.AsArray().FirstOrDefault();
            if (first is not null)
            {
                return new Rig(fixture, variant, departmentId, (Guid)first["id"]!);
            }
        }

        throw new InvalidOperationException("The geography seed has no city.");
    }

    private sealed class PassingBuyerCaptcha : IBuyerCaptcha
    {
        public Task<bool> VerifyAsync(string token, string visitorIp, string host, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private static Dictionary<string, object?> Billing(Rig rig, string email = "ana@test.co") => new()
    {
        ["name"] = "Ana Pérez",
        ["identificationType"] = "CC",
        ["identificationNumber"] = "123456",
        ["phone"] = "3001234567",
        ["email"] = email,
        ["address"] = "Calle 1 # 2-3",
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
            ["note"] = null,
            ["acceptsDataPolicy"] = true,
            ["lines"] = new[] { new { variantId = rig.Variant, quantity = 1 } },
        };
        tweak?.Invoke(body);
        return body;
    }

    private static Dictionary<string, object?> WithAccount(Rig rig, string password = Password, string email = "ana@test.co") =>
        Body(rig, body =>
        {
            body["billing"] = Billing(rig, email);
            body["createAccount"] = new { password, acceptsDataPolicy = true };
        });

    private static async Task<HttpResponseMessage> SubmitAsync(Rig rig, object body, string? visitorIp = null, string? session = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/order-requests")) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Visitor-Ip", visitorIp ?? NextIp());
        if (session is not null)
        {
            request.Headers.Add("X-Qep-Buyer-Session", session);
        }

        return await rig.Fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonNode> CreatedAsync(HttpResponseMessage response)
    {
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, json.ToJsonString());
        return json;
    }

    /// <summary>Mismos campos del recibo salvo id y número. El total se compara como número: el releído trae la escala de la columna.</summary>
    private static void AssertSameReceiptShape(JsonNode expected, JsonNode actual)
    {
        Assert.Equal(
            ["buyerSessionRejected", "currency", "id", "number", "total"],
            actual.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal((decimal)expected["total"]!, (decimal)actual["total"]!);
        Assert.Equal((string)expected["currency"]!, (string)actual["currency"]!);
        Assert.Equal((bool)expected["buyerSessionRejected"]!, (bool)actual["buyerSessionRejected"]!);
    }

    [Fact]
    public async Task TheSignupMessageCarriesAHashAndNeverThePlaintext()
    {
        await using var rig = await OpenRigAsync();

        await CreatedAsync(await SubmitAsync(rig, WithAccount(rig)));

        var payloadJson = Assert.Single(await rig.SignupPayloadsAsync());
        Assert.DoesNotContain(Password, payloadJson, StringComparison.Ordinal);
        var payload = JsonNode.Parse(payloadJson)!.AsObject();
        Assert.False(payload.ContainsKey("password"));
        var hash = (string)payload["passwordHash"]!;
        var hasher = rig.Fixture.Settings.Factory.Services.GetRequiredService<IBuyerPasswordHasher>();
        Assert.NotEqual(BuyerPasswordCheck.Failed, hasher.Verify(hash, Password));

        foreach (var row in await rig.TextsAsync("SELECT row_to_json(t)::text FROM store.order_requests t"))
        {
            Assert.DoesNotContain(Password, row, StringComparison.Ordinal);
        }

        foreach (var row in await rig.TextsAsync("SELECT row_to_json(t)::text FROM audit.entries t"))
        {
            Assert.DoesNotContain(Password, row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheSignupMessageCommitsWithTheOrderRequest()
    {
        await using var rig = await OpenRigAsync();
        var body = WithAccount(rig, email: "  Ana.Perez@Test.CO ");

        var receipt = await CreatedAsync(await SubmitAsync(rig, body, visitorIp: "203.0.113.77"));

        var payload = JsonNode.Parse(Assert.Single(await rig.SignupPayloadsAsync()))!;
        Assert.Equal((Guid)receipt["id"]!, (Guid)payload["orderRequestId"]!);
        Assert.Equal((Guid)body["clientRequestId"]!, (Guid)payload["clientRequestId"]!);
        Assert.Equal(Tenant, (Guid)payload["tenantId"]!);
        // Review Focus 2: el email de ingreso sale normalizado igual que en el registro.
        Assert.Equal("ana.perez@test.co", (string)payload["email"]!);
        Assert.Equal(PublicStoreFixture.Host, (string)payload["storeHost"]!);
        Assert.Equal(StoreName, (string)payload["storeName"]!);
        Assert.Equal("203.0.113.77", (string)payload["visitorIp"]!);
        Assert.Equal("Ana Pérez", (string)payload["billing"]!["name"]!);
        Assert.Null(payload["shipping"]);
    }

    [Fact]
    public async Task ARetryWithTheSameClientRequestIdDoesNotSignUpTwice()
    {
        await using var rig = await OpenRigAsync();
        var body = WithAccount(rig);

        var first = await CreatedAsync(await SubmitAsync(rig, body));
        var second = await CreatedAsync(await SubmitAsync(rig, body));

        Assert.Equal((Guid)first["id"]!, (Guid)second["id"]!);
        Assert.Equal((string)first["number"]!, (string)second["number"]!);
        AssertSameReceiptShape(first, second);
        Assert.False((bool)second["buyerSessionRejected"]!);
        Assert.Single(await rig.SignupPayloadsAsync());

        await rig.DrainBuyerSignupsAsync();
        await rig.DrainBuyerSignupsAsync();

        Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM buyers.accounts"));
        Assert.Single(await rig.OutboxAsync(BuyerIntegrationEvents.EmailVerificationRequested));
    }

    [Fact]
    public async Task CreateAccountCreatesAnUnverifiedAccountWithTheOrderBlocks()
    {
        await using var rig = await OpenRigAsync();

        await CreatedAsync(await SubmitAsync(rig, WithAccount(rig), visitorIp: "203.0.113.41"));
        await rig.DrainBuyerSignupsAsync();

        Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM buyers.accounts WHERE email = 'ana@test.co' AND email_verified_at IS NULL"));
        var profile = JsonNode.Parse((await rig.ScalarTextAsync("SELECT data::text FROM buyers.profiles"))!)!;
        Assert.Equal("Ana Pérez", (string)profile["billing"]!["name"]!);
        Assert.Equal("ana@test.co", (string)profile["billing"]!["email"]!);
        Assert.Equal(rig.City, (Guid)profile["billing"]!["cityId"]!);
        Assert.Null(profile["shipping"]);
        Assert.True((bool)profile["shippingSameAsBilling"]!);
        Assert.Equal(PublicStoreFixture.Host, await rig.ScalarTextAsync("SELECT store_host FROM buyers.consents"));
        Assert.Equal("203.0.113.41", await rig.ScalarTextAsync("SELECT visitor_ip FROM buyers.consents"));
        var verify = JsonNode.Parse(Assert.Single(await rig.OutboxAsync(BuyerIntegrationEvents.EmailVerificationRequested)))!;
        Assert.Equal(StoreName, (string)verify["storeName"]!);
    }

    [Fact]
    public async Task TheReceiptIsTheSameWhetherTheEmailHasAnAccountOrNot()
    {
        await using var rig = await OpenRigAsync();
        await rig.RegisterVerifiedAsync("verificada@test.co");
        var hashBefore = await rig.ScalarTextAsync("SELECT password_hash FROM buyers.accounts WHERE email = 'verificada@test.co'");

        var fresh = await CreatedAsync(await SubmitAsync(rig, WithAccount(rig, email: "nueva@test.co")));
        var existing = await CreatedAsync(await SubmitAsync(rig, WithAccount(rig, email: "verificada@test.co")));

        AssertSameReceiptShape(fresh, existing);
        await rig.DrainBuyerSignupsAsync();
        Assert.Single(await rig.OutboxAsync(BuyerIntegrationEvents.ExistingAccountNotice));
        Assert.Equal(hashBefore, await rig.ScalarTextAsync("SELECT password_hash FROM buyers.accounts WHERE email = 'verificada@test.co'"));
    }

    [Fact]
    public async Task AnUnverifiedEmailFromCheckoutReplacesPasswordAndProfileAndResendsVerification()
    {
        await using var rig = await OpenRigAsync();
        var oldToken = await rig.RegisterAsync("ana@test.co");

        await CreatedAsync(await SubmitAsync(rig, WithAccount(rig)));
        await rig.DrainBuyerSignupsAsync();

        var messageHash = (string)JsonNode.Parse(Assert.Single(await rig.SignupPayloadsAsync()))!["passwordHash"]!;
        Assert.Equal(messageHash, await rig.ScalarTextAsync("SELECT password_hash FROM buyers.accounts WHERE email = 'ana@test.co'"));
        Assert.Equal("Ana Pérez", await rig.ScalarTextAsync("SELECT data->'billing'->>'name' FROM buyers.profiles"));
        Assert.Equal(2, (await rig.OutboxAsync(BuyerIntegrationEvents.EmailVerificationRequested)).Count);
        var stale = await rig.PostBuyerAsync("/email/verify", new { token = oldToken });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Equal("store.buyer.token_invalid", (string)(await JsonAsync(stale))["code"]!);
    }

    [Fact]
    public async Task TwoSimultaneousSubmissionsOfTheSameIdLeaveOneSignupMessage()
    {
        await using var rig = await OpenRigAsync();
        var body = WithAccount(rig);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SubmitAsync(rig, body)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM store.order_requests"));
        Assert.Single(await rig.SignupPayloadsAsync());
    }

    [Fact]
    public async Task AShortCreateAccountPasswordIs422BeforeTheOrderIsAccepted()
    {
        await using var rig = await OpenRigAsync();

        var shortPassword = await SubmitAsync(rig, WithAccount(rig, password: "corta-123"));
        var noPolicy = await SubmitAsync(rig, Body(rig, body => body["createAccount"] = new { password = Password, acceptsDataPolicy = false }));
        var withSession = await SubmitAsync(
            rig, Body(rig, body => { body["createAccount"] = new { password = Password, acceptsDataPolicy = true }; body["updateProfile"] = true; }));

        await AssertValidationAsync(shortPassword, "CreateAccount.Password");
        await AssertValidationAsync(noPolicy, "CreateAccount.AcceptsDataPolicy");
        await AssertValidationAsync(withSession, "CreateAccount");
        Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM store.order_requests"));
        Assert.Empty(await rig.SignupPayloadsAsync());
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("validation.failed", (string)json["code"]!);
        Assert.True(json["errors"]!.AsObject().ContainsKey(field), json.ToJsonString());
    }

    private sealed class ThrowingBuyerAccounts : IStoreBuyerAccounts
    {
        public string HashPassword(string password) => throw new InvalidOperationException("hasher down");

        public Task<bool> ReplaceProfileAsync(
            string host, string? sessionToken, PublicBillingInput billing, PublicShippingInput? shipping, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("buyers down");
    }

    [Fact]
    public async Task ABuyerFailureDoesNotFailTheOrder()
    {
        await using var rig = await OpenRigAsync(services =>
        {
            services.RemoveAll<IStoreBuyerAccounts>();
            services.AddSingleton<IStoreBuyerAccounts>(new ThrowingBuyerAccounts());
        });

        // D6 no deja pedir las dos cosas a la vez: un pedido para cada método.
        var withAccount = await CreatedAsync(await SubmitAsync(rig, WithAccount(rig)));
        var withProfile = await CreatedAsync(await SubmitAsync(rig, Body(rig, body => body["updateProfile"] = true), session: "cualquier-token"));

        Assert.Equal("T-0001", (string)withAccount["number"]!);
        Assert.False((bool)withAccount["buyerSessionRejected"]!);
        Assert.Equal("T-0002", (string)withProfile["number"]!);
        Assert.False((bool)withProfile["buyerSessionRejected"]!);
        Assert.Empty(await rig.SignupPayloadsAsync());
        Assert.Equal(2, await rig.CountAsync("SELECT count(*) FROM store.order_requests"));
    }

    private static async Task<string> LoginAsync(Rig rig, string email = "ana@test.co", string password = "una-contrasena-larga")
    {
        var response = await rig.PostBuyerAsync("/session", new { email, password });
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
        return (string)json["sessionToken"]!;
    }

    private static async Task<JsonNode> BuyerAsync(Rig rig, string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/buyer/session"));
        request.Headers.Add("X-Qep-Buyer-Session", session);
        var response = await rig.Fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
        return json;
    }

    private static Dictionary<string, object?> UpdatingProfile(Rig rig) => Body(rig, body =>
    {
        var billing = Billing(rig, email: "facturas@empresa.co");
        billing["name"] = "Ana María Pérez";
        billing["identificationType"] = "cc";
        body["billing"] = billing;
        body["shipping"] = new Dictionary<string, object?>
        {
            ["recipientName"] = "Luis Gómez",
            ["phone"] = "3007654321",
            ["address"] = "Carrera 9 # 8-7",
            ["country"] = "CO",
            ["departmentId"] = rig.Department,
            ["cityId"] = rig.City,
            ["cityName"] = null,
        };
        body["updateProfile"] = true;
    });

    [Fact]
    public async Task UpdateProfileReplacesTheProfileWithTheOrderBlocks()
    {
        await using var rig = await OpenRigAsync();
        await rig.RegisterVerifiedAsync("ana@test.co");
        var session = await LoginAsync(rig);

        var receipt = await CreatedAsync(await SubmitAsync(rig, UpdatingProfile(rig), session: session));

        Assert.False((bool)receipt["buyerSessionRejected"]!);
        var buyer = await BuyerAsync(rig, session);
        // El email de ingreso no cambia; el de facturación del perfil sí.
        Assert.Equal("ana@test.co", (string)buyer["email"]!);
        var profile = buyer["profile"]!;
        Assert.Equal("Ana María Pérez", (string)profile["billing"]!["name"]!);
        Assert.Equal("facturas@empresa.co", (string)profile["billing"]!["email"]!);
        Assert.Equal("CC", (string)profile["billing"]!["identificationType"]!);
        Assert.Equal("Luis Gómez", (string)profile["shipping"]!["recipientName"]!);
        Assert.False((bool)profile["shippingSameAsBilling"]!);
        Assert.Empty(await rig.SignupPayloadsAsync());
    }

    [Fact]
    public async Task UpdateProfileWithASessionFromAnotherHostIsIgnoredAndFlagged()
    {
        await using var rig = await OpenRigAsync();
        await rig.RegisterVerifiedAsync("ana@test.co");
        await LoginAsync(rig);
        // Una sesión válida de la misma cuenta, emitida en otra tienda.
        var accountId = Guid.Parse((await rig.ScalarTextAsync("SELECT id FROM buyers.accounts"))!);
        string foreignSession;
        await using (var scope = rig.Fixture.Settings.Factory.Services.CreateAsyncScope())
        {
            (foreignSession, _) = await scope.ServiceProvider.GetRequiredService<BuyerSessions>()
                .IssueAsync(accountId, "otra-tienda.lulocrm.com", "203.0.113.5", TestContext.Current.CancellationToken);
        }

        var receipt = await CreatedAsync(await SubmitAsync(rig, UpdatingProfile(rig), session: foreignSession));

        Assert.True((bool)receipt["buyerSessionRejected"]!);
        Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM buyers.profiles"));
        Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM store.order_requests"));
    }

    [Fact]
    public async Task UpdateProfileWithoutTheHeaderIsFlagged()
    {
        await using var rig = await OpenRigAsync();

        var receipt = await CreatedAsync(await SubmitAsync(rig, UpdatingProfile(rig)));

        Assert.True((bool)receipt["buyerSessionRejected"]!);
        Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM store.order_requests"));
    }

    [Fact]
    public async Task ARetryWithUpdateProfileReturnsTheFirstReceiptAndDoesNotTouchTheProfile()
    {
        await using var rig = await OpenRigAsync();
        await rig.RegisterVerifiedAsync("ana@test.co");
        var session = await LoginAsync(rig);
        var body = UpdatingProfile(rig);
        await CreatedAsync(await SubmitAsync(rig, body, session: session));
        var savedAt = await rig.ScalarTextAsync("SELECT updated_at::text FROM buyers.profiles");

        // El reintento, ya sin una sesión que sirva: igual devuelve el primer recibo, sin marca y sin tocar el perfil.
        var retry = await CreatedAsync(await SubmitAsync(rig, body, session: "token-que-no-existe"));

        Assert.False((bool)retry["buyerSessionRejected"]!);
        Assert.Equal(savedAt, await rig.ScalarTextAsync("SELECT updated_at::text FROM buyers.profiles"));
    }
}
