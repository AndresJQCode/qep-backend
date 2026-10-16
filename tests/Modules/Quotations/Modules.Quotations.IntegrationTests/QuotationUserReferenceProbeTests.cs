using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La sonda con la que Quotations retiene a un usuario mientras un pedido lo referencie, contra Postgres y resuelta
/// del contenedor real, como la consulta la limpieza de usuarios huérfanos. Desde el spec 2026-10-15 el pedido tiene
/// su propio asesor y su propio historial: los dos son referencias aunque ninguna cotización apunte a la membresía.
/// </summary>
public sealed class QuotationUserReferenceProbeTests
{
    [Fact]
    public async Task TheOrderAdvisorAndTheOrderHistoryEachRetainTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var orderId = await CreateManualOrderAsync(client, tenantId);
        var stranger = Guid.CreateVersion7();

        // Sólo el asesor del pedido apunta al dueño.
        await ExecuteAsync(connectionString,
            "UPDATE quotations.orders SET converted_by = @stranger WHERE id = @order; " +
            "UPDATE quotations.order_history SET member_id = @stranger WHERE order_id = @order",
            stranger, orderId);
        var byAdvisor = await HasReferencesAsync(factory, ownerUserId);

        // Sólo el historial del pedido apunta al dueño.
        await ExecuteAsync(connectionString,
            "UPDATE quotations.order_history SET member_id = (SELECT advisor_id FROM quotations.orders WHERE id = @order) WHERE order_id = @order; " +
            "UPDATE quotations.orders SET advisor_id = @stranger WHERE id = @order",
            stranger, orderId);
        var byHistory = await HasReferencesAsync(factory, ownerUserId);

        // Ya nada apunta al dueño.
        await ExecuteAsync(connectionString,
            "UPDATE quotations.order_history SET member_id = @stranger WHERE order_id = @order",
            stranger, orderId);
        var byNothing = await HasReferencesAsync(factory, ownerUserId);

        Assert.Equal((true, true, false), (byAdvisor, byHistory, byNothing));
    }

    private static async Task<Guid> CreateManualOrderAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders",
            new CreateOrderRequest(
                clientId, null, null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new OrderEditItemRequest(productId, 1m)], "PaymentPending", []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail.Order.Id;
    }

    private static async Task<bool> HasReferencesAsync(QepApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var probe = Assert.Single(
            scope.ServiceProvider.GetServices<IUserReferenceProbe>(),
            candidate => candidate.Source == "quotations");
        return await probe.HasReferencesAsync(userId, TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, Guid stranger, Guid orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stranger", stranger);
        command.Parameters.AddWithValue("order", orderId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
