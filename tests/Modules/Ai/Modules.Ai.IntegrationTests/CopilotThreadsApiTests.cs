using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>Spec 2026-11-17 (copiloto) §8 y D-C8, plan fase 2 C1.5: los hilos privados de punta a punta. Cada miembro ve sólo los suyos; otro
/// tenant es 403 de ruta y nunca datos; preguntar deja la respuesta y el trabajo en la base, en una transacción.</summary>
public sealed class CopilotThreadsApiTests
{
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000b2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ThreadsUrl(string tenantId = AiApiHarness.TenantId) => $"{AiUrl(tenantId)}/copilot/threads";

    [Fact]
    public async Task AMemberOnlyListsItsOwnThreads()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = await EnabledAsync(connectionString);
        using var owner = CreateClient(factory, AiApiHarness.TenantId, AiPermissions.CopilotUse);
        using var other = MemberClient(factory, OtherSubjectId);

        var created = await owner.PostAsJsonAsync(ThreadsUrl(), new { text = "¿Cuánto vendimos este mes?" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var thread = await JsonAsync(created);
        var threadId = thread.GetProperty("id").GetGuid();
        Assert.True(thread.GetProperty("pending").GetBoolean());
        Assert.Equal(
            ["User", "Assistant"],
            thread.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("role").GetString()));

        // §7 paso 1: la pregunta, la respuesta pendiente y el trabajo del copiloto quedan escritos.
        Assert.Equal(2L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM ai.copilot_messages WHERE thread_id = @id", ("id", threadId)));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM ai.model_jobs WHERE copilot_thread_id = @id AND status = 'Pending' AND session_id IS NULL AND test_id IS NULL",
            ("id", threadId)));

        var mine = await JsonAsync(await owner.GetAsync(ThreadsUrl(), Ct));
        var item = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal((threadId, 1L), (item.GetProperty("id").GetGuid(), item.GetProperty("version").GetInt64()));
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("nextCursor").ValueKind);

        // El otro miembro del mismo tenant: lista vacía y 404 (no 403) en cada ruta del hilo ajeno.
        Assert.Empty((await JsonAsync(await other.GetAsync(ThreadsUrl(), Ct))).GetProperty("items").EnumerateArray());
        foreach (var response in new[]
        {
            await other.GetAsync($"{ThreadsUrl()}/{threadId}", Ct),
            await other.PostAsJsonAsync($"{ThreadsUrl()}/{threadId}/messages", new { text = "¿Y esto?" }, Ct),
            await SendAsync(other, HttpMethod.Put, $"{ThreadsUrl()}/{threadId}", new { title = "Mío" }, ifMatch: 1),
            await other.DeleteAsync($"{ThreadsUrl()}/{threadId}", Ct),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("ai.copilot.thread_not_found", (await JsonAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM ai.copilot_threads WHERE id = @id", ("id", threadId)));
    }

    [Fact]
    public async Task AThreadOfAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = await EnabledAsync(database.GetConnectionString());
        using var owner = CreateClient(factory, AiApiHarness.TenantId, AiPermissions.CopilotUse);
        var threadId = (await JsonAsync(await owner.PostAsJsonAsync(ThreadsUrl(), new { text = "Hola" }, Ct))).GetProperty("id").GetGuid();

        // Quien llama está en otro tenant y pide la ruta del primero: la política de ruta lo frena con 403, sin tocar el hilo.
        using var foreign = CreateClient(factory, OtherTenantId, AiPermissions.CopilotUse);
        var read = await foreign.GetAsync($"{ThreadsUrl()}/{threadId}", Ct);
        var list = await foreign.GetAsync(ThreadsUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.DoesNotContain("Hola", await BodyAsync(read), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondQuestionWhilePendingIs409AndRenamingNeedsIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = await EnabledAsync(database.GetConnectionString());
        using var owner = CreateClient(factory, AiApiHarness.TenantId, AiPermissions.CopilotUse);
        var threadId = (await JsonAsync(await owner.PostAsJsonAsync(ThreadsUrl(), new { text = "Hola" }, Ct))).GetProperty("id").GetGuid();

        var busy = await owner.PostAsJsonAsync($"{ThreadsUrl()}/{threadId}/messages", new { text = "¿Y ahora?" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("ai.copilot.busy", (await JsonAsync(busy)).GetProperty("code").GetString());

        Assert.Equal(
            HttpStatusCode.PreconditionRequired,
            (await SendAsync(owner, HttpMethod.Put, $"{ThreadsUrl()}/{threadId}", new { title = "Ventas" })).StatusCode);
        var renamed = await SendAsync(owner, HttpMethod.Put, $"{ThreadsUrl()}/{threadId}", new { title = "Ventas" }, ifMatch: 1);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        // El cuerpo se lee una vez: una segunda lectura del mismo contenido encuentra el stream cerrado.
        var renamedBody = await JsonAsync(renamed);
        Assert.Equal(("Ventas", 2L), (renamedBody.GetProperty("title").GetString(), renamedBody.GetProperty("version").GetInt64()));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{ThreadsUrl()}/{threadId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{ThreadsUrl()}/{threadId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task TheHourlyLimitIs429WithRetryAfterInTheBodyAndTheHeader()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = await EnabledAsync(
            database.GetConnectionString(), new Dictionary<string, string> { ["Ai:Copilot:MessagesPerHour"] = "1", ["Ai:Copilot:MessagesPerDay"] = "5" });
        using var owner = CreateClient(factory, AiApiHarness.TenantId, AiPermissions.CopilotUse);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(ThreadsUrl(), new { text = "Una" }, Ct)).StatusCode);

        var limited = await owner.PostAsJsonAsync(ThreadsUrl(), new { text = "Otra" }, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var problem = await JsonAsync(limited);
        Assert.Equal("ai.copilot.rate_limited", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("retryAfterSeconds").GetInt32() > 0);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    /// <summary>Revisión de la rama, hallazgo 4: borrar el hilo no devuelve el cupo, y dos preguntas paralelas del mismo miembro hacen fila
    /// en el candado: con tope 1 por hora, de dos POST simultáneos entra uno.</summary>
    [Fact]
    public async Task DeletingAThreadDoesNotGiveBackTheQuotaAndParallelQuestionsQueue()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = await EnabledAsync(
            database.GetConnectionString(), new Dictionary<string, string> { ["Ai:Copilot:MessagesPerHour"] = "1", ["Ai:Copilot:MessagesPerDay"] = "5" });
        using var owner = CreateClient(factory, AiApiHarness.TenantId, AiPermissions.CopilotUse);
        var threadId = (await JsonAsync(await owner.PostAsJsonAsync(ThreadsUrl(), new { text = "Una" }, Ct))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{ThreadsUrl()}/{threadId}", Ct)).StatusCode);

        var afterDelete = await owner.PostAsJsonAsync(ThreadsUrl(), new { text = "Otra" }, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, afterDelete.StatusCode);

        // Otro miembro, cupo nuevo: de dos preguntas paralelas sólo una cabe.
        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Subject-Id", Guid.CreateVersion7().ToString());
        other.DefaultRequestHeaders.Add("X-Tenant-Id", AiApiHarness.TenantId);
        other.DefaultRequestHeaders.Add("X-Permissions", AiPermissions.CopilotUse);
        var both = await Task.WhenAll(
            other.PostAsJsonAsync(ThreadsUrl(), new { text = "A" }, Ct), other.PostAsJsonAsync(ThreadsUrl(), new { text = "B" }, Ct));
        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.TooManyRequests],
            both.Select(response => response.StatusCode).OrderBy(code => (int)code));
    }

    /// <summary>El tenant con el módulo, la cuenta de Anthropic y el copiloto prendido. Los workers apagados: nadie reclama el trabajo.</summary>
    private static async Task<QepApiFactory> EnabledAsync(string connectionString, IReadOnlyDictionary<string, string>? settings = null)
    {
        var all = new Dictionary<string, string> { ["Ai:Workers:Enabled"] = "false" };
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            all[key] = value;
        }

        var factory = new QepApiFactory(connectionString, settings: all);
        await SeedTenantAsync(factory);
        await SeedAnthropicConnectionAsync(factory);
        using var manager = CreateClient(factory, AiApiHarness.TenantId, AiPermissions.CopilotManage);
        var model = (await JsonAsync(await manager.GetAsync($"{AiUrl()}/copilot/settings", Ct)))
            .GetProperty("models").GetProperty("providers")[0].GetProperty("models")[0].GetProperty("id").GetString();
        var saved = await SendAsync(
            manager,
            HttpMethod.Put,
            $"{AiUrl()}/copilot/settings",
            new { enabled = true, provider = "anthropic", model, instructions = "", monthlyTokenBudget = (long?)null, retentionDays = 90 },
            ifMatch: 0);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return factory;
    }

    private static HttpClient MemberClient(QepApiFactory factory, string subjectId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AiApiHarness.TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", AiPermissions.CopilotUse);
        return client;
    }
}
