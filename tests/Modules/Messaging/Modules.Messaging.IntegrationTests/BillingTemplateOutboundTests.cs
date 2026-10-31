using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Cobros recurrentes §5: la plantilla de cobro sale al hilo del número en la conexión, sin las guardas de la bandeja y sin
/// documento ni R2. Es el camino real de producción de <see cref="IBillingTemplateOutbound"/>, resuelto de un scope como el del
/// procesador de avisos; el gemelo de <see cref="QuotationTemplateOutboundTests"/> sin el PDF.</summary>
public sealed class BillingTemplateOutboundTests
{
    private const string Phone = "573001234567";
    private const string TemplateName = "qep_cobro_recordatorio";

    private static readonly string[] Values = ["Ana Pérez", "Hosting plan pro", "$ 150.000", "10/03/2026"];

    private static Dictionary<string, string> ValuesOf(IReadOnlyList<string> values) => new(StringComparer.Ordinal)
    {
        ["nombre_contacto"] = values[0],
        ["servicio"] = values[1],
        ["valor"] = values[2],
        ["vencimiento"] = values[3],
    };

    /// <summary>La fila POSITIONAL que dejó el aprovisionamiento viejo: <c>{{1}}</c>…<c>{{4}}</c>, sin <c>asesor</c>, <c>empresa</c> ni <c>saludo</c>.</summary>
    private static ApprovedTemplate LegacyPositional() => new(
        TemplateName, "es", "UTILITY", "POSITIONAL", BillingTemplateDefinitions.Reminder.Components().ToJsonString());

    private static ApprovedTemplate Named(string body) => new(
        TemplateName, "es", "UTILITY", "NAMED",
        TemplateComponentsBuilder.Components(
            new TemplateDraft(TemplateName, "es", "UTILITY", null, body, null, [], new Dictionary<string, string>()), "4::handle").ToJsonString());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid MemberId);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Fixture(factory, factory, connectionString, tenant, connectionId, memberId);
    }

    private static void ScriptMetaOk(FakeMetaGraphHandler meta) =>
        meta.Respond("/111/messages$", HttpStatusCode.OK,
            """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.b"}]}""");

    private static List<MetaRequest> MetaCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/111/", StringComparison.Ordinal) && !IsReadReceipt(request)).ToList();

    private static BillingTemplateSend Send(
        Fixture f, string waId = Phone, Guid? customerId = null, IReadOnlyList<string>? values = null, ApprovedTemplate? template = null) =>
        new(f.Tenant.TenantId, f.ConnectionId, f.MemberId, waId, customerId, "Ana Pérez", MessageTemplateKind.BillingReminder,
            template ?? LegacyPositional(), ValuesOf(values ?? Values));

    private static async Task<BillingTemplateSendResult> SendAsync(Fixture f, BillingTemplateSend send)
    {
        using var scope = f.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBillingTemplateOutbound>().SendAsync(send, Ct);
    }

    [Fact]
    public async Task SendsThePositionalTemplateIntoANewOpenUnassignedConversationWithTheAssigneeAsAuthor()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var customerId = Guid.CreateVersion7();

        var sent = Assert.IsType<BillingTemplateSendResult.Sent>(await SendAsync(f, Send(f, customerId: customerId)));

        // Abre por teléfono, sin asignar a nadie, con el cliente.
        Assert.Equal($"Open||{Phone}||{customerId}", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT status || '|' || coalesce(assigned_member_id::text, '') || '|' || wa_id || '|' || coalesce(user_id, '') || '|' || customer_id
            FROM messaging.conversations WHERE id = @id
            """,
            ("id", sent.ConversationId)));
        // Reclamo + DetailsJson + liquidación (status 1 = Sent) + el encargado como autor.
        var text = "Hola Ana Pérez, te recordamos que tu servicio Hosting plan pro por $ 150.000 vence el 10/03/2026. "
            + "Si ya realizaste el pago, ignora este mensaje.";
        Assert.Equal($"{sent.ConversationId}|{f.MemberId}|{TemplateName}|es|1|{text}", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT conversation_id || '|' || sent_by_member_id || '|' || (details->'template'->>'name') || '|' || (details->'template'->>'language')
                   || '|' || status || '|' || text
            FROM messaging.messages WHERE id = @id
            """,
            ("id", sent.MessageId)));
        Assert.Equal("wamid.b", await ScalarAsync<string>(f.ConnectionString, "SELECT wamid FROM messaging.messages WHERE id = @id", ("id", sent.MessageId)));

        // Una sola llamada a Meta: por teléfono, la plantilla con parámetros posicionales y sin encabezado de documento.
        var call = Assert.Single(MetaCalls(f));
        Assert.Equal("/v24.0/111/messages", call.Uri!.AbsolutePath);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal(Phone, body.RootElement.GetProperty("to").GetString());
        var template = body.RootElement.GetProperty("template");
        Assert.Equal(TemplateName, template.GetProperty("name").GetString());
        var components = template.GetProperty("components").EnumerateArray().ToArray();
        var component = Assert.Single(components);
        Assert.Equal("body", component.GetProperty("type").GetString());
        Assert.Equal(Values, component.GetProperty("parameters").EnumerateArray().Select(item => item.GetProperty("text").GetString()));
    }

    /// <summary>La ventana de 24 h cerrada no impide la plantilla: es justo para lo que existe.</summary>
    [Fact]
    public async Task AClosedTwentyFourHourWindowStillSendsTheTemplateIntoTheExistingConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using (var anonymous = f.Host.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", Phone, "w1", DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds(), "hola"));
            await DrainDeliveriesAsync(f.Host);
        }

        var existing = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        ScriptMetaOk(f.Factory.MetaHandler);

        var sent = Assert.IsType<BillingTemplateSendResult.Sent>(await SendAsync(f, Send(f)));

        Assert.Equal(existing, sent.ConversationId);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @id AND direction = 2 AND status = 1", ("id", existing)));
    }

    [Fact]
    public async Task AConnectionThatIsNotActiveReturnsConnectionUnavailableAndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        ScriptMetaOk(f.Factory.MetaHandler);

        var result = await SendAsync(f, Send(f));

        Assert.Equal(new BillingTemplateSendResult.ConnectionUnavailable("paused"), result);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Empty(MetaCalls(f));
    }

    /// <summary>Un 190 al enviar dice que la credencial ya no sirve: la conexión queda NeedsAttention y no queda fila de mensaje.</summary>
    [Fact]
    public async Task ARejectedTokenOnSendFlagsTheConnectionAndReturnsConnectionUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.Unauthorized, FakeMetaGraphHandler.GraphError(190));

        var result = await SendAsync(f, Send(f));

        Assert.Equal(new BillingTemplateSendResult.ConnectionUnavailable("credentials_rejected"), result);
        Assert.Equal("NeedsAttention", await ScalarAsync<string>(f.ConnectionString, "SELECT status FROM integrations.connections"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
    }

    /// <summary>Meta rechaza la plantilla: el mensaje queda Failed (status 4) con su código, para que el hilo muestre qué no salió.</summary>
    [Fact]
    public async Task MetaRejectingTheTemplateLeavesAFailedMessageAndReturnsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015));

        var result = await SendAsync(f, Send(f));

        Assert.Equal(new BillingTemplateSendResult.Failed("messaging.message.rejected"), result);
        Assert.Equal("4|132015", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AnInvalidValueIsRejectedBeforeAnyCall()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var result = await SendAsync(f, Send(f, values: ["Ana\nPérez", "Hosting plan pro", "$ 150.000", "10/03/2026"]));

        Assert.Equal(new BillingTemplateSendResult.Failed("messaging.message.rejected"), result);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Empty(MetaCalls(f));
    }

    /// <summary>Lo que más importa de la tarea: una fila vieja no lleva <c>asesor</c>, <c>empresa</c> ni <c>saludo</c> y sigue saliendo igual.</summary>
    [Fact]
    public async Task ALegacyPositionalReminderSendsItsFourValuesInOrder()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var sent = Assert.IsType<BillingTemplateSendResult.Sent>(await SendAsync(f, Send(f)));

        var call = Assert.Single(MetaCalls(f));
        using var body = JsonDocument.Parse(call.Body!);
        var parameters = Assert.Single(body.RootElement.GetProperty("template").GetProperty("components").EnumerateArray())
            .GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(Values, parameters.Select(item => item.GetProperty("text").GetString()));
        Assert.All(parameters, item => Assert.False(item.TryGetProperty("parameter_name", out JsonElement ignored)));
        var text = await ScalarAsync<string>(f.ConnectionString, "SELECT text FROM messaging.messages WHERE id = @id", ("id", sent.MessageId));
        Assert.Equal(
            "Hola Ana Pérez, te recordamos que tu servicio Hosting plan pro por $ 150.000 vence el 10/03/2026. Si ya realizaste el pago, ignora este mensaje.",
            text);
    }

    [Fact]
    public async Task ANamedReminderUsesTheEncargadoAsAdvisorAndSendsOnlyItsVariables()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var template = Named("Hola {{nombre_contacto}}, {{asesor}} te recuerda {{servicio}} hoy.");
        // Nombres distintos para el miembro y el tenant: con el mismo, un memberId equivocado se taparía con el respaldo (la empresa).
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.memberships SET display_name = 'Ana Asesora' WHERE id = @id", ("id", f.MemberId));
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenants SET display_name = 'Andina Ltda' WHERE id = @id", ("id", f.Tenant.TenantId));

        var sent = Assert.IsType<BillingTemplateSendResult.Sent>(await SendAsync(f, Send(f, template: template)));

        var call = Assert.Single(MetaCalls(f));
        using var body = JsonDocument.Parse(call.Body!);
        var parameters = Assert.Single(body.RootElement.GetProperty("template").GetProperty("components").EnumerateArray())
            .GetProperty("parameters").EnumerateArray().ToArray();
        // asesor es el nombre visible del encargado (SentByMemberId), no el del tenant.
        Assert.Equal(
            [("nombre_contacto", "Ana Pérez"), ("asesor", "Ana Asesora"), ("servicio", "Hosting plan pro")],
            parameters.Select(item => (item.GetProperty("parameter_name").GetString()!, item.GetProperty("text").GetString()!)));
        Assert.Equal(
            "Hola Ana Pérez, Ana Asesora te recuerda Hosting plan pro hoy.",
            await ScalarAsync<string>(f.ConnectionString, "SELECT text FROM messaging.messages WHERE id = @id", ("id", sent.MessageId)));
    }
}
