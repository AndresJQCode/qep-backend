using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Ai.IntegrationTests.AiApiHarness;
using TenancyDomain = Modules.Tenancy.Domain;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (copiloto) §7 y §10, plan fase 2 C3.5: el recorrido completo con el worker prendido y la Messages API de mentira. El
/// modelo pide el resumen de ventas y una búsqueda de clientes, cita al cliente (y una cita inventada que el backend descarta), y la
/// respuesta queda en el hilo con su consumo por miembro y sus <c>tool_calls</c>.
/// </summary>
public sealed class CopilotEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFullRoundTripAsksForTheSummaryAndCitesIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var anthropic = new ScriptedAnthropicHandler();
        anthropic.Enqueue(_ => FakeAnthropicMessagesHandler.ToolUse("toolu_1", "get_orders_summary", """{"from":"2026-10-01","to":"2026-10-31","advisorId":null,"clientId":null,"paymentStatus":null,"source":null}"""));
        anthropic.Enqueue(_ => FakeAnthropicMessagesHandler.ToolUse("toolu_2", "search_customers", """{"search":"Acme","identificationNumber":null,"phone":null,"page":null}"""));
        anthropic.Enqueue(request => CopilotHarness.Answer("Acme es tu cliente.", CopilotHarness.FirstCustomerRef(request), Guid.NewGuid().ToString()));
        using var factory = CopilotHarness.Factory(connectionString, anthropic);
        await CopilotHarness.EnableAsync(factory);
        var customerId = await CopilotHarness.CreateCustomerAsync(factory, TenantId, "Acme SAS");

        var thread = await CopilotHarness.AskAndWaitAsync(factory, "¿Cuánto vendimos en octubre?");

        var answer = thread.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal(("Done", "Acme es tu cliente."), (answer.GetProperty("status").GetString(), answer.GetProperty("text").GetString()));
        // D-C6: sólo la cita que salió de un ref de este trabajo; la inventada se descarta.
        var citation = Assert.Single(answer.GetProperty("citations").EnumerateArray());
        Assert.Equal(
            (CopilotCitationKinds.Customer, customerId.ToString(), "Acme SAS"),
            (citation.GetProperty("kind").GetString(), citation.GetProperty("id").GetString(), citation.GetProperty("label").GetString()));

        // La segunda llamada al modelo llevó el resultado del resumen con su alcance; las herramientas corrieron como el miembro.
        Assert.Contains("scope", anthropic.Bodies[1], StringComparison.Ordinal);
        Assert.Equal(
            2L,
            await ScalarAsync<long>(connectionString, "SELECT count(*) FROM ai.tool_calls WHERE error_code IS NULL AND tool IN ('get_orders_summary','search_customers')"));
        Assert.Equal(
            3L,
            await ScalarAsync<long>(
                connectionString,
                "SELECT count(*) FROM ai.usage_records WHERE copilot_thread_id IS NOT NULL AND agent_id IS NULL AND member_user_id = @member",
                ("member", Guid.Parse(SubjectId))));

        // D-C12: la configuración ya muestra el consumo del miembro.
        using var manager = CreateClient(factory, TenantId, AiPermissions.CopilotManage);
        var usage = (await JsonAsync(await manager.GetAsync($"{AiUrl()}/copilot/settings", Ct))).GetProperty("usageThisMonth");
        var member = Assert.Single(usage.GetProperty("byMember").EnumerateArray());
        Assert.Equal((SubjectId, "Admin de pruebas"), (member.GetProperty("userId").GetString(), member.GetProperty("name").GetString()));
    }
}

/// <summary>La Messages API de mentira del copiloto: cada respuesta se arma con el cuerpo de la petición (para citar un id que devolvió una
/// herramienta), y <see cref="OnRequest"/> deja a la prueba cambiar el mundo entre dos llamadas. Anota los cuerpos, nunca la clave.</summary>
internal sealed class ScriptedAnthropicHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<string, string>> _script = new();

    public List<string> Bodies { get; } = [];

    public List<bool> KeyMatched { get; } = [];

    /// <summary>Corre antes de responder la petición número <c>index</c> (desde 0).</summary>
    public Func<int, Task>? OnRequest { get; set; }

    public void Enqueue(Func<string, string> response) => _script.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : request.Headers.Authorization?.Parameter;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        int index;
        lock (Bodies)
        {
            index = Bodies.Count;
            Bodies.Add(body);
            KeyMatched.Add(key == AnthropicTestKey);
        }

        if (OnRequest is { } hook)
        {
            await hook(index);
        }

        var response = _script.TryDequeue(out var next) ? next(body) : CopilotHarness.Answer("Listo.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}

/// <summary>El arranque de las pruebas de punta a punta del copiloto: worker prendido, cuenta de Anthropic, módulo y copiloto encendidos.</summary>
internal static partial class CopilotHarness
{
    public static string ThreadsUrl => $"{AiUrl()}/copilot/threads";

    public static QepApiFactory Factory(string connectionString, HttpMessageHandler anthropic, IReadOnlyDictionary<string, string>? settings = null) =>
        new(connectionString, services => AgentPlaygroundApiTests.UseFakeAnthropic(services, anthropic), settings);

    public static async Task EnableAsync(QepApiFactory factory)
    {
        await SeedTenantAsync(factory);
        await SeedAnthropicConnectionAsync(factory);
        using var manager = CreateClient(factory, TenantId, AiPermissions.CopilotManage);
        var model = (await JsonAsync(await manager.GetAsync($"{AiUrl()}/copilot/settings", TestContext.Current.CancellationToken)))
            .GetProperty("models").GetProperty("providers")[0].GetProperty("models")[0].GetProperty("id").GetString();
        var saved = await SendAsync(
            manager,
            HttpMethod.Put,
            $"{AiUrl()}/copilot/settings",
            new { enabled = true, provider = "anthropic", model, instructions = "", monthlyTokenBudget = (long?)null, retentionDays = 90 },
            ifMatch: 0);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    /// <summary>Un cliente de afuera de Colombia (sin ciudad DIVIPOLA), creado por la API como lo crearía un asesor.</summary>
    public static async Task<Guid> CreateCustomerAsync(QepApiFactory factory, string tenantId, string name)
    {
        using var client = CreateClient(factory, tenantId, "customers.customer.read", "customers.customer.manage");
        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers",
            new
            {
                name,
                businessName = name,
                identificationType = "PASAPORTE",
                identificationNumber = $"P{Random.Shared.Next(100000, 999999)}",
                // El teléfono es obligatorio al crear un cliente (CustomerWriteRules).
                phone = "+13055550123",
                email = (string?)null,
                address = (string?)null,
                country = "US",
                cityId = (Guid?)null,
                cityName = "Miami",
                classificationId = (Guid?)null,
                vatSurplus = false,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await JsonAsync(created)).GetProperty("id").GetGuid();
    }

    /// <summary>Un segundo tenant con fila y módulos, para las pruebas de aislamiento. Sin membresía de quien pregunta: no hace falta.</summary>
    public static async Task SeedOtherTenantAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenancyDomain.TenantId(Guid.Parse(OtherTenantId));
        var now = DateTimeOffset.UtcNow;
        tenancy.Tenants.Add(TenancyDomain.Tenant.Create(
            id, "ai-api-tests-other", "Otra empresa", "es-CO", "America/Bogota", "yyyy-MM-dd", TenancyDomain.MembershipId.New(), now));
        await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
        foreach (var key in TenancyDomain.TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({id.Value}, {key.Value}, {now}, {TenancyDomain.TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Crea el hilo con la pregunta y sondea como el frontend hasta que no quede respuesta pendiente (30 s de tope).</summary>
    public static async Task<JsonElement> AskAndWaitAsync(QepApiFactory factory, string question)
    {
        using var client = CreateClient(factory, TenantId, AiPermissions.CopilotUse);
        var created = await client.PostAsJsonAsync(ThreadsUrl, new { text = question }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var threadId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var thread = await JsonAsync(await client.GetAsync($"{ThreadsUrl}/{threadId}", TestContext.Current.CancellationToken));
            if (!thread.GetProperty("pending").GetBoolean())
            {
                return thread;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "The copilot job did not finish in 30 s.");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>La herramienta final <c>answer</c> con su texto y sus citas (ids).</summary>
    public static string Answer(string text, params string?[] citations) =>
        FakeAnthropicMessagesHandler.ToolUse(
            "toolu_answer",
            CopilotToolNames.Answer,
            JsonSerializer.Serialize(new { text, citations = citations.Where(citation => citation is not null) }));

    /// <summary>El id del primer <c>ref</c> de cliente que viajó al modelo en un resultado de herramienta (escapado dentro del JSON).</summary>
    public static string? FirstCustomerRef(string requestBody)
    {
        var match = CustomerRef().Match(requestBody);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Cambia los roles de la membresía de quien pregunta (o su estado) a mitad de un trabajo, como lo haría un admin. Por SQL
    /// directo, así que sube la versión de acceso del tenant como lo hace todo camino que cambia permisos: sin eso la caché de permisos
    /// (<c>MemoryPermissionCache</c>, prendida aunque Valkey esté apagado) sirve los de antes hasta 60 s.</summary>
    public static async Task UpdateMembershipAsync(QepApiFactory factory, string connectionString, string setClause)
    {
        var tenantId = Guid.Parse(TenantId);
        var accessVersion = factory.Services.GetRequiredService<Modules.Tenancy.Application.ITenantAccessVersion>();
        await accessVersion.BumpAsync(tenantId, TestContext.Current.CancellationToken);
        await ExecuteAsync(
            connectionString,
            $"UPDATE tenancy.memberships SET {setClause}, version = version + 1 WHERE tenant_id = @tenant AND user_id = @user",
            ("tenant", tenantId), ("user", Guid.Parse(SubjectId)));
        await accessVersion.BumpAsync(tenantId, TestContext.Current.CancellationToken);
    }

    // La comilla del JSON anidado en el tool_result puede viajar como \" o como " (lo que escape el serializador).
    [GeneratedRegex("""kind(?:\\u0022|\\?")\s*:\s*(?:\\u0022|\\?")customer(?:\\u0022|\\?")\s*,\s*(?:\\u0022|\\?")id(?:\\u0022|\\?")\s*:\s*(?:\\u0022|\\?")([0-9a-f-]{36})""")]
    private static partial Regex CustomerRef();
}
