using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// GET y PUT del layout de columnas (spec 2026-09-24, "Pruebas"): el efectivo con ETag "1" sin fila;
/// el primer PUT con If-Match "1" crea y devuelve "2"; 412, 428, 422 de campo y de dominio, 403, y
/// la auditoría en el outbox. Permisos de settings pedidos por X-Permissions como texto: este
/// proyecto no referencia Modules.Tenancy.Application (hallazgo 13).
/// </summary>
public sealed class OrdersExportLayoutApiTests
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";
    private const string AuditEvent = "platform.audit.recorded.v1";
    private const string AuditAction = "quotations.orders_export_layout.updated";

    private static string LayoutUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders-export-layout";

    [Fact]
    public async Task GetWithoutAStoredLayoutReturnsTheCatalogWithETagOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        using var response = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        var layout = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(layout);
        Assert.Equal(tenantId, layout.TenantId);
        Assert.Equal(1, layout.Version);
        Assert.Equal(47, layout.Columns.Count);
        Assert.All(layout.Columns, column => Assert.Equal("Catalog", column.Kind));
        // Sin fila, cada columna sale como nace: todas visibles menos "coordinadora_city"
        // (ajuste 2026-10-02) y las "payment_method_N" (ajuste 2026-10-03), que nacen ocultas y lo
        // dicen en defaultVisible.
        Assert.All(layout.Columns, column => Assert.Equal(column.DefaultVisible, column.Visible));
        Assert.Equal(new ColumnPayload("Catalog", "company", "EMPRESA", 1, true, "EMPRESA", null, true), layout.Columns[0]);
        Assert.Equal(new ColumnPayload("Catalog", "email", "Email", 19, true, "Email", null, true), layout.Columns[18]);
        Assert.Equal(38, layout.Columns[37].DefaultPosition);
        Assert.Equal(new ColumnPayload("Catalog", "tax_rate", "Tasa IVA", 39, true, "Tasa IVA", null, true), layout.Columns[38]);
        Assert.Equal(new ColumnPayload("Catalog", "company_tax_id", "NIT Empresa", 40, true, "NIT Empresa", null, true), layout.Columns[39]);
        Assert.Equal(
            new ColumnPayload("Catalog", "coordinadora_city", "Ciudad Coordinadora", 41, false, "Ciudad Coordinadora", null, false),
            layout.Columns[40]);
        Assert.Equal(
            new ColumnPayload("Catalog", "payment_method_1", "Forma de pago 1", 42, false, "Forma de pago 1", null, false),
            layout.Columns[41]);
        Assert.Equal(
            new ColumnPayload("Catalog", "payment_method_5", "Forma de pago 5", 46, false, "Forma de pago 5", null, false),
            layout.Columns[45]);
        Assert.Equal(
            new ColumnPayload("Catalog", "order_total", "Total facturado", 47, true, "Total facturado", null, true),
            layout.Columns[46]);
    }

    // D9: el primer PUT viaja con "1" y la fila nace en 2. El GET siguiente la devuelve tal cual.
    [Fact]
    public async Task TheFirstPutWithIfMatchOneCreatesTheLayoutAndReturnsETagTwo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns.Insert(0, Fixed("Tipo Doc", "FV"));
        columns[19] = columns[19] with { Header = "Correo" };
        columns[1] = columns[1] with { Visible = false };

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var saved = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Version);
        // 47 del catálogo (desde el ajuste 2026-10-05) + 1 fija.
        Assert.Equal(48, saved.Columns.Count);
        Assert.Equal(new ColumnPayload("Fixed", null, null, null, null, "Tipo Doc", "FV", true), saved.Columns[0]);
        Assert.Equal(new ColumnPayload("Catalog", "company", "EMPRESA", 1, true, "EMPRESA", null, false), saved.Columns[1]);
        Assert.Equal("Correo", saved.Columns[19].Header);
        Assert.Equal("Email", saved.Columns[19].DefaultHeader);

        using var read = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal("\"2\"", read.Headers.ETag?.Tag);
        var reread = await read.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(reread);
        Assert.Equal(saved.Version, reread.Version);
        // Elemento a elemento: ColumnPayload es un record, y la lista se compara por contenido.
        Assert.Equal(saved.Columns, reread.Columns);
    }

    [Fact]
    public async Task PutWithAStaleVersionIsAPreconditionFailure()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[19] = columns[19] with { Header = "Correo" };
        using var first = await PutAsync(client, tenantId, columns, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        columns[19] = columns[19] with { Header = "E-mail" };

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("concurrency.conflict", problem?.Code);
    }

    // Sin fila, la única versión válida es la implícita: "2" también es 412.
    [Fact]
    public async Task PutWithoutARowAndAVersionOtherThanOneIsAPreconditionFailure()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);

        using var response = await PutAsync(client, tenantId, columns, "\"2\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task PutWithoutIfMatchIsPreconditionRequired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);

        using var response = await PutAsync(client, tenantId, columns, ifMatch: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("precondition.if_match_required", problem?.Code);
    }

    // Review Focus 2: el ETag débil también es la versión 1.
    [Fact]
    public async Task PutAcceptsAWeakIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[19] = columns[19] with { Header = "Correo" };

        using var response = await PutAsync(client, tenantId, columns, "W/\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task PutWithABlankHeaderMarksTheColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[3] = columns[3] with { Header = "   " };

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Equal(["Columns[3].Header"], problem!.Errors!.Keys);
    }

    [Fact]
    public async Task PutWithATooLongFixedValueMarksTheColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns.Insert(0, Fixed("Bodega", new string('v', 129)));

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Equal(["Columns[0].Value"], problem!.Errors!.Keys);
    }

    [Fact]
    public async Task PutWithAnUnknownKindMarksTheColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[1] = columns[1] with { Kind = "catalog" };

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(["Columns[1].Kind"], problem!.Errors!.Keys);
    }

    [Fact]
    public async Task PutWithoutColumnsMarksTheList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        using var request = new HttpRequestMessage(HttpMethod.Put, LayoutUrl(tenantId))
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(["Columns"], problem!.Errors!.Keys);
    }

    // Los cuatro códigos de dominio que el validador no tapa (hallazgo 9). Sin mapa `errors`: el
    // formulario los muestra como mensaje general del pie.
    [Fact]
    public async Task PutWithAnUnknownKeyIsColumnsInvalid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[0] = columns[0] with { Key = "Company" };

        await AssertDomainCodeAsync(client, tenantId, columns, "quotations.orders_export_layout.columns_invalid");
    }

    // Ajuste 2026-09-25: una llave puede repetirse bajo otro encabezado. Se guarda cada entrada, y
    // el GET devuelve las dos con el mismo defecto.
    [Fact]
    public async Task PutWithARepeatedKeyUnderAnotherHeaderIsSaved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns.Add(columns[18] with { Header = "Otro correo" });

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var read = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        var layout = await read.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        // 47 del catálogo (desde el ajuste 2026-10-05) + la repetida.
        Assert.Equal(48, layout!.Columns.Count);
        Assert.Equal(
            [
                new ColumnPayload("Catalog", "email", "Email", 19, true, "Email", null, true),
                new ColumnPayload("Catalog", "email", "Email", 19, true, "Otro correo", null, true),
            ],
            layout.Columns.Where(column => column.Key == "email"));
    }

    [Fact]
    public async Task PutWithTwoVisibleColumnsSharingAHeaderIsHeaderDuplicated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[18] = columns[18] with { Header = "ciudad" };

        await AssertDomainCodeAsync(client, tenantId, columns, "quotations.orders_export_layout.header_duplicated");
    }

    [Fact]
    public async Task PutHidingEveryColumnIsAllHidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = (await DefaultColumnsAsync(client, tenantId))
            .Select(column => column with { Visible = false })
            .ToList();

        await AssertDomainCodeAsync(client, tenantId, columns, "quotations.orders_export_layout.all_hidden");
    }

    // Review Focus 4: la fija 41 sobre una fila con cuarenta la rechaza y deja la fila intacta
    // (el tope pasó de 10 a 40 el 2026-09-25).
    [Fact]
    public async Task PutWithFortyOneFixedColumnsIsTooManyFixedColumns()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var forty = await DefaultColumnsAsync(client, tenantId);
        forty.AddRange(Enumerable.Range(1, 40).Select(number => Fixed($"Fija {number}", $"{number}")));
        using var saved = await PutAsync(client, tenantId, forty, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var fortyOne = (await saved.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken))!.Columns.ToList();
        fortyOne.Add(Fixed("Fija 41", "41"));

        await AssertDomainCodeAsync(client, tenantId, fortyOne, "quotations.orders_export_layout.too_many_fixed_columns", ifMatch: "\"2\"");

        using var read = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal("\"2\"", read.Headers.ETag?.Tag);
        var layout = await read.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(40, layout!.Columns.Count(column => column.Kind == "Fixed"));
    }

    [Fact]
    public async Task PutForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var __ = otherOwner;
        var columns = await DefaultColumnsAsync(owner, tenantId);

        using var response = await PutAsync(otherOwner, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var read = await owner.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal("\"1\"", read.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task GetForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, SettingsRead);
        using var __ = otherOwner;

        using var response = await otherOwner.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // D5: leer no es editar. La política del endpoint corta antes del handler.
    [Fact]
    public async Task PutWithOnlySettingsReadIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetWithoutSettingsReadIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsUpdate);
        using var _ = client;

        using var response = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AChangedLayoutIsAuditedAndAnUnchangedOneIsNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[19] = columns[19] with { Header = "Correo" };

        using var changed = await PutAsync(client, tenantId, columns, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var audits = (await OutboxMessagesAsync(factory, AuditEvent))
            .Where(message => ActionOf(message) == AuditAction)
            .ToArray();
        var entry = Assert.Single(audits);
        using (var payload = JsonDocument.Parse(entry.PayloadJson))
        {
            Assert.Equal(tenantId, payload.RootElement.GetProperty("tenantId").GetGuid());
            Assert.Equal(ownerUserId, payload.RootElement.GetProperty("actorId").GetGuid());
            Assert.Equal(tenantId.ToString(), payload.RootElement.GetProperty("resourceId").GetString());
            Assert.Equal("success", payload.RootElement.GetProperty("outcome").GetString());
        }

        using var unchanged = await PutAsync(client, tenantId, columns, "\"2\"");
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal("\"2\"", unchanged.Headers.ETag?.Tag);
        Assert.Single(
            await OutboxMessagesAsync(factory, AuditEvent), message => ActionOf(message) == AuditAction);
    }

    // D8: un PUT no exige el catálogo entero; lo que falte va al final, con su nombre y visible
    // según su defecto: "coordinadora_city" (ajuste 2026-10-02) y las "payment_method_N" (ajuste
    // 2026-10-03) se completan ocultas.
    [Fact]
    public async Task PutWithoutSomeCatalogKeysCompletesThemAtTheEnd()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        using var response = await PutAsync(
            client, tenantId, [Catalog("email", "Correo"), Catalog("order_number", "Pedido")], "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var layout = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(47, layout!.Columns.Count);
        Assert.Equal("email", layout.Columns[0].Key);
        Assert.Equal("order_number", layout.Columns[1].Key);
        Assert.Equal(new ColumnPayload("Catalog", "company", "EMPRESA", 1, true, "EMPRESA", null, true), layout.Columns[2]);
        Assert.Equal(
            ["coordinadora_city", "payment_method_1", "payment_method_2", "payment_method_3", "payment_method_4", "payment_method_5", "order_total"],
            layout.Columns.TakeLast(7).Select(column => column.Key));
        // Las seis primeras nacen ocultas; "order_total" (ajuste 2026-10-05) nace visible.
        Assert.All(layout.Columns.TakeLast(7).SkipLast(1), column => Assert.False(column.Visible));
        Assert.True(layout.Columns[^1].Visible);
    }

    // D10: restaurar es un PUT con el catálogo en su orden y nombres, sin fijas. La fila queda.
    [Fact]
    public async Task RestoringIsAPutWithTheCatalogDefaultsWithoutFixedColumns()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var defaults = await DefaultColumnsAsync(client, tenantId);
        var custom = defaults.ToList();
        custom.Insert(0, Fixed("Tipo Doc", "FV"));
        custom[19] = custom[19] with { Header = "Correo", Visible = false };
        using var saved = await PutAsync(client, tenantId, custom, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var restored = await PutAsync(client, tenantId, defaults, "\"2\"");

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("\"3\"", restored.Headers.ETag?.Tag);
        var layout = await restored.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(47, layout!.Columns.Count);
        Assert.DoesNotContain(layout.Columns, column => column.Kind == "Fixed");
        Assert.Equal(defaults, layout.Columns);
    }

    // Spec 2026-10-05, D6: el GET trae el nombre efectivo y el default, en camelCase como el resto
    // del cuerpo. Sin fila, los dos son "Pedidos".
    [Fact]
    public async Task GetWithoutAStoredLayoutReturnsTheDefaultSheetName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        using var response = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Pedidos", body.RootElement.GetProperty("sheetName").GetString());
        Assert.Equal("Pedidos", body.RootElement.GetProperty("defaultSheetName").GetString());
    }

    // D5: guardar sólo el nombre es un cambio — sube la versión, se audita y el GET lo devuelve.
    [Fact]
    public async Task PutWithOnlyANewSheetNameSavesItAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);

        using var response = await PutAsync(client, tenantId, columns, "MIGRACION 1", "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var saved = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("MIGRACION 1", saved!.SheetName);
        Assert.Equal("Pedidos", saved.DefaultSheetName);

        var reread = await client.GetFromJsonAsync<LayoutPayload>(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal("MIGRACION 1", reread!.SheetName);
        Assert.Equal(2, reread.Version);

        Assert.Single(
            await OutboxMessagesAsync(factory, AuditEvent), message => ActionOf(message) == AuditAction);
    }

    // D6: un PUT sin sheetName —el de un frontend anterior a este campo— conserva el nombre.
    [Fact]
    public async Task PutWithoutASheetNameKeepsTheStoredOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        using var first = await PutAsync(client, tenantId, columns, "MIGRACION 1", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        columns[18] = columns[18] with { Header = "Correo" };

        using var response = await PutAsync(client, tenantId, columns, "\"2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(3, saved!.Version);
        Assert.Equal("MIGRACION 1", saved.SheetName);
        Assert.Equal("Correo", saved.Columns[18].Header);
    }

    // D4 y D6: vacía no es ausente. El 422 trae el mapa errors con la llave del campo, que es lo
    // que la pantalla usa para marcar el input.
    [Theory]
    [InlineData("   ")]
    [InlineData("Hoja/1")]
    [InlineData("History")]
    public async Task PutWithAnInvalidSheetNameMarksTheField(string sheetName)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);

        using var response = await PutAsync(client, tenantId, columns, sheetName, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Equal(["SheetName"], problem!.Errors!.Keys);
    }

    private static async Task AssertDomainCodeAsync(
        HttpClient client, Guid tenantId, IReadOnlyList<ColumnPayload> columns, string code, string ifMatch = "\"1\"")
    {
        using var response = await PutAsync(client, tenantId, columns, ifMatch);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem?.Code);
        Assert.Null(problem?.Errors);
    }

    /// <summary>El efectivo tal como lo devuelve el GET, listo para editarlo y mandarlo de vuelta:
    /// el mismo record sirve de request, y la API ignora `defaultHeader`/`defaultPosition`.</summary>
    private static async Task<List<ColumnPayload>> DefaultColumnsAsync(HttpClient client, Guid tenantId)
    {
        var layout = await client.GetFromJsonAsync<LayoutPayload>(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(layout);
        return [.. layout.Columns];
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid tenantId, IReadOnlyList<ColumnPayload> columns, string? ifMatch) =>
        SendPutAsync(client, tenantId, JsonContent.Create(new { columns }), ifMatch);

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid tenantId, IReadOnlyList<ColumnPayload> columns, string sheetName, string? ifMatch) =>
        SendPutAsync(client, tenantId, JsonContent.Create(new { columns, sheetName }), ifMatch);

    private static Task<HttpResponseMessage> SendPutAsync(
        HttpClient client, Guid tenantId, HttpContent content, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, LayoutUrl(tenantId))
        {
            Content = content,
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static ColumnPayload Catalog(string key, string header, bool visible = true) =>
        new("Catalog", key, null, null, null, header, null, visible);

    private static ColumnPayload Fixed(string header, string value, bool visible = true) =>
        new("Fixed", null, null, null, null, header, value, visible);

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    private sealed record LayoutPayload(
        Guid TenantId, IReadOnlyList<ColumnPayload> Columns, long Version, string? SheetName, string? DefaultSheetName);

    private sealed record ColumnPayload(
        string Kind, string? Key, string? DefaultHeader, int? DefaultPosition, bool? DefaultVisible, string Header,
        string? Value, bool Visible);

    private sealed record ProblemPayload(string? Code, Dictionary<string, string[]>? Errors);
}
