using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Customers.Application;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>CD-03: <c>GET /customers/identification-matches</c> avisa, antes de guardar, quién más tiene ese número.</summary>
public sealed class CustomerIdentificationMatchesApiTests
{
    private const string OwnerName = "GYSELYS VIVAS PACHECO";
    private const string OwnerNit = "92-1396892";

    private static string MatchesUrl(string query, string tenantId = TenantId) =>
        $"{CustomersUrl(tenantId)}/identification-matches{query}";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheFormSeesTheOtherTypeBeforeSaving()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var owner = await CreateCustomerAsync(client, city.CityId, classification.Id, OwnerName, OwnerNit);

        var response = await client.GetAsync(MatchesUrl($"?number={OwnerNit}&type=CE"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await BodyAsync(response)).GetProperty("items").EnumerateArray());
        Assert.Equal(owner.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(OwnerName, item.GetProperty("name").GetString());
        Assert.Equal("NIT", item.GetProperty("identificationType").GetString());
        Assert.Equal(OwnerNit, item.GetProperty("identificationNumber").GetString());
        Assert.Equal(owner.Cuc, item.GetProperty("cuc").GetString());
        Assert.True(item.GetProperty("isComplete").GetBoolean());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        Assert.Equal("OtherType", item.GetProperty("match").GetString());
    }

    [Fact]
    public async Task ExcludeCustomerIdLeavesOutTheOneBeingEdited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var owner = await CreateCustomerAsync(client, city.CityId, classification.Id, OwnerName, OwnerNit);

        var response = await client.GetAsync(
            MatchesUrl($"?number={OwnerNit}&type=NIT&excludeCustomerId={owner.Id}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await BodyAsync(response)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task WithoutMatchesItemsIsEmpty()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.GetAsync(MatchesUrl("?number=555666777&type=NIT"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"items":[]}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEmptyNumberIs422OnNumberAndAnUnknownTypeIs422OnType()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var empty = await client.GetAsync(MatchesUrl("?number="), TestContext.Current.CancellationToken);
        var missing = await client.GetAsync(MatchesUrl(string.Empty), TestContext.Current.CancellationToken);
        var tooLong = await client.GetAsync(MatchesUrl($"?number={new string('1', 33)}"), TestContext.Current.CancellationToken);
        var unknownType = await client.GetAsync(MatchesUrl("?number=123&type=RUT"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Contains("Number", await ValidationFieldsAsync(empty));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("Number", await ValidationFieldsAsync(missing));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Contains("Number", await ValidationFieldsAsync(tooLong));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownType.StatusCode);
        Assert.Contains("Type", await ValidationFieldsAsync(unknownType));
    }

    [Fact]
    public async Task AnotherTenantIs403AndWithoutReadIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var reader = CreateClient(factory, SubjectId, TenantId, CustomersPermissions.CustomerRead);
        using var noPermission = CreateClient(factory, SubjectId, TenantId);

        var otherTenant = await reader.GetAsync(MatchesUrl("?number=123", OtherTenantId), TestContext.Current.CancellationToken);
        var withoutRead = await noPermission.GetAsync(MatchesUrl("?number=123"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutRead.StatusCode);
    }
}
