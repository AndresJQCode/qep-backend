using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Application;
using Modules.Quotations.IntegrationTests;
using Modules.Shopify.Infrastructure.Workers;
using Modules.Storage.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-15: el flujo de punta a punta sin dobles en el camino del pedido. Un webhook firmado entra por la ruta
/// real, el worker de entregas (corrido a mano, <c>DrainAsync</c>) lo lleva al espejo y al importador, y el pedido queda en
/// Quotations con su cliente, su envío y su evento. Lo que la pantalla hace después (aprobar, facturar, subir comprobantes,
/// reconocer la transportadora, exportar el Excel) va por las APIs de Quotations, como lo haría el frontend.
/// </summary>
public sealed class ShopifyImportFlowTests
{
    private const string ShopifyOrderKeyPrefix = "954889:";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ImportWithDocumentCreatesCompleteCustomerWithCuc()
    {
        await using var flow = await Flow.StartAsync();

        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included"));

        Assert.Equal("Imported", await flow.MirrorAsync(5550000000001, "import_state"));
        Assert.Equal(
            $"Complete|true|Cc 1036000111|{flow.World.ClassificationId}|Laura Gómez",
            await flow.ScalarAsync(
                """
                SELECT c.completeness || '|' || (coalesce(c.cuc, '') <> '') || '|' || c.identification_type || ' '
                       || c.identification_number || '|' || c.classification_id || '|' || c.name
                FROM shopify.orders m JOIN customers.customers c ON c.id = m.qep_customer_id AND c.tenant_id = m.tenant_id
                WHERE m.shopify_order_id = 5550000000001
                """));
        // El cliente completo no deja aviso customer_incomplete (el único aviso es el de la transportadora, que el tenant no tiene).
        Assert.Equal("carrier_unrecognized", await flow.MirrorAsync(5550000000001, "SELECT string_agg(w ->> 'code', ',') FROM jsonb_array_elements(warnings) w"));
    }

    [Fact]
    public async Task ImportWithoutDocumentIncompleteAndPendingOrder()
    {
        await using var flow = await Flow.StartAsync();

        await flow.DeliverAsync("orders/create", "wh-1", Fixture("no-document"));

        Assert.Equal(
            "Incomplete|none|Pending|Shopify",
            await flow.ScalarAsync(
                """
                SELECT c.completeness || '|' || coalesce(c.cuc, 'none') || '|' || o.status || '|' || o.source
                FROM shopify.orders m
                JOIN customers.customers c ON c.id = m.qep_customer_id AND c.tenant_id = m.tenant_id
                JOIN quotations.orders o ON o.id = m.qep_order_id AND o.tenant_id = m.tenant_id
                WHERE m.shopify_order_id = 5550000000011
                """));
        // §6.2.1 paso 4: sin documento, el aviso no trae documento que mostrar.
        Assert.Equal("""{"code": "customer_incomplete", "detail": null}""", await flow.MirrorAsync(5550000000011, "warnings -> 0"));
    }

    // §6.2.1 paso 2: el comprador de un segundo pedido es el mismo cliente si lo delata el id de cliente de Shopify ya visto en el
    // espejo, el teléfono o el correo, cada uno sin los anteriores. Ninguno de los tres pedidos trae documento.
    [Fact]
    public async Task SecondOrderSameBuyerReusesCustomer()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included"));

        // Mismo id de cliente de Shopify; teléfono y correo distintos.
        await flow.DeliverAsync("orders/create", "wh-2", Fixture("taxes-included", order =>
        {
            Renumber(order, 5550000000101, "#1101");
            order["note_attributes"] = new JsonArray();
            SetBuyer(order, customerId: 7770000000001, phone: "+573009999901", email: "otra.persona@example.com");
        }));

        // Otro id de cliente y otro correo; el mismo teléfono.
        await flow.DeliverAsync("orders/create", "wh-3", Fixture("taxes-included", order =>
        {
            Renumber(order, 5550000000102, "#1102");
            order["note_attributes"] = new JsonArray();
            SetBuyer(order, customerId: 7770000000099, phone: "+573001112233", email: "tercera@example.com");
        }));

        // Otro id de cliente, sin teléfono en ningún lado; el mismo correo.
        await flow.DeliverAsync("orders/create", "wh-4", Fixture("taxes-included", order =>
        {
            Renumber(order, 5550000000103, "#1103");
            order["note_attributes"] = new JsonArray();
            SetBuyer(order, customerId: 7770000000098, phone: null, email: "laura.gomez@example.com");
        }));

        Assert.Equal(
            "Imported,Imported,Imported,Imported|1",
            await flow.ScalarAsync(
                """
                SELECT string_agg(import_state, ',' ORDER BY shopify_order_id) || '|' || count(DISTINCT qep_customer_id)
                FROM shopify.orders
                """));
        Assert.Equal(1L, await ScalarAsync<long>(flow.ConnectionString, "SELECT count(*) FROM customers.customers"));
    }

    // §6.2.3: el pedido de Shopify nace sin cotización, así que no aparece en la lista de cotizaciones.
    [Fact]
    public async Task ImportNoQuotationNotInQuotationsList()
    {
        await using var flow = await Flow.StartAsync();

        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included"));

        Assert.Equal("none", await flow.ScalarAsync("SELECT coalesce(quotation_id::text, 'none') FROM quotations.orders"));
        Assert.Equal(0L, await ScalarAsync<long>(flow.ConnectionString, "SELECT count(*) FROM quotations.quotations"));
        using var client = flow.Client(QuotationsPermissions.QuotationRead);
        var page = await client.GetFromJsonAsync<JsonObject>($"/api/v1/tenants/{flow.World.TenantId}/quotations", Ct);
        Assert.Equal(0, page!["total"]!.GetValue<int>());
    }

    // Spec 2026-10-15 §6.2.3 y CLAUDE.md (pedidos sin cotización): el evento de alta lleva source y quotationId nulo, que es lo que
    // Pipeline lee por nombre.
    [Fact]
    public async Task ImportPublishesOrderCreatedWithShopifySource()
    {
        await using var flow = await Flow.StartAsync();

        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included"));

        var orderId = await flow.ScalarAsync("SELECT qep_order_id::text FROM shopify.orders");
        var payload = await flow.ScalarAsync(
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = 'quotations.order.created.v1'");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(
            ("Shopify", JsonValueKind.Null, orderId, flow.World.TenantId.ToString(), flow.World.AdvisorMembershipId.ToString()),
            (root.GetProperty("source").GetString(), root.GetProperty("quotationId").ValueKind, root.GetProperty("orderId").GetString(),
                root.GetProperty("tenantId").GetString(), root.GetProperty("advisorMemberId").GetString()));
        Assert.Equal(1L, await ScalarAsync<long>(
            flow.ConnectionString, "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'quotations.order.created.v1'"));
    }

    // §6.2.2 y spec del flete: el envío cobrado entra al total del pedido y sale en «Valor Flete» del Excel; el cliente incompleto
    // no tiene CUC y «Documento» sale vacío.
    [Fact]
    public async Task ImportChargedFreightInTotalAndExcel()
    {
        await using var flow = await Flow.StartAsync();

        await flow.DeliverAsync("orders/create", "wh-1", Fixture("no-document", order =>
        {
            SetShipping(order, "15000.00");
            order["total_price"] = "115000.00";
            order["total_price_set"]!["shop_money"]!["amount"] = "115000.00";
            order["total_price_set"]!["presentment_money"]!["amount"] = "115000.00";
        }));

        Assert.Equal(
            "Charged|15000.00|115000.00|0.00",
            await flow.ScalarAsync(
                """
                SELECT o.freight_charge_mode || '|' || o.freight_amount || '|' || o.total || '|' || m.total_difference
                FROM shopify.orders m JOIN quotations.orders o ON o.id = m.qep_order_id AND o.tenant_id = m.tenant_id
                """));
        var sheet = await flow.ExportOrdersAsync();
        var header = sheet.Rows[0];
        var row = Assert.Single(sheet.Rows.Skip(1));
        var freight = Column(header, "Valor Flete");
        Assert.Equal(15_000m, decimal.Parse(row[freight], System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(sheet.NumericCells[1][freight]);
        Assert.Equal(string.Empty, row[Column(header, "Documento")]);
    }

    // Spec 2026-10-15 D-S25: el título que ninguna transportadora reconoció se reconoce desde el pedido y se recuerda;
    // el siguiente pedido con el mismo título entra con la transportadora y sin aviso.
    [Fact]
    public async Task ImportUnrecognizedCarrierThenRecognizeNextOrderMatches()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included"));
        Assert.Equal("carrier_unrecognized", await flow.MirrorAsync(5550000000001, "warnings -> 0 ->> 'code'"));
        var firstOrder = Guid.Parse(await flow.MirrorAsync(5550000000001, "qep_order_id::text") ?? string.Empty);
        var carrierId = await flow.CreateCarrierAsync("Envíos SV");

        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var recognized = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{flow.World.TenantId}/orders/{firstOrder}/carrier-recognition",
            new { carrierId, rememberName = true },
            Ct);
        Assert.Equal(HttpStatusCode.OK, recognized.StatusCode);

        await flow.DeliverAsync("orders/create", "wh-2", Fixture("taxes-included", order => Renumber(order, 5550000000201, "#1201")));

        Assert.Equal("Imported", await flow.MirrorAsync(5550000000201, "import_state"));
        Assert.Null(await flow.MirrorAsync(5550000000201, "warnings::text"));
        Assert.Equal(
            $"{carrierId}|{carrierId}",
            await flow.ScalarAsync(
                """
                SELECT string_agg(o.carrier_id::text, '|' ORDER BY m.shopify_order_id)
                FROM shopify.orders m JOIN quotations.orders o ON o.id = m.qep_order_id AND o.tenant_id = m.tenant_id
                """));
    }

    // La fila del espejo se perdió (borrada a mano, o el worker cayó entre crear el pedido y guardarla). El mismo
    // pedido vuelve a llegar: la clave externa lo frena y la fila nueva apunta al pedido que ya existía.
    [Fact]
    public async Task ReimportAfterMirrorRowDeletedDoesNotDuplicateOrder()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included"));
        var orderId = await flow.ScalarAsync("SELECT qep_order_id::text FROM shopify.orders");
        await ExecuteAsync(flow.ConnectionString, "DELETE FROM shopify.orders");

        await flow.DeliverAsync("orders/updated", "wh-2", Fixture("taxes-included", order => order["updated_at"] = "2026-10-01T10:30:00-05:00"));

        Assert.Equal(1L, await ScalarAsync<long>(flow.ConnectionString, "SELECT count(*) FROM quotations.orders"));
        Assert.Equal($"Imported|{orderId}", await flow.ScalarAsync("SELECT import_state || '|' || qep_order_id FROM shopify.orders"));
    }

    // Con la fila perdida, el pedido llega cancelado. La fila es nueva, pero el pedido existe en QEP: se anula,
    // no queda vivo con la fila ignorada.
    [Fact]
    public async Task ReimportAfterMirrorRowDeletedCancelledCancelsTheExistingOrder()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included"));
        var orderId = await flow.ScalarAsync("SELECT qep_order_id::text FROM shopify.orders");
        await ExecuteAsync(flow.ConnectionString, "DELETE FROM shopify.orders");

        await flow.DeliverAsync("orders/cancelled", "wh-2", Cancelled(Fixture("taxes-included")));

        Assert.Equal($"Cancelled|{orderId}", await flow.ScalarAsync("SELECT import_state || '|' || qep_order_id FROM shopify.orders"));
        Assert.Equal("Cancelled|Cancelado en Shopify: customer", await flow.ScalarAsync("SELECT status || '|' || cancellation_reason FROM quotations.orders"));
    }

    // §5.2 y §6.1: con el módulo apagado el webhook se recibe y el espejo se guarda, pero no se importa. Al prenderlo otra vez, la
    // conciliación reintenta la fila en espera y la importa.
    [Fact]
    public async Task ModuleOffStoresMirrorWithoutImportingThenReenableImports()
    {
        await using var flow = await Flow.StartAsync();
        await ExecuteAsync(
            flow.ConnectionString,
            "UPDATE tenancy.tenant_modules SET status = 'inactive', status_changed_at = now() WHERE tenant_id = @t AND module_key = 'shopify'",
            ("t", flow.World.TenantId));

        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included"));

        Assert.Equal("1|none|none", await flow.DeliveryAsync("wh-1"));
        Assert.Equal("Waiting", await flow.MirrorAsync(5550000000001, "import_state"));
        Assert.Equal(0L, await ScalarAsync<long>(flow.ConnectionString, "SELECT count(*) FROM quotations.orders"));

        await EnableShopifyAsync(flow.ConnectionString, flow.World.TenantId);
        await flow.ReconcileAsync();

        Assert.Equal("Imported", await flow.MirrorAsync(5550000000001, "import_state"));
        Assert.Equal(1L, await ScalarAsync<long>(flow.ConnectionString, "SELECT count(*) FROM quotations.orders"));
    }

    // §6.3: Shopify cancela cuatro pedidos que quedaron en cuatro lugares distintos de QEP. Pendiente y aprobado se anulan; el
    // facturado no se toca y la fila avisa invoiced_before_cancel; el que esperaba un producto se ignora y nunca nace.
    [Fact]
    public async Task CancelOnPendingApprovedInvoicedAndWaiting()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-p", Fixture("taxes-included", order => Renumber(order, 5550000000301, "#1301")));
        await flow.DeliverAsync("orders/create", "wh-a", Fixture("taxes-included", order => Renumber(order, 5550000000302, "#1302")));
        await flow.DeliverAsync("orders/create", "wh-i", Fixture("taxes-included", order => Renumber(order, 5550000000303, "#1303")));
        await flow.DeliverAsync("orders/create", "wh-w", Fixture("taxes-included", order =>
        {
            Renumber(order, 5550000000304, "#1304");
            order["line_items"]![0]!["sku"] = "NO-EXISTE";
        }));
        Assert.Equal("product_unmatched", await flow.MirrorAsync(5550000000304, "wait_reason"));
        using var approver = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderApprove, OrdersPermissions.OrderInvoice);
        var approved = await flow.QepOrderAsync(5550000000302);
        var invoiced = await flow.QepOrderAsync(5550000000303);
        foreach (var (orderId, action) in new[] { (approved, "approve"), (invoiced, "approve"), (invoiced, "invoice") })
        {
            var response = await approver.PostAsync($"/api/v1/tenants/{flow.World.TenantId}/orders/{orderId}/{action}", content: null, Ct);
            Assert.True(response.IsSuccessStatusCode, $"{action} {orderId}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        }

        foreach (var (id, name) in new[] { (5550000000301L, "#1301"), (5550000000302L, "#1302"), (5550000000303L, "#1303"), (5550000000304L, "#1304") })
        {
            await flow.DeliverAsync("orders/cancelled", $"wh-cancel-{id}", Cancelled(Fixture("taxes-included", order => Renumber(order, id, name))));
        }

        Assert.Equal(
            "5550000000301|Cancelled||Cancelled,"
            + "5550000000302|Cancelled||Cancelled,"
            + "5550000000303|Cancelled|invoiced_before_cancel|Invoiced,"
            + "5550000000304|Ignored||",
            await flow.ScalarAsync(
                """
                SELECT string_agg(
                    m.shopify_order_id || '|' || m.import_state || '|' || coalesce(m.wait_detail ->> 'reason', '') || '|'
                    || coalesce(o.status, ''), ',' ORDER BY m.shopify_order_id)
                FROM shopify.orders m LEFT JOIN quotations.orders o ON o.id = m.qep_order_id AND o.tenant_id = m.tenant_id
                """));
        Assert.Equal(3L, await ScalarAsync<long>(flow.ConnectionString, "SELECT count(*) FROM quotations.orders"));
    }

    // §6.3 y H5: un pedido pendiente que Shopify da por pagado se sincroniza; uno al que el equipo ya le subió comprobantes no se
    // toca (el pago lo lleva QEP) y la fila guarda el rechazo en last_error.
    [Fact]
    public async Task PaymentWithAndWithoutProofs()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included", order =>
        {
            Renumber(order, 5550000000401, "#1401");
            order["financial_status"] = "pending";
        }));
        await flow.DeliverAsync("orders/create", "wh-2", Fixture("taxes-included", order =>
        {
            Renumber(order, 5550000000402, "#1402");
            order["financial_status"] = "pending";
        }));
        var withProofs = await flow.QepOrderAsync(5550000000402);
        await flow.AddProofAsync(withProofs);

        foreach (var (id, name) in new[] { (5550000000401L, "#1401"), (5550000000402L, "#1402") })
        {
            await flow.DeliverAsync("orders/paid", $"wh-paid-{id}", Fixture("taxes-included", order =>
            {
                Renumber(order, id, name);
                order["updated_at"] = "2026-10-01T11:00:00-05:00";
            }));
        }

        Assert.Equal(
            "5550000000401|paid|none|FullPaymentReceived,5550000000402|paid|order.order.payment_has_proofs|PartialPaymentReceived",
            await flow.ScalarAsync(
                """
                SELECT string_agg(
                    m.shopify_order_id || '|' || m.financial_status || '|' || coalesce(m.last_error, 'none') || '|' || o.payment_status,
                    ',' ORDER BY m.shopify_order_id)
                FROM shopify.orders m JOIN quotations.orders o ON o.id = m.qep_order_id AND o.tenant_id = m.tenant_id
                """));
    }

    private static int Column(IReadOnlyList<string> header, string name)
    {
        var index = header.ToList().IndexOf(name);
        Assert.True(index >= 0, $"The orders export has no \"{name}\" column.");
        return index;
    }

    private static JsonObject Fixture(string name, Action<JsonObject>? edit = null)
    {
        var order = (JsonObject)JsonNode.Parse(OrderFixture(name))!;
        edit?.Invoke(order);
        return order;
    }

    private static void Renumber(JsonObject order, long id, string name)
    {
        order["id"] = id;
        order["admin_graphql_api_id"] = $"gid://shopify/Order/{id}";
        order["name"] = name;
    }

    private static void SetBuyer(JsonObject order, long customerId, string? phone, string email)
    {
        order["email"] = email;
        order["phone"] = phone;
        var customer = order["customer"]!.AsObject();
        customer["id"] = customerId;
        customer["email"] = email;
        customer["phone"] = phone;
        order["shipping_address"]!["phone"] = phone;
    }

    private static void SetShipping(JsonObject order, string amount)
    {
        var line = order["shipping_lines"]![0]!.AsObject();
        line["price"] = amount;
        line["discounted_price"] = amount;
        foreach (var key in new[] { "price_set", "discounted_price_set" })
        {
            line[key]!["shop_money"]!["amount"] = amount;
            line[key]!["presentment_money"]!["amount"] = amount;
        }

        line["tax_lines"] = new JsonArray();
    }

    private static JsonObject Cancelled(JsonObject order)
    {
        order["updated_at"] = "2026-10-01T12:00:00-05:00";
        order["cancelled_at"] = "2026-10-01T12:00:00-05:00";
        order["cancel_reason"] = "customer";
        return order;
    }

    /// <summary>Un tenant con la tienda enlazada y configurada, el host con Storage en memoria (comprobantes y Excel) y sin los
    /// workers que la prueba corre a mano.</summary>
    private sealed class Flow : IAsyncDisposable
    {
        private readonly TestDatabase database;

        private Flow(TestDatabase database, QepApiFactory factory, ShopifyWorld world, InMemoryObjectStorage storage)
        {
            this.database = database;
            Factory = factory;
            World = world;
            Storage = storage;
            Webhooks = factory.CreateClient();
        }

        public QepApiFactory Factory { get; }

        public ShopifyWorld World { get; }

        public InMemoryObjectStorage Storage { get; }

        public HttpClient Webhooks { get; }

        public string ConnectionString => database.GetConnectionString();

        public static async Task<Flow> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var storage = new InMemoryObjectStorage();
            var factory = new QepApiFactory(database.GetConnectionString())
            {
                ConfigureTestServices = services =>
                {
                    services.RemoveAll<IObjectStorage>();
                    services.AddSingleton<IObjectStorage>(storage);
                    // El worker de exportaciones toma jobs cada 5 s; la prueba corre el suyo a mano (ExportOrdersAsync).
                    foreach (var worker in services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                            && descriptor.ImplementationType?.Name == "ExportJobWorker")
                        .ToArray())
                    {
                        services.Remove(worker);
                    }

                    // La conciliación habla con Shopify: acá no trae nada y sólo reintenta lo que espera.
                    services.AddHttpClient(Modules.Shopify.Infrastructure.Admin.ShopifyGraphQlClient.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => new FakeShopifyAdmin());
                },
            };
            var world = await ShopifyWorld.ArrangeAsync(factory, database.GetConnectionString());
            await world.ConfigureStoreAsync(factory);
            return new Flow(database, factory, world, storage);
        }

        public HttpClient Client(params string[] permissions) =>
            CreateClient(Factory, World.Tenant.OwnerUserId, World.TenantId, [.. SeedPermissions, .. permissions]);

        /// <summary>El webhook firmado y el worker corrido hasta vaciar la cola.</summary>
        public async Task DeliverAsync(string topic, string webhookId, JsonObject order)
        {
            var response = await PostWebhookAsync(Webhooks, topic, webhookId, Encoding.UTF8.GetBytes(order.ToJsonString()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var worker = ActivatorUtilities.CreateInstance<ShopifyWebhookDeliveryWorker>(Factory.Services);
            await worker.DrainAsync(Ct);
        }

        public async Task ReconcileAsync()
        {
            var worker = ActivatorUtilities.CreateInstance<ShopifyReconcileWorker>(Factory.Services);
            await worker.RunOnceAsync(Ct);
        }

        public Task<string> ScalarAsync(string sql) => ScalarAsync<string>(ConnectionString, sql);

        public async Task<string?> MirrorAsync(long shopifyOrderId, string expression)
        {
            await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand(
                $"SELECT ({expression})::text FROM shopify.orders WHERE shopify_order_id = @id", connection);
            command.Parameters.AddWithValue("id", shopifyOrderId);
            var value = await command.ExecuteScalarAsync(Ct);
            return value is null or DBNull ? null : (string)value;
        }

        public async Task<Guid> QepOrderAsync(long shopifyOrderId) =>
            Guid.Parse(await MirrorAsync(shopifyOrderId, "qep_order_id") ?? throw new InvalidOperationException("Not imported."));

        /// <summary><c>attempts|ignored_reason|last_error</c> de una entrega ya cerrada; <c>pending</c> si sigue abierta.</summary>
        public Task<string> DeliveryAsync(string webhookId) =>
            ScalarAsync<string>(
                ConnectionString,
                """
                SELECT CASE WHEN processed_at IS NULL THEN 'pending'
                            ELSE attempts || '|' || coalesce(ignored_reason, 'none') || '|' || coalesce(last_error, 'none') END
                FROM shopify.webhook_deliveries WHERE webhook_id = @id
                """,
                ("id", webhookId));

        public async Task<Guid> CreateCarrierAsync(string name)
        {
            using var client = Client();
            var response = await client.PostAsJsonAsync(
                $"/api/v1/tenants/{World.TenantId}/carriers",
                new { name, externalNames = Array.Empty<string>(), supportsCashOnDelivery = true },
                Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<Guid>();
        }

        /// <summary>Un comprobante PDF subido por Storage (sesión, bytes en R2 y completar) y adjuntado al pedido pendiente.</summary>
        public async Task AddProofAsync(Guid orderId)
        {
            using var client = Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage, StoragePermissions.FileUpload, StoragePermissions.FileRead);
            var payload = "%PDF-1.7\nproof"u8.ToArray();
            var session = await client.PostAsJsonAsync(
                $"/api/v1/tenants/{World.TenantId}/files",
                new { ownerId = Guid.NewGuid(), ownerType = "User", name = "proof.pdf", mimeType = "application/pdf", sizeBytes = payload.Length },
                Ct);
            Assert.True(session.IsSuccessStatusCode, await session.Content.ReadAsStringAsync(Ct));
            var upload = (await session.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
            Storage.Upload(upload["storageKey"]!.GetValue<string>(), payload);
            var fileId = upload["fileResourceId"]!.GetValue<Guid>();
            var completed = await client.PostAsync($"/api/v1/tenants/{World.TenantId}/files/{fileId}/complete", content: null, Ct);
            Assert.True(completed.IsSuccessStatusCode, await completed.Content.ReadAsStringAsync(Ct));

            var proofs = await client.PostAsJsonAsync(
                $"/api/v1/tenants/{World.TenantId}/orders/{orderId}/proofs",
                new AddOrderPaymentProofsRequest("PartialPaymentReceived", [new OrderPaymentProofRequest(fileId, 50_000m)]),
                Ct);
            Assert.True(proofs.IsSuccessStatusCode, await proofs.Content.ReadAsStringAsync(Ct));
        }

        /// <summary>El Excel de pedidos como lo pide la pantalla: el job se encola, se corre a mano y se lee el archivo subido. El
        /// rango cubre el día de los fixtures (los pedidos de Shopify entran con su fecha de creación en Shopify).</summary>
        public async Task<ExportWorkbookSheet> ExportOrdersAsync()
        {
            using var client = Client(OrdersPermissions.OrderRead);
            var response = await client.PostAsync(
                $"/api/v1/tenants/{World.TenantId}/orders/export?convertedFrom=2026-09-30&convertedTo=2026-10-02", content: null, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            var jobId = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["jobId"]!.GetValue<Guid>();
            await using (var scope = Factory.Services.CreateAsyncScope())
            {
                Assert.Equal(ExportJobRunOutcome.Completed, await scope.ServiceProvider.GetRequiredService<ExportJobRunner>().RunNextAsync(Ct));
            }

            return ExportWorkbookReader.Read(await Storage.DownloadAsync($"exports/tenants/{World.TenantId:N}/jobs/{jobId:N}.xlsx", Ct));
        }

        public async ValueTask DisposeAsync()
        {
            Webhooks.Dispose();
            World.Tenant.Seeder.Dispose();
            await Factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}

/// <summary>R2 en memoria, copia mínima de <c>QuotationsApiHarness.InMemoryObjectStorage</c>: los comprobantes y el Excel quedan
/// acá, donde la prueba los ve.</summary>
internal sealed class InMemoryObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> contentTypes = new(StringComparer.Ordinal);

    public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(objects.TryGetValue(key, out var content)
            ? new StoredObject(content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)))
            : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken)
    {
        objects[destinationKey] = objects[sourceKey].ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => Task.FromResult(objects[key].ToArray());

    public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        objects[key] = content.ToArray();
        contentTypes[key] = contentType;
        return Task.CompletedTask;
    }

    public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        objects[key] = buffer.ToArray();
        contentTypes[key] = contentType;
    }

    public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(objects.TryGetValue(key, out var bytes)
            ? new StoredObjectStream(new MemoryStream(bytes), contentTypes.GetValueOrDefault(key, "application/octet-stream"), bytes.LongLength)
            : null);

    public void Upload(string key, byte[] content) => objects[key] = content.ToArray();
}
