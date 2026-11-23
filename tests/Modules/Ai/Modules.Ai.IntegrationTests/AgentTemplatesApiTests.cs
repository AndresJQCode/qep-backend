using System.Net;
using System.Text.Json;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §4: la galería de plantillas de agente del sistema y crear un agente desde una. <c>available</c> lo calcula el backend
/// con los módulos efectivos del tenant; el frontend sólo lo pinta.
/// </summary>
public sealed class AgentTemplatesApiTests
{
    private static readonly string[] Manage = [AiPermissions.AgentRead, AiPermissions.AgentManage];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Templates => $"{AiUrl()}/agent-templates";

    private static Task<int> DisableSchedulingAsync(string connectionString) =>
        ExecuteAsync(
            connectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'scheduling'",
            ("t", Guid.Parse(TenantId)));

    [Fact]
    public async Task TheTemplateCatalogSaysWhatTheTenantCanAdopt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);

        var before = await JsonAsync(await client.GetAsync(Templates, Ct));
        await DisableSchedulingAsync(database.GetConnectionString());
        var after = await JsonAsync(await client.GetAsync(Templates, Ct));

        var template = Assert.Single(before.EnumerateArray());
        Assert.Equal("asistente-de-citas", template.GetProperty("id").GetString());
        Assert.Equal("Asistente de citas", template.GetProperty("name").GetString());
        Assert.Equal(["scheduling"], template.GetProperty("requires").EnumerateArray().Select(key => key.GetString()));
        Assert.True(template.GetProperty("available").GetBoolean());
        Assert.False(Assert.Single(after.EnumerateArray()).GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task ListingTemplatesNeedsManage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var reader = CreateClient(factory, permissions: AiPermissions.AgentRead);

        var response = await reader.GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
