using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El nombre de la asesora cruza tres módulos —membresía en Tenancy, correo en Identity,
/// cotización en Quotations— por el adaptador de <c>Bootstrapper</c>, que ninguna prueba
/// unitaria ve. Ésta lo recorre punta a punta.
/// </summary>
public sealed class QuotationAdvisorNameApiTests
{
    // X-Permissions reemplaza el set por defecto del stub, así que los permisos de Tenancy para
    // leer el roster y renombrar se piden explícitos.
    private static readonly string[] Permissions =
        [.. ManagerPermissions, "advisorship.read", "advisorship.manage"];

    [Fact]
    public async Task TheQuotationDetailCarriesTheAdvisorNameOnceTheMemberIsNamed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // La asesora es el owner, que nace sin nombre porque lo crea register-tenant y no una
        // invitación: el detalle trae sólo el correo.
        Assert.Null(quotation.AdvisorName);
        Assert.NotNull(quotation.AdvisorEmail);

        var version = await MembershipVersionAsync(client, tenantId, quotation.AdvisorId);
        using var rename = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/memberships/{quotation.AdvisorId}/display-name")
        {
            Content = JsonContent.Create(new { displayName = "Laura Gómez" })
        };
        rename.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        var renamed = await client.SendAsync(rename, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);

        Assert.NotNull(fetched);
        Assert.Equal("Laura Gómez", fetched.AdvisorName);
        // D1: el correo sigue viajando igual; la pantalla lo sigue usando.
        Assert.Equal(quotation.AdvisorEmail, fetched.AdvisorEmail);
    }

    // La versión se lee del roster en vez de suponerla: If-Match tiene que llevar la vigente.
    private static async Task<long> MembershipVersionAsync(
        HttpClient client, Guid tenantId, Guid membershipId)
    {
        var roster = await client.GetFromJsonAsync<MembershipListPayload>(
            $"/api/v1/tenants/{tenantId}/memberships", TestContext.Current.CancellationToken);
        Assert.NotNull(roster);
        return Assert.Single(roster.Items, item => item.Id == membershipId).Version;
    }

    private sealed record MembershipListPayload(IReadOnlyList<MembershipRowPayload> Items);

    private sealed record MembershipRowPayload(Guid Id, long Version);
}
