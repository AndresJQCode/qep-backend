using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-11-15 §4: el asesor ve la cuenta de comprador de un pedido de la tienda y la cambia o la quita. Las cuentas
/// de comprador son globales: se siembran por SQL en <c>buyers.accounts</c>, cada prueba con su propio correo.
/// </summary>
public sealed class OrderBuyerAccountApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private const string AuditEvent = "platform.audit.recorded.v1";
    private const string ChangedAction = "order.buyer_account_changed";

    private static string OrderUrl(Guid tenantId, Guid orderId) => $"/api/v1/tenants/{tenantId}/orders/{orderId}";

    private static string BuyerAccountUrl(Guid tenantId, Guid orderId) => $"{OrderUrl(tenantId, orderId)}/buyer-account";

    [Fact]
    public async Task DetailShowsTheBuyerAccountEmail()
    {
        var factory = await sharedHost.StartTestAsync();
        var email = UniqueEmail();
        var accountId = await SeedBuyerAccountAsync(sharedHost.Database.GetConnectionString(), email, verified: true);
        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, accountId);
        using var _ = client;

        var detail = await GetDetailAsync(client, tenantId, orderId);

        Assert.Equal(new BuyerAccountResponse(accountId, email), detail.BuyerAccount);
    }

    // Review Focus 5: borrar la cuenta no limpia el pedido; el id queda y el detalle no inventa un correo.
    [Fact]
    public async Task DetailOfAnOrderWhoseAccountWasDeletedShowsNoAccount()
    {
        var factory = await sharedHost.StartTestAsync();
        var connectionString = sharedHost.Database.GetConnectionString();
        var accountId = await SeedBuyerAccountAsync(connectionString, UniqueEmail(), verified: true);
        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, accountId);
        using var _ = client;
        await ExecuteAsync(connectionString, "DELETE FROM buyers.accounts WHERE id = @id", accountId);

        var detail = await GetDetailAsync(client, tenantId, orderId);

        Assert.Null(detail.BuyerAccount);
        Assert.Equal(accountId, await StoredBuyerAccountAsync(connectionString, orderId));
    }

    [Fact]
    public async Task PutLinksToAVerifiedAccountMarksAuditsAndBumpsVersion()
    {
        var factory = await sharedHost.StartTestAsync();
        var connectionString = sharedHost.Database.GetConnectionString();
        var email = UniqueEmail();
        var accountId = await SeedBuyerAccountAsync(connectionString, email, verified: true);
        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, buyerAccountId: null);
        using var _ = client;
        var before = await GetDetailAsync(client, tenantId, orderId);

        // El correo llega con mayúsculas y espacios: la búsqueda normaliza como Buyers.
        var response = await PutAsync(client, tenantId, orderId, before.Order.Version, $"  {email.ToUpperInvariant()} ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ReadDetailAsync(response);
        Assert.Equal(new BuyerAccountResponse(accountId, email), detail.BuyerAccount);
        Assert.Equal(before.Order.Version + 1, detail.Order.Version);
        Assert.Equal($"\"{detail.Order.Version}\"", response.Headers.ETag?.Tag);
        Assert.True(await StoredSetByMemberAsync(connectionString, orderId));
        var audit = Assert.Single(await ChangedAuditsAsync(factory, orderId));
        Assert.Equal((null, accountId), DetailsOf(audit));
        Assert.DoesNotContain(email, audit.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PutWithAnUnverifiedOrUnknownEmailIs422BuyerAccountNotFound(bool accountExists)
    {
        var factory = await sharedHost.StartTestAsync();
        var connectionString = sharedHost.Database.GetConnectionString();
        var email = UniqueEmail();
        if (accountExists)
        {
            await SeedBuyerAccountAsync(connectionString, email, verified: false);
        }

        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, buyerAccountId: null);
        using var _ = client;
        var before = await GetDetailAsync(client, tenantId, orderId);

        var response = await PutAsync(client, tenantId, orderId, before.Order.Version, email);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.buyer_account.not_found", await CodeOfAsync(response));
        Assert.Equal(before.Order.Version, (await GetDetailAsync(client, tenantId, orderId)).Order.Version);
        Assert.False(await StoredSetByMemberAsync(connectionString, orderId));
        Assert.Empty(await ChangedAuditsAsync(factory, orderId));
    }

    [Fact]
    public async Task PutWithTheSameAccountTwiceDoesNotBumpTheSecondTime()
    {
        var factory = await sharedHost.StartTestAsync();
        var email = UniqueEmail();
        await SeedBuyerAccountAsync(sharedHost.Database.GetConnectionString(), email, verified: true);
        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, buyerAccountId: null);
        using var _ = client;
        var before = await GetDetailAsync(client, tenantId, orderId);
        var first = await ReadDetailAsync(await PutAsync(client, tenantId, orderId, before.Order.Version, email));

        var second = await PutAsync(client, tenantId, orderId, first.Order.Version, email);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(first.Order.Version, (await ReadDetailAsync(second)).Order.Version);
        Assert.Single(await ChangedAuditsAsync(factory, orderId));
    }

    [Fact]
    public async Task DeleteUnlinksMarksAndAudits()
    {
        var factory = await sharedHost.StartTestAsync();
        var connectionString = sharedHost.Database.GetConnectionString();
        var accountId = await SeedBuyerAccountAsync(connectionString, UniqueEmail(), verified: true);
        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, accountId);
        using var _ = client;
        var before = await GetDetailAsync(client, tenantId, orderId);

        var response = await DeleteAsync(client, tenantId, orderId, before.Order.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ReadDetailAsync(response);
        Assert.Null(detail.BuyerAccount);
        Assert.Equal(before.Order.Version + 1, detail.Order.Version);
        Assert.Null(await StoredBuyerAccountAsync(connectionString, orderId));
        Assert.True(await StoredSetByMemberAsync(connectionString, orderId));
        var audit = Assert.Single(await ChangedAuditsAsync(factory, orderId));
        Assert.Equal((accountId, null), DetailsOf(audit));
    }

    // «Quitar» sobre un pedido vacío significa «no lo enlaces solo»: también marca, sube versión y audita.
    [Fact]
    public async Task DeleteOnAnOrderWithoutAccountStillMarks()
    {
        var factory = await sharedHost.StartTestAsync();
        var connectionString = sharedHost.Database.GetConnectionString();
        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, buyerAccountId: null);
        using var _ = client;
        var before = await GetDetailAsync(client, tenantId, orderId);

        var response = await DeleteAsync(client, tenantId, orderId, before.Order.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before.Order.Version + 1, (await ReadDetailAsync(response)).Order.Version);
        Assert.True(await StoredSetByMemberAsync(connectionString, orderId));
        var audit = Assert.Single(await ChangedAuditsAsync(factory, orderId));
        Assert.Equal((null, null), DetailsOf(audit));
    }

    [Fact]
    public async Task PutOnAManualOrderIs422SourceInvalid()
    {
        var factory = await sharedHost.StartTestAsync();
        var email = UniqueEmail();
        await SeedBuyerAccountAsync(sharedHost.Database.GetConnectionString(), email, verified: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateManualOrderAsync(client, tenantId);

        var put = await PutAsync(client, tenantId, order.Order.Id, order.Order.Version, email);
        var delete = await DeleteAsync(client, tenantId, order.Order.Id, order.Order.Version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("order.order.source_invalid", await CodeOfAsync(put));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, delete.StatusCode);
        Assert.Equal("order.order.source_invalid", await CodeOfAsync(delete));
    }

    [Fact]
    public async Task WithoutManagePermissionIs403()
    {
        var factory = await sharedHost.StartTestAsync();
        var email = UniqueEmail();
        await SeedBuyerAccountAsync(sharedHost.Database.GetConnectionString(), email, verified: true);
        var (tenantId, ownerUserId, client, orderId) = await StoreOrderAsync(factory, buyerAccountId: null);
        using var _ = client;
        var before = await GetDetailAsync(client, tenantId, orderId);
        using var reader = CreateClient(
            factory, ownerUserId.ToString(), tenantId.ToString(), OrdersPermissions.OrderRead);

        var put = await PutAsync(reader, tenantId, orderId, before.Order.Version, email);
        var delete = await DeleteAsync(reader, tenantId, orderId, before.Order.Version);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(before.Order.Version, (await GetDetailAsync(client, tenantId, orderId)).Order.Version);
    }

    // 403 y nunca 404: un 404 confirmaría que el pedido existe en otro tenant.
    [Fact]
    public async Task OtherTenantIs403()
    {
        var factory = await sharedHost.StartTestAsync();
        var email = UniqueEmail();
        await SeedBuyerAccountAsync(sharedHost.Database.GetConnectionString(), email, verified: true);
        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, buyerAccountId: null);
        using var _ = client;
        var before = await GetDetailAsync(client, tenantId, orderId);
        var (_, _, stranger) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = stranger;

        var put = await PutAsync(stranger, tenantId, orderId, before.Order.Version, email);
        var delete = await DeleteAsync(stranger, tenantId, orderId, before.Order.Version);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(before.Order.Version, (await GetDetailAsync(client, tenantId, orderId)).Order.Version);
    }

    [Fact]
    public async Task StaleIfMatchIs412()
    {
        var factory = await sharedHost.StartTestAsync();
        var connectionString = sharedHost.Database.GetConnectionString();
        var email = UniqueEmail();
        await SeedBuyerAccountAsync(connectionString, email, verified: true);
        var (tenantId, _, client, orderId) = await StoreOrderAsync(factory, buyerAccountId: null);
        using var _ = client;
        var before = await GetDetailAsync(client, tenantId, orderId);

        // Versiones por encima de la actual: un pedido recién creado está en 1, y 0 no es un If-Match válido (sería 428).
        var put = await PutAsync(client, tenantId, orderId, before.Order.Version + 1, email);
        var delete = await DeleteAsync(client, tenantId, orderId, before.Order.Version + 2);
        var missing = await PutAsync(client, tenantId, orderId, version: null, email);

        Assert.Equal(HttpStatusCode.PreconditionFailed, put.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(put));
        Assert.Equal(HttpStatusCode.PreconditionFailed, delete.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.False(await StoredSetByMemberAsync(connectionString, orderId));
    }

    private static string UniqueEmail() => $"buyer-{Guid.CreateVersion7():N}@example.com";

    private static async Task<(Guid TenantId, Guid OwnerUserId, HttpClient Client, Guid OrderId)> StoreOrderAsync(
        QepApiFactory factory, Guid? buyerAccountId)
    {
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        Guid advisorId;
        using (var scope = factory.Services.CreateScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<IMembershipDirectory>();
            var membershipId = await directory.FindActiveMembershipIdAsync(
                ownerUserId, tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(membershipId);
            advisorId = membershipId.Value;
        }

        var party = new StoreOrderParty(
            "Ana Pérez", "+573001112233", "ana@example.com", "Calle 1 # 2-3", "CO", city.DepartmentId, city.CityId,
            null, "CC", "1020304050");
        var draft = new StoreOrderDraft(
            tenantId, Guid.CreateVersion7(), customerId, advisorId, IsRetail: true,
            new QuotationBillingAccountRequest(account.CompanyId, account.BankName, account.AccountNumber, account.Currency),
            party, party, BillingVatSurplus: false, [new StoreQuoteLine(variantId, 1m)], null, buyerAccountId);
        using var gatewayScope = factory.Services.CreateScope();
        var result = await gatewayScope.ServiceProvider.GetRequiredService<IStoreOrderGateway>()
            .CreateAsync(draft, TestContext.Current.CancellationToken);
        return (tenantId, ownerUserId, client, result.OrderId);
    }

    private static async Task<OrderDetailResponse> CreateManualOrderAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders",
            new CreateOrderRequest(
                clientId, null, null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new OrderEditItemRequest(productId, 1m)], []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadDetailAsync(response);
    }

    private static async Task<Guid> SeedBuyerAccountAsync(string connectionString, string email, bool verified)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO buyers.accounts (id, email, password_hash, email_verified_at, created_at, updated_at)
            VALUES (@id, @email, 'hash', @verified_at, now(), now())
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("verified_at", verified ? DateTimeOffset.UtcNow : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid?> StoredBuyerAccountAsync(string connectionString, Guid orderId) =>
        await ScalarAsync(connectionString, "SELECT buyer_account_id FROM quotations.orders WHERE id = @id", orderId)
            is Guid id ? id : null;

    private static async Task<bool> StoredSetByMemberAsync(string connectionString, Guid orderId) =>
        (bool)(await ScalarAsync(
            connectionString, "SELECT buyer_account_set_by_member FROM quotations.orders WHERE id = @id", orderId))!;

    private static async Task<object?> ScalarAsync(string connectionString, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is DBNull ? null : value;
    }

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid tenantId, Guid orderId, long? version, string email)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BuyerAccountUrl(tenantId, orderId))
        {
            Content = JsonContent.Create(new { email }),
        };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid tenantId, Guid orderId, long version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuyerAccountUrl(tenantId, orderId));
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<OrderDetailResponse> GetDetailAsync(HttpClient client, Guid tenantId, Guid orderId)
    {
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderUrl(tenantId, orderId), TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<OrderDetailResponse> ReadDetailAsync(HttpResponseMessage response)
    {
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<IReadOnlyList<QuotationsOutboxMessage>> ChangedAuditsAsync(QepApiFactory factory, Guid orderId) =>
        (await OutboxMessagesAsync(factory, AuditEvent))
            .Where(message =>
            {
                using var payload = JsonDocument.Parse(message.PayloadJson);
                return payload.RootElement.GetProperty("action").GetString() == ChangedAction
                    && payload.RootElement.GetProperty("resourceId").GetString() == orderId.ToString();
            })
            .ToList();

    // La auditoría lleva sólo los dos ids (nunca correos), en el único campo libre del evento: changedFields.
    private static (Guid? Previous, Guid? Current) DetailsOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var details = Assert.Single(payload.RootElement.GetProperty("changedFields").EnumerateArray().ToArray());
        return (IdOf(details, "previousBuyerAccountId"), IdOf(details, "buyerAccountId"));

        static Guid? IdOf(JsonElement details, string name) =>
            details.GetProperty(name).ValueKind is JsonValueKind.Null ? null : details.GetProperty(name).GetGuid();
    }
}
