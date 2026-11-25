using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Siigo.Application;
using static Modules.Siigo.IntegrationTests.InvoiceWhatsAppHarness;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;
using static Modules.Siigo.IntegrationTests.SiigoInvoicingHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Spec 2026-11-21: <c>POST …/siigo-invoice/{invoiceId}/send-whatsapp</c> de punta a punta con el host entero. Siigo, los canales, el sender
/// y R2 son dobles; el envío, el canal y el teléfono son de Quotations y acá sólo se ve el contrato HTTP y que nada se guarde.
/// </summary>
public sealed class SiigoInvoiceWhatsAppApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SendRoute(InvoicingWorld world, Guid invoiceId) => $"{world.BaseRoute}/{invoiceId}/send-whatsapp";

    // Factura creada (por el host del mundo) y host derivado con los canales de la prueba y su cliente.
    private static async Task<(HttpClient Client, InvoiceWhatsAppDoubles Doubles, Guid InvoiceId)> ArrangeSendAsync(
        InvoicingWorld world, params WhatsAppChannel[] channels)
    {
        var invoiceId = (await IssueRowsThroughBatchAsync(world))[0].GetProperty("id").GetGuid();
        SiigoServesThePdf(world);
        var (factory, doubles) = Host(world, channels);
        var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, InvoicerPermissions);
        return (client, doubles, invoiceId);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("code").GetString()!;

    [Fact]
    public async Task PostSendsTheInvoicePdfByMetaAndReturns204()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (client, doubles, invoiceId) = await ArrangeSendAsync(world, Meta);

        var response = await client.PostAsync(SendRoute(world, invoiceId), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var (channel, message) = Assert.Single(doubles.Sender.Invoices);
        Assert.Equal(Meta.ConnectionId, channel.ConnectionId);
        Assert.Equal(PdfBytes, message.Document);
        Assert.Null(message.DocumentUrl);
        Assert.Equal("FV-1-123", message.InvoiceNumber);
        Assert.Empty(doubles.Storage.Published);
        Assert.Equal(
            1,
            await ScalarAsync<long>(
                world.ConnectionString,
                "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'siigo.invoice.whatsapp_sent' AND resource_id = @r",
                ("t", world.TenantId),
                ("r", invoiceId.ToString())));
    }

    [Fact]
    public async Task PostByZenviaPublishesOneCopyUnderInvoices()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (client, doubles, invoiceId) = await ArrangeSendAsync(world, Zenvia);

        var response = await client.PostAsync(SendRoute(world, invoiceId), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var published = Assert.Single(doubles.Storage.Published);
        Assert.StartsWith($"{RecordingInvoiceStorage.PublicBase}invoices/", published.Url, StringComparison.Ordinal);
        Assert.Equal(PdfBytes, published.Content);
        var (channel, message) = Assert.Single(doubles.Sender.Invoices);
        Assert.IsType<ZenviaWhatsAppChannel>(channel);
        Assert.Equal(published.Url, message.DocumentUrl);
        Assert.Null(message.Document);
    }

    [Fact]
    public async Task ARoleWithoutInvoiceIssueGets403()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (_, doubles, invoiceId) = await ArrangeSendAsync(world, Meta);
        var calls = world.Siigo.Calls.Count;
        var (factory, _) = Host(world, Meta);
        using var other = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, SiigoPermissions.SettingsManage);

        var response = await other.PostAsync(SendRoute(world, invoiceId), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(calls, world.Siigo.Calls.Count);
        Assert.Empty(doubles.Sender.Invoices);
    }

    [Fact]
    public async Task AnotherTenantsInvoiceGets403Not404()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (_, doubles, invoiceId) = await ArrangeSendAsync(world, Meta);
        var intruder = await RegisterTenantAsync(world.Factory);
        await EnableSiigoAsync(world.ConnectionString, intruder.TenantId);
        var (factory, intruderDoubles) = Host(world, Meta);
        using var client = CreateClient(factory, intruder.OwnerUserId, intruder.TenantId, InvoicerPermissions);

        var viaForeignRoute = await client.PostAsync(SendRoute(world, invoiceId), null, Ct);
        var viaOwnRoute = await client.PostAsync(
            $"/api/v1/tenants/{intruder.TenantId}/orders/{world.OrderId}/siigo-invoice/{invoiceId}/send-whatsapp", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, viaForeignRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaOwnRoute.StatusCode);
        Assert.Empty(doubles.Sender.Invoices);
        Assert.Empty(intruderDoubles.Sender.Invoices);
    }

    [Fact]
    public async Task AnInvoiceThatIsNotOfTheOrderIs404AndNothingIsCalled()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (client, doubles, _) = await ArrangeSendAsync(world, Meta);
        var calls = world.Siigo.Calls.Count;

        var response = await client.PostAsync(SendRoute(world, Guid.NewGuid()), null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("siigo.invoice.not_found", await CodeAsync(response));
        Assert.Equal(calls, world.Siigo.Calls.Count);
        Assert.Empty(doubles.Sender.Invoices);
    }

    [Fact]
    public async Task NoChannelReturns422InvoiceWhatsappNoChannel()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (client, doubles, invoiceId) = await ArrangeSendAsync(world);

        var response = await client.PostAsync(SendRoute(world, invoiceId), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invoice.whatsapp.no_channel", await CodeAsync(response));
        Assert.Empty(doubles.Sender.Invoices);
    }

    [Fact]
    public async Task ACustomerWithoutPhoneReturns422NoPhone()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (client, doubles, invoiceId) = await ArrangeSendAsync(world, Meta);
        await ExecuteAsync(world.ConnectionString, "UPDATE customers.customers SET phone = ''");
        var pdfPath = "/v1/invoices/d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f/pdf";
        var before = world.Siigo.CallsTo(pdfPath);

        var response = await client.PostAsync(SendRoute(world, invoiceId), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invoice.whatsapp.no_phone", await CodeAsync(response));
        Assert.Empty(doubles.Sender.Invoices);
        // El handler pide el PDF antes de entregarlo (Quotations mira el teléfono dentro del envío): una sola lectura, y nada salió.
        Assert.Equal(1, world.Siigo.CallsTo(pdfPath) - before);
    }

    [Fact]
    public async Task AZenviaSendWithoutAPublicBucketReturns422SendFailedNotA500()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (client, doubles, invoiceId) = await ArrangeSendAsync(world, Zenvia);
        doubles.Storage.PublishFailure = new InvalidOperationException("Public bucket is not configured.");

        var response = await client.PostAsync(SendRoute(world, invoiceId), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invoice.whatsapp.send_failed", await CodeAsync(response));
        Assert.Empty(doubles.Sender.Invoices);
    }

    [Fact]
    public async Task TheInvoiceListCarriesCanSendWhatsApp()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        var (oneChannel, _) = Host(world, Meta);
        var (noChannel, _) = Host(world, []);
        var (twoChannels, _) = Host(world, Meta, Zenvia);

        async Task<bool> CanSendAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
        {
            using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, InvoicerPermissions);
            return (await ReadAsync(await client.GetAsync(world.BaseRoute, Ct)))[0].GetProperty("canSendWhatsApp").GetBoolean();
        }

        Assert.True(await CanSendAsync(oneChannel));
        Assert.False(await CanSendAsync(noChannel));
        // Dos canales sin elección guardada: el envío fallaría con channel_required, así que no se ofrece.
        Assert.False(await CanSendAsync(twoChannels));
    }

    [Fact]
    public async Task TheListOfAnInvoiceWithoutPdfIsNotSendable()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest(
            "/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_item","Message":"x"}]}"""));
        await IssueThroughBatchAsync(world);
        var (factory, _) = Host(world, Meta);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, InvoicerPermissions);

        var row = (await ReadAsync(await client.GetAsync(world.BaseRoute, Ct)))[0];

        Assert.False(row.GetProperty("hasPdf").GetBoolean());
        Assert.False(row.GetProperty("canSendWhatsApp").GetBoolean());
    }
}
