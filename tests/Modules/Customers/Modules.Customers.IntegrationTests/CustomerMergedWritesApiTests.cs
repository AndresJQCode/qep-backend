using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-24 §3.1: un cliente fusionado se lee pero no se escribe. Cada escritura responde 422
/// <c>customers.customer.merged</c>; el documento que sólo él tenía queda libre para el que quedó.</summary>
public sealed class CustomerMergedWritesApiTests
{
    private const string MergedCode = "customers.customer.merged";
    private const string MergedNit = "830.111.111-1";

    private static async Task<(Guid Survivor, Guid Merged, HttpClient Client, CityAndClassification Setup)> ArrangeAsync(
        QepApiFactory factory, string connectionString)
    {
        var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var survivor = await CreateCustomerAsync(client, city.CityId, classification.Id, "Quien Queda", "900.222.222-2");
        var merged = await CreateCustomerAsync(client, city.CityId, classification.Id, "El Fusionado", MergedNit);
        await MarkMergedAsync(connectionString, merged.Id, survivor.Id);
        return (survivor.Id, merged.Id, client, new CityAndClassification(city.CityId, classification.Id));
    }

    private sealed record CityAndClassification(Guid CityId, Guid ClassificationId);

    private static async Task AssertMergedAsync(HttpResponseMessage response, string what)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{what}: {(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal(MergedCode, body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EveryWriteOnAMergedCustomerIs422Merged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (_, mergedId, client, setup) = await ArrangeAsync(factory, database.GetConnectionString());
        using var _client = client;
        var url = $"{CustomersUrl()}/{mergedId}";
        var ct = TestContext.Current.CancellationToken;

        // Cuerpo válido (ruling B2): el 422 tiene que ser `merged` y no `validation.failed`.
        await AssertMergedAsync(await client.PutAsJsonAsync(
            url, NewCustomerBody(setup.CityId, setup.ClassificationId, name: "El Fusionado", identificationNumber: MergedNit), ct), "PUT");
        await AssertMergedAsync(await client.PostAsync($"{url}/activate", null, ct), "activate");
        await AssertMergedAsync(await client.PostAsync($"{url}/deactivate", null, ct), "deactivate");
        await AssertMergedAsync(await client.PostAsJsonAsync(
            $"{url}/addresses", new { name = "Bodega", address = "Carrera 7 # 71-21", cityId = setup.CityId, isPrincipal = false }, ct), "addresses");
        await AssertMergedAsync(await client.PostAsJsonAsync($"{url}/notes", new { body = "hola" }, ct), "notes");
        await AssertMergedAsync(await client.PutAsJsonAsync($"{url}/tags", new { tagIds = Array.Empty<Guid>() }, ct), "tags");
        await AssertMergedAsync(await client.PutAsJsonAsync($"{url}/marketing-consent", new { optedOut = true }, ct), "marketing-consent");
    }

    [Fact]
    public async Task AMergedCustomerStillReads()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (_, mergedId, client, _) = await ArrangeAsync(factory, database.GetConnectionString());
        using var _client = client;
        var url = $"{CustomersUrl()}/{mergedId}";
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/notes", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/tags", ct)).StatusCode);
    }

    [Fact]
    public async Task TheSurvivorCanTakeTheMergedDocument()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (survivorId, _, client, setup) = await ArrangeAsync(factory, database.GetConnectionString());
        using var _client = client;

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{survivorId}",
            NewCustomerBody(setup.CityId, setup.ClassificationId, name: "Quien Queda", identificationNumber: MergedNit),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TwoOwnersOfTheSameDocumentOneMergedDoNotBreakTheLookup()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (survivorId, _, client, setup) = await ArrangeAsync(factory, database.GetConnectionString());
        using var _client = client;
        var ct = TestContext.Current.CancellationToken;
        var put = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{survivorId}",
            NewCustomerBody(setup.CityId, setup.ClassificationId, name: "Quien Queda", identificationNumber: MergedNit), ct);
        put.EnsureSuccessStatusCode();

        // Los dos tienen ahora el mismo documento; el lookup (una sola consulta, ToDictionary) no puede lanzar.
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var stored = await GetAsync(client, survivorId);
        var found = await repository.FindExistingIdentificationsAsync(
            Guid.Parse(TenantId), [(IdentificationType.Nit, stored.IdentificationNumber!)], ct);
        Assert.Equal(survivorId, Assert.Single(found).Value.Value);

        var post = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(setup.CityId, setup.ClassificationId, name: "Otro", identificationNumber: MergedNit), ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
        var body = await post.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("customers.customer.identification_taken", body.GetProperty("code").GetString());
        Assert.Equal(survivorId, body.GetProperty("existing").GetProperty("id").GetGuid());
    }
}
