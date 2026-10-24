using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Spec 2026-10-24 §3.2: <c>POST /customers/{survivorId}/merge</c>. El contrato con el frontend (B3): la respuesta es el
/// <c>CustomerResponse</c> del que queda en el mismo objeto más <c>droppedTags: [{ id, name }]</c>, siempre presente y sin
/// versión. <c>If-Match</c> con <c>survivor.version</c> de la vista previa: 428 sin él, 412 con una vieja. Todo pasa en una
/// transacción: un error a mitad deshace también lo de Customers.
/// </summary>
public sealed class CustomerMergeApiTests
{
    private const string SharedNumber = "92-1396892";
    private const string MergedAction = CustomerAuditActions.Merged;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record Arranged(
        string ConnectionString, HttpClient Client, Guid CityId, Guid ClassificationId, Guid Survivor, Guid Merged);

    private static HttpClient CreateMerger(QepApiFactory factory) =>
        CreateClient(
            factory, SubjectId, TenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerManage,
            CustomersPermissions.CustomerMerge,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);

    private static async Task<Guid> CreateAsync(
        HttpClient client, Guid cityId, Guid classificationId, string name, string type, string number)
    {
        var response = await client.PostAsJsonAsync(
            CustomersUrl(), NewCustomerBody(cityId, classificationId, name, type, number), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken))!.Id;
    }

    private static async Task<Arranged> ArrangeAsync(QepApiFactory factory, string connectionString)
    {
        var client = CreateMerger(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var survivor = await CreateAsync(client, city.CityId, classification.Id, "GYSELYS", "NIT", SharedNumber);
        var merged = await CreateAsync(client, city.CityId, classification.Id, "GISELY", "CE", SharedNumber);
        return new Arranged(connectionString, client, city.CityId, classification.Id, survivor, merged);
    }

    private static string MergeUrl(Guid survivor, string tenantId = TenantId) => $"{CustomersUrl(tenantId)}/{survivor}/merge";

    private static string PreviewUrl(Guid survivor, IEnumerable<Guid> merged) =>
        $"{CustomersUrl()}/{survivor}/merge-preview?" + string.Join('&', merged.Select(id => $"mergedCustomerIds={id}"));

    /// <summary>La versión que el frontend manda en <c>If-Match</c>: la de la vista previa (B1).</summary>
    private static async Task<long> PreviewVersionAsync(HttpClient client, Guid survivor, params Guid[] merged)
    {
        var response = await client.GetAsync(PreviewUrl(survivor, merged), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("survivor").GetProperty("version").GetInt64();
    }

    private static async Task<HttpResponseMessage> PostMergeAsync(
        HttpClient client, Guid survivor, string? ifMatch, IEnumerable<Guid> merged, string tenantId = TenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MergeUrl(survivor, tenantId))
        {
            Content = JsonContent.Create(new { mergedCustomerIds = merged.ToArray() }),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> MergeAsync(HttpClient client, Guid survivor, params Guid[] merged)
    {
        var version = await PreviewVersionAsync(client, survivor, merged);
        return await BodyAsync(await PostMergeAsync(client, survivor, $"\"{version}\"", merged), HttpStatusCode.OK);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql) =>
        long.Parse((await TextAsync(connectionString, sql))!, CultureInfo.InvariantCulture);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static Task<long> VersionAsync(string connectionString, Guid customer) =>
        ScalarAsync(connectionString, $"SELECT version FROM customers.customers WHERE id = '{customer}'");

    private static Task<string?> MergedIntoAsync(string connectionString, Guid customer) =>
        TextAsync(connectionString, $"SELECT merged_into_id FROM customers.customers WHERE id = '{customer}'");

    private static Task<string?> BsuidAsync(string connectionString, Guid customer) =>
        TextAsync(connectionString, $"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{customer}'");

    private static Task SetBsuidAsync(string connectionString, Guid customer, string bsuid) =>
        ExecuteAsync(connectionString, $"UPDATE customers.customers SET whatsapp_user_id = '{bsuid}' WHERE id = '{customer}'");

    private static async Task PostNoteAsync(HttpClient client, Guid customerId, string body)
    {
        var response = await client.PostAsJsonAsync($"{CustomersUrl()}/{customerId}/notes", new { body }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<Guid> PostTagAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync($"{CustomersUrl()}/tags", new { name, color = "blue" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    private static async Task SetTagsAsync(HttpClient client, Guid customerId, IEnumerable<Guid> tagIds)
    {
        var response = await client.PutAsJsonAsync($"{CustomersUrl()}/{customerId}/tags", new { tagIds }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Lo que una fusión fallida no puede dejar: versiones, fusionados, BSUID, notas, direcciones, etiquetas y auditoría.
    private const string State =
        "SELECT (SELECT coalesce(sum(version), 0) FROM customers.customers) || '|' "
        + "|| (SELECT count(*) FROM customers.customers WHERE merged_into_id IS NOT NULL) || '|' "
        + "|| (SELECT string_agg(id || ':' || coalesce(whatsapp_user_id, '-') || ':' || coalesce(identification_number, '-'), ',' ORDER BY id) FROM customers.customers) || '|' "
        + "|| (SELECT coalesce(string_agg(id || ':' || customer_id, ',' ORDER BY id), '') FROM customers.customer_notes) || '|' "
        + "|| (SELECT coalesce(string_agg(id || ':' || customer_id || ':' || is_principal, ',' ORDER BY id), '') FROM customers.customer_addresses) || '|' "
        + "|| (SELECT coalesce(string_agg(customer_id || ':' || tag_id, ',' ORDER BY customer_id, tag_id), '') FROM customers.customer_tags) || '|' "
        + "|| (SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = '" + MergedAction + "')";

    [Fact]
    public async Task MergingAnswersTheSurvivorWithDroppedTagsAndMarksTheMerged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;

        var body = await MergeAsync(client, arranged.Survivor, arranged.Merged);

        // B3: los campos de CustomerResponse en el mismo objeto, droppedTags siempre (vacío) y sin versión.
        Assert.Equal(arranged.Survivor, body.GetProperty("id").GetGuid());
        Assert.Equal("GYSELYS", body.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("cuc").GetString()));
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("tags").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("marketingConsent").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("droppedTags").ValueKind);
        Assert.Empty(body.GetProperty("droppedTags").EnumerateArray());
        Assert.False(body.TryGetProperty("version", out _));
        Assert.False(body.TryGetProperty("survivor", out _));
        var typed = body.Deserialize<CustomerMergeResponse>(WebJson)!;
        Assert.Equal(arranged.Survivor, typed.Id);
        Assert.Empty(typed.DroppedTags);

        var mergedDetail = await GetAsync(client, arranged.Merged);
        Assert.False(mergedDetail.IsActive);
        Assert.Equal(arranged.Survivor.ToString(), await MergedIntoAsync(arranged.ConnectionString, arranged.Merged));
        Assert.Equal(1, await ScalarAsync(
            arranged.ConnectionString, $"SELECT count(*) FROM customers.customers WHERE id = '{arranged.Merged}' AND merged_at IS NOT NULL"));
        Assert.Null(await MergedIntoAsync(arranged.ConnectionString, arranged.Survivor));
    }

    [Fact]
    public async Task EmptyFieldsAreFilledAndNothingIsOverwritten()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var mergedBody = JsonSerializer.SerializeToNode(
            NewCustomerBody(arranged.CityId, arranged.ClassificationId, "GISELY", "CE", SharedNumber, email: "gisely@otro.co"))!.AsObject();
        mergedBody["businessName"] = "Gisely S.A.S.";
        (await client.PutAsJsonAsync($"{CustomersUrl()}/{arranged.Merged}", mergedBody, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var before = await VersionAsync(arranged.ConnectionString, arranged.Survivor);

        var body = await MergeAsync(client, arranged.Survivor, arranged.Merged);

        Assert.Equal("Gisely S.A.S.", body.GetProperty("businessName").GetString());
        Assert.Equal("compras@verde.co", body.GetProperty("email").GetString());
        Assert.Equal("GYSELYS", body.GetProperty("name").GetString());
        Assert.Equal("NIT", body.GetProperty("identificationType").GetString());
        Assert.Equal(SharedNumber, body.GetProperty("identificationNumber").GetString());
        Assert.Equal(before + 1, await VersionAsync(arranged.ConnectionString, arranged.Survivor));
    }

    [Fact]
    public async Task TheSurvivorWithTheSmallerIdTakesTheMergedBsuid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        // Review Focus 1: EF ordena los UPDATE de un SaveChanges por clave primaria, así que con un solo SaveChanges el del
        // que queda (id menor) iría antes de que el fusionado suelte el BSUID y chocaría con IX_customers_tenant_whatsapp_user_id.
        var survivor = arranged.Survivor.CompareTo(arranged.Merged) < 0 ? arranged.Survivor : arranged.Merged;
        var merged = survivor == arranged.Survivor ? arranged.Merged : arranged.Survivor;
        Assert.True(survivor.CompareTo(merged) < 0);
        await SetBsuidAsync(arranged.ConnectionString, merged, "CO.1349120865530274");

        await MergeAsync(client, survivor, merged);

        Assert.Equal("CO.1349120865530274", await BsuidAsync(arranged.ConnectionString, survivor));
        Assert.Null(await BsuidAsync(arranged.ConnectionString, merged));
    }

    [Fact]
    public async Task AMergedBsuidStaysWhenTheSurvivorHasOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        await SetBsuidAsync(arranged.ConnectionString, arranged.Survivor, "CO.1111111111111111");
        await SetBsuidAsync(arranged.ConnectionString, arranged.Merged, "CO.2222222222222222");

        await MergeAsync(client, arranged.Survivor, arranged.Merged);

        Assert.Equal("CO.1111111111111111", await BsuidAsync(arranged.ConnectionString, arranged.Survivor));
        Assert.Equal("CO.2222222222222222", await BsuidAsync(arranged.ConnectionString, arranged.Merged));
    }

    [Fact]
    public async Task TheMarketingOptOutIsInheritedWithItsSourceAndDate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        await ExecuteAsync(
            arranged.ConnectionString,
            "UPDATE customers.customers SET marketing_opt_out = true, marketing_opt_out_source = 'meta', "
            + $"marketing_opt_out_at = '2026-03-04T05:06:07Z' WHERE id = '{arranged.Merged}'");

        var body = await MergeAsync(client, arranged.Survivor, arranged.Merged);

        var consent = body.GetProperty("marketingConsent");
        Assert.True(consent.GetProperty("optedOut").GetBoolean());
        Assert.Equal("meta", consent.GetProperty("source").GetString());
        Assert.Equal(
            new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
            consent.GetProperty("optedOutAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task NotesAndAddressesMoveAndThePrincipalStaysTheSurvivors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        await PostNoteAsync(client, arranged.Survivor, "del que queda");
        await PostNoteAsync(client, arranged.Merged, "del fusionado uno");
        await PostNoteAsync(client, arranged.Merged, "del fusionado dos");
        var survivorPrincipal = (await AddAddressAsync(client, arranged.Survivor, arranged.CityId, isPrincipal: true, name: "Sede"))
            .Addresses.Single(address => address.IsPrincipal).Id;
        var mergedAddresses = (await AddAddressAsync(client, arranged.Merged, arranged.CityId, isPrincipal: true, name: "Bodega"))
            .Addresses.Select(address => address.Id).ToArray();
        var survivorAddresses = (await GetAsync(client, arranged.Survivor)).Addresses.Count;

        var body = await MergeAsync(client, arranged.Survivor, arranged.Merged);

        // La respuesta trae la libreta que movió el SQL (FC-04 duda 1: se relee después de MoveOwnedAsync).
        var addresses = body.GetProperty("addresses").EnumerateArray().ToArray();
        Assert.Equal(survivorAddresses + mergedAddresses.Length, addresses.Length);
        Assert.All(mergedAddresses, id => Assert.Contains(addresses, address => address.GetProperty("id").GetGuid() == id));
        var principal = Assert.Single(addresses, address => address.GetProperty("isPrincipal").GetBoolean());
        Assert.Equal(survivorPrincipal, principal.GetProperty("id").GetGuid());
        Assert.Equal(3, await ScalarAsync(
            arranged.ConnectionString, $"SELECT count(*) FROM customers.customer_notes WHERE customer_id = '{arranged.Survivor}'"));
        Assert.Equal(0, await ScalarAsync(
            arranged.ConnectionString, $"SELECT count(*) FROM customers.customer_notes WHERE customer_id = '{arranged.Merged}'"));
        Assert.Empty((await GetAsync(client, arranged.Merged)).Addresses);
    }

    [Fact]
    public async Task TagsAreUnitedUpToTheCapAndTheRestComeBackAsDroppedTags()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var tags = new List<Guid>();
        for (var index = 0; index < 22; index++)
        {
            tags.Add(await PostTagAsync(client, $"Etiqueta {index:00}"));
        }

        // El que queda lleva 19; el fusionado, una repetida (la PK (customer_id, tag_id) no puede chocar) y tres nuevas:
        // cabe una, en el orden de asignación (la misma fecha, así que por id) y las otras dos vuelven en droppedTags.
        await SetTagsAsync(client, arranged.Survivor, tags.Take(19));
        await SetTagsAsync(client, arranged.Merged, [tags[0], tags[19], tags[20], tags[21]]);
        var fresh = new[] { tags[19], tags[20], tags[21] }.Order().ToArray();

        var body = await MergeAsync(client, arranged.Survivor, arranged.Merged);

        var dropped = body.GetProperty("droppedTags").EnumerateArray().ToArray();
        Assert.Equal(fresh[1..], dropped.Select(tag => tag.GetProperty("id").GetGuid()));
        Assert.All(dropped, tag => Assert.StartsWith("Etiqueta ", tag.GetProperty("name").GetString(), StringComparison.Ordinal));
        Assert.All(dropped, tag => Assert.Equal(2, tag.EnumerateObject().Count()));
        var survivorTags = body.GetProperty("tags").EnumerateArray().Select(tag => tag.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(20, survivorTags.Length);
        Assert.Contains(fresh[0], survivorTags);
        Assert.Equal(20, await ScalarAsync(
            arranged.ConnectionString, $"SELECT count(*) FROM customers.customer_tags WHERE customer_id = '{arranged.Survivor}'"));
        Assert.Equal(0, await ScalarAsync(
            arranged.ConnectionString, $"SELECT count(*) FROM customers.customer_tags WHERE customer_id = '{arranged.Merged}'"));
    }

    [Fact]
    public async Task AChainIsRewrittenToTheNewSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var chained = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "ENCADENADO", "CC", "8.888.888");

        // C en B por la ruta, después B en A: C tiene que apuntar a A (D-F2). El segundo If-Match va en la forma débil.
        await MergeAsync(client, arranged.Merged, chained);
        var version = await PreviewVersionAsync(client, arranged.Survivor, arranged.Merged);
        await BodyAsync(
            await PostMergeAsync(client, arranged.Survivor, $"W/\"{version}\"", [arranged.Merged]), HttpStatusCode.OK);

        Assert.Equal(arranged.Survivor.ToString(), await MergedIntoAsync(arranged.ConnectionString, arranged.Merged));
        Assert.Equal(arranged.Survivor.ToString(), await MergedIntoAsync(arranged.ConnectionString, chained));
    }

    [Fact]
    public async Task TheMergeIsAuditedOnEveryCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var second = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "OTRO", "CC", "1.020.304");

        await MergeAsync(client, arranged.Survivor, arranged.Merged, second);

        var audited = await TextAsync(
            arranged.ConnectionString,
            $"SELECT string_agg(payload->>'resourceId', ',' ORDER BY payload->>'resourceId') FROM platform.outbox_messages "
            + $"WHERE payload->>'action' = '{MergedAction}'");
        Assert.Equal(
            string.Join(',', new[] { arranged.Survivor, arranged.Merged, second }.Select(id => id.ToString()).Order(StringComparer.Ordinal)),
            audited);
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndAStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var stale = await PreviewVersionAsync(client, arranged.Survivor, arranged.Merged);
        var update = JsonSerializer.SerializeToNode(
            NewCustomerBody(arranged.CityId, arranged.ClassificationId, "GYSELYS", "NIT", SharedNumber))!.AsObject();
        update["businessName"] = "Gyselys S.A.S.";
        (await client.PutAsJsonAsync($"{CustomersUrl()}/{arranged.Survivor}", update, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var before = await TextAsync(arranged.ConnectionString, State);

        var missing = await BodyAsync(
            await PostMergeAsync(client, arranged.Survivor, null, [arranged.Merged]), HttpStatusCode.PreconditionRequired);
        var conflict = await BodyAsync(
            await PostMergeAsync(client, arranged.Survivor, $"\"{stale}\"", [arranged.Merged]), HttpStatusCode.PreconditionFailed);

        Assert.Equal("precondition.if_match_required", missing.GetProperty("code").GetString());
        Assert.Equal("concurrency.conflict", conflict.GetProperty("code").GetString());
        Assert.Equal(before, await TextAsync(arranged.ConnectionString, State));
    }

    [Fact]
    public async Task ASecondMergeWithTheOldVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var second = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "OTRO", "CC", "1.020.304");
        // Review Focus 5: las dos pestañas leyeron la misma vista previa. La primera no llena nada y aun así sube la versión.
        var version = await PreviewVersionAsync(client, arranged.Survivor, arranged.Merged);

        await BodyAsync(await PostMergeAsync(client, arranged.Survivor, $"\"{version}\"", [arranged.Merged]), HttpStatusCode.OK);
        var conflict = await BodyAsync(
            await PostMergeAsync(client, arranged.Survivor, $"\"{version}\"", [second]), HttpStatusCode.PreconditionFailed);

        Assert.Equal("concurrency.conflict", conflict.GetProperty("code").GetString());
        Assert.Null(await MergedIntoAsync(arranged.ConnectionString, second));
        Assert.Equal(version + 1, await VersionAsync(arranged.ConnectionString, arranged.Survivor));
    }

    [Fact]
    public async Task EachRuleIs422WithItsCodeAndNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var ct = TestContext.Current.CancellationToken;
        var connectionString = arranged.ConnectionString;

        var gone = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "YA FUSIONADO", "CC", "5.555.555");
        await MarkMergedAsync(connectionString, gone, arranged.Merged);
        var inactive = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "INACTIVO", "CC", "6.666.666");
        (await client.PostAsync($"{CustomersUrl()}/{inactive}/deactivate", null, ct)).EnsureSuccessStatusCode();
        Guid incomplete;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
            incomplete = (await directory.EnsureAsync(
                Guid.Parse(TenantId), new WhatsAppContact("CO.1349120865530274", "+573001234567", "Laura", null), ct)).CustomerId;
        }

        var cases = new (Guid Survivor, Guid[] Merged, string Code)[]
        {
            (arranged.Survivor, [Guid.CreateVersion7()], "customers.merge.customer_not_found"),
            (arranged.Survivor, [gone], "customers.merge.already_merged"),
            (gone, [arranged.Survivor], "customers.merge.already_merged"),
            (inactive, [arranged.Merged], "customers.merge.survivor_inactive"),
            (incomplete, [arranged.Merged], "customers.merge.survivor_incomplete"),
        };
        var before = await TextAsync(connectionString, State);

        foreach (var (survivor, merged, code) in cases)
        {
            var version = await VersionAsync(connectionString, survivor);
            var body = await BodyAsync(
                await PostMergeAsync(client, survivor, $"\"{version}\"", merged), HttpStatusCode.UnprocessableEntity);
            Assert.Equal(code, body.GetProperty("code").GetString());
        }

        Assert.Equal(before, await TextAsync(connectionString, State));
    }

    [Fact]
    public async Task InvalidMergedIdsAre422OnMergedCustomerIds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var version = $"\"{await VersionAsync(arranged.ConnectionString, arranged.Survivor)}\"";
        var invalid = new Guid[][]
        {
            [],
            [arranged.Survivor],
            [arranged.Merged, arranged.Merged],
            [Guid.Empty],
            [.. Enumerable.Range(0, CustomerMergeLimits.MaxMerged + 1).Select(_ => Guid.CreateVersion7())],
        };

        foreach (var ids in invalid)
        {
            var response = await PostMergeAsync(client, arranged.Survivor, version, ids);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(["MergedCustomerIds"], await ValidationFieldsAsync(response));
        }

        Assert.Null(await MergedIntoAsync(arranged.ConnectionString, arranged.Merged));
    }

    [Fact]
    public async Task AMergedIdFromAnotherTenantIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        using var stranger = CreateClient(
            factory, OtherSubjectId, OtherTenantId,
            CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage, CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);
        var foreignClassification = await CreateClassificationAsync(stranger, tenantId: OtherTenantId);
        var city = await EnsureCityAsync(stranger);
        var foreignResponse = await stranger.PostAsJsonAsync(
            CustomersUrl(OtherTenantId),
            NewCustomerBody(city.CityId, foreignClassification.Id, "AJENO", "CC", "7.777.777"),
            TestContext.Current.CancellationToken);
        foreignResponse.EnsureSuccessStatusCode();
        var foreign = (await foreignResponse.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken))!.Id;
        var version = await VersionAsync(arranged.ConnectionString, arranged.Survivor);

        var body = await BodyAsync(
            await PostMergeAsync(client, arranged.Survivor, $"\"{version}\"", [foreign]), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("customers.merge.customer_not_found", body.GetProperty("code").GetString());
        Assert.Null(await MergedIntoAsync(arranged.ConnectionString, foreign));
        Assert.Equal(version, await VersionAsync(arranged.ConnectionString, arranged.Survivor));
    }

    [Fact]
    public async Task AnotherTenantIs403AndWithoutMergeIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        using var withoutMerge = CreateClient(
            factory, SubjectId, TenantId, CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage);
        var version = $"\"{await VersionAsync(arranged.ConnectionString, arranged.Survivor)}\"";

        var otherTenant = await PostMergeAsync(client, arranged.Survivor, version, [arranged.Merged], OtherTenantId);
        var noPermission = await PostMergeAsync(withoutMerge, arranged.Survivor, version, [arranged.Merged]);

        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);
        Assert.Null(await MergedIntoAsync(arranged.ConnectionString, arranged.Merged));
    }

    [Fact]
    public async Task AnErrorHalfwayUndoesEverythingIncludingCustomers()
    {
        await using var database = await StartDatabaseAsync();
        // El último paso (lo de los otros módulos) falla: para entonces los dos SaveChanges y el SQL de notas y direcciones
        // ya corrieron dentro de la transacción. Nada de eso puede quedar.
        using var factory = new QepApiFactory(
            database.GetConnectionString(),
            services => services.AddScoped<ICustomerMergeReferences, FailingMergeReferences>());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        await SetBsuidAsync(arranged.ConnectionString, arranged.Merged, "CO.1349120865530274");
        await PostNoteAsync(client, arranged.Merged, "del fusionado");
        await AddAddressAsync(client, arranged.Merged, arranged.CityId);
        await SetTagsAsync(client, arranged.Merged, [await PostTagAsync(client, "VIP")]);
        var version = await VersionAsync(arranged.ConnectionString, arranged.Survivor);
        var before = await TextAsync(arranged.ConnectionString, State);

        var response = await PostMergeAsync(client, arranged.Survivor, $"\"{version}\"", [arranged.Merged]);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await TextAsync(arranged.ConnectionString, State));
        Assert.Equal("CO.1349120865530274", await BsuidAsync(arranged.ConnectionString, arranged.Merged));
        Assert.Null(await MergedIntoAsync(arranged.ConnectionString, arranged.Merged));
    }

    private sealed class FailingMergeReferences : ICustomerMergeReferences
    {
        public Task MoveAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated failure moving the references of another module.");

        public Task<CustomerMergeReferenceCounts> CountAsync(
            Guid tenantId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            Task.FromResult(new CustomerMergeReferenceCounts(0, 0, 0, 0, 0));
    }
}
