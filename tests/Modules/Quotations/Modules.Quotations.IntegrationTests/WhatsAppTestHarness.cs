using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Tenancy.Application;
using Npgsql;

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
}
