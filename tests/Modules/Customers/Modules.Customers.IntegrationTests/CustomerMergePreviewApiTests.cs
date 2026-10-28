using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-24 §3.4: la vista previa de la fusión cuenta lo que pasaría y no escribe nada. El contrato con el
/// frontend (B1, B2, B4): <c>survivor.version</c> entero, <c>moves</c> con las ocho clases también en cero, <c>filledFields</c>
/// por nombre y <c>documentsDiffer</c> sobre el número normalizado.</summary>
public sealed class CustomerMergePreviewApiTests
{
    private const string SharedNumber = "92-1396892";
    private static readonly string[] EveryMove =
        ["Quotations", "Orders", "Conversations", "PosSales", "Deals", "Notes", "Addresses", "Tags"];

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
        HttpClient client, Guid cityId, Guid classificationId, string name, string type, string number, string? businessName = null)
    {
        var body = JsonSerializer.SerializeToNode(NewCustomerBody(cityId, classificationId, name, type, number))!.AsObject();
        if (businessName is not null)
        {
            body["businessName"] = businessName;
        }

        var response = await client.PostAsJsonAsync(CustomersUrl(), body, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken))!.Id;
    }

    private static async Task<Arranged> ArrangeAsync(QepApiFactory factory, string connectionString, string mergedNumber = SharedNumber)
    {
        var client = CreateMerger(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var survivor = await CreateAsync(client, city.CityId, classification.Id, "GYSELYS", "NIT", SharedNumber);
        var merged = await CreateAsync(client, city.CityId, classification.Id, "GISELY", "CE", mergedNumber);
        return new Arranged(connectionString, client, city.CityId, classification.Id, survivor, merged);
    }

    private static string PreviewUrl(Guid survivor, IEnumerable<Guid> merged, string tenantId = TenantId) =>
        $"{CustomersUrl(tenantId)}/{survivor}/merge-preview?" + string.Join('&', merged.Select(id => $"mergedCustomerIds={id}"));

    private static async Task<JsonElement> PreviewAsync(HttpClient client, Guid survivor, params Guid[] merged)
    {
        var response = await client.GetAsync(PreviewUrl(survivor, merged), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static int Move(JsonElement preview, string kind) =>
        preview.GetProperty("moves").EnumerateArray().Single(move => move.GetProperty("kind").GetString() == kind).GetProperty("count").GetInt32();

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

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

    [Fact]
    public async Task ThePreviewCarriesBothPartiesTheVersionAndEveryMoveEvenAtZero()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        await PostNoteAsync(client, arranged.Merged, "primera");
        await PostNoteAsync(client, arranged.Merged, "segunda");
        await AddAddressAsync(client, arranged.Merged, arranged.CityId);
        var tag = await PostTagAsync(client, "VIP");
        await SetTagsAsync(client, arranged.Merged, [tag]);
        var mergedDetail = await GetAsync(client, arranged.Merged);

        var preview = await PreviewAsync(client, arranged.Survivor, arranged.Merged);

        var survivor = preview.GetProperty("survivor");
        Assert.Equal(arranged.Survivor, survivor.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Number, survivor.GetProperty("version").ValueKind);
        Assert.Equal(1, survivor.GetProperty("version").GetInt64());
        Assert.Equal("NIT", survivor.GetProperty("identificationType").GetString());
        Assert.True(survivor.GetProperty("isComplete").GetBoolean());
        Assert.True(survivor.GetProperty("isActive").GetBoolean());
        var merged = Assert.Single(preview.GetProperty("merged").EnumerateArray());
        Assert.Equal(arranged.Merged, merged.GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(merged.GetProperty("cuc").GetString()));
        Assert.Equal("CE", merged.GetProperty("identificationType").GetString());
        Assert.Equal(JsonValueKind.Number, merged.GetProperty("version").ValueKind);

        var moves = preview.GetProperty("moves").EnumerateArray().ToArray();
        Assert.Equal(EveryMove, moves.Select(move => move.GetProperty("kind").GetString()));
        Assert.Equal(2, Move(preview, "Notes"));
        Assert.Equal(mergedDetail.Addresses.Count, Move(preview, "Addresses"));
        Assert.True(Move(preview, "Addresses") >= 1);
        Assert.Equal(1, Move(preview, "Tags"));
        foreach (var kind in new[] { "Quotations", "Orders", "Conversations", "PosSales", "Deals" })
        {
            Assert.Equal(0, Move(preview, kind));
        }
    }

    [Fact]
    public async Task FilledFieldsSaysWhatTheSurvivorGains()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{arranged.Merged}",
            JsonSerializer.SerializeToNode(NewCustomerBody(arranged.CityId, arranged.ClassificationId, "GISELY", "CE", SharedNumber))!
                .AsObject().Also(body => body["businessName"] = "Gisely S.A.S."),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var preview = await PreviewAsync(client, arranged.Survivor, arranged.Merged);

        Assert.Equal(["BusinessName"], preview.GetProperty("filledFields").EnumerateArray().Select(field => field.GetString()));
    }

    [Fact]
    public async Task DocumentsDifferIsFalseForTheSameNumberAndTrueForAnother()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var other = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "OTRO", "CC", "1.020.304");

        // El mismo número con otro tipo y otra puntuación se normaliza igual: no difiere.
        var same = await PreviewAsync(client, arranged.Survivor, arranged.Merged);
        var different = await PreviewAsync(client, arranged.Survivor, arranged.Merged, other);

        Assert.False(same.GetProperty("documentsDiffer").GetBoolean());
        Assert.True(different.GetProperty("documentsDiffer").GetBoolean());
    }

    [Fact]
    public async Task ThePreviewWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        await PostNoteAsync(client, arranged.Merged, "una");
        const string State = "SELECT coalesce(sum(version), 0) * 1000 + (SELECT count(*) FROM customers.customer_notes) * 10 "
            + "+ (SELECT count(*) FROM customers.customers WHERE merged_into_id IS NOT NULL) FROM customers.customers";
        var before = await ScalarAsync(arranged.ConnectionString, State);

        await PreviewAsync(client, arranged.Survivor, arranged.Merged);

        Assert.Equal(before, await ScalarAsync(arranged.ConnectionString, State));
        Assert.Equal(0, await ScalarAsync(arranged.ConnectionString, "SELECT count(*) FROM customers.customers WHERE merged_into_id IS NOT NULL"));
    }

    [Fact]
    public async Task EachRuleIs422WithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var ct = TestContext.Current.CancellationToken;

        // already_merged: el fusionado ya apunta a otro.
        var gone = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "YA FUSIONADO", "CC", "5.555.555");
        await MarkMergedAsync(arranged.ConnectionString, gone, arranged.Merged);
        var alreadyMerged = await ProblemAsync(
            await client.GetAsync(PreviewUrl(arranged.Survivor, [gone]), ct), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("customers.merge.already_merged", alreadyMerged.GetProperty("code").GetString());

        // survivor_inactive.
        var inactive = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "INACTIVO", "CC", "6.666.666");
        (await client.PostAsync($"{CustomersUrl()}/{inactive}/deactivate", null, ct)).EnsureSuccessStatusCode();
        var survivorInactive = await ProblemAsync(
            await client.GetAsync(PreviewUrl(inactive, [arranged.Merged]), ct), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("customers.merge.survivor_inactive", survivorInactive.GetProperty("code").GetString());

        // survivor_incomplete: el que queda es incompleto y el fusionado, completo.
        Guid incomplete;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
            incomplete = (await directory.EnsureAsync(
                Guid.Parse(TenantId), new WhatsAppContact("CO.1349120865530274", "+573001234567", "Laura", null), ct)).CustomerId;
        }

        var survivorIncomplete = await ProblemAsync(
            await client.GetAsync(PreviewUrl(incomplete, [arranged.Merged]), ct), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("customers.merge.survivor_incomplete", survivorIncomplete.GetProperty("code").GetString());
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

        var body = await ProblemAsync(
            await client.GetAsync(PreviewUrl(arranged.Survivor, [foreign]), TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("customers.merge.customer_not_found", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("moves", out _));
    }

    [Fact]
    public async Task AnUnknownSurvivorIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;

        var body = await ProblemAsync(
            await client.GetAsync(PreviewUrl(Guid.CreateVersion7(), [arranged.Merged]), TestContext.Current.CancellationToken),
            HttpStatusCode.NotFound);

        Assert.Equal("customers.customer.not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BadIdsAre422ValidationFailedOnMergedCustomerIds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;

        var none = await client.GetAsync($"{CustomersUrl()}/{arranged.Survivor}/merge-preview", TestContext.Current.CancellationToken);
        var itself = await client.GetAsync(PreviewUrl(arranged.Survivor, [arranged.Survivor]), TestContext.Current.CancellationToken);

        Assert.Equal(["MergedCustomerIds"], await ValidationFieldsAsync(none));
        Assert.Equal(["MergedCustomerIds"], await ValidationFieldsAsync(itself));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
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
        var ct = TestContext.Current.CancellationToken;

        var otherTenant = await client.GetAsync(PreviewUrl(arranged.Survivor, [arranged.Merged], OtherTenantId), ct);
        var noPermission = await withoutMerge.GetAsync(PreviewUrl(arranged.Survivor, [arranged.Merged]), ct);

        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);
    }

    [Fact]
    public async Task ThePreviewCountsTheChainAndTheTagsWithinTheCap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;

        // Cadena (ruling B4): C ya apunta a B, y B es el que se fusiona en A. Las notas de C también pasan.
        var chained = await CreateAsync(client, arranged.CityId, arranged.ClassificationId, "ENCADENADO", "CC", "8.888.888");
        await PostNoteAsync(client, arranged.Merged, "de B");
        await PostNoteAsync(client, chained, "de C uno");
        await PostNoteAsync(client, chained, "de C dos");
        await MarkMergedAsync(arranged.ConnectionString, chained, arranged.Merged);

        // Tope: el que queda lleva 19 etiquetas y el fusionado 3 (una repetida): sólo cabe 1.
        var tags = new List<Guid>();
        for (var index = 0; index < 22; index++)
        {
            tags.Add(await PostTagAsync(client, $"Etiqueta {index:00}"));
        }

        await SetTagsAsync(client, arranged.Survivor, tags.Take(19));
        await SetTagsAsync(client, arranged.Merged, [tags[0], tags[20], tags[21]]);

        var preview = await PreviewAsync(client, arranged.Survivor, arranged.Merged);

        Assert.Equal(3, Move(preview, "Notes"));
        Assert.Equal(1, Move(preview, "Tags"));
    }
}

internal static class JsonObjectExtensions
{
    public static System.Text.Json.Nodes.JsonObject Also(this System.Text.Json.Nodes.JsonObject value, Action<System.Text.Json.Nodes.JsonObject> action)
    {
        action(value);
        return value;
    }
}
