using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Los ajustes del pedido por tenant (spec 2026-11-10 §6): <c>GET</c> y <c>PUT /orders-settings</c>,
/// con los permisos de settings y el documento entero en los dos sentidos.</summary>
public sealed class OrderSettingsApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";

    private static string SettingsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders-settings";

    [Fact]
    public async Task ATenantThatNeverSavedGetsTheDefaults()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        var settings = await client.GetFromJsonAsync<SettingsPayload>(
            SettingsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(new SettingsPayload(false, true, false), settings);
    }

    [Fact]
    public async Task SavingReplacesTheWholeDocumentAndIsAudited()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var put = await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { requirePaymentProofToMarkPaid = true, requireFullPaymentToInvoice = false, autoArchive = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(
            new SettingsPayload(true, false, true),
            await put.Content.ReadFromJsonAsync<SettingsPayload>(TestContext.Current.CancellationToken));
        Assert.Equal(
            new SettingsPayload(true, false, true),
            await client.GetFromJsonAsync<SettingsPayload>(SettingsUrl(tenantId), TestContext.Current.CancellationToken));
        var audit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => ActionOf(message) == "quotations.order_settings.updated");
        using var payload = JsonDocument.Parse(audit.PayloadJson);
        Assert.Equal(tenantId.ToString(), payload.RootElement.GetProperty("resourceId").GetString());
    }

    [Fact]
    public async Task AMissingFieldIsAValidationError()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var ownedClient = client;

        var response = await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { requirePaymentProofToMarkPaid = true, requireFullPaymentToInvoice = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("AutoArchive", out _));
    }

    [Fact]
    public async Task ReadingNeedsTheSettingsPermission()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var get = await client.GetAsync(SettingsUrl(tenantId), TestContext.Current.CancellationToken);
        var put = await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { requirePaymentProofToMarkPaid = true, requireFullPaymentToInvoice = true, autoArchive = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    private sealed record SettingsPayload(
        bool RequirePaymentProofToMarkPaid, bool RequireFullPaymentToInvoice, bool AutoArchive);
}
