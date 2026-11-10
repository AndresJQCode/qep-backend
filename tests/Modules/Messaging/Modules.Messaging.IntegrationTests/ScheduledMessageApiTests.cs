using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.ScheduledMessages;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-09 «Contrato HTTP» y pruebas 8, 9 y 10: las diez rutas del programado contra el host real. Una prueba pasa por todas
/// (la que delata un handler sin registrar: sin registro la ruta responde 500, no un error de compilación). Los errores salen por el mapeo
/// central de <c>ApiExceptionHandler</c>, con el código de la spec. El CSRF por <c>X-Qep-Client</c> lo cubre el harness de auth real de
/// Tenancy, igual que las demás rutas de Messaging.</summary>
public sealed class ScheduledMessageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(string? Code, string[] ErrorKeys)> ProblemOf(HttpResponseMessage response) => await ProblemAsync(response);

    [Fact]
    public async Task CreatingATextReturns201WithTheContractShape()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var clientId = Guid.CreateVersion7();
        var at = ScheduledFixture.InTwoHours();

        var response = await f.PostTextAsync(clientId, at, "  Te escribo como quedamos  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        string[] contract =
        [
            "id", "conversationId", "clientId", "status", "scheduledAt", "kind", "isVoiceNote", "text", "caption", "media", "author", "createdBy",
            "updatedBy", "updatedAt", "failureCode", "canRetry", "sentMessageId", "version",
        ];
        Assert.Equal(
            contract.Order(StringComparer.Ordinal).ToArray(),
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(("Scheduled", "Text", false, "Te escribo como quedamos", clientId, f.ConversationId, 1L),
            (body.GetProperty("status").GetString(), body.GetProperty("kind").GetString(), body.GetProperty("isVoiceNote").GetBoolean(),
             body.GetProperty("text").GetString(), body.GetProperty("clientId").GetGuid(), body.GetProperty("conversationId").GetGuid(),
             body.GetProperty("version").GetInt64()));
        Assert.Equal(at.ToUnixTimeSeconds(), body.GetProperty("scheduledAt").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal(f.MemberId, body.GetProperty("author").GetProperty("memberId").GetGuid());
        Assert.Equal(f.MemberId, body.GetProperty("createdBy").GetProperty("memberId").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("updatedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("media").ValueKind);
        Assert.False(body.GetProperty("canRetry").GetBoolean());
    }

    [Fact]
    public async Task RepeatingTheClientIdReturns200()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var clientId = Guid.CreateVersion7();
        var first = await f.CreateTextAsync(clientId);

        var again = await f.PostTextAsync(clientId, ScheduledFixture.InTwoHours().AddMinutes(30), "otro texto");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(first.GetProperty("id").GetGuid(), body.GetProperty("id").GetGuid());
        Assert.Equal("Te escribo como quedamos", body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ASecondActiveScheduleIs409AlreadyExists()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        await f.CreateTextAsync();

        var second = await f.PostTextAsync(Guid.CreateVersion7(), ScheduledFixture.InTwoHours());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("messaging.scheduled.already_exists", (await ProblemOf(second)).Code);
    }

    [Fact]
    public async Task AnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var scheduled = await f.CreateTextAsync();
        var id = scheduled.GetProperty("id").GetGuid();
        var other = await RegisterTenantAsync(f.Host);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        // Quien administra otro tenant llama con SU tenant en el header y el del primero en la ruta: 403, nunca 404.
        using var intruder = CreateClient(f.Host, other.OwnerUserId, other.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await intruder.GetAsync(f.ScheduledUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(intruder, HttpMethod.Post, f.SchedulesUrl,
            new { clientId = Guid.CreateVersion7(), scheduledAt = ScheduledFixture.InTwoHours(), text = "hola" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(intruder, HttpMethod.Post, f.ScheduleUrl(id) + "/cancel", new { expectedVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await intruder.GetAsync(f.ScheduleUrl(id) + "/media", Ct)).StatusCode);
    }

    [Fact]
    public async Task OnlyReadingIsNotEnoughToScheduleOrManage()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var scheduled = await f.CreateTextAsync();
        var id = scheduled.GetProperty("id").GetGuid();
        using var reader = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(f.ScheduledUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(reader, HttpMethod.Post, f.SchedulesUrl,
            new { clientId = Guid.CreateVersion7(), scheduledAt = ScheduledFixture.InTwoHours(), text = "hola" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(reader, HttpMethod.Put, f.ScheduleUrl(id), new { expectedVersion = 1, text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(reader, HttpMethod.Post, f.ScheduleUrl(id) + "/send-now", new { expectedVersion = 1 })).StatusCode);
    }

    [Fact]
    public async Task AnUnknownIdIs404NotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var unknown = Guid.CreateVersion7();

        foreach (var response in new[]
        {
            await f.EditAsync(unknown, new { expectedVersion = 1, text = "x" }),
            await f.ActAsync(unknown, "cancel", 1),
            await f.ActAsync(unknown, "send-now", 1),
            await f.ActAsync(unknown, "retry", 1),
            await f.ActAsync(unknown, "discard", 1),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("messaging.scheduled.not_found", (await ProblemOf(response)).Code);
        }
    }

    [Fact]
    public async Task AnUnknownConversationKeeps404ConversationNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);

        // Es la señal de despliegue del frontend: un backend sin la ruta responde 404 sin código; con la ruta, este 404 lleva el código.
        var response = await f.Client.GetAsync(ConversationUrl(f.Tenant.TenantId, Guid.CreateVersion7()) + "/scheduled-message", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("messaging.conversation.not_found", (await ProblemOf(response)).Code);
    }

    [Fact]
    public async Task TheGetIsAnEnvelopeWithNullWhenNothingIsScheduled()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);

        var empty = await f.Client.GetFromJsonAsync<JsonElement>(f.ScheduledUrl, Ct);
        await f.CreateTextAsync();
        var filled = await f.Client.GetFromJsonAsync<JsonElement>(f.ScheduledUrl, Ct);

        Assert.Equal(JsonValueKind.Null, empty.GetProperty("scheduledMessage").ValueKind);
        Assert.Equal("Scheduled", filled.GetProperty("scheduledMessage").GetProperty("status").GetString());
    }

    [Fact]
    public async Task SendNowIs202AndTheGetIsEmptyAfterTheWorkerSends()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var scheduled = await f.CreateTextAsync();
        var id = scheduled.GetProperty("id").GetGuid();

        var response = await f.ActAsync(id, "send-now", scheduled.GetProperty("version").GetInt64());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Scheduled", 2L), (body.GetProperty("status").GetString(), body.GetProperty("version").GetInt64()));
        Assert.Equal(0, f.MetaSends());

        await DrainScheduledMessagesAsync(f.Host);

        Assert.Equal(1, f.MetaSends());
        var after = await f.Client.GetFromJsonAsync<JsonElement>(f.ScheduledUrl, Ct);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("scheduledMessage").ValueKind);
    }

    [Fact]
    public async Task AVoiceNoteMultipartIs201()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var clientId = Guid.CreateVersion7();

        var response = await f.PostVoiceNoteAsync(clientId, ScheduledFixture.InTwoHours());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Audio", true, clientId, "audio/ogg"),
            (body.GetProperty("kind").GetString(), body.GetProperty("isVoiceNote").GetBoolean(), body.GetProperty("clientId").GetGuid(),
             body.GetProperty("media").GetProperty("mimeType").GetString()));

        // Repetirlo con el mismo clientId es el 200 de siempre.
        var again = await f.PostVoiceNoteAsync(clientId, ScheduledFixture.InTwoHours());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task AVoiceNoteWithoutFileOrTimeIs422WithFieldKeys()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);

        var response = await f.PostMultipartAsync(f.SchedulesUrl + "/audio", new MultipartFormDataContent { { new StringContent("no-es-un-guid"), "clientId" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["clientId", "file", "scheduledAt"], (await ProblemOf(response)).ErrorKeys);
    }

    [Fact]
    public async Task TheMediaRouteServesTheStoredFileWithRanges()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var scheduled = await f.CreateDocumentAsync();
        var id = scheduled.GetProperty("id").GetGuid();
        var pdf = ScheduledFixture.Pdf();
        var url = f.ScheduleUrl(id) + "/media";

        var whole = await f.Client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(pdf, await whole.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("application/pdf", whole.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", whole.Headers.GetValues("X-Content-Type-Options").Single());

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 9);
        var partial = await f.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(pdf[..10], await partial.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal($"bytes 0-9/{pdf.Length}", partial.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task TheMediaRouteIs404ForATextAnUnknownIdAndAnotherConversation()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var text = await f.CreateTextAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync(f.ScheduleUrl(text.GetProperty("id").GetGuid()) + "/media", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync(f.ScheduleUrl(Guid.CreateVersion7()) + "/media", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await f.Client.GetAsync(ConversationUrl(f.Tenant.TenantId, Guid.CreateVersion7()) + $"/scheduled-messages/{text.GetProperty("id").GetGuid()}/media", Ct)).StatusCode);
    }

    [Fact]
    public async Task AStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var scheduled = await f.CreateTextAsync();
        var id = scheduled.GetProperty("id").GetGuid();
        var version = scheduled.GetProperty("version").GetInt64();

        var edit = await f.EditAsync(id, new { expectedVersion = version + 5, text = "otro" });
        var cancel = await f.ActAsync(id, "cancel", version + 5);

        Assert.Equal((HttpStatusCode.PreconditionFailed, HttpStatusCode.PreconditionFailed), (edit.StatusCode, cancel.StatusCode));
        Assert.Equal("concurrency.conflict", (await ProblemOf(edit)).Code);
        Assert.Equal(version, await f.VersionAsync());
    }

    [Fact]
    public async Task AMissingExpectedVersionIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var id = (await f.CreateTextAsync()).GetProperty("id").GetGuid();

        var response = await SendAsync(f.Client, HttpMethod.Post, f.ScheduleUrl(id) + "/cancel", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["expectedVersion"], (await ProblemOf(response)).ErrorKeys);
    }

    [Fact]
    public async Task APutWithoutScheduledAtKeepsTheStoredTime()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var scheduled = await f.CreateDocumentAsync(caption: "El contrato");
        var id = scheduled.GetProperty("id").GetGuid();
        // La hora guardada es la que lee la base (microsegundos), no la que devolvió el POST con la del reloj.
        var stored = (await f.Client.GetFromJsonAsync<JsonElement>(f.ScheduledUrl, Ct)).GetProperty("scheduledMessage");

        var response = await f.EditAsync(id, new { expectedVersion = scheduled.GetProperty("version").GetInt64(), caption = "El contrato firmado" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(stored.GetProperty("scheduledAt").GetDateTimeOffset(), body.GetProperty("scheduledAt").GetDateTimeOffset());
        Assert.Equal(("El contrato firmado", 2L), (body.GetProperty("caption").GetString(), body.GetProperty("version").GetInt64()));
        Assert.Equal(f.MemberId, body.GetProperty("updatedBy").GetProperty("memberId").GetGuid());
    }

    [Fact]
    public async Task AnEditAfterTheClaimIsNotEditable()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var scheduled = await f.CreateTextAsync();
        var id = scheduled.GetProperty("id").GetGuid();
        var version = scheduled.GetProperty("version").GetInt64();
        // El asesor pide «Enviar ya» y el worker reclama la fila antes de que otra pestaña la edite: la fila ya es Sending.
        Assert.Equal(HttpStatusCode.Accepted, (await f.ActAsync(id, "send-now", version)).StatusCode);
        using (var scope = f.Host.Services.CreateScope())
        {
            var claimed = await scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>()
                .ClaimDueAsync(20, DateTimeOffset.UtcNow, ScheduledMessageDispatcher.LeaseLength, Ct);
            Assert.Single(claimed);
        }

        var edit = await f.EditAsync(id, new { expectedVersion = version + 1, text = "tarde" });

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("messaging.scheduled.not_editable", (await ProblemOf(edit)).Code);
    }

    [Fact]
    public async Task RetryingAScheduledOneIs409NotRetriable()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var scheduled = await f.CreateTextAsync();

        var retry = await f.ActAsync(scheduled.GetProperty("id").GetGuid(), "retry", 1);
        var discard = await f.ActAsync(scheduled.GetProperty("id").GetGuid(), "discard", 1);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (retry.StatusCode, discard.StatusCode));
        Assert.Equal("messaging.scheduled.not_retriable", (await ProblemOf(retry)).Code);
        Assert.Equal("messaging.scheduled.not_retriable", (await ProblemOf(discard)).Code);
    }

    [Fact]
    public async Task RetryingWithABlockedContactIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);
        var id = (await f.CreateTextAsync()).GetProperty("id").GetGuid();
        await f.MakeFailedAsync(id);
        var version = await f.VersionAsync();
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = @member, blocked_wa_id = '573001234567', version = version + 1",
            ("member", f.MemberId));

        var response = await f.ActAsync(id, "retry", version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.contact.blocked", (await ProblemOf(response)).Code);
    }

    [Fact]
    public async Task CreatingOutsideTheWindowOrTooSoonIs422WithTheSpecCodes()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);

        var tooSoon = await f.PostTextAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddSeconds(10));
        var outside = await f.PostTextAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(24));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity), (tooSoon.StatusCode, outside.StatusCode));
        Assert.Equal("messaging.scheduled.too_soon", (await ProblemOf(tooSoon)).Code);
        Assert.Equal("messaging.scheduled.outside_window", (await ProblemOf(outside)).Code);
    }

    [Fact]
    public async Task EveryRouteIsRegisteredAndAnswersItsStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ScheduledFixture.ArrangeAsync(database);

        // create (JSON) y GET
        var text = await f.CreateTextAsync();
        var id = text.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync(f.ScheduledUrl, Ct)).StatusCode);

        // edit, send-now, cancel
        var edit = await f.EditAsync(id, new { expectedVersion = 1, text = "Otro texto" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var sendNow = await f.ActAsync(id, "send-now", 2);
        Assert.Equal(HttpStatusCode.Accepted, sendNow.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.ActAsync(id, "cancel", 3)).StatusCode);

        // create (nota de voz) y media
        var voice = await f.PostVoiceNoteAsync(Guid.CreateVersion7(), ScheduledFixture.InTwoHours());
        Assert.Equal(HttpStatusCode.Created, voice.StatusCode);
        var voiceId = (await voice.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var media = await f.Client.GetAsync(f.ScheduleUrl(voiceId) + "/media", Ct);
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);
        Assert.Equal("audio/ogg", media.Content.Headers.ContentType?.MediaType);

        // retry y discard sobre un Failed
        await f.MakeFailedAsync(voiceId);
        var retry = await f.ActAsync(voiceId, "retry", await f.VersionAsync());
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await f.MakeFailedAsync(voiceId);
        Assert.Equal(HttpStatusCode.NoContent, (await f.ActAsync(voiceId, "discard", await f.VersionAsync())).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await f.Client.GetFromJsonAsync<JsonElement>(f.ScheduledUrl, Ct)).GetProperty("scheduledMessage").ValueKind);
    }
}
