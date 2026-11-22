using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Quotations.Domain;
using Modules.Siigo.Application;
using static Modules.Siigo.IntegrationTests.InvoiceWhatsAppHarness;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;
using static Modules.Siigo.IntegrationTests.SiigoInvoicingHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Spec 2026-11-18 §9 y CLAUDE.md (secretos): la clave de acceso de la conexión, el token de Siigo y los datos del cliente facturado
/// (documento y dirección) no salen del backend salvo hacia Siigo. Se ejerce un ciclo completo con el host en <c>Trace</c> (probar la
/// conexión, leer la configuración, el borrador, un rechazo de Siigo que repite la dirección del cliente y la factura creada) y se busca
/// cada sentinela en todo lo que el host logueó, en cada respuesta que no sea el borrador y en <c>audit.entries</c>.
/// </summary>
public sealed class SiigoNoLeakTests
{
    private const string AccessKey = "siigo-access-key-sentinel";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAccessKeyTokenAndCustomerDataNeverReachLogsOrAudit()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var logs = new RecordingLoggerProvider();
        using var factory = world.Factory.WithWebHostBuilder(builder => builder
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs))
            // Las reglas de appsettings (Default Information) ganan sobre SetMinimumLevel; una regla del proveedor de la prueba sin
            // categoría gana sobre todas ellas para ese proveedor, así que captura todo desde Trace.
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddFilter<RecordingLoggerProvider>(null, LogLevel.Trace)));
        using var client = CreateClient(
            factory,
            world.Tenant.OwnerUserId,
            world.TenantId,
            [.. InvoicerPermissions, IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage, SiigoPermissions.CallsRead]);

        var customer = await ReadAsync(await client.GetAsync($"/api/v1/tenants/{world.TenantId}/customers/{world.CustomerId}", Ct));
        var documentNumber = customer.GetProperty("identificationNumber").GetString()!;
        var secrets = new[] { AccessKey, FakeSiigoApi.Token, documentNumber, documentNumber.Split('-')[0].Replace(".", string.Empty), "Calle 10 # 45-12" };
        Assert.All(secrets, secret => Assert.False(string.IsNullOrWhiteSpace(secret)));

        var bodies = new List<(string Call, string Body)>();
        async Task<HttpStatusCode> CallAsync(string call, Func<Task<HttpResponseMessage>> send)
        {
            using var response = await send();
            bodies.Add(($"{call} → {(int)response.StatusCode}", await response.Content.ReadAsStringAsync(Ct)));
            return response.StatusCode;
        }

        var tenantRoute = $"/api/v1/tenants/{world.TenantId}";
        await CallAsync("POST test connection", () => client.PostAsync($"{tenantRoute}/integrations/connections/{world.ConnectionId}/test", null, Ct));
        await CallAsync("GET connection", () => client.GetAsync($"{tenantRoute}/integrations/connections/{world.ConnectionId}", Ct));
        await CallAsync("GET connections", () => client.GetAsync($"{tenantRoute}/integrations/connections", Ct));
        await CallAsync("GET settings", () => client.GetAsync($"{tenantRoute}/siigo/connections/{world.ConnectionId}/settings", Ct));
        await CallAsync("GET options", () => client.GetAsync($"{tenantRoute}/siigo/connections/{world.ConnectionId}/options/taxes", Ct));

        // Siigo rechaza y su mensaje repite la dirección del cliente; después la factura se crea. La petición (con el cliente) sólo
        // viaja a Siigo: ni el log del cliente HTTP, ni la fila ni la respuesta la repiten.
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(
            HttpStatusCode.BadRequest,
            """{"Errors":[{"Code":"invalid_customer","Message":"Cliente invalido: Calle 10 # 45-12"}]}"""));
        await CallAsync("POST invoice rejected", () => client.PostAsync(world.BaseRoute, null, Ct));
        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-123", "d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f", 0m));
        await CallAsync("POST invoice created", () => client.PostAsync(world.BaseRoute, null, Ct));
        await CallAsync("GET invoices", () => client.GetAsync(world.BaseRoute, Ct));
        // Spec 2026-11-21: la lista de fallas no lleva el curl ni la respuesta (que repiten la dirección y el documento del cliente).
        await CallAsync("GET call-failures", () => client.GetAsync($"{tenantRoute}/siigo/call-failures", Ct));

        // La prueba ejerció el camino de las credenciales y de los datos: Siigo recibió el token y el cliente, y cada llamada llegó a su
        // handler (no un 500 ni un 401 que no diga nada).
        Assert.Contains(world.Siigo.Calls, call => call.Header("Authorization") == $"Bearer {FakeSiigoApi.Token}");
        Assert.Contains(world.Siigo.Calls, call => call.Path == "/auth" && call.Body!.Contains(AccessKey, StringComparison.Ordinal));
        Assert.Contains(world.Siigo.Calls, call => call.Path == "/v1/customers" && call.Body is not null && call.Body.Contains("Calle 10", StringComparison.Ordinal));
        Assert.Equal(2, InvoiceCalls(world.Siigo).Count);
        Assert.Equal(
            [
                "POST test connection → 200", "GET connection → 200", "GET connections → 200", "GET settings → 200", "GET options → 200",
                "POST invoice rejected → 200", "POST invoice created → 200", "GET invoices → 200", "GET call-failures → 200",
            ],
            bodies.Select(entry => entry.Call));
        // Control positivo: el host registró en Trace.
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Trace);
        // El cliente de Siigo no tiene loggers (RemoveAllLoggers). Con los defaults de .NET 10 su log ya redacta headers y queries, así que
        // ningún sentinela se vería aunque volviera: la invariante es más estricta, ni una línea de su categoría (verificado quitando
        // RemoveAllLoggers: esta aserción falla con «Start processing HTTP request POST https://api.siigo.com/auth»).
        Assert.Empty(
            logs.Entries
                .Where(entry => entry.Category.StartsWith("System.Net.Http.HttpClient.siigo", StringComparison.Ordinal))
                .Select(entry => $"{entry.Level} {entry.Message}"));

        var audit = await ScalarAsync<string>(world.ConnectionString, "SELECT coalesce(string_agg(e::text, E'\\n'), '') FROM audit.entries e");
        // La auditoría que se revisa incluye la de Siigo: el rechazo y la creación la escribieron.
        Assert.Equal(
            ["siigo.invoice.created", "siigo.invoice.rejected"],
            (await ScalarAsync<string>(
                world.ConnectionString,
                "SELECT coalesce(string_agg(action, ',' ORDER BY action), '') FROM audit.entries WHERE tenant_id = @t AND action LIKE 'siigo.invoice.%'",
                ("t", world.TenantId))).Split(',', StringSplitOptions.RemoveEmptyEntries));
        // La fila del rechazo guarda el curl y la respuesta de Siigo (con datos del cliente: es su propósito, y sólo el detalle los devuelve),
        // pero nunca el token ni la clave de acceso, ni siquiera tras autenticar: control positivo con el código que Siigo repitió.
        var failures = await ScalarAsync<string>(world.ConnectionString, "SELECT coalesce(string_agg(f::text, E'\n'), '') FROM siigo.call_failures f");
        Assert.Contains("invalid_customer", failures, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, failures, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSiigoApi.Token, failures, StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
            Assert.Empty(bodies.Where(entry => entry.Body.Contains(secret, StringComparison.Ordinal)).Select(entry => $"{entry.Call} lleva «{secret}»: {entry.Body}"));
        }
    }

    /// <summary>
    /// Spec 2026-11-21: mandar la factura por WhatsApp. El PDF, la URL pública de la copia de Zenvia y el teléfono del cliente sólo existen
    /// en memoria y en el mensaje que sale: tras un envío bueno y uno que falla, ninguno aparece en lo que el host logueó, en
    /// <c>siigo.call_failures</c>, en la auditoría ni en una respuesta.
    /// </summary>
    [Fact]
    public async Task ThePdfThePublicUrlAndThePhoneNeverReachLogsFailuresAuditOrResponsesWhenSendingByWhatsApp()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await world.Client.PostAsync(world.BaseRoute, null, Ct));
        var invoiceId = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0].GetProperty("id").GetGuid();
        SiigoServesThePdf(world);
        var (host, doubles) = Host(world, Zenvia);
        var logs = new RecordingLoggerProvider();
        using var factory = host.WithWebHostBuilder(builder => builder
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs))
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddFilter<RecordingLoggerProvider>(null, LogLevel.Trace)));
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, [.. InvoicerPermissions, SiigoPermissions.CallsRead]);

        var bodies = new List<(string Call, string Body)>();
        async Task<HttpStatusCode> CallAsync(string call, Func<Task<HttpResponseMessage>> send)
        {
            using var response = await send();
            bodies.Add(($"{call} → {(int)response.StatusCode}", await response.Content.ReadAsStringAsync(Ct)));
            return response.StatusCode;
        }

        var route = $"{world.BaseRoute}/{invoiceId}/send-whatsapp";
        await CallAsync("POST send ok", () => client.PostAsync(route, null, Ct));
        doubles.Sender.Failure = new QuotationsDomainException("invoice.whatsapp.send_failed", "The WhatsApp send failed.");
        await CallAsync("POST send failed", () => client.PostAsync(route, null, Ct));
        await CallAsync("GET invoices", () => client.GetAsync(world.BaseRoute, Ct));
        await CallAsync("GET call-failures", () => client.GetAsync($"/api/v1/tenants/{world.TenantId}/siigo/call-failures", Ct));

        // Control positivo: cada envío publicó su copia (el malo también: publica antes de mandar), el bueno llevó el teléfono y el malo
        // contestó su 422; si no, buscar acá no probaría nada.
        Assert.Equal(2, doubles.Storage.Published.Count);
        var sent = Assert.Single(doubles.Sender.Invoices).Message;
        Assert.Equal(["POST send ok → 204", "POST send failed → 422", "GET invoices → 200", "GET call-failures → 200"], bodies.Select(entry => entry.Call));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Trace);
        Assert.False(string.IsNullOrWhiteSpace(sent.ToPhone));

        string[] secrets =
        [
            .. doubles.Storage.Published.Select(copy => copy.Url),
            .. doubles.Storage.Published.Select(copy => copy.Url.Split('/')[^1].Replace(".pdf", string.Empty, StringComparison.Ordinal)),
            PdfBase64,
            "invoice-pdf-sentinel-7c41",
            sent.ToPhone!,
            sent.ToPhone!.Replace(" ", string.Empty, StringComparison.Ordinal),
        ];
        var audit = await ScalarAsync<string>(world.ConnectionString, "SELECT coalesce(string_agg(e::text, E'\n'), '') FROM audit.entries e");
        var failures = await ScalarAsync<string>(world.ConnectionString, "SELECT coalesce(string_agg(f::text, E'\n'), '') FROM siigo.call_failures f");
        Assert.Contains("siigo.invoice.whatsapp_sent", audit, StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            Assert.False(string.IsNullOrWhiteSpace(secret));
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, failures, StringComparison.Ordinal);
            Assert.Empty(bodies.Where(entry => entry.Body.Contains(secret, StringComparison.Ordinal)).Select(entry => $"{entry.Call} lleva «{secret}»"));
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception)));
        }
    }
}
