using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-10-21 §7 y Anexo B de punta a punta: el subconjunto del flujo de Clasic importado por la API, con su conexión «API externa»
/// real (creada por Integrations y resuelta por el adaptador de Bootstrapper), contra una API de Clasic de mentira
/// (<see cref="FakeExternalApiHandler"/>). Los entrantes entran por el webhook de Meta, como en <see cref="EngineEndToEndTests"/>.
/// </summary>
public sealed class ClasicFlowEndToEndTests
{
    private const string PhoneNumberId = "311";
    private const string WaId = "573004445566";
    private const string ApiKey = "clasic-key-SENTINEL";
    private const string BaseUrl = "https://api-clasic.test";

    /// <summary>Anexo C: <c>GET /customers</c> es paginado (<c>data</c>, <c>total</c>).</summary>
    private const string CustomerFound = """{"data":[{"id":7,"firstName":"Laura","lastName":"Gómez","acceptPolicy":true}],"total":1}""";

    // Anexo C dice que all-invoice-items y failures responden un arreglo en la raíz; JsonPath (D-H4) sólo lee rutas que empiezan por un
    // nombre, así que el fixture mapea «data». Ver el reporte de la tarea 12: el flujo real necesita una regla para la raíz.
    private const string TwoEquipments = """
        {"data":[{"id":1,"referenceCode":"CAL-001","address":"Calle 1 # 2-3","haveOrderOpen":false},{"id":2,"referenceCode":"CAL-002","address":"Carrera 4 # 5-6","haveOrderOpen":false}]}
        """;

    private const string Failures = """{"data":[{"id":3,"description":"No enciende"},{"id":4,"description":"Fuga de agua"}]}""";

    private static readonly byte[] InvoiceBytes = Encoding.UTF8.GetBytes("factura de compra de Clasic");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, string ConnectionString, RegisteredTenant Tenant, Guid ApiConnectionId,
        HttpClient Anonymous, Graph Graph, CapturedLogs Logs) : IDisposable
    {
        public FakeExternalApiHandler Api => Factory.ExternalApi;

        public void Dispose()
        {
            Anonymous.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string? flowJson = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var store = new FakeMediaStore();
        var logs = new CapturedLogs();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessagingMediaStore>(store);
            services.AddSingleton<ILoggerProvider>(logs);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var whatsApp = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Servicio Clasic", PhoneNumberId, "322");
        // Spec 2026-10-21 D-H1: la conexión de verdad, por la API de Integrations; el FakeExternalApiHandler reemplaza el handler del
        // bloque, así que el DNS de api-clasic.test no se consulta.
        var apiConnectionId = await SeedExternalApiConnectionAsync(
            host, tenant.TenantId, tenant.OwnerUserId, "API de Clasic", "Active", BaseUrl, ApiKey);
        using (var admin = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions))
        {
            var imported = await ImportChatbotAsync(
                admin, tenant.TenantId, flowJson ?? ClasicSubset.Json, new Dictionary<Guid, Guid> { [ClasicSubset.ClasicApi] = apiConnectionId });
            // D-C15: el mapa reescribió la conexión de todos los bloques HTTP (si quedara una, publicar fallaría por http.connection_missing).
            Assert.DoesNotContain(ClasicSubset.ClasicApi.ToString(), imported.Draft.Flow.GetRawText(), StringComparison.Ordinal);
            await PublishImportedAsync(admin, tenant.TenantId, imported, whatsApp);
        }

        // El camino feliz por defecto; cada prueba pisa lo que necesita (gana la regla registrada última).
        factory.ExternalApi.Respond(@"^/customers\?", HttpStatusCode.OK, CustomerFound);
        factory.ExternalApi.Respond(@"^/invoices/customer/7/all-invoice-items$", HttpStatusCode.OK, TwoEquipments);
        factory.ExternalApi.Respond(@"^/failures$", HttpStatusCode.OK, Failures);
        factory.ExternalApi.Respond(@"^/service-orders/auto-select-service-type\?", HttpStatusCode.OK, """{"id":9,"name":"Servicio técnico"}""");
        factory.ExternalApi.Respond(@"^/service-orders$", HttpStatusCode.Created, """{"id":55,"state":"Draft"}""");
        factory.ExternalApi.Respond(@"^/customers/new-customer$", HttpStatusCode.Created, """{"serviceOrderNumber":"OS-123"}""");

        // §8.6: la copia del adjunto baja de Graph la URL firmada y luego los bytes, que se verifican contra el sha256.
        var sha = Convert.ToHexStringLower(SHA256.HashData(InvoiceBytes));
        factory.MetaHandler.Respond(
            "/media-1", HttpStatusCode.OK,
            $$"""{"url":"https://lookaside.test/m/1","mime_type":"image/jpeg","sha256":"{{sha}}","file_size":{{InvoiceBytes.Length}},"id":"media-1"}""");
        factory.MetaHandler.RespondWith("^/m/1$", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(InvoiceBytes) });
        return new Fixture(factory, host, connectionString, tenant, apiConnectionId, host.CreateClient(), new Graph(factory.MetaHandler), logs);
    }

    /// <summary>Segundos Unix recientes y crecientes con el número del wamid: la ventana de 24 h está abierta y el orden es el de llegada.</summary>
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

    /// <summary>Hasta el teléfono de contacto: «Hola», cédula, equipo 1, servicio técnico, falla 3, «Ana» y el teléfono.</summary>
    private static async Task ToTheOrderAsync(Fixture f)
    {
        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await ChooseAsync(f, "w3", "1", "CAL-001");
        await TapAsync(f, "w4", "tech", "Servicio técnico");
        await ChooseAsync(f, "w5", "3", "No enciende");
        await SayAsync(f, "w6", "Ana");
        await SayAsync(f, "w7", "3001234567");
    }

    private static Task<string> TextAsync(Fixture f, string sql) => ScalarAsync<string>(f.ConnectionString, sql);

    private static Task<string> HttpCallsAsync(Fixture f) =>
        TextAsync(f,
            """
            SELECT coalesce(string_agg(method || ' ' || path || ' ' || coalesce(status_code::text, '-') || ' ' || outcome || ' ' || coalesce(error_code, '-'), ',' ORDER BY id), '')
              FROM automations.http_calls
            """);

    [Fact]
    public async Task TheHappyPathCreatesAServiceOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await SayAsync(f, "w1", "Hola");
        Assert.Equal(
            ["Hola Laura, bienvenido a Clasic. Te ayudamos a programar el servicio de tu calentador.", "Escribe tu número de documento, sin puntos ni espacios."],
            f.Graph.Sends());

        await SayAsync(f, "w2", "10203");
        // P2-R15: las filas de la lista dinámica salen de la respuesta de la API, más la extra.
        Assert.Equal(["1", "2", "other"], f.Graph.LastListRowIds());
        Assert.Equal("[list] Elige el equipo que necesita servicio.", f.Graph.Sends()[^1]);

        await ChooseAsync(f, "w3", "1", "CAL-001");
        Assert.Equal("[button] ¿Qué tipo de servicio necesitas?", f.Graph.Sends()[^1]);
        await TapAsync(f, "w4", "tech", "Servicio técnico");
        Assert.Equal(["3", "4"], f.Graph.LastListRowIds());
        await ChooseAsync(f, "w5", "3", "No enciende");
        await SayAsync(f, "w6", "Ana");
        await SayAsync(f, "w7", "3001234567");

        Assert.Equal("Listo, Ana. Tu orden es la 55; te llamamos al 3001234567 para agendar la visita.", f.Graph.Sends()[^1]);
        Assert.Equal("Resolved|None", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
        var runId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM automations.conversation_runs");
        Assert.Equal("Resolved", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));

        var requests = f.Api.Requests.ToArray();
        Assert.Equal(
            [
                "GET /customers?identificationNumber=10203", "GET /invoices/customer/7/all-invoice-items", "GET /failures",
                "GET /service-orders/auto-select-service-type?invoiceItemId=1", "POST /service-orders",
            ],
            requests.Select(request => $"{request.Method} {request.Uri.PathAndQuery}"));
        Assert.All(requests, request => Assert.Equal(("api-clasic.test", ApiKey), (request.Uri.Host, request.ApiKey)));

        // P2-R11: el token suelto escribe el número tal cual; el que va dentro de comillas, su texto.
        var order = JsonNode.Parse(requests[^1].Body!)!;
        Assert.Equal(JsonValueKind.Number, order["customerId"]!.GetValueKind());
        Assert.Equal(7, order["customerId"]!.GetValue<int>());
        Assert.Equal(3, order["failureId"]!.GetValue<int>());
        Assert.Equal(9, order["serviceId"]!.GetValue<int>());
        var item = order["invoiceItems"]![0]!;
        Assert.Equal(JsonValueKind.String, item["id"]!.GetValueKind());
        Assert.Equal(("1", "Ana", "3001234567", true),
            (item["id"]!.GetValue<string>(), item["contact"]!.GetValue<string>(), item["optionalPhone"]!.GetValue<string>(), item["update"]!.GetValue<bool>()));

        // D-H6: una fila por llamada, de esta corrida, con la ruta interpolada y sin query; la tabla no tiene dónde guardar un cuerpo.
        Assert.Equal(
            "GET /customers 200 ok -,GET /invoices/customer/7/all-invoice-items 200 ok -,GET /failures 200 ok -,"
            + "GET /service-orders/auto-select-service-type 200 ok -,POST /service-orders 201 ok -",
            await HttpCallsAsync(f));
        Assert.Equal(5L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM automations.http_calls WHERE run_id = @run AND connection_id = @connection AND tenant_id = @tenant",
            ("run", runId), ("connection", f.ApiConnectionId), ("tenant", f.Tenant.TenantId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM information_schema.columns
             WHERE table_schema = 'automations' AND table_name = 'http_calls'
               AND (column_name LIKE '%body%' OR column_name LIKE '%header%' OR column_name LIKE '%response%' OR column_name LIKE '%query%')
            """));
    }

    [Fact]
    public async Task AnUnknownCustomerGoesToRegistrationAndUploadsTheInvoice()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/customers\?", HttpStatusCode.OK, """{"data":[],"total":0}""");

        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        // Anexo B paso 6: el botón URL de registro y las dos opciones.
        Assert.Equal(
            ["[cta_url] No encontramos ese documento. Puedes registrarte en la página o dejarnos tus datos por aquí.", "[button] ¿Qué prefieres hacer?"],
            f.Graph.Sends()[^2..]);

        await TapAsync(f, "w3", "leave", "Dejar mis datos");
        await SayAsync(f, "w4", "Laura Gómez");
        await SayAsync(f, "w5", "Calle 1 # 2-3, Laureles");
        await SayAsync(f, "w6", "Calentador a gas, no enciende");
        Assert.Equal("Envíanos una foto o el PDF de la factura de compra.", f.Graph.Sends()[^1]);

        await InboundAsync(f, Payloads.Message(WaId, "w7", Timestamp("w7"), "image", new JsonObject
        {
            ["id"] = "media-1", ["mime_type"] = "image/jpeg", ["sha256"] = "c2hhMjU2",
        }));
        // D-H8: el archivo ya está en R2 cuando el motor procesa el entrante.
        await DrainMediaAsync(f.Host);
        await DrainAutomationsAsync(f.Host);

        var registration = Assert.Single(f.Api.Requests, request => request.Uri.AbsolutePath == "/customers/new-customer");
        Assert.Equal(HttpMethod.Post, registration.Method);
        Assert.Equal(ApiKey, registration.ApiKey);
        var file = Assert.Single(registration.Parts, part => part.Name == "file");
        Assert.Equal(InvoiceBytes, file.Bytes);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.NotNull(file.FileName);
        var fields = registration.Parts.Where(part => part.Name != "file").ToDictionary(part => part.Name, part => part.Text);
        Assert.Equal("10203", fields["identificationNumber"]);
        Assert.Equal("Laura Gómez", fields["fullName"]);
        Assert.Equal("Calle 1 # 2-3, Laureles", fields["address"]);
        Assert.Equal("Calentador a gas, no enciende", fields["failureDescription"]);
        Assert.False(string.IsNullOrEmpty(fields["phone"]));

        Assert.Equal("Recibimos tus datos. Tu solicitud es la OS-123.", f.Graph.Sends()[^1]);
        Assert.Equal("Resolved|None", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
        Assert.Equal(
            "GET /customers 200 ok -,POST /customers/new-customer 201 ok -",
            await HttpCallsAsync(f));
    }

    [Fact]
    public async Task AnApiErrorHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        // Anexo C: 409 cuando la dirección ya tiene una orden abierta.
        f.Api.Respond(@"^/service-orders$", HttpStatusCode.Conflict, """{"message":"Ya existe una orden abierta"}""");

        await ToTheOrderAsync(f);

        Assert.Equal(
            ["No pudimos crear la orden: puede que ya exista una abierta para esa dirección.", "Te paso con un asesor."],
            f.Graph.Sends()[^2..]);
        Assert.Equal("Open|Paused|", await TextAsync(f,
            "SELECT status || '|' || automation_status || '|' || coalesce(assigned_member_id::text, '') FROM messaging.conversations"));
        Assert.Equal("HandedOff", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));
        Assert.Equal(
            "POST /service-orders 409 error_status http_409",
            await TextAsync(f, "SELECT method || ' ' || path || ' ' || status_code || ' ' || outcome || ' ' || error_code FROM automations.http_calls ORDER BY id DESC LIMIT 1"));
    }

    /// <summary>D-H4 dentro del lease de 30 s del inbox (§5.2). Con un timeout de 1 s y una API que tardaría 5 s, la unidad termina en
    /// menos de 5: lo mismo que 8 s contra 9 s, sin que la prueba espere nueve.</summary>
    [Fact]
    public async Task ATimeoutHandsOffWithinTheLease()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, ClasicSubset.WithTimeout("find-customer", 1));
        f.Api.RespondAfter(@"^/customers\?", TimeSpan.FromSeconds(5), HttpStatusCode.OK, CustomerFound);
        await SayAsync(f, "w1", "Hola");

        var watch = Stopwatch.StartNew();
        await SayAsync(f, "w2", "10203");
        watch.Stop();

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Equal("Te paso con un asesor.", f.Graph.Sends()[^1]);
        Assert.Equal("Paused", await TextAsync(f, "SELECT automation_status FROM messaging.conversations"));
        Assert.Equal("HandedOff", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));
        Assert.Equal("GET /customers - timeout timeout", await HttpCallsAsync(f));
        // La fila del entrante quedó cerrada en su primer intento; otra pasada no la vuelve a reclamar ni a llamar a la API.
        Assert.Equal("1|true", await TextAsync(f,
            "SELECT attempts || '|' || (processed_at IS NOT NULL) FROM messaging.automation_inbox ORDER BY created_at DESC, message_id DESC LIMIT 1"));
        await DrainAutomationsAsync(f.Host);
        Assert.Single(f.Api.Requests);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.http_calls"));
    }

    /// <summary>D-H5 y D-H6: la clave sale sólo en el header hacia la API. Ni los logs del host, ni la auditoría, ni <c>http_calls</c>, ni la
    /// corrida, ni <c>platform.request_failures</c> la tienen.</summary>
    [Fact]
    public async Task TheKeyNeverAppearsInLogsAuditOrHttpCalls()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/service-orders$", HttpStatusCode.BadRequest, """{"message":"Cliente bloqueado"}""");

        await ToTheOrderAsync(f);

        // Controles positivos: la clave sí viajó en el header, y los logs del host y la auditoría sí se capturan.
        Assert.All(f.Api.Requests, request => Assert.Equal(ApiKey, request.ApiKey));
        Assert.Contains(f.Logs.Entries, entry => entry.StartsWith("Chatbot HTTP block", StringComparison.Ordinal));
        Assert.True(await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries") > 0);
        Assert.True(await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.http_calls") >= 5);

        Assert.DoesNotContain(ApiKey, f.Logs.AllText, StringComparison.Ordinal);
        foreach (var table in new[] { "automations.http_calls", "audit.entries", "automations.conversation_runs", "platform.request_failures" })
        {
            var rows = await TextAsync(f, $"SELECT coalesce(string_agg(row_to_json(t)::text, E'\\n'), '') FROM {table} AS t");
            Assert.DoesNotContain(ApiKey, rows, StringComparison.Ordinal);
        }
    }

    /// <summary>Lo que el bot mandó a Graph: responde <c>wamid.N</c> a cada envío (el acuse de leído aparte), como el <c>BotMeta</c> de
    /// <see cref="EngineEndToEndTests"/>.</summary>
    private sealed class Graph
    {
        private readonly FakeMetaGraphHandler handler;
        private int sent;

        public Graph(FakeMetaGraphHandler handler)
        {
            this.handler = handler;
            handler.RespondWith($"/{PhoneNumberId}/messages", request =>
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
        }

        private IEnumerable<JsonNode> Bodies() =>
            handler.Requests
                .Where(request => request.Uri!.AbsolutePath.EndsWith($"/{PhoneNumberId}/messages", StringComparison.Ordinal) && !IsReadReceipt(request.Body))
                .Select(request => JsonNode.Parse(request.Body ?? "{}")!);

        /// <summary><c>text.body</c>, o el cuerpo del interactivo con su tipo adelante (<c>[list] …</c>).</summary>
        public List<string> Sends() =>
        [
            .. Bodies().Select(root => root["type"]?.GetValue<string>() == "interactive"
                ? $"[{root["interactive"]?["type"]?.GetValue<string>()}] {root["interactive"]?["body"]?["text"]?.GetValue<string>()}"
                : root["text"]?["body"]?.GetValue<string>() ?? string.Empty),
        ];

        /// <summary>Los id de las filas de la última lista enviada.</summary>
        public List<string> LastListRowIds() =>
        [
            .. Bodies().Last(root => root["interactive"]?["type"]?.GetValue<string>() == "list")["interactive"]!["action"]!["sections"]!.AsArray()
                .SelectMany(section => section!["rows"]!.AsArray())
                .Select(row => row!["id"]!.GetValue<string>()),
        ];
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
    private sealed class FakeMediaStore : IMessagingMediaStore
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

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
    }

    /// <summary>Todo lo que registra el host, ya formateado y con la excepción entera, como el <c>CapturedLogs</c> de Integrations.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> entries = new();

        public IReadOnlyCollection<string> Entries => entries;

        public string AllText => string.Join('\n', entries);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
        }
    }
}
