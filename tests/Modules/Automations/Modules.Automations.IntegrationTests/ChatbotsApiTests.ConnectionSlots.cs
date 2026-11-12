using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-10-29 §4.3 y D-A2 (agentes IA): quién atiende cada conexión vive también en <c>messaging.connection_bots</c>, la tabla
/// neutral que comparten chatbots y agentes. Automations la mantiene al día en la misma transacción en que guarda el chatbot, así que
/// nunca queda una a medias; y si un agente ya ocupa la conexión, publicar responde 409 nombrándolo, sin dejar nada escrito.
/// </summary>
public sealed partial class ChatbotsApiTests
{
    private sealed record Slot(short Kind, Guid BotId, string BotName);

    private static async Task<Slot?> SlotAsync(string connectionString, Guid connectionId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT bot_kind, bot_id, bot_name FROM messaging.connection_bots WHERE connection_id = @id", connection);
        command.Parameters.AddWithValue("id", connectionId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? new Slot(reader.GetInt16(0), reader.GetGuid(1), reader.GetString(2)) : null;
    }

    [Fact]
    public async Task ADraftDoesNotTakeTheSlotAndPublishingDoes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);

        Assert.Null(await SlotAsync(connectionString, setup.WhatsAppId));

        await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));

        Assert.Equal(new Slot(1, id, "Servicio técnico"), await SlotAsync(connectionString, setup.WhatsAppId));
    }

    // D-C13 y D-A10: pausar conserva la conexión; borrar la libera.
    [Fact]
    public async Task PausingKeepsTheSlotAndDeletingReleasesIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));

        var paused = await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "pause", published.Chatbot.Version));

        Assert.Equal(new Slot(1, id, "Servicio técnico"), await SlotAsync(connectionString, setup.WhatsAppId));

        var deleted = await DeleteAsync(setup, id, paused.Chatbot.Version);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await SlotAsync(connectionString, setup.WhatsAppId));
    }

    [Fact]
    public async Task MovingAndRenamingAPublishedChatbotFollowsInTheSlot()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var other = await SeedWhatsAppConnectionAsync(factory, setup.Tenant.TenantId, "Ventas", Digits(), Digits());
        var (id, version) = await PublishableAsync(setup);
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));

        await ReadEditorAsync(await SetConnectionsAsync(setup, id, "Soporte", published.Chatbot.Version, [other], []));

        Assert.Null(await SlotAsync(connectionString, setup.WhatsAppId));
        Assert.Equal(new Slot(1, id, "Soporte"), await SlotAsync(connectionString, other));
    }

    // Spec 2026-10-23 D-X1: tomar la conexión de otro chatbot mueve también la fila.
    [Fact]
    public async Task TakingOverAConnectionMovesTheSlot()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (first, firstVersion) = await PublishableAsync(setup);
        var (second, secondVersion) = await PublishableAsync(setup, "Ventas");
        await ReadEditorAsync(await ActAsync(setup.Client, setup, first, "publish", firstVersion));

        await ReadEditorAsync(await PublishAsync(setup.Client, setup, second, secondVersion, [setup.WhatsAppId]));

        Assert.Equal(new Slot(1, second, "Ventas"), await SlotAsync(connectionString, setup.WhatsAppId));
    }

    // §4.3: un agente ya atiende la conexión. Publicar no deja nada a medias: ni el chatbot activo, ni su fila, ni la del agente tocada.
    [Fact]
    public async Task PublishingOnAConnectionHeldByAnAgentIs409NamingTheAgent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var agentId = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.connection_bots (connection_id, tenant_id, bot_kind, bot_id, bot_name, reserved_at)
            VALUES (@connection, @tenant, 2, @agent, 'Asistente de ventas', now())
            """,
            ("connection", setup.WhatsAppId), ("tenant", setup.Tenant.TenantId), ("agent", agentId));
        var (id, version) = await PublishableAsync(setup);

        var response = await ActAsync(setup.Client, setup, id, "publish", version);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("automations.chatbot.connection_taken", problem.RootElement.GetProperty("code").GetString());
        Assert.Contains("Asistente de ventas", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var occupant = problem.RootElement.GetProperty("occupant");
        Assert.Equal(
            ("agent", agentId, "Asistente de ventas"),
            (occupant.GetProperty("kind").GetString(), occupant.GetProperty("id").GetGuid(), occupant.GetProperty("name").GetString()));
        Assert.Equal(("Draft", version), ((await GetAsync(setup, id)).Chatbot.Status, (await GetAsync(setup, id)).Chatbot.Version));
        Assert.Equal(0L, await ActiveRowsAsync(connectionString, setup.WhatsAppId));
        Assert.Equal(new Slot(2, agentId, "Asistente de ventas"), await SlotAsync(connectionString, setup.WhatsAppId));
    }

    // El 409 entre chatbots también nombra al ocupante con su tipo, para que la pantalla ofrezca ir a pausarlo.
    [Fact]
    public async Task PublishingOnAConnectionHeldByAChatbotCarriesTheOccupant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (first, firstVersion) = await PublishableAsync(setup);
        var (second, secondVersion) = await PublishableAsync(setup, "Ventas");
        await ReadEditorAsync(await ActAsync(setup.Client, setup, first, "publish", firstVersion));

        var response = await ActAsync(setup.Client, setup, second, "publish", secondVersion);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var occupant = problem.RootElement.GetProperty("occupant");
        Assert.Equal(
            ("chatbot", first, "Servicio técnico"),
            (occupant.GetProperty("kind").GetString(), occupant.GetProperty("id").GetGuid(), occupant.GetProperty("name").GetString()));
    }

    // Migración AddConnectionBots: un chatbot publicado antes de que exista la tabla queda con su fila. Se baja Mensajería a la migración
    // anterior (borra la tabla) y se vuelve a subir: el respaldo la llena desde automations.chatbot_connections.
    [Fact]
    public async Task TheMigrationBackfillsTheChatbotsAlreadyPublished()
    {
        const string LastMigrationBeforeSlots = "20261111061438_MakeCampaignRecipientCustomerOptional";
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));
        await using var scope = factory.Services.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<MessagingDbContext>().GetService<IMigrator>();

        await migrator.MigrateAsync(LastMigrationBeforeSlots, Ct);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM pg_tables WHERE schemaname = 'messaging' AND tablename = 'connection_bots'"));
        await migrator.MigrateAsync(null, Ct);

        Assert.Equal(new Slot(1, id, "Servicio técnico"), await SlotAsync(connectionString, setup.WhatsAppId));
    }
}
