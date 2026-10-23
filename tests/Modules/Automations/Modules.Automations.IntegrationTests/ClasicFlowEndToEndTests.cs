using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-10-21 §7 y Anexo B de punta a punta: el flujo completo de Clasic (<see cref="ClasicFlow"/>) importado por la API con su
/// layout y su conexión «API externa» real (creada por Integrations y resuelta por el adaptador de Bootstrapper), contra una API de Clasic
/// de mentira (<see cref="FakeExternalApiHandler"/>) que responde con la forma real de su contrato (Anexo C). Los entrantes entran por el
/// webhook de Meta, como en <see cref="EngineEndToEndTests"/>.
/// </summary>
public sealed class ClasicFlowEndToEndTests
{
    private const string PhoneNumberId = "311";
    private const string WaId = "573004445566";
    private const string ApiKey = "clasic-key-SENTINEL";
    private const string BaseUrl = "https://api-clasic.test";

    /// <summary>Anexo C con la forma que de verdad responde Clasic: <c>GET /customers</c> (por teléfono y por cédula) es paginado con
    /// <c>items</c> y <c>totalCount</c>; el fixture lee <c>items</c> y <c>items[0]</c>.</summary>
    private const string CustomerFound = """{"items":[{"id":7,"firstName":"Laura","lastName":"Gómez","acceptPolicy":true}],"totalCount":1}""";

    /// <summary>Anexo C: all-invoice-items, failures y services responden un arreglo en la raíz (<c>[].id</c>); el fixture lo mapea con
    /// <c>$</c> o <c>$[0]</c> (P2-R36).</summary>
    private const string TwoEquipments = """
        [{"id":1,"referenceCode":"CAL-001","address":"Calle 1 # 2-3","haveOrderOpen":false},{"id":2,"referenceCode":"CAL-002","address":"Carrera 4 # 5-6","haveOrderOpen":false}]
        """;

    private const string Failures = """[{"id":3,"description":"No enciende"},{"id":4,"description":"Fuga de agua"}]""";

    private const string MaintenanceFailure = """[{"id":5,"description":"MANTENIMIENTO","cause":"MANTENIMIENTO"}]""";

    private const string MaintenanceService = """[{"id":12,"name":"STM","description":"Servicio técnico de mantenimiento"}]""";

    /// <summary>Anexo C: auto-select-service-type responde un objeto, no un arreglo; el fixture lo mapea entero con <c>$</c>.</summary>
    private const string WarrantyService = """{"id":9,"name":"STG","description":"Servicio técnico en garantía"}""";

    private static readonly byte[] InvoiceBytes = Encoding.UTF8.GetBytes("factura de compra de Clasic");

    /// <summary>D-K3: el mismo cliente que encuentra la cédula, pero sin la política aceptada.</summary>
    private const string CustomerWithoutPolicy = """{"items":[{"id":7,"firstName":"Laura","lastName":"Gómez","acceptPolicy":false}],"totalCount":1}""";

    private const string NoCustomers = """{"items":[],"totalCount":0}""";

    // Lo que manda el bot en los pasos que más se miran, tal como está en el fixture. La bienvenida saluda corto: no repite la
    // presentación de la política a quien pasa por ella, pero quien ya la aceptó también recibe un saludo.
    private const string Welcome =
        "Hola. Para solicitar un servicio o una asistencia técnica, te vamos a pedir unos datos. Recuerda que puedes reiniciar la conversación en cualquier momento escribiendo *REINICIAR*.";

    private const string AskDocument = "Escribe tu número de documento, sólo números, sin puntos ni espacios.";

    private const string AskEquipment = "[list] Elige el equipo o la dirección donde necesitas el servicio.";

    private const string AskServiceKind = "[button] ¿Qué tipo de servicio necesitas?";

    private const string AskContact = "¿Cómo se llama la persona de contacto para la visita?";

    private const string OpenOrderNotice = "[button] Ese equipo ya tiene una orden de servicio abierta.";

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

    /// <param name="traceLogs">Ruling P2-R38: el host registra todo desde Trace, el nivel en que el logging de HttpClient escribe los
    /// headers. A Information una regresión de <c>RemoveAllLoggers()</c> pasaría sin que nada lo viera.</param>
    /// <param name="layoutJson">El <c>Layout</c> del archivo que se importa; por defecto, el del fixture de Clasic.</param>
    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string? flowJson = null, string? layoutJson = null, bool traceLogs = false)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var store = new FakeMediaStore();
        var logs = new CapturedLogs();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IMessagingMediaStore>(store);
                services.AddSingleton<ILoggerProvider>(logs);
            });
            if (traceLogs)
            {
                // Las reglas de appsettings (Default Information, Microsoft.AspNetCore Warning) ganan sobre SetMinimumLevel; una regla del
                // proveedor de la prueba sin categoría gana sobre todas ellas para ese proveedor, así que lo captura todo desde Trace.
                builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddFilter<CapturedLogs>(null, LogLevel.Trace));
            }
        });
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
                admin, tenant.TenantId, flowJson ?? ClasicFlow.FlowJson, layoutJson ?? ClasicFlow.LayoutJson,
                new Dictionary<Guid, Guid> { [ClasicFlow.ClasicApi] = apiConnectionId });
            // D-C15: el mapa reescribió la conexión de todos los bloques HTTP (si quedara una, publicar fallaría por http.connection_missing).
            Assert.DoesNotContain(ClasicFlow.ClasicApi.ToString(), imported.Draft.Flow.GetRawText(), StringComparison.Ordinal);
            if (layoutJson is null)
            {
                // El layout del archivo llega al borrador: el editor abre el flujo importado con sus posiciones.
                Assert.True(imported.Draft.Layout.GetProperty("positions").TryGetProperty("policy-lookup", out _));
            }

            await PublishImportedAsync(admin, tenant.TenantId, imported, whatsApp);
        }

        // El camino feliz por defecto, con la forma real de cada respuesta (Anexo C); cada prueba pisa lo que necesita (gana la regla
        // registrada última). Por teléfono y por cédula van en reglas aparte para que una prueba pise sólo una de las dos búsquedas.
        factory.ExternalApi.Respond(@"^/customers\?phone=", HttpStatusCode.OK, CustomerFound);
        factory.ExternalApi.Respond(@"^/customers\?document=", HttpStatusCode.OK, CustomerFound);
        factory.ExternalApi.Respond(@"^/customers/7$", HttpStatusCode.OK, "{}");
        factory.ExternalApi.Respond(@"^/invoices/customer/7/all-invoice-items$", HttpStatusCode.OK, TwoEquipments);
        factory.ExternalApi.Respond(@"^/failures$", HttpStatusCode.OK, Failures);
        factory.ExternalApi.Respond(@"^/failures\?cause=MANTENIMIENTO$", HttpStatusCode.OK, MaintenanceFailure);
        factory.ExternalApi.Respond(@"^/services\?name=STM$", HttpStatusCode.OK, MaintenanceService);
        factory.ExternalApi.Respond(@"^/service-orders/auto-select-service-type\?", HttpStatusCode.OK, WarrantyService);
        factory.ExternalApi.Respond(@"^/service-orders$", HttpStatusCode.Created, """{"id":55}""");
        // Anexo C: serviceOrderNumber es un número (verificado contra Clasic local, revisión final 2026-10-23).
        factory.ExternalApi.Respond(@"^/customers/new-customer$", HttpStatusCode.Created, """{"serviceOrderNumber":3}""");

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

    /// <summary>Hasta el teléfono de contacto: «Hola» (cliente por teléfono con la política aceptada), cédula, equipo 1, servicio técnico,
    /// falla 3, «Ana» y el teléfono.</summary>
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

    /// <summary>El registro de un documento que Clasic no conoce, hasta la factura: «Dejar mis datos», nombres, apellidos, dirección, barrio,
    /// ciudad, la falla de la lista y la foto, ya copiada a R2 cuando el motor procesa el entrante (D-H8).</summary>
    private static async Task RegisterAsync(Fixture f, string failureRowId, string failureTitle)
    {
        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await RegisterFromOptionsAsync(f, failureRowId, failureTitle);
    }

    /// <summary>Desde los botones de «¿Qué prefieres hacer?» hasta la factura.</summary>
    private static async Task RegisterFromOptionsAsync(Fixture f, string failureRowId, string failureTitle)
    {
        await TapAsync(f, "w3", "leave", "Dejar mis datos");
        await SayAsync(f, "w4", "Laura");
        await SayAsync(f, "w5", "Gómez");
        await SayAsync(f, "w6", "Calle 1 # 2-3");
        await SayAsync(f, "w7", "Laureles");
        await SayAsync(f, "w8", "Medellín");
        await ChooseAsync(f, "w9", failureRowId, failureTitle);
        await InboundAsync(f, Payloads.Message(WaId, "w10", Timestamp("w10"), "image", new JsonObject
        {
            ["id"] = "media-1", ["mime_type"] = "image/jpeg", ["sha256"] = "c2hhMjU2",
        }));
        await DrainMediaAsync(f.Host);
        await DrainAutomationsAsync(f.Host);
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

        // D-K3: el teléfono encuentra a un cliente con la política aceptada, así que no se pregunta.
        await SayAsync(f, "w1", "Hola");
        Assert.Equal([Welcome, AskDocument], f.Graph.Sends());

        await SayAsync(f, "w2", "10203");
        // P2-R15: las filas de la lista dinámica salen de la respuesta de la API, más la extra.
        Assert.Equal(["1", "2", "other"], f.Graph.LastListRowIds());
        Assert.Equal(AskEquipment, f.Graph.Sends()[^1]);

        await ChooseAsync(f, "w3", "1", "CAL-001");
        Assert.Equal(AskServiceKind, f.Graph.Sends()[^1]);
        await TapAsync(f, "w4", "tech", "Servicio técnico");
        Assert.Equal(["3", "4"], f.Graph.LastListRowIds());
        await ChooseAsync(f, "w5", "3", "No enciende");
        Assert.Equal(AskContact, f.Graph.Sends()[^1]);
        await SayAsync(f, "w6", "Ana");
        await SayAsync(f, "w7", "3001234567");

        // El resumen lee el servicio del objeto de auto-select, el título de la fila elegida y la dirección del equipo (item de la lista).
        Assert.Equal(
            "Resumen de la solicitud:\n\nServicio: Servicio técnico en garantía\nTipo de daño: No enciende\nNombre de contacto: Ana\n"
            + "Número de contacto: 3001234567\nNúmero de identificación: 10203\nDirección de visita: Calle 1 # 2-3\n\n"
            + "Gracias por contactarnos. Tu servicio será atendido con la orden N° 55; un agente se comunicará contigo para confirmar la fecha y "
            + "hora de la visita.",
            f.Graph.Sends()[^1]);
        Assert.Equal("Resolved|None", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
        var runId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM automations.conversation_runs");
        Assert.Equal("Resolved", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));

        var requests = f.Api.Requests.ToArray();
        Assert.Equal(
            [
                $"GET /customers?phone={WaId}", "GET /customers?document=10203", "GET /invoices/customer/7/all-invoice-items",
                "GET /failures", "GET /service-orders/auto-select-service-type?invoiceItemId=1", "POST /service-orders",
            ],
            requests.Select(request => $"{request.Method} {request.Uri.PathAndQuery}"));
        Assert.All(requests, request => Assert.Equal(("api-clasic.test", ApiKey), (request.Uri.Host, request.ApiKey)));

        // P2-R11: el token suelto escribe el número tal cual; el que va dentro de comillas, su texto. invoiceItems[0].id es el número del
        // elemento (equipo.item.id), no el id de la fila (texto): Clasic responde 500 con "id": "1" (revisión final, 2026-10-23).
        var order = JsonNode.Parse(requests[^1].Body!)!;
        Assert.Equal(JsonValueKind.Number, order["customerId"]!.GetValueKind());
        Assert.Equal(7, order["customerId"]!.GetValue<int>());
        Assert.Equal(3, order["failureId"]!.GetValue<int>());
        Assert.Equal(9, order["serviceId"]!.GetValue<int>());
        var item = order["invoiceItems"]![0]!;
        Assert.Equal(JsonValueKind.Number, item["id"]!.GetValueKind());
        Assert.Equal((1, "Ana", "3001234567", true),
            (item["id"]!.GetValue<int>(), item["contact"]!.GetValue<string>(), item["optionalPhone"]!.GetValue<string>(), item["update"]!.GetValue<bool>()));

        // D-H6: una fila por llamada, de esta corrida, con la ruta interpolada y sin query; la tabla no tiene dónde guardar un cuerpo.
        Assert.Equal(
            "GET /customers 200 ok -,GET /customers 200 ok -,GET /invoices/customer/7/all-invoice-items 200 ok -,GET /failures 200 ok -,"
            + "GET /service-orders/auto-select-service-type 200 ok -,POST /service-orders 201 ok -",
            await HttpCallsAsync(f));
        Assert.Equal(6L, await CountAsync(f.ConnectionString,
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
        f.Api.Respond(@"^/customers\?document=", HttpStatusCode.OK, NoCustomers);

        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        // Anexo B paso 6: el botón URL de registro y las dos opciones.
        Assert.Equal(
            [
                "[cta_url] No te encuentras registrado en nuestra base de datos. Puedes dejarnos tus datos por aquí para agendar tu visita de "
                + "asistencia técnica o volver a intentarlo con otro documento usando los botones.",
                "[button] ¿Qué prefieres hacer?",
            ],
            f.Graph.Sends()[^2..]);
        f.Api.Requests.Clear();

        await RegisterFromOptionsAsync(f, "3", "No enciende");

        // El orden de las preguntas del registro, desde «Dejar mis datos» hasta la factura.
        Assert.Equal(
            [
                "¿Cuáles son tus nombres?", "¿Y tus apellidos?", "Escribe la dirección donde está el calentador.", "¿En qué barrio?", "¿En qué ciudad?",
                "[list] ¿Qué falla presenta el calentador?", "Envía la foto o el PDF de la factura de compra del calentador.",
            ],
            f.Graph.Sends()[^8..^1]);
        Assert.Equal(["GET /failures", "POST /customers/new-customer"], f.Api.Requests.Select(request => $"{request.Method} {request.Uri.PathAndQuery}"));
        var registration = f.Api.Requests.Last();
        Assert.Equal(ApiKey, registration.ApiKey);
        var file = Assert.Single(registration.Parts, part => part.Name == "file");
        Assert.Equal(InvoiceBytes, file.Bytes);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.NotNull(file.FileName);
        var fields = registration.Parts.Where(part => part.Name != "file").ToDictionary(part => part.Name, part => part.Text, StringComparer.Ordinal);
        Assert.Equal(
            ("10203", "Laura", "Gómez", "Calle 1 # 2-3", "Laureles", "Ciudad: Medellín", WaId, "3"),
            (fields["identificationNumber"], fields["firstName"], fields["lastName"], fields["address"], fields["neighborhood"],
                fields["addressDescription"], fields["phone"], fields["failureId"]));

        Assert.Equal(
            "Recibimos tus datos. Tu solicitud quedó registrada con el número 3; un agente se comunicará contigo para confirmar la visita.",
            f.Graph.Sends()[^1]);
        Assert.Equal("Resolved|None", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
        Assert.Equal(
            "GET /customers 200 ok -,GET /customers 200 ok -,GET /failures 200 ok -,POST /customers/new-customer 201 ok -",
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
            [
                // La frase del asesor la dice sólo el handoff: el aviso del error no la repite.
                "No pudimos crear la orden: puede que ya exista una abierta para esa dirección o que haya un bloqueo.",
                "Te paso con un asesor.",
            ],
            f.Graph.Sends()[^2..]);
        Assert.Equal("Open|Paused|", await TextAsync(f,
            "SELECT status || '|' || automation_status || '|' || coalesce(assigned_member_id::text, '') FROM messaging.conversations"));
        Assert.Equal("HandedOff", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));
        Assert.Equal(
            "POST /service-orders 409 error_status http_409",
            await TextAsync(f, "SELECT method || ' ' || path || ' ' || status_code || ' ' || outcome || ' ' || error_code FROM automations.http_calls ORDER BY id DESC LIMIT 1"));
    }

    /// <summary>Un bloque HTTP cuya salida <c>ok</c> vuelve a él, contra una API que siempre responde 200 (el validador permite el ciclo).</summary>
    private const string HttpLoopFlow = """
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
          "nodes": [
            { "id": "n1", "kind": "message", "title": "Hola", "text": "Hola, ya te ayudamos.", "outputs": { "next": "n2" } },
            { "id": "n2", "kind": "http", "title": "Consultar", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/status",
              "mapping": [], "outputs": { "ok": "n2", "error": "n3" } },
            { "id": "n3", "kind": "handoff", "title": "Asesor", "text": "Te paso con un asesor." }
          ] }
        """;

    /// <summary>Ruling P2-R40: cada resultado del HTTP vuelve a entrar al motor en la misma unidad; sin tope daría vueltas pasado el lease. A
    /// las 25 llamadas la corrida termina Failed <c>loop_detected</c>, la conversación pasa a un asesor y la fila del entrante se cierra en su
    /// primer intento (otra pasada no vuelve a llamar).</summary>
    [Fact]
    public async Task AnHttpLoopIsCutAtTheReentryCapAndHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, HttpLoopFlow, layoutJson: "{}");
        f.Api.Respond(@"^/status$", HttpStatusCode.OK, "{}");

        await SayAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Failed|automations.run.loop_detected", await TextAsync(f,
            "SELECT status || '|' || coalesce(failure_code, '') FROM automations.conversation_runs"));
        Assert.Equal("Open|Paused", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
        Assert.Equal(25L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.http_calls"));
        Assert.Equal(25, f.Api.Requests.Count);
        Assert.Equal(["Hola, ya te ayudamos."], f.Graph.Sends());
        Assert.Equal("1|true|run_failed", await TextAsync(f,
            "SELECT attempts || '|' || (processed_at IS NOT NULL) || '|' || coalesce(outcome, '-') FROM messaging.automation_inbox"));
    }

    /// <summary>D-H4 dentro del lease de 30 s del inbox (§5.2). Con un timeout de 1 s y una API que tardaría 5 s, la unidad termina en
    /// menos de 5: lo mismo que 8 s contra 9 s, sin que la prueba espere nueve.</summary>
    [Fact]
    public async Task ATimeoutHandsOffWithinTheLease()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, ClasicFlow.WithTimeout("find-customer", 1));
        // Sólo la búsqueda por cédula tarda: la del teléfono (policy-lookup, con su timeout de 10 s) responde enseguida.
        f.Api.RespondAfter(@"^/customers\?document=", TimeSpan.FromSeconds(5), HttpStatusCode.OK, CustomerFound);
        await SayAsync(f, "w1", "Hola");
        Assert.Equal([Welcome, AskDocument], f.Graph.Sends());

        var watch = Stopwatch.StartNew();
        await SayAsync(f, "w2", "10203");
        watch.Stop();

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Equal("Te paso con un asesor.", f.Graph.Sends()[^1]);
        Assert.Equal("Paused", await TextAsync(f, "SELECT automation_status FROM messaging.conversations"));
        Assert.Equal("HandedOff", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));
        Assert.Equal("GET /customers 200 ok -,GET /customers - timeout timeout", await HttpCallsAsync(f));
        // La fila del entrante quedó cerrada en su primer intento; otra pasada no la vuelve a reclamar ni a llamar a la API.
        Assert.Equal("1|true", await TextAsync(f,
            "SELECT attempts || '|' || (processed_at IS NOT NULL) FROM messaging.automation_inbox ORDER BY created_at DESC, message_id DESC LIMIT 1"));
        await DrainAutomationsAsync(f.Host);
        Assert.Equal(
            [$"GET /customers?phone={WaId}", "GET /customers?document=10203"],
            f.Api.Requests.Select(request => $"{request.Method} {request.Uri.PathAndQuery}"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.http_calls"));
    }

    /// <summary>D-H5 y D-H6: la clave sale sólo en el header hacia la API. Ni los logs del host, ni la auditoría, ni <c>http_calls</c>, ni la
    /// corrida, ni <c>platform.request_failures</c> la tienen.</summary>
    [Fact]
    public async Task TheKeyNeverAppearsInLogsAuditOrHttpCalls()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, traceLogs: true);
        f.Api.Respond(@"^/service-orders$", HttpStatusCode.BadRequest, """{"message":"Cliente bloqueado"}""");

        await ToTheOrderAsync(f);

        // Controles positivos: la clave sí viajó en el header, y los logs del host y la auditoría sí se capturan.
        Assert.All(f.Api.Requests, request => Assert.Equal(ApiKey, request.ApiKey));
        Assert.Contains(f.Logs.Entries, entry => entry.StartsWith("Chatbot HTTP block", StringComparison.Ordinal));
        Assert.Contains(LogLevel.Trace, f.Logs.Levels);
        Assert.True(await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries") > 0);
        Assert.Equal(6L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.http_calls"));

        Assert.DoesNotContain(ApiKey, f.Logs.AllText, StringComparison.Ordinal);
        // P2-R38: el logging de HttpClient redacta por defecto los valores de los headers (y la query), así que la clave sola no delata un
        // RemoveAllLoggers() que falte en el cliente del bloque; la URL sí: «Start processing HTTP request GET https://api-clasic.test/…»
        // (verificado quitando RemoveAllLoggers: esta aserción falla).
        Assert.DoesNotContain("document=10203", f.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain($"phone={WaId}", f.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(BaseUrl, f.Logs.AllText, StringComparison.Ordinal);
        foreach (var table in new[] { "automations.http_calls", "audit.entries", "automations.conversation_runs", "platform.request_failures" })
        {
            var rows = await TextAsync(f, $"SELECT coalesce(string_agg(row_to_json(t)::text, E'\\n'), '') FROM {table} AS t");
            Assert.DoesNotContain(ApiKey, rows, StringComparison.Ordinal);
        }
    }

    /// <summary>D-K3: el teléfono del cliente encuentra a alguien que no ha aceptado la política. El bot la pide antes que nada con dos
    /// botones y, con «Acepto», la guarda en Clasic (<c>PUT /customers/{id}</c>) y sigue con la bienvenida.</summary>
    [Fact]
    public async Task ACustomerWhoHasNotAcceptedThePolicyIsAskedAndTheAcceptanceIsSaved()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/customers\?phone=", HttpStatusCode.OK, CustomerWithoutPolicy);

        await SayAsync(f, "w1", "Hola");
        var policy = Assert.Single(f.Graph.Sends());
        Assert.Contains("política de tratamiento de datos", policy, StringComparison.Ordinal);
        Assert.Equal([$"GET /customers?phone={WaId}"], f.Api.Requests.Select(request => $"{request.Method} {request.Uri.PathAndQuery}"));

        await TapAsync(f, "w2", "acepto", "Acepto");

        var save = Assert.Single(f.Api.Requests, request => request.Method == HttpMethod.Put);
        Assert.Equal("/customers/7", save.Uri.PathAndQuery);
        Assert.Equal(ApiKey, save.ApiKey);
        // El cuerpo es la plantilla interpolada tal cual (con sus espacios): se compara el JSON, no el texto. Clasic recibe el texto "true".
        var body = JsonNode.Parse(save.Body!)!;
        Assert.Equal(JsonValueKind.String, body["acceptPolicy"]!.GetValueKind());
        Assert.Equal(("true", "Whatsapp"), (body["acceptPolicy"]!.GetValue<string>(), body["sourceAcceptPolicy"]!.GetValue<string>()));
        Assert.Equal([Welcome, AskDocument], f.Graph.Sends()[1..]);
        Assert.Equal("Waiting|document", await TextAsync(f, "SELECT status || '|' || current_node_id FROM automations.conversation_runs"));
    }

    /// <summary>Un teléfono que Clasic no conoce no tiene a quién guardarle la aceptación: el bot da la bienvenida sin preguntar.</summary>
    /// <summary>«No acepto» no guarda nada en Clasic: el bot explica que sin la política no puede atender y resuelve la conversación,
    /// así el siguiente mensaje del cliente vuelve a empezar (D-M12).</summary>
    [Fact]
    public async Task ACustomerWhoDeclinesThePolicyIsToldAndTheConversationIsResolved()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/customers\?phone=", HttpStatusCode.OK, CustomerWithoutPolicy);

        await SayAsync(f, "w1", "Hola");
        await TapAsync(f, "w2", "no-acepto", "No acepto");

        Assert.DoesNotContain(f.Api.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("Sin la aceptación de la política", f.Graph.Sends()[^1], StringComparison.Ordinal);
        Assert.Equal("Resolved|None", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
        Assert.Equal("Resolved", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));
    }

    [Fact]
    public async Task AnUnknownPhoneSkipsThePolicy()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/customers\?phone=", HttpStatusCode.OK, NoCustomers);

        await SayAsync(f, "w1", "Hola");

        Assert.Equal([Welcome, AskDocument], f.Graph.Sends());
        Assert.Equal([$"GET /customers?phone={WaId}"], f.Api.Requests.Select(request => $"{request.Method} {request.Uri.PathAndQuery}"));
        Assert.DoesNotContain(f.Api.Requests, request => request.Method == HttpMethod.Put);
    }

    /// <summary>Anexo B §8: un equipo con orden abierta no deja seguir. El bot avisa con dos botones; «Elegir otro equipo» vuelve a mandar la
    /// misma lista y con otro equipo sigue a los botones del tipo de servicio, sin haber intentado crear la orden (revisión final,
    /// 2026-10-23).</summary>
    [Fact]
    public async Task AnEquipmentWithAnOpenOrderIsRefusedAndTheListIsSentAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/invoices/customer/7/all-invoice-items$", HttpStatusCode.OK, """
            [{"id":1,"referenceCode":"CAL-001","address":"Calle 1 # 2-3","haveOrderOpen":true},{"id":2,"referenceCode":"CAL-002","address":"Carrera 4 # 5-6","haveOrderOpen":false}]
            """);

        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await ChooseAsync(f, "w3", "1", "CAL-001");

        Assert.Equal(OpenOrderNotice, f.Graph.Sends()[^1]);
        await TapAsync(f, "w4", "another", "Elegir otro equipo");

        Assert.Equal(AskEquipment, f.Graph.Sends()[^1]);
        Assert.Equal(2, f.Graph.Sends().Count(sent => sent == AskEquipment));
        Assert.Equal(["1", "2", "other"], f.Graph.LastListRowIds());

        await ChooseAsync(f, "w5", "2", "CAL-002");

        Assert.Equal(AskServiceKind, f.Graph.Sends()[^1]);
        Assert.DoesNotContain(f.Api.Requests, request => request.Uri.AbsolutePath.StartsWith("/service-orders", StringComparison.Ordinal));
    }

    /// <summary>Anexo B §8: desde el aviso de la orden abierta el cliente puede pedir un asesor (revisión final, 2026-10-23).</summary>
    [Fact]
    public async Task AnEquipmentWithAnOpenOrderCanGoToAnAgent()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/invoices/customer/7/all-invoice-items$", HttpStatusCode.OK, """
            [{"id":1,"referenceCode":"CAL-001","address":"Calle 1 # 2-3","haveOrderOpen":true}]
            """);

        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await ChooseAsync(f, "w3", "1", "CAL-001");
        await TapAsync(f, "w4", "agent", "Hablar con un asesor");

        Assert.Equal([OpenOrderNotice, "Te paso con un asesor."], f.Graph.Sends()[^2..]);
        Assert.Equal("Open|Paused", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
        Assert.Equal("HandedOff", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));
    }

    /// <summary>Anexo B, mantenimiento: el servicio sale de <c>/services?name=STM</c> y la falla de <c>/failures?cause=MANTENIMIENTO</c>, sin
    /// lista de fallas ni <c>auto-select-service-type</c>; el resumen trae los valores del mantenimiento.</summary>
    [Fact]
    public async Task MaintenanceUsesTheStmServiceAndTheMaintenanceFailure()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await ChooseAsync(f, "w3", "1", "CAL-001");
        await TapAsync(f, "w4", "maint", "Mantenimiento");
        Assert.Equal(AskContact, f.Graph.Sends()[^1]);
        await SayAsync(f, "w5", "Ana");
        await SayAsync(f, "w6", "3001234567");

        Assert.Equal(
            [
                $"GET /customers?phone={WaId}", "GET /customers?document=10203", "GET /invoices/customer/7/all-invoice-items",
                "GET /services?name=STM", "GET /failures?cause=MANTENIMIENTO", "POST /service-orders",
            ],
            f.Api.Requests.Select(request => $"{request.Method} {request.Uri.PathAndQuery}"));
        var order = JsonNode.Parse(f.Api.Requests.Last().Body!)!;
        Assert.Equal((7, 5, 12), (order["customerId"]!.GetValue<int>(), order["failureId"]!.GetValue<int>(), order["serviceId"]!.GetValue<int>()));
        var item = order["invoiceItems"]![0]!;
        Assert.Equal(JsonValueKind.Number, item["id"]!.GetValueKind());
        Assert.Equal((1, "Ana", "3001234567"),
            (item["id"]!.GetValue<int>(), item["contact"]!.GetValue<string>(), item["optionalPhone"]!.GetValue<string>()));

        var summary = f.Graph.Sends()[^1];
        Assert.Contains("Servicio: Servicio técnico de mantenimiento", summary, StringComparison.Ordinal);
        Assert.Contains("Dirección de visita: Calle 1 # 2-3", summary, StringComparison.Ordinal);
        Assert.Contains("orden N° 55", summary, StringComparison.Ordinal);
        Assert.Contains("$120.000", summary, StringComparison.Ordinal);
        Assert.Equal("Resolved|None", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
    }

    /// <summary>Plan 4 Task 3 (revisión final, 2026-10-23): si Clasic no tiene el servicio STM, <c>/services?name=STM</c> responde <c>[]</c>,
    /// el mapeo <c>$[0]</c> deja <c>servicio</c> en <c>null</c> y el bloque sale por <c>ok</c>. La condición <c>maintenance-check</c> lo ve
    /// y pasa a un asesor antes de pedir el contacto, en vez de crear una orden sin servicio.</summary>
    [Fact]
    public async Task AMissingStmServiceHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/services\?name=STM$", HttpStatusCode.OK, "[]");

        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await ChooseAsync(f, "w3", "1", "CAL-001");
        await TapAsync(f, "w4", "maint", "Mantenimiento");

        Assert.Equal("Te paso con un asesor.", f.Graph.Sends()[^1]);
        Assert.DoesNotContain(AskContact, f.Graph.Sends());
        Assert.Equal("HandedOff", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));
        Assert.DoesNotContain(f.Api.Requests, request => request.Method == HttpMethod.Post);
        Assert.EndsWith("GET /services 200 ok -,GET /failures 200 ok -", await HttpCallsAsync(f), StringComparison.Ordinal);
    }

    /// <summary>D-K8: «Otra dirección» de un cliente que ya existe no se puede registrar por la API (new-customer rechaza el documento), así
    /// que el bot pide la dirección y pasa a un asesor; el texto queda en «Datos capturados» de la conversación.</summary>
    [Fact]
    public async Task AnotherAddressOfAKnownCustomerHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        const string NewAddress = "Carrera 9 # 8-7, Belén, Medellín";

        await SayAsync(f, "w1", "Hola");
        await SayAsync(f, "w2", "10203");
        await ChooseAsync(f, "w3", "other", "Otra dirección");
        Assert.Equal("Escribe la dirección, el barrio y la ciudad de la nueva dirección.", f.Graph.Sends()[^1]);
        await SayAsync(f, "w4", NewAddress);

        Assert.Equal("Gracias. Te paso con un asesor para registrar la dirección nueva.", f.Graph.Sends()[^1]);
        Assert.Equal("Open|Paused", await TextAsync(f, "SELECT status || '|' || automation_status FROM messaging.conversations"));
        Assert.Equal("HandedOff", await TextAsync(f, "SELECT status FROM automations.conversation_runs"));
        Assert.DoesNotContain(f.Api.Requests, request => request.Method != HttpMethod.Get);

        // §5.5: lo que el asesor ve en la bandeja, con el permiso de la bandeja y no el de Automations (D-C10).
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        using var agent = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingPermissions.ConversationRead);
        var details = await agent.GetFromJsonAsync<ConversationAutomationDetailsDto>(
            $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/conversations/{conversationId}/automation", Ct);
        Assert.Equal("HandedOff", details!.Status);
        Assert.Equal(NewAddress, details.Variables.GetProperty("direccionNueva").GetString());
    }

    /// <summary>Anexo C: <c>POST /customers/new-customer</c> recibe exactamente los campos de <c>CreateCustomerWhatsappDto</c> más el archivo;
    /// departamento y ciudad van fijos (Antioquia, Medellín) y la falla es el id de la fila que eligió el cliente.</summary>
    [Fact]
    public async Task RegistrationSendsTheRealMultipartFields()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Api.Respond(@"^/customers\?document=", HttpStatusCode.OK, NoCustomers);

        await RegisterAsync(f, failureRowId: "4", failureTitle: "Fuga de agua");

        var registration = Assert.Single(f.Api.Requests, request => request.Uri.AbsolutePath == "/customers/new-customer");
        string[] expected =
        [
            "identificationNumber", "firstName", "lastName", "address", "neighborhood", "addressDescription", "phone", "departmentId", "cityId",
            "failureId", "file",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), registration.Parts.Select(part => part.Name).Order(StringComparer.Ordinal));
        var fields = registration.Parts.Where(part => part.Name != "file").ToDictionary(part => part.Name, part => part.Text, StringComparer.Ordinal);
        Assert.Equal(("5", "5001000", "4"), (fields["departmentId"], fields["cityId"], fields["failureId"]));
        Assert.Equal(InvoiceBytes, Assert.Single(registration.Parts, part => part.Name == "file").Bytes);
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

        private readonly ConcurrentQueue<LogLevel> levels = new();

        public IReadOnlyCollection<string> Entries => entries;

        /// <summary>El nivel de cada entrada: el control positivo de que el host de verdad registra desde Trace (P2-R38).</summary>
        public IReadOnlyCollection<LogLevel> Levels => levels;

        public string AllText => string.Join('\n', entries);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries, levels);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries, ConcurrentQueue<LogLevel> levels) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                levels.Enqueue(logLevel);
                entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
            }
        }
    }
}
