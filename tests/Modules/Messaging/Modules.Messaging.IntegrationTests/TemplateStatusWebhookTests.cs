// tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateStatusWebhookTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.3 «Webhook» de punta a punta: el evento cambia la fila del espejo de las conexiones de esa WABA (y sólo esas),
/// el catálogo de envío empieza a ver la aprobada, el rechazo conserva el motivo, y un id desconocido no escribe nada pero la entrega
/// queda procesada.</summary>
public sealed class TemplateStatusWebhookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Listing = """
        {"data":[{"id":"1407","name":"promo","language":"es","status":"PENDING","category":"MARKETING","parameter_format":"POSITIONAL",
                  "components":[{"type":"BODY","text":"Hola {{1}}"}]}]}
        """;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var other = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Otra WABA", "113", "333");
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Listing);
        factory.MetaHandler.Respond("/333/message_templates", HttpStatusCode.OK, Listing);
        var admin = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions);
        // Llena los dos espejos: las dos conexiones tienen una plantilla con el mismo id de Meta, como pasa con WABAs distintas.
        await admin.GetAsync(TemplateManagementUrl(tenant.TenantId, connectionId), Ct);
        await admin.GetAsync(TemplateManagementUrl(tenant.TenantId, other), Ct);
        return new Fixture(factory, connectionString, tenant, connectionId, admin, factory.CreateClient());
    }

    private static Task<string> StatusOfAsync(Fixture f, string wabaName) =>
        ScalarAsync<string>(f.ConnectionString,
            """
            SELECT t.status || '|' || coalesce(t.status_reason, '-')
              FROM messaging.message_templates t JOIN integrations.connections c ON c.id = t.connection_id
             WHERE c.name = @name
            """, ("name", wabaName));

    [Fact]
    public async Task AnApprovedEventUpdatesOnlyThatWabaAndTheSendCatalogSeesIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var catalogUrl = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates";
        Assert.Empty((await reader.GetFromJsonAsync<JsonElement>(catalogUrl, Ct)).EnumerateArray());

        var response = await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 1407L, "promo", "es", "APPROVED", "NONE"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("APPROVED|-", await StatusOfAsync(f, "Ventas"));
        Assert.Equal("PENDING|-", await StatusOfAsync(f, "Otra WABA"));
        var catalog = (await reader.GetFromJsonAsync<JsonElement>(catalogUrl, Ct)).EnumerateArray().ToArray();
        Assert.Equal("promo", Assert.Single(catalog).GetProperty("name").GetString());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task ARejectedEventKeepsTheReasonAndTheScreenShowsIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", "1407", "promo", "es", "REJECTED", "INVALID_FORMAT"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("REJECTED|INVALID_FORMAT", await StatusOfAsync(f, "Ventas"));
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        var template = Assert.Single(screen.GetProperty("templates").EnumerateArray());
        Assert.Equal(("REJECTED", "INVALID_FORMAT"), (template.GetProperty("status").GetString(), template.GetProperty("statusReason").GetString()));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE resource_type = 'message_template'"));
    }

    [Fact]
    public async Task AnUnknownTemplateIdWritesNothingAndTheDeliveryIsProcessed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 999L, "nueva", "es", "APPROVED", "NONE"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("PENDING|-", await StatusOfAsync(f, "Ventas"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task AnUnknownEventIsStoredAsTextForTheScreen()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 1407L, "promo", "es", "some_new_event", null));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("SOME_NEW_EVENT|-", await StatusOfAsync(f, "Ventas"));
    }

    /// <summary>Revisión final F2: <c>FLAGGED</c> es feedback negativo, no un estado; la plantilla sigue enviable. La fila queda como
    /// estaba (estado y motivo) y el catálogo de envío la sigue devolviendo.</summary>
    [Fact]
    public async Task AFlaggedEventLeavesTheRowAsItWasAndTheTemplateStaysSendable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var catalogUrl = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates";
        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 1407L, "promo", "es", "APPROVED", "NONE"));
        await DrainDeliveriesAsync(f.Factory);

        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 1407L, "promo", "es", "FLAGGED", "LOW_QUALITY"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("APPROVED|-", await StatusOfAsync(f, "Ventas"));
        var catalog = (await reader.GetFromJsonAsync<JsonElement>(catalogUrl, Ct)).EnumerateArray().ToArray();
        Assert.Equal("promo", Assert.Single(catalog).GetProperty("name").GetString());
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }

    /// <summary>Revisión final F2: <c>REINSTATED</c> («ya no está marcada ni deshabilitada y se puede volver a enviar») deja la fila
    /// <c>APPROVED</c>, y el catálogo de envío vuelve a verla.</summary>
    [Fact]
    public async Task AReinstatedEventOnAPausedRowLeavesItApprovedAndTheSendCatalogSeesIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var catalogUrl = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates";
        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 1407L, "promo", "es", "PAUSED", "NONE"));
        await DrainDeliveriesAsync(f.Factory);
        Assert.Equal("PAUSED|-", await StatusOfAsync(f, "Ventas"));
        Assert.Empty((await reader.GetFromJsonAsync<JsonElement>(catalogUrl, Ct)).EnumerateArray());

        await PostWebhookAsync(f.Anonymous, MetaPayloads.TemplateStatusUpdate("222", 1407L, "promo", "es", "REINSTATED", "NONE"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("APPROVED|-", await StatusOfAsync(f, "Ventas"));
        var catalog = (await reader.GetFromJsonAsync<JsonElement>(catalogUrl, Ct)).EnumerateArray().ToArray();
        Assert.Equal("promo", Assert.Single(catalog).GetProperty("name").GetString());
    }
}
