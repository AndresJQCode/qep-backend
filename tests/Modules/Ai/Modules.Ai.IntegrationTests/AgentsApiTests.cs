using System.Net;
using System.Text.Json;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-10-29 §4.6, §4.7 y §5, plan 2026-11-12 C-16..C-18: crear, leer, editar, guardar el borrador, publicar, pausar, reanudar y
/// borrar un agente. Publicar exige la conexión <c>Active</c> del proveedor del agente y ocupa sus conexiones en la tabla neutral de
/// Mensajería (§4.3), que comparte con los chatbots.
/// </summary>
public sealed class AgentsApiTests
{
    private static readonly string[] Manage = [AiPermissions.AgentRead, AiPermissions.AgentManage];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Agents => $"{AiUrl()}/agents";

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, HttpClient Client, Guid WhatsAppId);

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString)
    {
        await SeedTenantAsync(factory);
        var whatsApp = await SeedWhatsAppConnectionAsync(factory, "Ventas", "100200300401");
        return new Setup(factory, connectionString, CreateClient(factory, permissions: Manage), whatsApp);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string name = "Asesor virtual")
    {
        var response = await SendAsync(client, HttpMethod.Post, Agents, new { name, source = new { kind = "blank" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static object ValidConfig(string provider = "anthropic", string model = "claude-sonnet-5-5") => new
    {
        instructions = "Atiende las dudas de los clientes de la tienda.",
        tone = "friendly",
        greeting = "¡Hola! Soy el asistente.",
        faq = new[] { new { id = "envios", question = "¿Hacen envíos?", answer = "Sí, a todo el país." } },
        useQuickReplies = false,
        answerOnlyFromSources = true,
        tools = new { catalog = false, orderStatus = false, quotationDraft = false },
        handoff = new { destination = "unassigned", memberId = (Guid?)null, message = (string?)null, maxRepliesWithoutHuman = 20, maxConsecutiveFailures = 2 },
        trigger = "NewConversation",
        model = new { provider, id = model },
    };

    /// <summary>Un agente listo para publicar: borrador válido y la conexión de WhatsApp elegida. Devuelve su id y su versión.</summary>
    private static async Task<(Guid Id, long Version)> PublishableAsync(Setup setup, string name = "Asesor virtual", object? config = null)
    {
        var created = await CreateAsync(setup.Client, name);
        var id = created.GetProperty("id").GetGuid();
        var renamed = await SendAsync(
            setup.Client, HttpMethod.Put, $"{Agents}/{id}", new { name, connectionIds = new[] { setup.WhatsAppId } }, ifMatch: 1);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var draft = await SendAsync(
            setup.Client, HttpMethod.Put, $"{Agents}/{id}/draft", config ?? ValidConfig(), ifMatch: (await JsonAsync(renamed)).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        return (id, (await JsonAsync(draft)).GetProperty("version").GetInt64());
    }

    private static async Task<(short Kind, Guid BotId, string Name)?> SlotAsync(string connectionString, Guid connectionId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT bot_kind, bot_id, bot_name FROM messaging.connection_bots WHERE connection_id = @id", connection);
        command.Parameters.AddWithValue("id", connectionId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? (reader.GetInt16(0), reader.GetGuid(1), reader.GetString(2)) : null;
    }

    /// <summary>El <c>xmin</c> de la fila de ocupación: cambia con cada escritura, aunque el valor quede igual.</summary>
    private static Task<string?> SlotXminAsync(string connectionString, Guid connectionId) =>
        ScalarAsync<string>(connectionString, "SELECT xmin::text FROM messaging.connection_bots WHERE connection_id = @id", ("id", connectionId));

    private static async Task<(string? Code, JsonElement Root)> ProblemAsync(HttpResponseMessage response)
    {
        var root = await JsonAsync(response);
        return (root.TryGetProperty("code", out var code) ? code.GetString() : null, root);
    }

    [Fact]
    public async Task ABlankAgentIsBornAsADraftWithTheDefaultModelAndItsMissingPieces()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;

        var agent = await CreateAsync(setup.Client);

        Assert.Equal("Asesor virtual", agent.GetProperty("name").GetString());
        Assert.Equal("Draft", agent.GetProperty("status").GetString());
        Assert.Equal(1, agent.GetProperty("version").GetInt64());
        var model = agent.GetProperty("draft").GetProperty("config").GetProperty("model");
        Assert.Equal(("anthropic", "claude-opus-5-5"), (model.GetProperty("provider").GetString(), model.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("published").ValueKind);
        Assert.False(agent.GetProperty("modelRetired").GetBoolean());
        Assert.Equal(
            ["connections_missing", "instructions_missing"],
            agent.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("code").GetString()).Order());
        var connection = Assert.Single(agent.GetProperty("connections").EnumerateArray());
        Assert.Equal(setup.WhatsAppId, connection.GetProperty("id").GetGuid());
        Assert.True(connection.GetProperty("botSupported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("servedBy").ValueKind);
        Assert.Equal(["anthropic", "openai"], agent.GetProperty("models").GetProperty("providers").EnumerateArray()
            .Select(provider => provider.GetProperty("provider").GetString()));
    }

    // D-A11: único por tenant, sin importar mayúsculas.
    [Fact]
    public async Task ARepeatedNameIgnoringCaseIs409()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await CreateAsync(setup.Client, "Asesor virtual");

        var response = await SendAsync(setup.Client, HttpMethod.Post, Agents, new { name = "ASESOR VIRTUAL", source = new { kind = "blank" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ai.agent.name_taken", (await ProblemAsync(response)).Code);
    }

    // D-A7: guardar siempre guarda y responde los problemas; If-Match como los chatbots (428 sin él, 412 si es viejo).
    [Fact]
    public async Task SavingTheDraftAlwaysSavesAndHonoursIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        var id = (await CreateAsync(setup.Client)).GetProperty("id").GetGuid();

        var missing = await SendAsync(setup.Client, HttpMethod.Put, $"{Agents}/{id}/draft", ValidConfig());
        var saved = await SendAsync(setup.Client, HttpMethod.Put, $"{Agents}/{id}/draft", ValidConfig(), ifMatch: 1);
        var stale = await SendAsync(setup.Client, HttpMethod.Put, $"{Agents}/{id}/draft", ValidConfig(), ifMatch: 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await JsonAsync(saved);
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal(["connections_missing"], body.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var read = await JsonAsync(await setup.Client.GetAsync($"{Agents}/{id}", Ct));
        Assert.Equal("friendly", read.GetProperty("draft").GetProperty("config").GetProperty("tone").GetString());
    }

    // Los topes y los valores cerrados son 422 validation.failed con el campo, no problemas del borrador.
    [Fact]
    public async Task AnOutOfRangeDraftIsRejectedWithTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        var id = (await CreateAsync(setup.Client)).GetProperty("id").GetGuid();
        var config = JsonSerializer.SerializeToNode(ValidConfig(), JsonSerializerOptions.Web)!;
        config["tone"] = "sarcastic";
        config["handoff"]!["maxRepliesWithoutHuman"] = 51;

        var response = await SendAsync(setup.Client, HttpMethod.Put, $"{Agents}/{id}/draft", config, ifMatch: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, root) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(
            ["Config.Handoff.MaxRepliesWithoutHuman", "Config.Tone"],
            root.GetProperty("errors").EnumerateObject().Select(error => error.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PublishingWithErrorsIs422WithTheIssuesByPath()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var id = (await CreateAsync(setup.Client)).GetProperty("id").GetGuid();

        var response = await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, root) = await ProblemAsync(response);
        Assert.Equal("ai.agent.invalid", code);
        Assert.Equal(["connections", "instructions"], root.GetProperty("errors").EnumerateObject().Select(error => error.Name).Order(StringComparer.Ordinal));
    }

    // C-18: sin la conexión Active del proveedor del agente no se publica, y la respuesta dice cuál falta.
    [Fact]
    public async Task PublishingWithoutTheProviderConnectionIs422NamingTheProvider()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);

        var response = await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, root) = await ProblemAsync(response);
        Assert.Equal("ai.provider.not_configured", code);
        Assert.Equal("anthropic", root.GetProperty("provider").GetString());
        Assert.Null(await SlotAsync(setup.ConnectionString, setup.WhatsAppId));
    }

    // C-18: la conexión de un proveedor no habilita a un agente del otro.
    [Fact]
    public async Task TheOtherProvidersConnectionDoesNotEnableTheAgent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), settings: new Dictionary<string, string> { ["Ai:Providers:openai:Models:0:Available"] = "true" });
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup, config: ValidConfig("openai", "gpt-6-luna"));

        var response = await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, root) = await ProblemAsync(response);
        Assert.Equal("ai.provider.not_configured", code);
        Assert.Equal("openai", root.GetProperty("provider").GetString());
    }

    // C-16: un modelo retirado de la lista es aviso al guardar y error al publicar.
    [Fact]
    public async Task ARetiredModelIsMarkedAndBlocksPublishing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup, config: ValidConfig(model: "claude-opus-4-1"));

        var read = await JsonAsync(await setup.Client.GetAsync($"{Agents}/{id}", Ct));
        var response = await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version);

        Assert.True(read.GetProperty("modelRetired").GetBoolean());
        var warning = Assert.Single(read.GetProperty("issues").EnumerateArray());
        Assert.Equal(("model_unavailable", "Warning"), (warning.GetProperty("code").GetString(), warning.GetProperty("severity").GetString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("ai.agent.invalid", (await ProblemAsync(response)).Code);
    }

    // C-17 y §4.3: publicar guarda proveedor y modelo en la versión y ocupa la conexión como agente.
    [Fact]
    public async Task PublishingTakesTheConnectionAndRecordsProviderAndModel()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup);

        var response = await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var agent = await JsonAsync(response);
        Assert.Equal("Active", agent.GetProperty("status").GetString());
        var published = agent.GetProperty("published");
        Assert.Equal(
            (1, "anthropic", "claude-sonnet-5-5"),
            (published.GetProperty("number").GetInt32(), published.GetProperty("provider").GetString(), published.GetProperty("model").GetString()));
        Assert.Equal(((short)2, id, "Asesor virtual"), await SlotAsync(setup.ConnectionString, setup.WhatsAppId));
        Assert.Equal(
            ("anthropic", "claude-sonnet-5-5"),
            (await ScalarAsync<string>(setup.ConnectionString, "SELECT provider FROM ai.agent_versions WHERE agent_id = @id", ("id", id)),
             await ScalarAsync<string>(setup.ConnectionString, "SELECT model FROM ai.agent_versions WHERE agent_id = @id", ("id", id))));
        var connections = await JsonAsync(await setup.Client.GetAsync($"{AiUrl()}/connections", Ct));
        var servedBy = Assert.Single(connections.EnumerateArray()).GetProperty("servedBy");
        Assert.Equal(("agent", id, "Asesor virtual"), (servedBy.GetProperty("kind").GetString(), servedBy.GetProperty("id").GetGuid(), servedBy.GetProperty("name").GetString()));
        Assert.Equal(1, await ScalarAsync<long>(
            setup.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'ai.agent.published' AND resource_id = @id", ("id", id.ToString())));
    }

    // §4.3: una conexión que atiende un chatbot da 409 con el ocupante, y nada queda escrito.
    [Fact]
    public async Task PublishingOnAConnectionHeldByAChatbotIs409NamingIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup);
        var chatbotId = Guid.CreateVersion7();
        await ExecuteAsync(
            setup.ConnectionString,
            "INSERT INTO messaging.connection_bots (connection_id, tenant_id, bot_kind, bot_id, bot_name, reserved_at) VALUES (@c, @t, 1, @b, 'Servicio técnico', now())",
            ("c", setup.WhatsAppId), ("t", Guid.Parse(TenantId)), ("b", chatbotId));

        var response = await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var (code, root) = await ProblemAsync(response);
        Assert.Equal("ai.agent.connection_taken", code);
        var occupant = root.GetProperty("occupant");
        Assert.Equal(("chatbot", chatbotId, "Servicio técnico"), (occupant.GetProperty("kind").GetString(), occupant.GetProperty("id").GetGuid(), occupant.GetProperty("name").GetString()));
        var read = await JsonAsync(await setup.Client.GetAsync($"{Agents}/{id}", Ct));
        Assert.Equal("Draft", read.GetProperty("status").GetString());
        Assert.Equal(0, await ScalarAsync<long>(setup.ConnectionString, "SELECT count(*) FROM ai.agent_versions WHERE agent_id = @id", ("id", id)));
    }

    // D-A6 y D-A10: pausar conserva la conexión; renombrar sigue en la fila; quitar la conexión y borrar la liberan.
    [Fact]
    public async Task PausingKeepsTheSlotRenamingFollowsAndDeletingReleases()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup);
        var published = await JsonAsync(await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version));

        var paused = await JsonAsync(await SendAsync(
            setup.Client, HttpMethod.Post, $"{Agents}/{id}/pause", ifMatch: published.GetProperty("version").GetInt64()));
        Assert.Equal("Paused", paused.GetProperty("status").GetString());
        Assert.Equal(((short)2, id, "Asesor virtual"), await SlotAsync(setup.ConnectionString, setup.WhatsAppId));

        var renamed = await JsonAsync(await SendAsync(
            setup.Client, HttpMethod.Put, $"{Agents}/{id}", new { name = "Asistente", connectionIds = new[] { setup.WhatsAppId } },
            ifMatch: paused.GetProperty("version").GetInt64()));
        Assert.Equal(((short)2, id, "Asistente"), await SlotAsync(setup.ConnectionString, setup.WhatsAppId));

        var resumed = await JsonAsync(await SendAsync(
            setup.Client, HttpMethod.Post, $"{Agents}/{id}/resume", ifMatch: renamed.GetProperty("version").GetInt64()));
        Assert.Equal("Active", resumed.GetProperty("status").GetString());

        var deleted = await SendAsync(setup.Client, HttpMethod.Delete, $"{Agents}/{id}", ifMatch: resumed.GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await SlotAsync(setup.ConnectionString, setup.WhatsAppId));
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Client.GetAsync($"{Agents}/{id}", Ct)).StatusCode);
    }

    // D-A7 y §4.3: el editor guarda el borrador mientras se escribe. Ese guardado no mueve la ocupación, así que no reescribe la fila.
    [Fact]
    public async Task SavingTheDraftOfAPublishedAgentDoesNotRewriteItsSlot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup);
        var published = await JsonAsync(await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version));
        var before = await SlotXminAsync(setup.ConnectionString, setup.WhatsAppId);

        var saved = await SendAsync(
            setup.Client, HttpMethod.Put, $"{Agents}/{id}/draft", ValidConfig(), ifMatch: published.GetProperty("version").GetInt64());

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.NotNull(before);
        Assert.Equal(before, await SlotXminAsync(setup.ConnectionString, setup.WhatsAppId));
    }

    // Plan 2026-11-12 (correcciones), Tarea 4: la sincronización de la ocupación toma el candado del tenant antes de tocar una fila, el
    // mismo que toman los chatbots (ConnectionSlotsLock, 20261112). Con el candado tomado desde otra conexión, el renombre espera; al
    // soltarlo, termina. Sin el candado, dos sincronizaciones del tenant pueden cruzarse en un deadlock.
    [Fact]
    public async Task TheSlotSyncWaitsForTheTenantLock()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup);
        var published = await JsonAsync(await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version));

        await using var holder = new Npgsql.NpgsqlConnection(setup.ConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var command = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(20261112, hashtext(@t))", holder, transaction))
        {
            command.Parameters.AddWithValue("t", TenantId);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var rename = SendAsync(
            setup.Client, HttpMethod.Put, $"{Agents}/{id}", new { name = "Asistente", connectionIds = new[] { setup.WhatsAppId } },
            ifMatch: published.GetProperty("version").GetInt64());

        Assert.NotSame(rename, await Task.WhenAny(rename, Task.Delay(TimeSpan.FromMilliseconds(1500), Ct)));
        await transaction.RollbackAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await rename).StatusCode);
        Assert.Equal(((short)2, id, "Asistente"), await SlotAsync(setup.ConnectionString, setup.WhatsAppId));
    }

    // Plan 2026-11-12 (correcciones), Tarea 3: borrar en Integrations una conexión que atiende un agente es 409 con el ocupante, y la fila
    // de ocupación sigue ahí. Ejerce el adaptador real sobre messaging.connection_bots.
    [Fact]
    public async Task DeletingAConnectionAnAgentServesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version)).StatusCode);

        var response = await DeleteConnectionAsync(factory, setup.WhatsAppId);

        await AssertInUseByAgentAsync(response, id);
        Assert.Equal(((short)2, id, "Asesor virtual"), await SlotAsync(setup.ConnectionString, setup.WhatsAppId));
    }

    // D-A10: un agente pausado conserva la conexión, así que borrarla sigue bloqueado.
    [Fact]
    public async Task DeletingAConnectionAPausedAgentServesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await SeedAnthropicConnectionAsync(factory);
        var (id, version) = await PublishableAsync(setup);
        var published = await JsonAsync(await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/publish", ifMatch: version));
        var paused = await SendAsync(setup.Client, HttpMethod.Post, $"{Agents}/{id}/pause", ifMatch: published.GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);

        var response = await DeleteConnectionAsync(factory, setup.WhatsAppId);

        await AssertInUseByAgentAsync(response, id);
        Assert.Equal(((short)2, id, "Asesor virtual"), await SlotAsync(setup.ConnectionString, setup.WhatsAppId));
    }

    // Plan 2026-11-12, Task 6: the occupancy check and the delete hold the tenant's slot lock until the delete commits. Another
    // connection plays a publish's slot sync: it takes the lock and occupies the free connection. The delete waits for it and, once it
    // commits, sees the occupant. Before Task 6 the check read the empty table, the FOR SHARE locked nothing, and the delete went through.
    [Fact]
    public async Task DeletingAConnectionWaitsForASlotSyncInFlight()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        var agentId = Guid.CreateVersion7();

        await using var sync = new Npgsql.NpgsqlConnection(setup.ConnectionString);
        await sync.OpenAsync(Ct);
        await using var transaction = await sync.BeginTransactionAsync(Ct);
        await using (var command = new Npgsql.NpgsqlCommand(
            """
            SELECT pg_advisory_xact_lock(20261112, hashtext(@t));
            INSERT INTO messaging.connection_bots (connection_id, tenant_id, bot_kind, bot_id, bot_name, reserved_at)
            VALUES (@c, @tenant, 2, @b, 'Asesor virtual', now());
            """, sync, transaction))
        {
            command.Parameters.AddWithValue("t", TenantId);
            command.Parameters.AddWithValue("c", setup.WhatsAppId);
            command.Parameters.AddWithValue("tenant", Guid.Parse(TenantId));
            command.Parameters.AddWithValue("b", agentId);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var delete = DeleteConnectionAsync(factory, setup.WhatsAppId);

        Assert.NotSame(delete, await Task.WhenAny(delete, Task.Delay(TimeSpan.FromMilliseconds(1500), Ct)));
        await transaction.CommitAsync(Ct);
        await AssertInUseByAgentAsync(await delete, agentId);
    }

    /// <summary><c>DELETE</c> de Integrations con el <c>If-Match</c> vigente de la conexión, desde un cliente con los permisos de
    /// Integrations: el del setup sólo trae los de Ai.</summary>
    private static async Task<HttpResponseMessage> DeleteConnectionAsync(QepApiFactory factory, Guid connectionId)
    {
        var url = $"/api/v1/tenants/{TenantId}/integrations/connections/{connectionId}";
        using var client = CreateClient(
            factory, permissions: [Modules.Integrations.Application.IntegrationsPermissions.ConnectionRead, Modules.Integrations.Application.IntegrationsPermissions.ConnectionManage]);
        var read = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        return await SendAsync(client, HttpMethod.Delete, url, ifMatch: (await JsonAsync(read)).GetProperty("version").GetInt64());
    }

    private static async Task AssertInUseByAgentAsync(HttpResponseMessage response, Guid agentId)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var (code, root) = await ProblemAsync(response);
        Assert.Equal("integrations.connection.in_use_by_bot", code);
        var occupant = root.GetProperty("occupant");
        Assert.Equal(
            ("agent", agentId, "Asesor virtual"),
            (occupant.GetProperty("kind").GetString(), occupant.GetProperty("id").GetGuid(), occupant.GetProperty("name").GetString()));
    }

    // §5: una conexión de otro tenant es 404 sin decir de quién es; una sin salida de bot (Instagram, por ahora) es 422.
    [Fact]
    public async Task OnlyTheTenantsWhatsAppConnectionsCanBeChosen()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        var instagram = await SeedInstagramConnectionAsync(factory, "Instagram", "17841400000000001");
        var id = (await CreateAsync(setup.Client)).GetProperty("id").GetGuid();

        var foreign = await SendAsync(
            setup.Client, HttpMethod.Put, $"{Agents}/{id}", new { name = "Asesor virtual", connectionIds = new[] { Guid.CreateVersion7() } }, ifMatch: 1);
        var unsupported = await SendAsync(
            setup.Client, HttpMethod.Put, $"{Agents}/{id}", new { name = "Asesor virtual", connectionIds = new[] { instagram } }, ifMatch: 1);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("ai.connection.not_found", (await ProblemAsync(foreign)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        Assert.Equal("ai.agent.channel_not_supported", (await ProblemAsync(unsupported)).Code);
        var connections = await JsonAsync(await setup.Client.GetAsync($"{AiUrl()}/connections", Ct));
        Assert.False(connections.EnumerateArray().Single(connection => connection.GetProperty("id").GetGuid() == instagram)
            .GetProperty("botSupported").GetBoolean());
    }

    [Fact]
    public async Task TheListSearchesByNameAndADuplicateCopiesTheDraft()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        var (id, _) = await PublishableAsync(setup, "Ventas");
        await CreateAsync(setup.Client, "Soporte");

        var copy = await SendAsync(setup.Client, HttpMethod.Post, Agents, new { name = "Ventas 2", source = new { kind = "duplicate", agentId = id } });
        var list = await JsonAsync(await setup.Client.GetAsync($"{Agents}?search=vent", Ct));

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var duplicated = await JsonAsync(copy);
        Assert.Equal("friendly", duplicated.GetProperty("draft").GetProperty("config").GetProperty("tone").GetString());
        Assert.Empty(duplicated.GetProperty("connectionIds").EnumerateArray());
        Assert.Equal(["Ventas", "Ventas 2"], list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()).Order());
        var summary = list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id);
        Assert.Equal(("anthropic", "claude-sonnet-5-5"), (summary.GetProperty("model").GetProperty("provider").GetString(), summary.GetProperty("model").GetProperty("id").GetString()));
        Assert.Equal([setup.WhatsAppId], summary.GetProperty("connections").EnumerateArray().Select(connection => connection.GetProperty("id").GetGuid()));
    }

    // §5: los permisos son de núcleo; sin manage no se escribe, y con la capacidad apagada es 403 con su código.
    [Fact]
    public async Task WritingNeedsManageAndTheModule()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        using var reader = CreateClient(factory, permissions: AiPermissions.AgentRead);

        var denied = await SendAsync(reader, HttpMethod.Post, Agents, new { name = "X", source = new { kind = "blank" } });
        await ExecuteAsync(
            setup.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'ai'",
            ("t", Guid.Parse(TenantId)));
        var disabled = await setup.Client.GetAsync(Agents, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
    }
}
