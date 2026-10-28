using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// The Categorías screen's writes (spec 2026-10-26-categorias-pos-acceso-rapido): rename/move and delete
/// with <c>If-Match</c>, and the register's quick access replaced whole. Mirrors
/// <see cref="CategoryApiTests"/>, which covers list and create.
/// </summary>
public sealed class CategoryAdminApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static readonly string[] ManagePermissions =
        [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    // ---- PUT /categories/{id} ------------------------------------------------------------

    [Fact]
    public async Task UpdateRenamesAndMovesAndBumpsTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var calzado = await CreateAsync(client, "Calzado", null);
        var botas = await CreateAsync(client, "Botas", null);

        var response = await UpdateAsync(client, botas.Id, "  Botas de cuero ", calzado.Id, botas.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadCategoryAsync(response);
        Assert.Equal(("Botas de cuero", (Guid?)calzado.Id, 2L), (updated.Name, updated.ParentId, updated.Version));
        Assert.Equal(botas.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= botas.UpdatedAt);
        Assert.Null(updated.PosQuickAccessPosition);

        var listed = (await ListAsync(client)).Single(item => item.Id == botas.Id);
        Assert.Equal(("Botas de cuero", (Guid?)calzado.Id, 2L), (listed.Name, listed.ParentId, listed.Version));
    }

    [Fact]
    public async Task UpdateToANullParentMakesItARootAndAnswersItsProductCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);
        var gaseosas = await CreateAsync(client, "Gaseosas", bebidas.Id);
        await CreateProductAsync(client, "Cola", "CO-1", gaseosas.Id);

        var updated = await ReadCategoryAsync(
            await UpdateAsync(client, gaseosas.Id, "Gaseosas", null, gaseosas.Version));

        Assert.Null(updated.ParentId);
        Assert.Equal(1, updated.ProductCount);
    }

    [Fact]
    public async Task WritesWithoutIfMatchArePreconditionRequired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);

        var update = await UpdateAsync(client, bebidas.Id, "Otra", null, version: null);
        var delete = await DeleteAsync(client, bebidas.Id, version: null);

        await AssertProblemAsync(update, HttpStatusCode.PreconditionRequired, "precondition.if_match_required");
        await AssertProblemAsync(delete, HttpStatusCode.PreconditionRequired, "precondition.if_match_required");
        Assert.Equal("Bebidas", Assert.Single(await ListAsync(client)).Name);
    }

    [Fact]
    public async Task WritesWithAStaleVersionArePreconditionFailed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);
        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(client, bebidas.Id, "Bebidas frías", null, 1)).StatusCode);

        var update = await UpdateAsync(client, bebidas.Id, "Otra", null, 1);
        var delete = await DeleteAsync(client, bebidas.Id, 1);

        await AssertProblemAsync(update, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        await AssertProblemAsync(delete, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        Assert.Equal("Bebidas frías", Assert.Single(await ListAsync(client)).Name);
    }

    // The weak form the SPA may send is accepted, as in the rest of the repository.
    [Fact]
    public async Task AWeakIfMatchIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);

        using var request = new HttpRequestMessage(HttpMethod.Put, CategoryUrl(bebidas.Id))
        {
            Content = JsonContent.Create(new { name = "Bebidas frías", parentId = (Guid?)null }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "W/\"1\"");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WritesOnAnUnknownOrForeignCategoryAreNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);
        var foreign = await CreateAsync(other, "Ajena", null, OtherTenantId);

        foreach (var id in new[] { Guid.CreateVersion7(), foreign.Id })
        {
            await AssertProblemAsync(
                await UpdateAsync(client, id, "Mía", null, 1), HttpStatusCode.NotFound, "catalog.category.not_found");
            await AssertProblemAsync(
                await DeleteAsync(client, id, 1), HttpStatusCode.NotFound, "catalog.category.not_found");
        }

        Assert.Equal("Ajena", Assert.Single(await ListAsync(other, OtherTenantId)).Name);
    }

    [Fact]
    public async Task MovingUnderItselfOrADescendantIsACycle()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);
        var gaseosas = await CreateAsync(client, "Gaseosas", bebidas.Id);
        var cola = await CreateAsync(client, "Cola", gaseosas.Id);

        await AssertProblemAsync(
            await UpdateAsync(client, bebidas.Id, "Bebidas", bebidas.Id, bebidas.Version),
            HttpStatusCode.UnprocessableEntity,
            "catalog.category.cycle");
        await AssertProblemAsync(
            await UpdateAsync(client, bebidas.Id, "Bebidas", cola.Id, bebidas.Version),
            HttpStatusCode.UnprocessableEntity,
            "catalog.category.cycle");
        Assert.Null((await ListAsync(client)).Single(item => item.Id == bebidas.Id).ParentId);
    }

    [Fact]
    public async Task MovingUnderAnUnknownOrForeignParentIsParentNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);
        var foreign = await CreateAsync(other, "Ajena", null, OtherTenantId);

        foreach (var parentId in new[] { Guid.CreateVersion7(), foreign.Id })
        {
            await AssertProblemAsync(
                await UpdateAsync(client, bebidas.Id, "Bebidas", parentId, bebidas.Version),
                HttpStatusCode.UnprocessableEntity,
                "catalog.category.parent_not_found");
        }
    }

    // The whole subtree moves: its deepest node has to stay within ten levels.
    [Fact]
    public async Task MovingASubtreeBelowTheTenthLevelIsTooDeep()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        Guid? parentId = null;
        var chain = new List<CategoryResponse>();
        for (var level = 1; level <= 9; level++)
        {
            var category = await CreateAsync(client, $"N{level}", parentId);
            chain.Add(category);
            parentId = category.Id;
        }

        // S1 → S2: under N9 S2 would land on level 11; under N8, on level 10.
        var subtreeRoot = await CreateAsync(client, "S1", null);
        await CreateAsync(client, "S2", subtreeRoot.Id);

        await AssertProblemAsync(
            await UpdateAsync(client, subtreeRoot.Id, "S1", chain[9 - 1].Id, subtreeRoot.Version),
            HttpStatusCode.UnprocessableEntity,
            "catalog.category.too_deep");
        Assert.Equal(
            HttpStatusCode.OK,
            (await UpdateAsync(client, subtreeRoot.Id, "S1", chain[8 - 1].Id, subtreeRoot.Version)).StatusCode);
    }

    [Fact]
    public async Task RenamingOrMovingOntoASiblingNameIsNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var calzado = await CreateAsync(client, "Calzado", null);
        var ropa = await CreateAsync(client, "Ropa", null);
        await CreateAsync(client, "Accesorios", calzado.Id);
        var accesoriosRopa = await CreateAsync(client, "Accesorios", ropa.Id);

        await AssertProblemAsync(
            await UpdateAsync(client, ropa.Id, " calzado ", null, ropa.Version),
            HttpStatusCode.UnprocessableEntity,
            "catalog.category.name_taken");
        await AssertProblemAsync(
            await UpdateAsync(client, accesoriosRopa.Id, "Accesorios", calzado.Id, accesoriosRopa.Version),
            HttpStatusCode.UnprocessableEntity,
            "catalog.category.name_taken");
    }

    [Fact]
    public async Task UpdateWithABlankOrLongNameReturnsTheFieldMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);

        foreach (var name in new[] { "   ", new string('a', 81) })
        {
            var response = await UpdateAsync(client, bebidas.Id, name, null, bebidas.Version);

            await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation.failed");
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("errors", body, StringComparison.Ordinal);
            Assert.Contains("Name", body, StringComparison.Ordinal);
        }
    }

    // ---- DELETE /categories/{id} ---------------------------------------------------------

    [Fact]
    public async Task DeleteAnEmptyLeafIsNoContentAndItIsGone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);
        var gaseosas = await CreateAsync(client, "Gaseosas", bebidas.Id);

        var response = await DeleteAsync(client, gaseosas.Id, gaseosas.Version);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(bebidas.Id, Assert.Single(await ListAsync(client)).Id);
    }

    // Active or inactive: an inactive product still holds the category.
    [Fact]
    public async Task DeleteACategoryWithProductsIsHasProducts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);
        var productId = await CreateProductAsync(client, "Agua", "AG-1", bebidas.Id);
        var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{productId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        await AssertProblemAsync(
            await DeleteAsync(client, bebidas.Id, bebidas.Version),
            HttpStatusCode.UnprocessableEntity,
            "catalog.category.has_products");
        Assert.Single(await ListAsync(client));
    }

    [Fact]
    public async Task DeleteACategoryWithChildrenIsHasChildren()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);
        await CreateAsync(client, "Gaseosas", bebidas.Id);

        await AssertProblemAsync(
            await DeleteAsync(client, bebidas.Id, bebidas.Version),
            HttpStatusCode.UnprocessableEntity,
            "catalog.category.has_children");
        Assert.Equal(2, (await ListAsync(client)).Count);
    }

    [Fact]
    public async Task DeleteAPinnedCategoryRecompactsTheQuickAccess()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var a = await CreateAsync(client, "A", null);
        var b = await CreateAsync(client, "B", null);
        var c = await CreateAsync(client, "C", null);
        Assert.Equal(HttpStatusCode.OK, (await ReplaceQuickAccessAsync(client, [b.Id, a.Id, c.Id])).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, a.Id, a.Version)).StatusCode);

        var items = await ListAsync(client);
        Assert.Equal(
            [("B", (int?)1), ("C", 2)],
            items.Select(item => (item.Name, item.PosQuickAccessPosition)));
    }

    // ---- PUT /categories/pos-quick-access ------------------------------------------------

    [Fact]
    public async Task ReplaceQuickAccessAnswersTheWholeListWithPositionsInBodyOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);
        var gaseosas = await CreateAsync(client, "Gaseosas", bebidas.Id);
        var calzado = await CreateAsync(client, "Calzado", null);

        var response = await ReplaceQuickAccessAsync(client, [gaseosas.Id, calzado.Id]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CategoriesResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        // Still by name; the position travels on each item.
        Assert.Equal(
            [("Bebidas", (int?)null), ("Calzado", 2), ("Gaseosas", 1)],
            body.Items.Select(item => (item.Name, item.PosQuickAccessPosition)));
        Assert.Equal(
            body.Items.Select(item => (item.Id, item.PosQuickAccessPosition)),
            (await ListAsync(client)).Select(item => (item.Id, item.PosQuickAccessPosition)));
    }

    [Fact]
    public async Task ReplaceQuickAccessReordersUnpinsAndAnEmptyListClearsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var a = await CreateAsync(client, "A", null);
        var b = await CreateAsync(client, "B", null);
        var c = await CreateAsync(client, "C", null);
        await ReplaceQuickAccessAsync(client, [a.Id, b.Id, c.Id]);

        await ReplaceQuickAccessAsync(client, [c.Id, a.Id]);
        var reordered = await ListAsync(client);
        Assert.Equal(
            [("A", (int?)2), ("B", null), ("C", 1)],
            reordered.Select(item => (item.Name, item.PosQuickAccessPosition)));

        await ReplaceQuickAccessAsync(client, []);
        Assert.All(await ListAsync(client), item => Assert.Null(item.PosQuickAccessPosition));
    }

    // D-5: pinning does not bump the version, so an open rename keeps working.
    [Fact]
    public async Task ReplaceQuickAccessDoesNotBumpTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);

        await ReplaceQuickAccessAsync(client, [bebidas.Id]);
        var listed = Assert.Single(await ListAsync(client));
        var renamed = await UpdateAsync(client, bebidas.Id, "Bebidas frías", null, bebidas.Version);

        Assert.Equal(1, listed.Version);
        Assert.Equal(bebidas.UpdatedAt, listed.UpdatedAt);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(1, (await ReadCategoryAsync(renamed)).PosQuickAccessPosition);
    }

    [Fact]
    public async Task ReplaceQuickAccessWithADuplicateIsRejectedAndChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var a = await CreateAsync(client, "A", null);
        var b = await CreateAsync(client, "B", null);
        await ReplaceQuickAccessAsync(client, [b.Id]);

        await AssertProblemAsync(
            await ReplaceQuickAccessAsync(client, [a.Id, a.Id]),
            HttpStatusCode.UnprocessableEntity,
            "catalog.category.quick_access_duplicate");
        Assert.Equal(
            [("A", (int?)null), ("B", 1)],
            (await ListAsync(client)).Select(item => (item.Name, item.PosQuickAccessPosition)));
    }

    [Fact]
    public async Task ReplaceQuickAccessWithAnUnknownOrForeignIdIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);
        var a = await CreateAsync(client, "A", null);
        var foreign = await CreateAsync(other, "Ajena", null, OtherTenantId);

        foreach (var id in new[] { Guid.CreateVersion7(), foreign.Id })
        {
            await AssertProblemAsync(
                await ReplaceQuickAccessAsync(client, [a.Id, id]),
                HttpStatusCode.UnprocessableEntity,
                "catalog.category.not_found");
        }

        Assert.Null(Assert.Single(await ListAsync(client)).PosQuickAccessPosition);
        Assert.Null(Assert.Single(await ListAsync(other, OtherTenantId)).PosQuickAccessPosition);
    }

    [Fact]
    public async Task ReplaceQuickAccessWithoutTheListIsAValidationError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/categories/pos-quick-access",
            new { },
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    // ---- Permissions and audit -----------------------------------------------------------

    [Fact]
    public async Task EveryWriteNeedsTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var reader = CreateClient(factory, SubjectId, TenantId, CatalogPermissions.ProductRead);
        using var intruder = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);
        var bebidas = await CreateAsync(manager, "Bebidas", null);

        foreach (var client in new[] { reader, intruder })
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await UpdateAsync(client, bebidas.Id, "Otra", null, bebidas.Version)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(client, bebidas.Id, bebidas.Version)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ReplaceQuickAccessAsync(client, [bebidas.Id])).StatusCode);
        }

        var listed = Assert.Single(await ListAsync(manager));
        Assert.Equal(("Bebidas", 1L, (int?)null), (listed.Name, listed.Version, listed.PosQuickAccessPosition));
    }

    [Fact]
    public async Task EachWriteRecordsOneAuditEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var bebidas = await CreateAsync(client, "Bebidas", null);

        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(client, bebidas.Id, "Bebidas frías", null, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReplaceQuickAccessAsync(client, [bebidas.Id])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, bebidas.Id, 2)).StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var updated = Assert.Single(await QueryAuditEventsAsync(connection, "catalog.category.updated"));
        var deleted = Assert.Single(await QueryAuditEventsAsync(connection, "catalog.category.deleted"));
        var replaced = Assert.Single(
            await QueryAuditEventsAsync(connection, "catalog.category.pos_quick_access_replaced"));
        Assert.Contains(bebidas.Id.ToString(), updated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(bebidas.Id.ToString(), deleted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TenantId, replaced, StringComparison.OrdinalIgnoreCase);
        Assert.All(new[] { updated, deleted, replaced }, payload =>
            Assert.Contains(SubjectId, payload, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static string CategoryUrl(Guid categoryId, string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/catalog/categories/{categoryId}";

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(code, body, StringComparison.Ordinal);
    }

    private static async Task<CategoryResponse> CreateAsync(
        HttpClient client,
        string name,
        Guid? parentId,
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/categories",
            new { name, parentId },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadCategoryAsync(response);
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name, string code, Guid categoryId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            ProductBodies.Create(name, code, categoryId: categoryId),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>(TestContext.Current.CancellationToken))!.Id;
    }

    private static Task<HttpResponseMessage> UpdateAsync(
        HttpClient client,
        Guid categoryId,
        string name,
        Guid? parentId,
        long? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, CategoryUrl(categoryId))
        {
            Content = JsonContent.Create(new { name, parentId }),
        };
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid categoryId, long? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, CategoryUrl(categoryId));
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> ReplaceQuickAccessAsync(HttpClient client, Guid[] categoryIds) =>
        client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/categories/pos-quick-access",
            new { categoryIds },
            TestContext.Current.CancellationToken);

    private static async Task<CategoryResponse> ReadCategoryAsync(HttpResponseMessage response)
    {
        var category = await response.Content.ReadFromJsonAsync<CategoryResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(category);
        return category;
    }

    private static async Task<IReadOnlyList<CategoryResponse>> ListAsync(HttpClient client, string tenantId = TenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/categories",
            TestContext.Current.CancellationToken);

        // The status is asserted here: a 403 or a 500 deserializes too, with Items null.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CategoriesResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Items;
    }

    private static async Task<IReadOnlyList<string>> QueryAuditEventsAsync(
        NpgsqlConnection connection,
        string action)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text
            FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'action' = @action
            ORDER BY occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("action", action);

        var payloads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
