using System.Net;
using System.Text.Json;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// La configuración pública del formulario y la geografía (plan fase 3, Task 5; F18, F19, contrato «Públicas»): lo que la página
/// necesita para dibujar, sin conocer la tabla de documentos ni la regla de campos. Host propio por clase (Turnstile de mentira); cada
/// prueba siembra su tenant y llega desde su IP.
/// </summary>
public sealed class SupportPublicFormApiTests(SupportPublicHost host) : IClassFixture<SupportPublicHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFormListsOnlyActivePublishedTypesInOrder()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        var tenantId = Guid.Parse(tenant);
        // El segundo tipo se despublica y el tercero se desactiva; el último pasa a ir primero.
        await ExecuteAsync(
            factory,
            """
            WITH ordered AS (SELECT id, row_number() OVER (ORDER BY position) AS n FROM support.ticket_types WHERE tenant_id = @tenant)
            UPDATE support.ticket_types t SET
                is_published = CASE WHEN o.n = 2 THEN false ELSE t.is_published END,
                is_active = CASE WHEN o.n = 3 THEN false ELSE t.is_active END,
                position = CASE WHEN o.n = (SELECT max(n) FROM ordered) THEN -1 ELSE t.position END
            FROM ordered o WHERE t.id = o.id
            """,
            ("tenant", tenantId));
        var expected = await NamesAsync(
            factory,
            "SELECT name FROM support.ticket_types WHERE tenant_id = @tenant AND is_active AND is_published ORDER BY position",
            tenantId);
        using var client = VisitorClient(factory);

        var response = await client.GetAsync(PublicUrl(slug, "/form"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var types = (await JsonAsync(response)).GetProperty("types").EnumerateArray().ToList();
        Assert.Equal(expected, types.Select(type => type.GetProperty("name").GetString()).ToList());
        Assert.Equal(
            2L,
            await ScalarAsync(
                factory,
                "SELECT count(*) FROM support.ticket_types WHERE tenant_id = @tenant AND (NOT is_active OR NOT is_published)",
                ("tenant", tenantId)));
        Assert.All(types, type => Assert.True(type.TryGetProperty("termBusinessDays", out _)));
    }

    [Fact]
    public async Task TheFieldsFollowTheFormVersion()
    {
        var factory = await host.StartTestAsync();
        var slug = await SeedPublishedTenantAsync(
            factory, NewTenantId(), showAddress: false, showLandline: true, businessNameRequired: false);
        using var client = VisitorClient(factory);

        var body = await JsonAsync(await client.GetAsync(PublicUrl(slug, "/form"), Ct));

        var fields = body.GetProperty("fields");
        Assert.True(fields.GetProperty("showDocument").GetBoolean());
        foreach (var variant in new[] { "natural", "nit", "noDocument" })
        {
            Assert.DoesNotContain("address", Keys(fields.GetProperty(variant)));
        }

        Assert.Equal(
            ["documentType", "documentNumber", "firstNames", "lastNames", "landlinePhone", "mobilePhone", "country", "city", "email"],
            Keys(fields.GetProperty("natural")));
        var businessName = fields.GetProperty("nit").EnumerateArray().Single(field => field.GetProperty("key").GetString() == "businessName");
        Assert.False(businessName.GetProperty("required").GetBoolean());
        Assert.Equal(["firstNames", "lastNames", "mobilePhone", "country", "city", "email"], Keys(fields.GetProperty("noDocument")));
        Assert.Equal(1, body.GetProperty("formVersion").GetInt32());
        Assert.Equal("#00B4FF", body.GetProperty("accentColor").GetString());
        Assert.Equal("Cuéntanos tu caso.", body.GetProperty("introText").GetString());
    }

    [Fact]
    public async Task TheDocumentTypesComeFromTheSharedTable()
    {
        var factory = await host.StartTestAsync();
        var slug = await SeedPublishedTenantAsync(factory, NewTenantId());
        using var client = VisitorClient(factory);

        var table = (await JsonAsync(await client.GetAsync(PublicUrl(slug, "/form"), Ct))).GetProperty("documentTypesByCountry");

        Assert.Equal(["NIT", "CC", "CE", "PASAPORTE"], Values(table.GetProperty("CO")));
        Assert.Equal(["NIF", "NIE", "PASAPORTE"], Values(table.GetProperty("ES")));
        Assert.Equal(["TAX_ID", "PASAPORTE"], Values(table.GetProperty("*")));
    }

    [Fact]
    public async Task TheLegalTextsAreTheCurrentVersion()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        await PublishLegalTextsAsync(factory, tenant, 2, "Autorizo, versión 2.");
        using var client = VisitorClient(factory);

        var legal = (await JsonAsync(await client.GetAsync(PublicUrl(slug, "/form"), Ct))).GetProperty("legal");

        Assert.Equal(2, legal.GetProperty("version").GetInt32());
        Assert.Equal("Autorizo, versión 2.", legal.GetProperty("authorizationText").GetString());
        Assert.Equal("https://example.com/politica", legal.GetProperty("policyUrl").GetString());
    }

    [Fact]
    public async Task TheDefaultCountryIsTheTenantCountry()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        using var client = VisitorClient(factory);

        var colombia = await JsonAsync(await client.GetAsync(PublicUrl(slug, "/form"), Ct));
        await ExecuteAsync(factory, "UPDATE tenancy.tenants SET country = 'ES' WHERE id = @id", ("id", Guid.Parse(tenant)));
        var spain = await JsonAsync(await client.GetAsync(PublicUrl(slug, "/form"), Ct));

        Assert.Equal("CO", colombia.GetProperty("defaultCountry").GetString());
        Assert.Equal("ES", spain.GetProperty("defaultCountry").GetString());
        Assert.Equal("Support API Tests", spain.GetProperty("tenantName").GetString());
        Assert.Equal(JsonValueKind.Null, spain.GetProperty("logoUrl").ValueKind);
    }

    [Fact]
    public async Task DepartmentsAndTheirCitiesAreListed()
    {
        var factory = await host.StartTestAsync();
        var slug = await SeedPublishedTenantAsync(factory, NewTenantId());
        using var client = VisitorClient(factory);

        var departments = await client.GetAsync(PublicUrl(slug, "/geography/departments"), Ct);
        var first = (await JsonAsync(departments)).GetProperty("items").EnumerateArray().First();
        var cities = await client.GetAsync(
            PublicUrl(slug, $"/geography/departments/{first.GetProperty("id").GetGuid()}/cities"), Ct);

        Assert.Equal(HttpStatusCode.OK, departments.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cities.StatusCode);
        Assert.NotEmpty((await JsonAsync(cities)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task CitiesOfAnUnknownDepartmentAre404()
    {
        var factory = await host.StartTestAsync();
        var slug = await SeedPublishedTenantAsync(factory, NewTenantId());
        using var client = VisitorClient(factory);

        var response = await client.GetAsync(PublicUrl(slug, $"/geography/departments/{Guid.CreateVersion7()}/cities"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.public.department_not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task GeographyIsAlso404ForAnUnpublishedForm()
    {
        var factory = await host.StartTestAsync();
        var slug = await SeedPublishedTenantAsync(factory, NewTenantId(), published: false);
        using var client = VisitorClient(factory);

        var departments = await client.GetAsync(PublicUrl(slug, "/geography/departments"), Ct);
        var form = await client.GetAsync(PublicUrl(slug, "/form"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, departments.StatusCode);
        Assert.Equal("support.public.not_found", await CodeOfAsync(departments));
        Assert.Equal(HttpStatusCode.NotFound, form.StatusCode);
    }

    private static List<string?> Keys(JsonElement fields) =>
        fields.EnumerateArray().Select(field => field.GetProperty("key").GetString()).ToList();

    private static List<string?> Values(JsonElement list) => list.EnumerateArray().Select(item => item.GetString()).ToList();

    private static async Task<List<string?>> NamesAsync(SupportApiHarness.QepApiFactory factory, string sql, Guid tenantId)
    {
        var names = new List<string?>();
        var count = await ScalarAsync(factory, "SELECT count(*) FROM support.ticket_types WHERE tenant_id = @tenant", ("tenant", tenantId));
        for (var i = 0; i < count; i++)
        {
            var name = await TextAsync(factory, $"{sql} OFFSET {i} LIMIT 1", ("tenant", tenantId));
            if (name is not null)
            {
                names.Add(name);
            }
        }

        return names;
    }
}
