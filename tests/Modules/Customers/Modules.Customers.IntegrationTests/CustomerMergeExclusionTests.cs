using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-24 §3.3: un cliente fusionado no aparece en listas, buscadores, audiencias, anomalías ni
/// coincidencias; su ficha sigue leyéndose y dice a dónde fue (<c>mergedInto</c>). El listado suma <c>createdAt</c> (B4).</summary>
public sealed class CustomerMergeExclusionTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private const string SurvivorNit = "900.222.222-2";
    private const string MergedNit = "830.111.111-1";
    private const string DefaultE164 = "+573109352187"; // el teléfono por defecto de NewCustomerBody

    private sealed record Pair(Guid Survivor, Guid Merged, HttpClient Client, string ConnectionString);

    // Quien queda y el fusionado comparten teléfono (el de NewCustomerBody) y, después de fusionar, también documento
    // (el índice parcial lo permite): así los dos caen en los mismos grupos de anomalías.
    private static async Task<Pair> ArrangeAsync(QepApiFactory factory, string connectionString, bool shareDocument = true)
    {
        var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var survivor = await CreateCustomerAsync(client, city.CityId, classification.Id, "Quien Queda", SurvivorNit);
        var merged = await CreateCustomerAsync(client, city.CityId, classification.Id, "El Fusionado", MergedNit);
        await MarkMergedAsync(connectionString, merged.Id, survivor.Id);
        // La ficha fusionada puede seguir activa para las pruebas de lectura: el filtro que se prueba es el de fusión.
        await ExecuteAsync(connectionString, "UPDATE customers.customers SET is_active = true WHERE id = @id", ("id", merged.Id));
        if (shareDocument)
        {
            await ExecuteAsync(
                connectionString,
                "UPDATE customers.customers SET identification_number = @number WHERE id = @id",
                ("number", SurvivorNit), ("id", merged.Id));
        }

        return new Pair(survivor.Id, merged.Id, client, connectionString);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    [Fact]
    public async Task AMergedCustomerIsNotListedNorSearched()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString(), shareDocument: false);
        using var client = pair.Client;

        var all = await ListAsync(client, string.Empty);
        Assert.Equal(pair.Survivor, Assert.Single(all.Items).Id);
        Assert.Equal(1, all.Total);

        // Por nombre y por documento, en el criterio combinado y en las cajas separadas. El CUC no se afirma aquí: la
        // redirección de la búsqueda por CUC hacia el que quedó es de FC-08.
        foreach (var query in new[] { "?search=Fusionado", $"?search={MergedNit}", "?name=Fusionado", $"?identificationNumber={MergedNit}" })
        {
            Assert.Empty((await ListAsync(client, query)).Items);
        }
    }

    [Fact]
    public async Task TheRepositoryReadsLeaveTheMergedOut()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString(), shareDocument: false);
        using var client = pair.Client;
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var ct = TestContext.Current.CancellationToken;

        var exported = await repository.ListForExportAsync(Tenant, null, null, null, null, 0, 100, ct);
        var selectable = await repository.SearchSelectableAsync(Tenant, null, 50, ct);
        var selectableByName = await repository.SearchSelectableAsync(Tenant, "Fusionado", 50, ct);

        Assert.Equal(pair.Survivor, Assert.Single(exported).Id.Value);
        Assert.Equal(pair.Survivor, Assert.Single(selectable).Id.Value);
        Assert.Empty(selectableByName);
    }

    [Fact]
    public async Task AMergedCustomerIsNotInACampaignAudience()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = pair.Client;
        var tag = Guid.NewGuid();
        await ExecuteAsync(
            pair.ConnectionString,
            "INSERT INTO customers.tags (id, tenant_id, name, color, version, created_at, updated_at) VALUES (@tag, @tenant, 'Masivo', 'blue', 1, now(), now())",
            ("tag", tag), ("tenant", Tenant));
        await ExecuteAsync(
            pair.ConnectionString,
            """
            INSERT INTO customers.customer_tags (customer_id, tag_id, tenant_id, assigned_by_user_id, assigned_at)
            VALUES (@merged, @tag, @tenant, @user, now())
            """,
            ("merged", pair.Merged), ("tag", tag), ("tenant", Tenant), ("user", Guid.Empty));
        await using var scope = factory.Services.CreateAsyncScope();
        var audience = scope.ServiceProvider.GetRequiredService<ICustomerAudienceRepository>();
        var ct = TestContext.Current.CancellationToken;

        var byId = await audience.ListAudienceBatchAsync(Tenant, [], [pair.Survivor, pair.Merged], null, 50, ct);
        var byTag = await audience.ListAudienceBatchAsync(Tenant, [tag], [], null, 50, ct);
        var countById = await audience.CountAudienceAsync(Tenant, [], [pair.Survivor, pair.Merged], ct);
        var countByTag = await audience.CountAudienceAsync(Tenant, [tag], [], ct);

        Assert.Equal(pair.Survivor, Assert.Single(byId).CustomerId);
        Assert.Empty(byTag);
        Assert.Equal(1, countById.Total);
        Assert.Equal(0, countByTag.Total);
    }

    [Fact]
    public async Task AMergedCustomerLeavesTheAnomaliesAndTheMatches()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = pair.Client;
        var ct = TestContext.Current.CancellationToken;

        foreach (var kind in new[] { "SharedPhone", "SharedIdentificationNumber" })
        {
            var anomalies = await BodyAsync(await client.GetAsync($"{CustomersUrl()}/anomalies?kind={kind}", ct));
            Assert.Equal(0, anomalies.GetProperty("total").GetInt32());
            Assert.Empty(anomalies.GetProperty("groups").EnumerateArray());
        }

        // El fusionado tiene el mismo documento que el que queda: sin el filtro, aparecería como coincidencia.
        var matches = await BodyAsync(await client.GetAsync(
            $"{CustomersUrl()}/identification-matches?number={SurvivorNit}&type=NIT&excludeCustomerId={pair.Survivor}", ct));
        Assert.Empty(matches.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task AMergedCustomerIsNotASharedPhoneCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = pair.Client;
        await using var scope = factory.Services.CreateAsyncScope();

        var listed = await scope.ServiceProvider.GetRequiredService<ICustomerPhoneDirectory>()
            .ListByPhoneAsync(Tenant, DefaultE164, 10, TestContext.Current.CancellationToken);

        Assert.Equal(pair.Survivor, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task TheDetailOfAMergedCustomerSaysWhereItWent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString(), shareDocument: false);
        using var client = pair.Client;

        var response = await client.GetAsync($"{CustomersUrl()}/{pair.Merged}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        var mergedInto = body.GetProperty("mergedInto");
        Assert.Equal(pair.Survivor, mergedInto.GetProperty("id").GetGuid());
        Assert.Equal("Quien Queda", mergedInto.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ANotMergedDetailCarriesMergedIntoNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString(), shareDocument: false);
        using var client = pair.Client;

        var body = await BodyAsync(await client.GetAsync($"{CustomersUrl()}/{pair.Survivor}", TestContext.Current.CancellationToken));

        // C6: la propiedad viaja siempre; ausente no es lo mismo que null para quien dibuja la ficha.
        Assert.True(body.TryGetProperty("mergedInto", out var mergedInto));
        Assert.Equal(JsonValueKind.Null, mergedInto.ValueKind);
    }

    [Fact]
    public async Task TheListItemCarriesCreatedAt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString(), shareDocument: false);
        using var client = pair.Client;
        var detail = await GetAsync(client, pair.Survivor);

        var raw = await BodyAsync(await client.GetAsync(CustomersUrl(), TestContext.Current.CancellationToken));
        var item = Assert.Single(raw.GetProperty("items").EnumerateArray());
        var page = await ListAsync(client, string.Empty);

        Assert.True(item.TryGetProperty("createdAt", out _));
        Assert.Equal(detail.CreatedAt, Assert.Single(page.Items).CreatedAt);
    }

    [Fact]
    public void TheMergeResponseMirrorsTheCustomerResponse()
    {
        // A6: CustomerMergeResponse copia CustomerResponse campo por campo; un campo nuevo en uno y no en el otro se perdería
        // en silencio en la fusión.
        var customer = typeof(CustomerResponse).GetProperties().Select(property => property.Name).Order().ToArray();
        var merge = typeof(CustomerMergeResponse).GetProperties().Where(property => property.Name != "DroppedTags")
            .Select(property => property.Name).Order().ToArray();

        Assert.Contains("MergedInto", customer);
        Assert.Equal(customer, merge);
    }
}
