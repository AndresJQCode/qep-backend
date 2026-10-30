using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Billing.Application;
using static Modules.Billing.IntegrationTests.BillingApiHarness;

namespace Modules.Billing.IntegrationTests;

/// <summary>
/// <c>GET/PUT /settings</c> (spec cobros-recurrentes §4): la conexión de WhatsApp elegida para los avisos y las conexiones que
/// ofrece el puerto de salida.
/// </summary>
public sealed class BillingSettingsApiTests
{
    private static readonly Guid Tenant = Guid.Parse(BillingApiHarness.TenantId);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string SettingsUrl(string tenantId = BillingApiHarness.TenantId) => $"{BillingUrl(tenantId)}/settings";

    private static readonly BillingConnectionDto ReadyConnection =
        new(Guid.Parse("01900000-0000-7000-8000-0000000000c1"), "Línea principal", "+573001112233", true, null);

    private static async Task<(Testcontainers.PostgreSql.PostgreSqlContainer, QepApiFactory)> OpenWithConnectionsAsync(
        params BillingConnectionDto[] connections)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(
            database.GetConnectionString(),
            services => services.AddScoped<IBillingWhatsAppOutbound>(_ => new FakeOutbound(connections)));
        await SeedTenantAsync(factory);
        return (database, factory);
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // Sin conexiones de WhatsApp activas en el tenant: sin ajustes guardados y sin nada que ofrecer.
    [Fact]
    public async Task GetSettingsAnswersTheStoredConnectionAndWhatListConnectionsReturns()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);

        var empty = await manager.GetAsync(SettingsUrl(), Ct);

        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var dto = await empty.Content.ReadFromJsonAsync<BillingSettingsDto>(Ct);
        Assert.Null(dto!.WhatsAppConnectionId);
        Assert.Empty(dto.Connections);

        var (database2, factory2) = await OpenWithConnectionsAsync(ReadyConnection);
        await using var ___ = database2;
        using var ____ = factory2;
        using var manager2 = CreateManager(factory2);
        var put = await manager2.PutAsJsonAsync(SettingsUrl(), new { whatsAppConnectionId = ReadyConnection.Id }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await manager2.GetAsync(SettingsUrl(), Ct);

        var stored = await get.Content.ReadFromJsonAsync<BillingSettingsDto>(Ct);
        Assert.Equal(ReadyConnection.Id, stored!.WhatsAppConnectionId);
        Assert.Equal([ReadyConnection], stored.Connections);
        Assert.Equal(1, await CountAuditEntriesAsync(factory2, "billing.settings.updated", Tenant.ToString()));
    }

    private static readonly BillingConnectionDto PendingConnection =
        new(Guid.Parse("01900000-0000-7000-8000-0000000000c2"), "Línea nueva", null, false, "template_not_approved");

    [Fact]
    public async Task GetSettingsListsTheConnectionsWithReadiness()
    {
        var (database, factory) = await OpenWithConnectionsAsync(ReadyConnection, PendingConnection);
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);

        var get = await manager.GetAsync(SettingsUrl(), Ct);

        var dto = await get.Content.ReadFromJsonAsync<BillingSettingsDto>(Ct);
        Assert.Equal([ReadyConnection, PendingConnection], dto!.Connections);
    }

    // Elegirla antes de que Meta apruebe las plantillas es válido: el envío falla con template_not_approved hasta entonces.
    [Fact]
    public async Task PutAcceptsAListedConnectionEvenIfNotReadyYet()
    {
        var (database, factory) = await OpenWithConnectionsAsync(ReadyConnection, PendingConnection);
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(SettingsUrl(), new { whatsAppConnectionId = PendingConnection.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(PendingConnection.Id, (await put.Content.ReadFromJsonAsync<BillingSettingsDto>(Ct))!.WhatsAppConnectionId);
    }

    [Fact]
    public async Task PutWithAnUnknownConnectionAnswers422()
    {
        var (database, factory) = await OpenWithConnectionsAsync(ReadyConnection);
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(SettingsUrl(), new { whatsAppConnectionId = Guid.CreateVersion7() }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("billing.settings.connection_unknown", await CodeOfAsync(put));
    }

    [Fact]
    public async Task PutNullClearsTheConnection()
    {
        var (database, factory) = await OpenWithConnectionsAsync(ReadyConnection);
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await manager.PutAsJsonAsync(SettingsUrl(), new { whatsAppConnectionId = ReadyConnection.Id }, Ct);

        var put = await manager.PutAsJsonAsync(SettingsUrl(), new { whatsAppConnectionId = (Guid?)null }, Ct);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null((await put.Content.ReadFromJsonAsync<BillingSettingsDto>(Ct))!.WhatsAppConnectionId);
        var get = await manager.GetAsync(SettingsUrl(), Ct);
        Assert.Null((await get.Content.ReadFromJsonAsync<BillingSettingsDto>(Ct))!.WhatsAppConnectionId);
    }

    [Fact]
    public async Task APureReaderCannotReadOrChangeTheSettings()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var get = await reader.GetAsync(SettingsUrl(), Ct);
        var put = await reader.PutAsJsonAsync(SettingsUrl(), new { whatsAppConnectionId = (Guid?)null }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    private sealed class FakeOutbound(BillingConnectionDto[] connections) : IBillingWhatsAppOutbound
    {
        public Task<BillingSendResult> SendAsync(
            BillingWhatsAppContext context, BillingCustomerMessage message, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<BillingSendResult>(new BillingSendResult.Failed("channel_not_configured"));

        public Task<IReadOnlyList<BillingConnectionDto>> ListConnectionsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BillingConnectionDto>>(connections);
    }
}
