using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Catalog.IntegrationTests.CatalogTestDatabase;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// La consola «Unidad tributaria» del operador de punta a punta, con el stub de desarrollo y calcada de
/// <c>CityOperatorApiTests</c>. A diferencia de la de ciudades, esta lee el calendario del tenant operador
/// (<c>ITenantClock</c>), así que el operador necesita una fila en <c>tenancy.tenants</c>.
/// </summary>
public sealed class TaxUnitOperatorApiTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");
    private static readonly Guid OperatorSubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

    private static readonly string[] ReadAndManage =
        [CatalogOperatorPermissions.TaxUnitsRead, CatalogOperatorPermissions.TaxUnitsManage];

    private static string Base => $"/api/v1/tenants/{OperatorTenantId}/operator/tax-units";

    [Fact]
    public async Task WithoutReadPermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = CreateClient(factory, OperatorTenantId, "tenancy.settings.read");

        var list = await client.GetAsync(Base + "/", TestContext.Current.CancellationToken);
        var put = await client.PutAsJsonAsync(
            $"{Base}/CO/2026", new { value = 49799 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task NonOperatorTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();
        using var client = CreateClient(factory, otherTenantId, ReadAndManage);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{otherTenantId}/operator/tax-units/", TestContext.Current.CancellationToken);

        // Lo corta la política: el stub ya le quitó los operator.* a un tenant que no es el operador.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutCreatesThenReplaces()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        await SeedOperatorTenantAsync(factory);
        using var client = CreateClient(factory, OperatorTenantId, ReadAndManage);
        var token = TestContext.Current.CancellationToken;
        var year = DateTime.UtcNow.Year;

        var first = await client.PutAsJsonAsync($"{Base}/CO/{year}", new { value = 49799 }, token);
        var second = await client.PutAsJsonAsync($"{Base}/co/{year}", new { value = 50000.5 }, token);
        var listing = await client.GetFromJsonAsync<TaxUnitsDto>(Base + "/", token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotNull(listing);
        var colombia = Assert.Single(listing.Countries);
        Assert.Equal(("CO", "UVT", "COP"), (colombia.Country, colombia.TaxUnit, colombia.Currency));
        var row = Assert.Single(colombia.Values);
        Assert.Equal((year, 50000.5m), (row.Year, row.Value));
    }

    [Fact]
    public async Task PutForCountryWithoutUnitIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        await SeedOperatorTenantAsync(factory);
        using var client = CreateClient(factory, OperatorTenantId, ReadAndManage);

        var response = await client.PutAsJsonAsync(
            $"{Base}/ES/{DateTime.UtcNow.Year}", new { value = 1 }, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "catalog.tax_unit.country_without_unit");
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(2100)]
    public async Task PutYearOutOfRangeIs422(int year)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        await SeedOperatorTenantAsync(factory);
        using var client = CreateClient(factory, OperatorTenantId, ReadAndManage);

        var response = await client.PutAsJsonAsync(
            $"{Base}/CO/{year}", new { value = 49799 }, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "catalog.tax_unit.year_out_of_range");
    }

    [Fact]
    public async Task PutNonPositiveValueIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        await SeedOperatorTenantAsync(factory);
        using var client = CreateClient(factory, OperatorTenantId, ReadAndManage);

        var response = await client.PutAsJsonAsync(
            $"{Base}/CO/{DateTime.UtcNow.Year}", new { value = 0 }, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "catalog.tax_unit.value_invalid");
    }

    private static async Task SeedOperatorTenantAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbContext.Tenants.Add(Tenant.Create(
            new TenantId(OperatorTenantId),
            "qcode-operator",
            "QCode Operator",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            MembershipId.New(),
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static HttpClient CreateClient(QepApiFactory factory, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", OperatorSubjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private sealed class QepApiFactory(string connectionString, Guid operatorTenantId)
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
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Siempre fijado: un Platform:OperatorTenantId en los user-secrets de quien corre las pruebas convertiría en operador
            // a un tenant ajeno.
            builder.UseSetting("Platform:OperatorTenantId", operatorTenantId.ToString());
        }
    }
}
