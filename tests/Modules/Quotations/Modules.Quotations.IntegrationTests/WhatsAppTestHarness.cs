using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Whatsapp;
using Modules.Tenancy.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de WhatsApp por tenant (spec 2026-10-07). Aparte de
/// <see cref="QuotationsApiHarness"/> porque nada de esto lo usa otra suite.
/// </summary>
internal static class WhatsAppTestHarness
{
    /// <summary>Una key de Zenvia inventada y fácil de buscar: la prueba de fugas la persigue por
    /// respuestas, logs y <c>platform.request_failures</c>. ASCII visible, sin espacios: pasa el
    /// validador.</summary>
    public const string SentinelApiKey = "zenvia-key-SENTINEL-7f3a9c";

    public const string FromNumber = "573001234567";

    public const string TemplateId = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static async Task<int> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public static string WhatsAppSettingsUrl(Guid tenantId) =>
        $"/api/v1/tenants/{tenantId}/quotations/whatsapp-settings";

    public static string WhatsAppChannelUrl(Guid tenantId) =>
        $"/api/v1/tenants/{tenantId}/quotations/whatsapp-channel";

    public static readonly string[] SettingsPermissions =
        [TenancyPermissions.SettingsRead, TenancyPermissions.SettingsUpdate];

    /// <summary>El cuerpo va como objeto anónimo a propósito: así una prueba manda exactamente lo
    /// que quiere (campos ausentes incluidos), sin un record que los rellene con null.</summary>
    public static async Task<HttpResponseMessage> PutSettingsAsync(
        HttpClient client, Guid tenantId, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, WhatsAppSettingsUrl(tenantId))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static object OwnBody(string apiKey = SentinelApiKey) => new
    {
        mode = "Own",
        provider = "Zenvia",
        apiKey,
        fromNumber = FromNumber,
        templateId = TemplateId,
    };

    /// <summary><see cref="QuotationsApiHarness.CreateClient"/> pide un <c>QepApiFactory</c>; un
    /// host derivado con <c>WithWebHostBuilder</c> no lo es. Mismos headers del stub.</summary>
    public static HttpClient CreateClientFor(
        WebApplicationFactory<Program> factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    /// <summary>Un cuerpo de Zenvia inventado: no puede aparecer en ningún camino de salida.</summary>
    public const string SentinelZenviaBody = "zenvia-body-SENTINEL-5b2d1e";

    public static readonly string[] SendPermissions = [.. ManagerPermissions, .. SettingsPermissions];

    /// <summary>Reemplaza el sender global (el de la cuenta de QEP) por uno que anota. Mismo
    /// mecanismo que <c>WithExportProcessors</c>: el real se saca primero.</summary>
    public static WebApplicationFactory<Program> WithWhatsAppSender(
        this WebApplicationFactory<Program> factory, IWhatsAppSender sender) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWhatsAppSender>();
            services.AddSingleton(sender);
        }));

    /// <summary>Reemplaza el HttpClient de Zenvia, que comparten la cuenta de QEP y las propias:
    /// lo que salga hacia Zenvia lo ve el handler de la prueba.</summary>
    public static WebApplicationFactory<Program> WithZenviaHandler(
        this WebApplicationFactory<Program> factory, HttpMessageHandler handler) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ZenviaHttpClient>();
            services.AddSingleton(new ZenviaHttpClient(new HttpClient(handler)));
        }));

    /// <summary>Un proveedor de logs más: LoggerFactory recibe todos los ILoggerProvider
    /// registrados, así que esto ve lo mismo que la consola.</summary>
    public static WebApplicationFactory<Program> WithCapturedLogs(
        this WebApplicationFactory<Program> factory, CapturedLogs logs) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ILoggerProvider>(logs)));

    /// <summary>Pisa la llave activa y declara las que se pasen. Se aplica después del
    /// ConfigureWebHost del harness, así que gana.</summary>
    public static WebApplicationFactory<Program> WithSecretProtection(
        this WebApplicationFactory<Program> factory, string activeKeyId, params (string Id, string Value)[] keys) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Quotations:SecretProtection:ActiveKeyId", activeKeyId);
            foreach (var (id, value) in keys)
            {
                builder.UseSetting($"Quotations:SecretProtection:Keys:{id}", value);
            }
        });

    /// <summary>Lo mínimo que <c>Quotation.EnsureComplete</c> pide para enviar —un producto,
    /// vigencia y cuenta de cobro— sin mandarla todavía (a diferencia de CreateSentQuotationAsync).
    /// El cliente sembrado tiene teléfono: un envío por la cuenta propia no cae en
    /// recipient_missing.</summary>
    public static async Task<Guid> CreateSendableQuotationAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(
            client,
            tenantId,
            clientId,
            billingAccount: new QuotationBillingAccountRequest(
                billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        return quotation.Id;
    }

    public static Task<HttpResponseMessage> SendAsync(
        HttpClient client, Guid tenantId, Guid quotationId, object? body = null) =>
        body is null
            ? client.PostAsync($"{QuotationsUrl(tenantId)}/{quotationId}/send", null, TestContext.Current.CancellationToken)
            : client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/{quotationId}/send", body, TestContext.Current.CancellationToken);

    /// <summary>Mensaje y detalle de todas las fallas guardadas: lo que lee la pantalla de Log
    /// con <c>platform.request_log.read</c>.</summary>
    public static Task<string> RequestFailuresTextAsync(string connectionString) =>
        ScalarAsync<string>(
            connectionString,
            "SELECT coalesce(string_agg(message || ' ' || detail, ' '), '') FROM platform.request_failures");
}

/// <summary>El sender de la cuenta de QEP, pero anotando. Propio de este proyecto: el de
/// QuotationsTestDoubles es internal de las unitarias.</summary>
internal sealed class RecordingIntegrationWhatsAppSender : IWhatsAppSender
{
    public ConcurrentQueue<WhatsAppQuotationMessage> Sent { get; } = new();

    public Task SendQuotationAsync(WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>Zenvia de mentira: anota token y cuerpo de cada request y responde lo que la prueba
/// pida.</summary>
internal sealed class CapturingZenviaHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
{
    public ConcurrentQueue<(string? Token, string Json)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.TryGetValues("X-API-TOKEN", out var values) ? values.FirstOrDefault() : null;
        var json = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((token, json));
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
    }
}

/// <summary>Todo lo que se loguea, ya formateado y con la excepción entera (mensaje, internas y
/// pila): es lo mismo que guarda el log JSON de producción.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public string AllText => string.Join('\n', _entries);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
    }
}
