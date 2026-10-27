using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-10-25 §11 de punta a punta: el flujo real de Clasic (<see cref="ClasicFlow"/>) deja una fila de <c>automations.run_units</c> por
/// entrante, sus envíos apuntan a mensajes reales, los <c>step_number</c> de <c>http_calls</c> salen de pasos de esas filas y la URL firmada
/// de la factura nunca llega a la tabla. El armado del escenario es el de <see cref="ClasicFlowEndToEndTests"/> (allí es privado), sin su
/// parte de logs.
/// </summary>
public sealed class RunUnitsEndToEndTests
{
    private const string PhoneNumberId = "311";
    private const string WaId = "573004445566";
    private const string ApiKey = "clasic-key-SENTINEL";
    private const string BaseUrl = "https://api-clasic.test";

    private const string CustomerFound = """{"items":[{"id":7,"firstName":"Laura","lastName":"Gómez","acceptPolicy":true}],"totalCount":1}""";

    private const string TwoEquipments = """
        [{"id":1,"referenceCode":"CAL-001","address":"Calle 1 # 2-3","haveOrderOpen":false},{"id":2,"referenceCode":"CAL-002","address":"Carrera 4 # 5-6","haveOrderOpen":false}]
        """;

    private const string Failures = """[{"id":3,"description":"No enciende"},{"id":4,"description":"Fuga de agua"}]""";

    private const string WarrantyService = """{"id":9,"name":"STG","description":"Servicio técnico en garantía"}""";

    private const string NoCustomers = """{"items":[],"totalCount":0}""";

    private static readonly byte[] InvoiceBytes = Encoding.UTF8.GetBytes("factura de compra de Clasic");

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, string ConnectionString, HttpClient Anonymous, FakeMediaStore Media)
        : IDisposable
    {
        public void Dispose()
        {
            Anonymous.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var media = new FakeMediaStore();
        var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(media)));
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var whatsApp = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Servicio Clasic", PhoneNumberId, "322");
        var apiConnectionId = await SeedExternalApiConnectionAsync(
            host, tenant.TenantId, tenant.OwnerUserId, "API de Clasic", "Active", BaseUrl, ApiKey);
        using (var admin = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions))
        {
            var imported = await ImportChatbotAsync(
                admin, tenant.TenantId, ClasicFlow.FlowJson, ClasicFlow.LayoutJson,
                new Dictionary<Guid, Guid> { [ClasicFlow.ClasicApi] = apiConnectionId });
            await PublishImportedAsync(admin, tenant.TenantId, imported, whatsApp);
        }

        factory.ExternalApi.Respond(@"^/customers\?phone=", HttpStatusCode.OK, CustomerFound);
        factory.ExternalApi.Respond(@"^/customers\?document=", HttpStatusCode.OK, CustomerFound);
        factory.ExternalApi.Respond(@"^/customers/7$", HttpStatusCode.OK, "{}");
        factory.ExternalApi.Respond(@"^/invoices/customer/7/all-invoice-items$", HttpStatusCode.OK, TwoEquipments);
        factory.ExternalApi.Respond(@"^/failures$", HttpStatusCode.OK, Failures);
        factory.ExternalApi.Respond(@"^/service-orders/auto-select-service-type\?", HttpStatusCode.OK, WarrantyService);
        factory.ExternalApi.Respond(@"^/service-orders$", HttpStatusCode.Created, """{"id":55}""");
        factory.ExternalApi.Respond(@"^/customers/new-customer$", HttpStatusCode.Created, """{"serviceOrderNumber":3}""");

        var sha = Convert.ToHexStringLower(SHA256.HashData(InvoiceBytes));
        factory.MetaHandler.Respond(
            "/media-1", HttpStatusCode.OK,
            $$"""{"url":"https://lookaside.test/m/1","mime_type":"image/jpeg","sha256":"{{sha}}","file_size":{{InvoiceBytes.Length}},"id":"media-1"}""");
        factory.MetaHandler.RespondWith("^/m/1$", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(InvoiceBytes) });
        // Cada envío del bot responde un wamid distinto (el acuse de leído aparte), como el Graph de ClasicFlowEndToEndTests.
        var sent = 0;
        factory.MetaHandler.RespondWith($"/{PhoneNumberId}/messages", request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            if (IsReadReceipt(body))
            {
                return FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}""");
            }

            var number = Interlocked.Increment(ref sent);
            return FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""
                {"messaging_product":"whatsapp","contacts":[{"input":"{{WaId}}","wa_id":"{{WaId}}"}],"messages":[{"id":"wamid.{{number}}"}]}
                """);
        });
        return new Fixture(factory, host, connectionString, host.CreateClient(), media);
    }

    private static long Timestamp(string wamid) =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600 + long.Parse(wamid.AsSpan(1), CultureInfo.InvariantCulture);

    private static async Task InboundAsync(Fixture f, string body)
    {
        var response = await PostWebhookAsync(f.Anonymous, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await DrainDeliveriesAsync(f.Host);
    }

    private static async Task SayAsync(Fixture f, string wamid, string text)
    {
        await InboundAsync(f, Payloads.Message(WaId, wamid, Timestamp(wamid), "text", new JsonObject { ["body"] = text }));
        await DrainAutomationsAsync(f.Host);
    }

    private static async Task TapAsync(Fixture f, string wamid, string replyId, string title)
    {
        await InboundAsync(f, Payloads.Message(WaId, wamid, Timestamp(wamid), "interactive", new JsonObject
        {
            ["type"] = "button_reply", ["button_reply"] = new JsonObject { ["id"] = replyId, ["title"] = title },
        }));
        await DrainAutomationsAsync(f.Host);
    }

    private static async Task ChooseAsync(Fixture f, string wamid, string rowId, string title)
    {
        await InboundAsync(f, Payloads.Message(WaId, wamid, Timestamp(wamid), "interactive", new JsonObject
        {
            ["type"] = "list_reply", ["list_reply"] = new JsonObject { ["id"] = rowId, ["title"] = title },
        }));
        await DrainAutomationsAsync(f.Host);
    }

    private static Task<string> TextAsync(Fixture f, string sql) => ScalarAsync<string>(f.ConnectionString, sql);

    /// <summary>Siete entrantes, hasta la orden de servicio (la misma ruta feliz de <see cref="ClasicFlowEndToEndTests"/>).</summary>
    private static async Task ToTheServiceOrderAsync(Fixture f)
    {
        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await ChooseAsync(f, "w3", "1", "CAL-001");
        await TapAsync(f, "w4", "tech", "Servicio técnico");
        await ChooseAsync(f, "w5", "3", "No enciende");
        await SayAsync(f, "w6", "Ana");
        await SayAsync(f, "w7", "3001234567");
    }

    [Fact]
    public async Task TheClasicFlowLeavesOneUnitPerInbound()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await ToTheServiceOrderAsync(f);

        // Una fila por entrante: ninguna se perdió ni se duplicó (el índice único lo impediría) y las siete son del mismo run.
        var inbounds = await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1");
        Assert.Equal(7L, inbounds);
        Assert.Equal(inbounds, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.run_units WHERE inbound_message_id IS NOT NULL"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(DISTINCT run_id) FROM automations.run_units"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM automations.run_units u
             WHERE u.inbound_message_id IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM messaging.messages m WHERE m.id = u.inbound_message_id AND m.direction = 1)
            """));

        // Cada envío registrado apunta a un mensaje real de la conversación del bot.
        var sendsWithClient = await CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM automations.run_units u, jsonb_array_elements(u.steps) st, jsonb_array_elements(st -> 'sends') sd
             WHERE sd ? 'clientId'
            """);
        Assert.True(sendsWithClient > 0);
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM automations.run_units u
              JOIN automations.conversation_runs r ON r.id = u.run_id,
                   jsonb_array_elements(u.steps) st, jsonb_array_elements(st -> 'sends') sd
             WHERE sd ? 'clientId'
               AND NOT EXISTS (
                   SELECT 1 FROM messaging.messages m
                    WHERE m.conversation_id = r.conversation_id AND m.client_id = (sd ->> 'clientId')::uuid)
            """));

        // Cada llamada HTTP trae el paso del bloque que la hizo, distinto por llamada (cada visita a un bloque HTTP
        // es un paso nuevo), y ese paso es el de un step kind = http de alguna fila. Clasic no revisita ningún bloque HTTP: lo que se prueba
        // es la unicidad y la correspondencia entre la llamada y su paso, no el caso de la revisita.
        var calls = await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.http_calls");
        Assert.Equal(6L, calls);
        Assert.Equal(calls, await CountAsync(f.ConnectionString, "SELECT count(DISTINCT step_number) FROM automations.http_calls WHERE step_number IS NOT NULL"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM automations.http_calls h
             WHERE NOT EXISTS (
                   SELECT 1 FROM automations.run_units u, jsonb_array_elements(u.steps) st
                    WHERE u.run_id = h.run_id AND lower(st ->> 'kind') = 'http' AND (st ->> 'step')::int = h.step_number)
            """));
    }

    /// <summary>D-E5: el adjunto pasa por R2 con una URL firmada para el bloque HTTP; esa URL vive lo que dura la llamada y no se escribe en
    /// ningún lado, tampoco en el registro de la unidad.</summary>
    [Fact]
    public async Task TheInvoiceUrlNeverReachesRunUnits()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Factory.ExternalApi.Respond(@"^/customers\?document=", HttpStatusCode.OK, NoCustomers);

        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await TapAsync(f, "w3", "leave", "Dejar mis datos");
        await SayAsync(f, "w4", "Laura");
        await SayAsync(f, "w5", "Gómez");
        await SayAsync(f, "w6", "Calle 1 # 2-3");
        await SayAsync(f, "w7", "Laureles");
        await SayAsync(f, "w8", "Medellín");
        await ChooseAsync(f, "w9", "3", "No enciende");
        await InboundAsync(f, Payloads.Message(WaId, "w10", Timestamp("w10"), "image", new JsonObject
        {
            ["id"] = "media-1", ["mime_type"] = "image/jpeg", ["sha256"] = "c2hhMjU2",
        }));
        await DrainMediaAsync(f.Host);
        await DrainAutomationsAsync(f.Host);

        // Control positivo: sin al menos una URL firmada generada, las ausencias de abajo no probarían nada.
        Assert.True(f.Media.DownloadUrlsCreated > 0);
        // Control positivo: el registro sí llegó a Clasic, así que el bloque que usa la factura corrió.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.http_calls WHERE method = 'POST' AND path LIKE '%new-customer'"));
        Assert.True(await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.run_units") >= 10);

        var steps = await TextAsync(f, "SELECT coalesce(string_agg(steps::text, E'\\n'), '') FROM automations.run_units");
        Assert.DoesNotContain("r2.test", steps, StringComparison.Ordinal);
        Assert.DoesNotContain("signed=1", steps, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Amz-", steps, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", steps, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, steps, StringComparison.Ordinal);
    }

    /// <summary>Los cuerpos de webhook con la forma literal de Meta, copia mínima de <c>MetaPayloads</c> de Mensajería.</summary>
    private static class Payloads
    {
        public static string Message(string waId, string wamid, long timestamp, string type, JsonObject content)
        {
            var userId = "CO." + waId;
            var message = new JsonObject
            {
                ["from"] = waId, ["from_user_id"] = userId, ["id"] = wamid, ["timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture),
                ["type"] = type, [type] = content,
            };
            var value = new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["metadata"] = new JsonObject { ["display_phone_number"] = "15550000000", ["phone_number_id"] = PhoneNumberId },
                ["contacts"] = new JsonArray(new JsonObject
                {
                    ["profile"] = new JsonObject { ["name"] = "Laura" }, ["user_id"] = userId, ["wa_id"] = waId,
                }),
                ["messages"] = new JsonArray(message),
            };
            return JsonSerializer.Serialize(new
            {
                @object = "whatsapp_business_account",
                entry = new[] { new { id = "322", changes = new[] { new { field = "messages", value = (JsonNode)value } } } },
            });
        }
    }

    /// <summary>R2 de mentira, como el de <c>MediaApiTests</c> de Mensajería: la copia del adjunto escribe acá y el bloque HTTP lee de acá.</summary>
    internal sealed class FakeMediaStore : IMessagingMediaStore
    {
        private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> objects = new(StringComparer.Ordinal);

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            objects[key] = (buffer.ToArray(), contentType);
        }

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(objects.TryGetValue(key, out var stored)
                ? new MediaStreamDto(new MemoryStream(stored.Bytes), stored.ContentType, stored.Bytes.Length, null)
                : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        private int downloadUrlsCreated;

        public int DownloadUrlsCreated => Volatile.Read(ref downloadUrlsCreated);

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref downloadUrlsCreated);
            return Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
        }
    }
}
