using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Store.Application;
using Modules.Store.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using static Modules.Store.IntegrationTests.PublicStoreFixture;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

internal sealed record CustomerRow(
    string Name,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? PhoneE164,
    string? Email,
    string? Address,
    string? Country,
    Guid? CityId,
    string? CityName,
    string? Cuc,
    string Completeness,
    Guid? ClassificationId,
    long Version);

internal sealed record AddressRow(string Name, string Address, string? Phone, string Country, Guid? CityId, string? CityName, bool IsPrincipal);

/// <summary>
/// La tienda publicada con una solicitud por enviar, un reloj del tenant que se mueve y quien atiende la bandeja. Compartido por las
/// pruebas de la bandeja y de «Tomar»; necesitan Docker y se corren una vez en la tanda final.
/// </summary>
internal sealed class StoreInboxRig : IAsyncDisposable
{
    public const string Permissions =
        "quotations.order.manage,quotations.order.read,customers.customer.read,customers.customer.manage";

    public static readonly DateTimeOffset Start = new(2026, 11, 3, 15, 0, 0, TimeSpan.Zero);

    private static int _ipCounter;

    private StoreInboxRig(
        PublicStoreFixture fixture,
        FakeCaptchaVerifier captcha,
        MutableTenantClock clock,
        HttpClient inbox,
        Guid callerMembershipId,
        (Guid Variant, Guid Department, string DepartmentName, Guid City, string CityName) place)
    {
        Fixture = fixture;
        Captcha = captcha;
        Clock = clock;
        Inbox = inbox;
        CallerMembershipId = callerMembershipId;
        Variant = place.Variant;
        Department = place.Department;
        DepartmentName = place.DepartmentName;
        City = place.City;
        CityName = place.CityName;
    }

    public PublicStoreFixture Fixture { get; }

    public FakeCaptchaVerifier Captcha { get; }

    public MutableTenantClock Clock { get; }

    /// <summary>Quien atiende la bandeja: el sujeto del harness, con una membresía activa.</summary>
    public HttpClient Inbox { get; }

    public Guid CallerMembershipId { get; }

    public Guid Variant { get; }

    public Guid Department { get; }

    public string DepartmentName { get; }

    public Guid City { get; }

    public string CityName { get; }

    public QepApiFactory Factory => Fixture.Settings.Factory;

    public static async Task<StoreInboxRig> OpenAsync(
        Action<StoreSettingsFixture, Dictionary<string, object?>>? tweak = null,
        bool eurPrice = false,
        DateTimeOffset? now = null,
        IEnumerable<TenantModuleKey>? modules = null)
    {
        var captcha = new FakeCaptchaVerifier();
        var clock = new MutableTenantClock(now ?? Start);
        var fixture = await PublicStoreFixture.OpenAsync(
            tweak,
            modules: modules,
            configureServices: services =>
            {
                services.RemoveAll<ICaptchaVerifier>();
                services.AddSingleton<ICaptchaVerifier>(captcha);
                services.RemoveAll<ITenantClock>();
                services.AddSingleton<ITenantClock>(clock);
            });
        var (_, variant) = await fixture.CreateProductAsync("Camiseta", "CAM-S", Prices(10_000m, eurPrice ? 4m : null));

        var departments = await fixture.GetJsonAsync("/geography/departments");
        foreach (var department in departments["items"]!.AsArray())
        {
            var departmentId = (Guid)department!["id"]!;
            var cities = await fixture.GetJsonAsync($"/geography/departments/{departmentId}/cities");
            if (cities["items"]!.AsArray().FirstOrDefault() is not { } first)
            {
                continue;
            }

            var membership = await SeedCallerMembershipAsync(fixture.Settings.Factory, "Quien atiende");
            var inbox = CreateClient(fixture.Settings.Factory, SubjectId, StoreApiHarness.TenantId, Permissions);
            return new StoreInboxRig(
                fixture, captcha, clock, inbox, membership,
                (variant, departmentId, (string)department["name"]!, (Guid)first["id"]!, (string)first["name"]!));
        }

        throw new InvalidOperationException("The geography seed has no city.");
    }

    public static string BaseUrl => $"{StoreUrl()}/order-requests";

    // ---- Envío público ----------------------------------------------------------------------------------------------------

    public Dictionary<string, object?> BillingBlock(Action<Dictionary<string, object?>>? tweak = null)
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
            ["departmentId"] = Department,
            ["cityId"] = City,
            ["cityName"] = null,
        };
        tweak?.Invoke(billing);
        return billing;
    }

    public Dictionary<string, object?> ShippingBlock(Action<Dictionary<string, object?>>? tweak = null)
    {
        var shipping = new Dictionary<string, object?>
        {
            ["recipientName"] = "Luis Gómez",
            ["phone"] = "3007654321",
            ["address"] = "Carrera 9 # 8-7",
            ["country"] = "CO",
            ["departmentId"] = Department,
            ["cityId"] = City,
            ["cityName"] = null,
        };
        tweak?.Invoke(shipping);
        return shipping;
    }

    /// <summary>Envía una solicitud por la ruta pública y devuelve su id. Falla la prueba si el envío no es 201.</summary>
    public async Task<Guid> SubmitAsync(
        Action<Dictionary<string, object?>>? billing = null,
        Action<Dictionary<string, object?>>? shipping = null,
        string currency = "COP",
        string? note = "Entregar en portería",
        int quantity = 2)
    {
        var body = new Dictionary<string, object?>
        {
            ["clientRequestId"] = Guid.CreateVersion7(),
            ["turnstileToken"] = "token",
            ["currency"] = currency,
            ["billing"] = BillingBlock(billing),
            ["shipping"] = shipping is null ? null : ShippingBlock(shipping),
            ["note"] = note,
            ["acceptsDataPolicy"] = true,
            ["lines"] = new[] { new { variantId = Variant, quantity } },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/order-requests")) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Visitor-Ip", $"203.0.113.{(Interlocked.Increment(ref _ipCounter) % 250) + 1}");
        var response = await Fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, json.ToJsonString());
        return (Guid)json["id"]!;
    }

    // ---- Bandeja ----------------------------------------------------------------------------------------------------------

    public HttpClient ClientFor(Guid userId) => CreateClient(Factory, userId.ToString(), StoreApiHarness.TenantId, Permissions);

    public Task<HttpResponseMessage> GetAsync(string path, HttpClient? client = null) =>
        (client ?? Inbox).GetAsync($"{BaseUrl}{path}", TestContext.Current.CancellationToken);

    public async Task<JsonNode> GetJsonAsync(string path, HttpClient? client = null)
    {
        var response = await GetAsync(path, client);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
        return json;
    }

    public Task<HttpResponseMessage> TakeAsync(Guid requestId, object body, HttpClient? client = null) =>
        (client ?? Inbox).PostAsJsonAsync($"{BaseUrl}/{requestId}/take", body, TestContext.Current.CancellationToken);

    public async Task<JsonNode> TakeOkAsync(Guid requestId, object body, HttpClient? client = null)
    {
        var response = await TakeAsync(requestId, body, client);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
        return json;
    }

    public Task<HttpResponseMessage> DiscardAsync(Guid requestId, object body, HttpClient? client = null) =>
        (client ?? Inbox).PostAsJsonAsync($"{BaseUrl}/{requestId}/discard", body, TestContext.Current.CancellationToken);

    public static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.StatusCode == status, json.ToJsonString());
        Assert.Equal(code, (string)json["code"]!);
    }

    public async Task<JsonNode> OrderAsync(Guid orderId)
    {
        var response = await Inbox.GetAsync($"/api/v1/tenants/{StoreApiHarness.TenantId}/orders/{orderId}", TestContext.Current.CancellationToken);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
        return json;
    }

    public async Task<StoreOrderRequest> StateAsync(Guid requestId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStoreOrderRequestRepository>();
        return (await repository.FindAsync(StoreSettingsFixture.Tenant, requestId, TestContext.Current.CancellationToken))!;
    }

    // ---- Clientes ---------------------------------------------------------------------------------------------------------

    /// <summary>Un cliente completo por la API de Customers, como lo haría la pantalla.</summary>
    public async Task<Guid> CreateCompleteCustomerAsync(
        string name,
        string documentNumber,
        string phone,
        string email = "cliente@test.co",
        string address = "Avenida 5 # 6-7",
        bool withRetention = false,
        bool vatSurplus = false,
        string identificationType = "CC",
        string country = "CO",
        string? cityName = null)
    {
        var response = await Inbox.PostAsJsonAsync(
            $"/api/v1/tenants/{StoreApiHarness.TenantId}/customers",
            new
            {
                name,
                businessName = (string?)null,
                identificationType,
                identificationNumber = documentNumber,
                phone,
                email,
                address,
                country,
                cityId = country == "CO" ? City : (Guid?)null,
                cityName,
                classificationId = (Guid?)null,
                withRetention,
                vatSurplus,
            },
            TestContext.Current.CancellationToken);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, json.ToJsonString());
        return (Guid)json["id"]!;
    }

    /// <summary>Una ficha incompleta como las que deja el POS (nombre y documento) o WhatsApp (nombre y teléfono).</summary>
    public async Task<Guid> SeedIncompleteAsync(
        string name,
        string? phone = null,
        string? phoneE164 = null,
        string? documentNumber = null,
        string? email = null,
        string? address = null,
        string? country = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, phone, phone_e164, email, address, identification_type, identification_number,
                                             country, completeness, whatsapp_user_id, with_retention, vat_surplus, version, created_at, updated_at)
            VALUES (@id, @tenant, @name, true, @phone, @phoneE164, @email, @address, @idType, @idNumber, @country, 'Incomplete', @wa, false, false, 1, now(), now())
            """,
            ("id", id),
            ("tenant", StoreSettingsFixture.Tenant),
            ("name", name),
            ("phone", phone),
            ("phoneE164", phoneE164),
            ("email", email),
            ("address", address),
            ("idType", documentNumber is null ? null : "Cc"),
            ("idNumber", documentNumber),
            ("country", country),
            ("wa", $"CO.{id:N}"));
        return id;
    }

    /// <summary>Marca al cliente como fusionado en otro: ya no es candidato ni se puede tomar.</summary>
    public Task MergeAsync(Guid customerId, Guid survivorId) =>
        ExecuteAsync(
            "UPDATE customers.customers SET merged_into_id = @survivor, merged_at = now(), is_active = false WHERE id = @id",
            ("id", customerId),
            ("survivor", survivorId));

    public async Task<CustomerRow> CustomerAsync(Guid customerId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT name, identification_type, identification_number, phone, phone_e164, email, address, country, city_id, city_name, cuc,
                   completeness, classification_id, version
            FROM customers.customers WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", customerId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken), "The customer does not exist.");
        return new CustomerRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetGuid(12),
            reader.GetInt64(13));
    }

    public async Task<List<AddressRow>> AddressesAsync(Guid customerId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT name, address, phone, country, city_id, city_name, is_principal
            FROM customers.customer_addresses WHERE customer_id = @id ORDER BY created_at, id
            """,
            connection);
        command.Parameters.AddWithValue("id", customerId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<AddressRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new AddressRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6)));
        }

        return rows;
    }

    // ---- Segundo asesor ---------------------------------------------------------------------------------------------------

    /// <summary>Otra persona con membresía activa: su usuario y su membresía.</summary>
    public async Task<(Guid UserId, Guid MembershipId)> SeedSecondCallerAsync(string displayName)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var userId = UserId.New();

        identity.Users.Add(User.CreateInvited(userId, $"second-{Guid.CreateVersion7():N}@example.com", now));
        var membership = Membership.CreateActive(
            MembershipId.New(), userId.Value, new Modules.Tenancy.Domain.TenantId(StoreSettingsFixture.Tenant), ["advisor"], "test", now);
        membership.UpdateProfile(displayName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return (userId.Value, membership.Id.Value);
    }

    // ---- Catálogo y base --------------------------------------------------------------------------------------------------

    public Task SetPriceAsync(decimal amount, string currency = "COP") =>
        ExecuteAsync(
            "UPDATE catalog.product_prices SET amount = @amount WHERE variant_id = @variant AND currency = @currency",
            ("amount", amount),
            ("variant", Variant),
            ("currency", currency));

    public Task DeactivateVariantAsync() =>
        ExecuteAsync("UPDATE catalog.product_variants SET is_active = false WHERE id = @variant", ("variant", Variant));

    public async Task<string?> TextAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
    }

    public async Task<long> CountAsync(string table)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public ValueTask DisposeAsync()
    {
        Inbox.Dispose();
        return Fixture.DisposeAsync();
    }
}
