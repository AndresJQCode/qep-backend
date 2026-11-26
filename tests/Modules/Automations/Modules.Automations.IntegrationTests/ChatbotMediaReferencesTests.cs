using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Api;
using Modules.Automations.Application;
using Modules.Storage.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-11-18 («Lifecycle»), plan Task 11: el adaptador de Bootstrapper que le dice a Storage qué archivos de chatbot siguen en uso.
/// Contra Postgres de verdad porque es SQL sobre el JSON del flujo: borrador, versiones y plantillas guardadas del tenant. Lo que nombra un
/// mensaje de la bandeja lo prueba <c>EngineEndToEndTests.AFileShownInAConversationIsNotReleased</c>, que lo deja mandar a un bot.
/// </summary>
public sealed class ChatbotMediaReferencesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PlainFlow = """
        { "schema": 1, "start": { "firstNodeId": "h1" }, "variables": [],
          "nodes": [ { "id": "h1", "kind": "handoff", "title": "Asesor", "text": null } ] }
        """;

    [Fact]
    public async Task AFileInADraftIsReferenced()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await TenantAsync(factory, connectionString);
        var fileId = Guid.CreateVersion7();
        var chatbotId = await BlankChatbotAsync(factory, tenant);
        await SetDraftAsync(connectionString, chatbotId, MediaFlow(fileId));

        Assert.Equal([fileId], await FindAsync(factory, tenant.TenantId, fileId));
    }

    // Review Focus 1: una pestaña vieja guardó el borrador sin el archivo, pero la versión publicada lo sigue mandando.
    [Fact]
    public async Task AFileOnlyInAPublishedVersionIsReferenced()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await TenantAsync(factory, connectionString);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Servicio", Digits(), Digits());
        var fileId = await UploadedFileAsync(factory, tenant.TenantId);
        using var admin = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var chatbotId = await PublishChatbotAsync(admin, tenant.TenantId, MediaFlow(fileId), connectionId);
        await SetDraftAsync(connectionString, chatbotId, PlainFlow);
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM automations.chatbots WHERE id = @id AND draft_flow::text LIKE '%' || @file || '%'",
            ("id", chatbotId), ("file", fileId.ToString())));

        Assert.Equal([fileId], await FindAsync(factory, tenant.TenantId, fileId));
    }

    // Una plantilla guardada del tenant es algo que se conserva: si el barrido soltara su archivo, crear desde ella daría un mensaje roto.
    [Fact]
    public async Task AFileInASavedTemplateIsReferenced()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await TenantAsync(factory, connectionString);
        var fileId = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO automations.chatbot_templates (id, tenant_id, name, category, flow, layout, created_at, created_by)
            VALUES (@id, @tenantId, 'Catálogo', 'general', @flow::jsonb, '{}'::jsonb, now(), @by)
            """,
            ("id", Guid.CreateVersion7()), ("tenantId", tenant.TenantId), ("flow", MediaFlow(fileId)), ("by", tenant.OwnerUserId));

        Assert.Equal([fileId], await FindAsync(factory, tenant.TenantId, fileId));
    }

    // Review Focus 2: un flujo pegado de otro tenant no protege nada, y el id que usa otro tenant no cuenta.
    [Fact]
    public async Task AnotherTenantsReferenceDoesNotCount()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantA = await TenantAsync(factory, connectionString);
        var tenantB = await TenantAsync(factory, connectionString);
        var fileId = Guid.CreateVersion7();
        await SetDraftAsync(connectionString, await BlankChatbotAsync(factory, tenantB), MediaFlow(fileId));

        Assert.Empty(await FindAsync(factory, tenantA.TenantId, fileId));
        Assert.Equal([fileId], await FindAsync(factory, tenantB.TenantId, fileId));
    }

    // Sólo vuelve lo que se nombra; un fileId mal formado en un flujo viejo, un bloque de otro tipo con un fileId o un flujo sin nodes no
    // hacen fallar la consulta.
    [Fact]
    public async Task AnUnreferencedFileIsNotReturned()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await TenantAsync(factory, connectionString);
        var used = Guid.CreateVersion7();
        var unused = Guid.CreateVersion7();
        await SetDraftAsync(connectionString, await BlankChatbotAsync(factory, tenant), $$"""
            { "schema": 1, "start": { "firstNodeId": "m1" }, "variables": [],
              "nodes": [
                { "id": "m1", "kind": "message", "title": "A", "text": "", "urlButton": null,
                  "media": { "kind": "image", "fileId": "{{used}}" }, "outputs": { "next": "m2" } },
                { "id": "m2", "kind": "message", "title": "B", "text": "", "urlButton": null,
                  "media": { "kind": "image", "fileId": "not-a-guid" }, "outputs": { "next": "h1" } },
                { "id": "h1", "kind": "handoff", "title": "Asesor", "text": null, "media": { "fileId": "{{unused}}" } }
              ] }
            """);
        await SetDraftAsync(connectionString, await BlankChatbotAsync(factory, tenant, "Vacío"), """{ "schema": 1, "nodes": {} }""");

        Assert.Equal([used], await FindAsync(factory, tenant.TenantId, used, unused));
        Assert.Empty(await FindAsync(factory, tenant.TenantId));
    }

    /// <summary>Un flujo publicable con un Mensaje con imagen; el <c>fileId</c> en minúscula, como lo escribe <c>FlowJson</c>.</summary>
    internal static string MediaFlow(Guid fileId) => $$"""
        { "schema": 1, "start": { "firstNodeId": "m1" }, "variables": [],
          "nodes": [
            { "id": "m1", "kind": "message", "title": "Catálogo", "text": "Mira", "urlButton": null,
              "media": { "kind": "image", "fileId": "{{fileId}}" }, "outputs": { "next": "h1" } },
            { "id": "h1", "kind": "handoff", "title": "Asesor", "text": null }
          ] }
        """;

    private static async Task<IReadOnlySet<Guid>> FindAsync(QepApiFactory factory, Guid tenantId, params Guid[] fileIds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IChatbotMediaReferences>().FindReferencedAsync(tenantId, fileIds, Ct);
    }

    private static async Task<RegisteredTenant> TenantAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        return tenant;
    }

    private static async Task<Guid> BlankChatbotAsync(QepApiFactory factory, RegisteredTenant tenant, string name = "Servicio")
    {
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await client.PostAsJsonAsync(
            $"{AutomationsUrl(tenant.TenantId)}/chatbots",
            new CreateChatbotRequest(name, "Atiende la línea.", "NewConversation", [], [], new ChatbotSourceRequest("blank", null, null, null)),
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!.Chatbot.Id;
    }

    /// <summary>El borrador escrito directo en la base: el adaptador lee el JSON guardado, venga de donde venga.</summary>
    private static async Task SetDraftAsync(string connectionString, Guid chatbotId, string flowJson) =>
        Assert.Equal(1, await ExecuteAsync(connectionString,
            "UPDATE automations.chatbots SET draft_flow = @flow::jsonb WHERE id = @id", ("flow", flowJson), ("id", chatbotId)));

    private static async Task<Guid> UploadedFileAsync(QepApiFactory factory, Guid tenantId)
    {
        var session = await factory.Media.OpenUploadAsync(tenantId, "catalogo.jpg", "image/jpeg", 2048, 25_000_000, Ct);
        await factory.Media.CompleteAsync(tenantId, session.FileId, Ct);
        return session.FileId;
    }

    private static string Digits() =>
        Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
