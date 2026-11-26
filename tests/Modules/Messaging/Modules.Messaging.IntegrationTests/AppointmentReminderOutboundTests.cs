using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Agenda: el recordatorio de cita por WhatsApp sale al hilo del número, con el cliente de la cita y sin asignar a nadie,
/// por el mismo núcleo que la plantilla de cobro (<see cref="BillingTemplateOutboundTests"/> es su gemela). Es el camino real de
/// <see cref="IAppointmentReminderOutbound"/>, resuelto de un scope como el del procesador del recordatorio. El <c>clientId</c> lo da
/// quien llama: repetirlo no manda un segundo recordatorio (Review Focus 7 del plan de la Agenda).</summary>
public sealed class AppointmentReminderOutboundTests
{
    private const string Phone = "573001234567";
    private const string TemplateName = "qep_agenda_recordatorio";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid MemberId, Guid CustomerId);

    private static ApprovedTemplate Approved() => new(
        TemplateName, "es", "UTILITY", "NAMED",
        TemplateComponentsBuilder.Components(
            AppointmentReminderTemplateDefinition.Draft(TemplateName, AppointmentReminderTemplateDefinition.BaseBody)).ToJsonString());

    private static Dictionary<string, string> ValuesOf(bool withLocation = true)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nombre_cliente"] = "Ana Pérez",
            ["servicio"] = "Consulta general",
            ["fecha_hora"] = "martes 24 de noviembre a las 15:00",
            ["recurso"] = "Dra. Paula Ruiz",
        };
        if (withLocation)
        {
            values["sede"] = "Sede Centro";
        }

        return values;
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        var customerId = await CreateCustomerAsync(factory, tenant, "ANA PÉREZ", "300 555 0101");
        return new Fixture(factory, connectionString, tenant, connectionId, memberId, customerId);
    }

    private static void ScriptMetaOk(FakeMetaGraphHandler meta) =>
        meta.Respond("/111/messages$", HttpStatusCode.OK,
            """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.n"}]}""");

    private static List<MetaRequest> MetaCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/111/", StringComparison.Ordinal) && !IsReadReceipt(request)).ToList();

    private static AppointmentReminderSend Send(Fixture f, Guid? clientId = null, Dictionary<string, string>? values = null) =>
        new(f.Tenant.TenantId, f.ConnectionId, f.MemberId, clientId ?? Guid.CreateVersion7(), Phone, f.CustomerId, "Ana Pérez", Approved(), values ?? ValuesOf());

    private static async Task<AppointmentReminderSendResult> SendAsync(Fixture f, AppointmentReminderSend send)
    {
        using var scope = f.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAppointmentReminderOutbound>().SendAsync(send, Ct);
    }

    [Fact]
    public async Task SendsTheNoticeIntoANewOpenUnassignedConversationWithoutCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var sent = Assert.IsType<AppointmentReminderSendResult.Sent>(await SendAsync(f, Send(f)));

        // Abre por teléfono, sin asignar a nadie, y la cuelga del cliente de la cita.
        Assert.Equal($"Open||{Phone}", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT status || '|' || coalesce(assigned_member_id::text, '') || '|' || wa_id
            FROM messaging.conversations WHERE id = @id
            """,
            ("id", sent.ConversationId)));
        Assert.Equal(f.CustomerId, await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT customer_id FROM messaging.conversations WHERE id = @id",
            ("id", sent.ConversationId)));
        // La fila de la plantilla (su details), liquidada como Sent (1) con el wamid de Meta, con el miembro dado como autor.
        var text = "¡Hola, Ana Pérez! Te recordamos tu cita de Consulta general el martes 24 de noviembre a las 15:00 con Dra. Paula Ruiz "
            + "en Sede Centro. Si no puedes asistir, avísanos con tiempo para reprogramarla.";
        Assert.Equal($"{sent.ConversationId}|{f.MemberId}|{TemplateName}|es|1|wamid.n|{text}", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT conversation_id || '|' || sent_by_member_id || '|' || (details->'template'->>'name') || '|' || (details->'template'->>'language')
                   || '|' || status || '|' || wamid || '|' || text
            FROM messaging.messages WHERE id = @id
            """,
            ("id", sent.MessageId)));

        // Una sola llamada a Meta: por teléfono, sólo el cuerpo, con las cinco variables por nombre.
        var call = Assert.Single(MetaCalls(f));
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal(Phone, body.RootElement.GetProperty("to").GetString());
        var template = body.RootElement.GetProperty("template");
        Assert.Equal(TemplateName, template.GetProperty("name").GetString());
        var component = Assert.Single(template.GetProperty("components").EnumerateArray());
        Assert.Equal("body", component.GetProperty("type").GetString());
        Assert.Equal(
            [
                "nombre_cliente|Ana Pérez", "servicio|Consulta general", "fecha_hora|martes 24 de noviembre a las 15:00",
                "recurso|Dra. Paula Ruiz", "sede|Sede Centro",
            ],
            component.GetProperty("parameters").EnumerateArray()
                .Select(item => $"{item.GetProperty("parameter_name").GetString()}|{item.GetProperty("text").GetString()}"));
    }

    /// <summary>Review Focus 7: la Agenda reintenta con el clientId derivado de la cita. Si el primer envío salió y marcar la cita falló, el
    /// segundo no llama a Meta: devuelve la misma fila.</summary>
    [Fact]
    public async Task TheSameClientIdTwiceSendsOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();

        var first = Assert.IsType<AppointmentReminderSendResult.Sent>(await SendAsync(f, Send(f, clientId)));
        var second = Assert.IsType<AppointmentReminderSendResult.Sent>(await SendAsync(f, Send(f, clientId)));

        Assert.Equal(first, second);
        Assert.Single(MetaCalls(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status::text FROM messaging.messages WHERE id = @id", ("id", first.MessageId)));
    }

    /// <summary>Una fila Failed con el reintento apagado es terminal (como <c>OutboundGuards.TakeInFlightAsync</c>): repetir el clientId
    /// no la reenvía ni llama a Meta, y devuelve <c>not_retriable</c>.</summary>
    [Fact]
    public async Task TheSameClientIdOnAFailedRowWithRetryDisabledDoesNotResend()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015));
        var clientId = Guid.CreateVersion7();
        Assert.IsType<AppointmentReminderSendResult.Failed>(await SendAsync(f, Send(f, clientId)));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.messages SET retry_disabled = true WHERE direction = 2");

        var second = await SendAsync(f, Send(f, clientId));

        Assert.Equal(new AppointmentReminderSendResult.Failed("messaging.message.not_retriable"), second);
        Assert.Single(MetaCalls(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("4", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status::text FROM messaging.messages WHERE direction = 2"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString,
            "SELECT retry_disabled FROM messaging.messages WHERE direction = 2"));
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

        Assert.Equal(new AppointmentReminderSendResult.ConnectionUnavailable("paused"), result);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Empty(MetaCalls(f));
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

        Assert.Equal(new AppointmentReminderSendResult.Failed("messaging.message.rejected"), result);
        Assert.Equal("4|132015", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AMissingValueIsRejectedBeforeAnyCall()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var result = await SendAsync(f, Send(f, values: ValuesOf(withLocation: false)));

        Assert.Equal(new AppointmentReminderSendResult.Failed("messaging.message.rejected"), result);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Empty(MetaCalls(f));
    }
}
