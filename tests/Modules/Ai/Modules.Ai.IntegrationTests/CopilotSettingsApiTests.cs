using System.Net;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>Spec 2026-11-17 (copiloto) §8, plan fase 2 C1.4 y adenda D-10: <c>GET|PUT /ai/copilot/settings</c> y <c>GET /ai/copilot/status</c>
/// de punta a punta, con la política, la capacidad <c>copilot</c> y el <c>If-Match</c>.</summary>
public sealed class CopilotSettingsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CopilotUrl(string tenantId = TenantId) => $"{AiUrl(tenantId)}/copilot";

    [Fact]
    public async Task WithoutTheCopilotModuleEverythingIs403ModuleNotEnabled()
    {
        var (database, factory) = await OpenAsync(Modules.Tenancy.Domain.TenantModuleKeys.All.Where(key => key != Modules.Tenancy.Domain.TenantModuleKeys.Copilot));
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, TenantId, AiPermissions.CopilotUse, AiPermissions.CopilotManage);

        foreach (var response in new[]
        {
            await client.GetAsync($"{CopilotUrl()}/settings", Ct),
            await client.GetAsync($"{CopilotUrl()}/status", Ct),
            await SendAsync(client, HttpMethod.Put, $"{CopilotUrl()}/settings", Body(enabled: false), ifMatch: 0),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(Modules.Tenancy.Application.TenantModuleGuard.ModuleNotEnabledCode, (await JsonAsync(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ThePutNeedsIfMatch()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, TenantId, AiPermissions.CopilotManage);

        var missing = await SendAsync(client, HttpMethod.Put, $"{CopilotUrl()}/settings", Body(enabled: false));
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        // Sin fila la versión es 0; el primer guardado la crea con 1 y el mismo If-Match otra vez es 412.
        var first = await client.GetAsync($"{CopilotUrl()}/settings", Ct);
        Assert.Equal(0, (await JsonAsync(first)).GetProperty("version").GetInt64());
        var saved = await SendAsync(client, HttpMethod.Put, $"{CopilotUrl()}/settings", Body(enabled: false), ifMatch: 0);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, (await JsonAsync(saved)).GetProperty("version").GetInt64());
        var stale = await SendAsync(client, HttpMethod.Put, $"{CopilotUrl()}/settings", Body(enabled: false), ifMatch: 0);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task EnablingWithTheConnectedProviderShowsItInTheStatus()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedAnthropicConnectionAsync(factory);
        using var manager = CreateClient(factory, TenantId, AiPermissions.CopilotManage);
        using var member = CreateClient(factory, TenantId, AiPermissions.CopilotUse);

        var before = await JsonAsync(await member.GetAsync($"{CopilotUrl()}/status", Ct));
        Assert.Equal("not_configured", before.GetProperty("reason").GetString());

        var settings = await JsonAsync(await manager.GetAsync($"{CopilotUrl()}/settings", Ct));
        var model = settings.GetProperty("models").GetProperty("providers")[0].GetProperty("models")[0].GetProperty("id").GetString()!;
        var saved = await SendAsync(manager, HttpMethod.Put, $"{CopilotUrl()}/settings", Body(enabled: true, model: model), ifMatch: 0);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await JsonAsync(await member.GetAsync($"{CopilotUrl()}/status", Ct));
        Assert.True(after.GetProperty("available").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, after.GetProperty("reason").ValueKind);

        // OpenAI sin cuenta: prenderlo con su modelo es 422 ai.provider.not_configured con el proveedor.
        var openAi = await SendAsync(
            manager, HttpMethod.Put, $"{CopilotUrl()}/settings", Body(enabled: true, provider: "openai", model: "gpt-6-luna"), ifMatch: 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, openAi.StatusCode);
    }

    [Fact]
    public async Task UseCannotReadTheSettings()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, TenantId, AiPermissions.CopilotUse);

        var response = await client.GetAsync($"{CopilotUrl()}/settings", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static object Body(bool enabled, string provider = "anthropic", string? model = null) =>
        new
        {
            enabled,
            provider = enabled || model is not null ? provider : null,
            model,
            instructions = "Somos una distribuidora.",
            monthlyTokenBudget = (long?)null,
            retentionDays = 90,
        };
}
