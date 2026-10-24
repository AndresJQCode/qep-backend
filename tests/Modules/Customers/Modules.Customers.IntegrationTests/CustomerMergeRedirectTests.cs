using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-24 §3.3: el CUC impreso de un fusionado sigue resolviendo hacia el que quedó. La búsqueda de pedidos
/// por CUC y la de cotizaciones por documento (desvío F7) traen también al que quedó, y el listado de clientes por CUC
/// devuelve al que quedó (ruling C2 de la pregunta 2).</summary>
public sealed class CustomerMergeRedirectTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private const string SurvivorNit = "900.222.222-2";
    private const string MergedNit = "830.111.111-1";

    private sealed record Pair(CustomerResponse Survivor, CustomerResponse Merged, HttpClient Client);

    private static async Task<Pair> ArrangeAsync(QepApiFactory factory, string connectionString)
    {
        var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var survivor = await CreateCustomerAsync(client, city.CityId, classification.Id, "Quien Queda", SurvivorNit);
        var merged = await CreateCustomerAsync(client, city.CityId, classification.Id, "El Fusionado", MergedNit);
        await MarkMergedAsync(connectionString, merged.Id, survivor.Id);
        return new Pair(survivor, merged, client);
    }

    private static async Task<IReadOnlySet<Guid>> InRepositoryAsync(
        QepApiFactory factory, Func<ICustomerRepository, Task<IReadOnlySet<Guid>>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICustomerRepository>());
    }

    [Fact]
    public async Task TheCucOfAMergedFindsTheSurvivorsOrdersToo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = pair.Client;

        var ids = await InRepositoryAsync(factory, repository =>
            repository.SearchIdsByCucAsync(Tenant, pair.Merged.Cuc!, TestContext.Current.CancellationToken));
        var survivorOnly = await InRepositoryAsync(factory, repository =>
            repository.SearchIdsByCucAsync(Tenant, pair.Survivor.Cuc!, TestContext.Current.CancellationToken));
        var foreign = await InRepositoryAsync(factory, repository =>
            repository.SearchIdsByCucAsync(Guid.Parse(OtherTenantId), pair.Merged.Cuc!, TestContext.Current.CancellationToken));

        Assert.Equal(new HashSet<Guid> { pair.Merged.Id, pair.Survivor.Id }, ids.ToHashSet());
        Assert.Equal(new HashSet<Guid> { pair.Survivor.Id }, survivorOnly.ToHashSet());
        Assert.Empty(foreign);
    }

    [Fact]
    public async Task TheDocumentOfAMergedFindsTheSurvivorsQuotationsToo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = pair.Client;

        var ids = await InRepositoryAsync(factory, repository =>
            repository.SearchIdsByIdentificationNumberAsync(Tenant, MergedNit, TestContext.Current.CancellationToken));
        var survivorOnly = await InRepositoryAsync(factory, repository =>
            repository.SearchIdsByIdentificationNumberAsync(Tenant, SurvivorNit, TestContext.Current.CancellationToken));

        Assert.Equal(new HashSet<Guid> { pair.Merged.Id, pair.Survivor.Id }, ids.ToHashSet());
        Assert.Equal(new HashSet<Guid> { pair.Survivor.Id }, survivorOnly.ToHashSet());
    }

    // §3.3, el correo del resolver de Shopify: como el teléfono, un fusionado nunca es «el más viejo» si un vivo tiene el
    // mismo correo. Sólo sale si es el único, y el adaptador lo cambia por el que quedó.
    [Fact]
    public async Task TheEmailLookupNeverPicksAMergedAsTheOldest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString()); // los dos con compras@verde.co; el fusionado, más nuevo
        using var client = pair.Client;
        await using (var connection = new Npgsql.NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new Npgsql.NpgsqlCommand(
                "UPDATE customers.customers SET created_at = created_at - interval '1 day' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", pair.Merged.Id);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var found = await scope.ServiceProvider.GetRequiredService<ICustomerRepository>()
            .FindIdByEmailAsync(Tenant, "compras@verde.co", TestContext.Current.CancellationToken);

        Assert.Equal(pair.Survivor.Id, found?.Value);
    }

    // Ruling C2: el CUC de un fusionado, en la caja de CUC del listado y en el criterio combinado, devuelve al que quedó. El
    // documento del fusionado no (CustomerMergeExclusionTests): sólo el CUC es lo impreso que sigue resolviendo.
    [Fact]
    public async Task TheCucOfAMergedListsTheSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var pair = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = pair.Client;

        foreach (var query in new[] { $"?cuc={pair.Merged.Cuc}", $"?search={pair.Merged.Cuc}" })
        {
            var page = await ListAsync(client, query);
            Assert.Equal(pair.Survivor.Id, Assert.Single(page.Items).Id);
            Assert.Equal(1, page.Total);
        }
    }
}
