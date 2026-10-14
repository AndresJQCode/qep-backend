using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.3 «Refrescar» y D-T8: el listado de Meta hace upsert por <c>id</c> en el espejo (conservando el kind), borra lo
/// que Meta ya no lista, salta lo que no tiene id o nombre, marca la sincronización y guarda una vez. El catálogo de envío sincroniza
/// sólo la primera vez o con refresh, y sirve las APPROVED del espejo.</summary>
public sealed class TemplateMirrorSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
    private static readonly MessagingTemplateSource Source = new("222", "token");

    private sealed record Rig(TemplateMirrorSync Sync, MirroredTemplateCatalog Catalog, FakeMessageTemplateRepository Repository, FakeWhatsAppClient Meta, FakeConnectionDirectory Connections, FakeUnitOfWork UnitOfWork);

    private static Rig NewRig(params JsonElement[] listing)
    {
        var repository = new FakeMessageTemplateRepository();
        var meta = new FakeWhatsAppClient { NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>(listing, null) };
        var connections = new FakeConnectionDirectory();
        var unitOfWork = new FakeUnitOfWork();
        var sync = new TemplateMirrorSync(repository, meta, connections, unitOfWork, new FakeClock(Now));
        return new Rig(sync, new MirroredTemplateCatalog(repository, sync, connections), repository, meta, connections, unitOfWork);
    }

    private static JsonElement Listed(string id, string name, string status = "APPROVED", string? rejectedReason = null, string category = "UTILITY", string body = "Hola {{1}}") =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id,
            name,
            language = "es",
            status,
            category,
            parameter_format = "POSITIONAL",
            rejected_reason = rejectedReason,
            components = new[] { new { type = "BODY", text = body } },
        })).RootElement.Clone();

    private static void ListingFails(Rig rig, MessagingGraphFailure failure) =>
        rig.Meta.NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>(null, failure);

    [Fact]
    public async Task ANewTemplateIsMirroredAsExternalWithMetasStatusAndReason()
    {
        var rig = NewRig(Listed("1", "promo", "REJECTED", "INVALID_FORMAT", "MARKETING"));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        var row = Assert.Single(rig.Repository.Rows);
        Assert.Equal(("1", "promo", "es", "MARKETING", "POSITIONAL", "REJECTED", "INVALID_FORMAT", MessageTemplateKind.External, Now),
            (row.MetaTemplateId, row.Name, row.Language, row.Category, row.ParameterFormat, row.Status, row.StatusReason, row.Kind, row.SyncedAt));
        Assert.Contains("\"BODY\"", row.ComponentsJson, StringComparison.Ordinal);
        Assert.Equal(Now, rig.Repository.Syncs[(TenantId, ConnectionId)]);
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AKnownTemplateIsUpdatedAndKeepsItsKind()
    {
        var rig = NewRig(Listed("1", MessageTemplateName.QuotationName, "APPROVED", "NONE"));
        rig.Repository.Rows.Add(MessageTemplate.Pending(TenantId, ConnectionId, "1", MessageTemplateName.QuotationName, "es", "UTILITY", "NAMED", "PENDING", "[]",
            MessageTemplateKind.Quotation, Guid.CreateVersion7(), Now.AddDays(-1)));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        var row = Assert.Single(rig.Repository.Rows);
        Assert.Equal((MessageTemplateKind.Quotation, "APPROVED", null, Now), (row.Kind, row.Status, row.StatusReason, row.SyncedAt));
    }

    [Fact]
    public async Task ATemplateMetaNoLongerListsIsRemoved()
    {
        var rig = NewRig(Listed("1", "queda"));
        rig.Repository.Rows.Add(MessageTemplate.Mirrored(TenantId, ConnectionId, "2", "se_fue", "es", "UTILITY", "POSITIONAL", "APPROVED", null, "[]", Now.AddDays(-1)));
        // La de otra conexión no se toca.
        rig.Repository.Rows.Add(MessageTemplate.Mirrored(TenantId, Guid.CreateVersion7(), "2", "ajena", "es", "UTILITY", "POSITIONAL", "APPROVED", null, "[]", Now));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        Assert.Equal(["ajena", "queda"], rig.Repository.Rows.Select(row => row.Name).Order(StringComparer.Ordinal));
    }

    // Review Focus 3: una paginación que repite un id no puede insertar dos filas.
    [Fact]
    public async Task ADuplicatedIdInTheListingIsTakenOnce()
    {
        var rig = NewRig(Listed("1", "promo"), Listed("1", "promo"));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        Assert.Single(rig.Repository.Rows);
    }

    [Fact]
    public async Task AnElementWithoutIdNameOrLanguageIsSkipped()
    {
        var withoutId = JsonDocument.Parse("""{"name":"x","language":"es","status":"APPROVED"}""").RootElement.Clone();
        var withoutName = JsonDocument.Parse("""{"id":"9","language":"es","status":"APPROVED"}""").RootElement.Clone();
        var notAnObject = JsonDocument.Parse("\"texto\"").RootElement.Clone();
        var rig = NewRig(withoutId, withoutName, notAnObject, Listed("1", "ok"));

        await rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct);

        Assert.Equal("ok", Assert.Single(rig.Repository.Rows).Name);
    }

    // Meta manda el id como string en el listado y como número en el webhook (Task 10 reusa el mismo lector).
    [Fact]
    public void AnIdIsReadAsTextOrAsANumber()
    {
        var asText = JsonDocument.Parse("""{"id":"771"}""").RootElement.Clone();
        var asNumber = JsonDocument.Parse("""{"id":772}""").RootElement.Clone();
        var asSomethingElse = JsonDocument.Parse("""{"id":true}""").RootElement.Clone();

        Assert.Equal(("771", "772", null, null),
            (TemplateMirrorSync.Identifier(asText, "id"), TemplateMirrorSync.Identifier(asNumber, "id"), TemplateMirrorSync.Identifier(asSomethingElse, "id"),
             TemplateMirrorSync.Identifier(asText, "missing")));
    }

    [Fact]
    public async Task AListingMetaDoesNotAnswerIsTemplatesUnavailableAndWritesNothing()
    {
        var rig = NewRig();
        ListingFails(rig, new MessagingGraphFailure(502, null, "http_502"));

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct));

        Assert.Equal(MessagingErrorCodes.TemplatesUnavailable, exception.Code);
        Assert.Equal(0, rig.UnitOfWork.Saves);
        Assert.Empty(rig.Repository.Syncs);
    }

    // Amendment 2: el listado no es una creación. Un 400 con el texto de «nombre ya existe» no es name_taken: es Meta sin lista.
    [Fact]
    public async Task AListingRejectedForAnyOtherReasonIsTemplatesUnavailableNeverACreateCode()
    {
        var rig = NewRig();
        ListingFails(rig, new MessagingGraphFailure(400, 100, "http_400", "Message Template Name Already Exists", "x"));

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct));

        Assert.Equal(MessagingErrorCodes.TemplatesUnavailable, exception.Code);
        Assert.Empty(rig.Connections.Reported);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ARejectedTokenMarksTheConnectionAndIsConnectionUnavailable()
    {
        var rig = NewRig();
        ListingFails(rig, new MessagingGraphFailure(401, 190, "http_401"));

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, exception.Code);
        Assert.Single(rig.Connections.Reported);
    }

    [Fact]
    public async Task AMissingPermissionInTheListingAlsoMarksTheConnection()
    {
        var rig = NewRig();
        ListingFails(rig, new MessagingGraphFailure(403, 200, "http_403"));

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Sync.RunAsync(TenantId, ConnectionId, Source, Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, exception.Code);
        Assert.Equal("token_expired", Assert.Single(rig.Connections.Reported).FailureCode);
    }

    [Fact]
    public async Task TheCatalogSyncsOnlyTheFirstTimeAndThenServesTheMirror()
    {
        var rig = NewRig(Listed("1", "b"), Listed("2", "a"), Listed("3", "c", "PAUSED"));

        var first = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);
        var second = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        Assert.Equal(1, rig.Meta.TemplateListCalls);
        Assert.Equal(["a", "b"], first.Select(template => template.Name));
        Assert.Equal(["a", "b"], second.Select(template => template.Name));
        Assert.Equal(["1"], first[0].Parameters);
    }

    [Fact]
    public async Task RefreshAsksMetaAgain()
    {
        var rig = NewRig(Listed("1", "a"));
        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: true, Ct);

        Assert.Equal(2, rig.Meta.TemplateListCalls);
    }

    [Fact]
    public async Task AnUnavailableConnectionIs422WithoutTouchingMeta()
    {
        var rig = NewRig(Listed("1", "a"));
        rig.Connections.TemplateSource = null;

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, exception.Code);
        Assert.Equal(0, rig.Meta.TemplateListCalls);
    }

    // Amendment 5: dos primeros llenados a la vez. El que pierde el PK de template_mirror_syncs no le devuelve un 412 a quien sólo
    // quería leer el catálogo: el otro ya llenó el espejo, y se lee de ahí.
    [Fact]
    public async Task AnImplicitFirstFillThatLosesTheRaceKeepsReadingTheMirror()
    {
        var rig = NewRig(Listed("1", "a"));
        rig.UnitOfWork.Failure = new RequestConcurrencyException("concurrency.conflict", "Another sync of the same connection committed first.");

        var catalog = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        Assert.Equal(["a"], catalog.Select(template => template.Name));
        Assert.Equal(1, rig.Meta.TemplateListCalls);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ARefreshThatLosesTheRaceIsStillAConcurrencyConflict()
    {
        var rig = NewRig(Listed("1", "a"));
        rig.Repository.Syncs[(TenantId, ConnectionId)] = Now.AddHours(-1);
        rig.UnitOfWork.Failure = new RequestConcurrencyException("concurrency.conflict", "Another sync of the same connection committed first.");

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(() => rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: true, Ct));

        Assert.Equal("concurrency.conflict", exception.Code);
    }

    // Lo único que el llenado implícito perdona es la carrera: Meta caída en el primer llenado sigue siendo templates.unavailable.
    [Fact]
    public async Task AnImplicitFirstFillWithMetaDownIsStillTemplatesUnavailable()
    {
        var rig = NewRig();
        ListingFails(rig, new MessagingGraphFailure(0, null, "unreachable"));

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct));

        Assert.Equal(MessagingErrorCodes.TemplatesUnavailable, exception.Code);
    }
}
