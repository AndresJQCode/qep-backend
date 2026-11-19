using System.Net;
using System.Net.Http.Json;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>Las rutas de distribuidores contra Postgres (contrato «Distribuidores», D-1, D-13, D-14).</summary>
public sealed class DistributorApiTests(SharedWholesaleHost host) : IClassFixture<SharedWholesaleHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleApiHarness.QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task InviteWithATypeNotAllowedForTheCountryAnswers422OnIdentificationType()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");

        var response = await manager.PostAsJsonAsync(
            WholesaleApiHarness.DistributorsUrl(tenant.TenantId),
            new
            {
                email = "ana@distri.co",
                businessName = "Distribuciones Norte SAS",
                country = "CO",
                identificationType = "NIF",
                identificationNumber = "900123456",
                contactName = "Ana Pérez",
                priceListId = list.Id,
                advisorId = tenant.MemberId,
                billingCompanyId = company,
            },
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("IdentificationType", await WholesaleApiHarness.BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptionsCarryTheDefaultCountry()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);

        var response = await manager.GetAsync($"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}/options", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = (await response.Content.ReadFromJsonAsync<DistributorOptionsDto>(Ct))!;
        Assert.Equal("CO", options.DefaultCountry);
    }

    [Fact]
    public async Task InviteReturns201InvitedAndQueuesOneEmailEventWithoutTheTokenInTheAudit()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");

        var response = await manager.PostAsJsonAsync(
            WholesaleApiHarness.DistributorsUrl(tenant.TenantId),
            new
            {
                email = "  Ana@Distri.CO ",
                businessName = "Distribuciones Norte SAS",
                country = "CO",
                identificationType = "NIT",
                identificationNumber = "900123456",
                contactName = "Ana Pérez",
                priceListId = list.Id,
                advisorId = tenant.MemberId,
                billingCompanyId = company,
            },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal($"/api/v1/tenants/{tenant.TenantId}/wholesale/distributors/{detail.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("Invited", "ana@distri.co", list.Id, WholesaleApiHarness.MemberName, company),
            (detail.Status, detail.Email, detail.PriceList.Id, detail.Advisor.Name, detail.BillingCompany.Id));
        Assert.Equal(["edit", "resend", "delete"], detail.AllowedActions);
        Assert.Equal(("CO", "NIT"), (detail.Country, detail.IdentificationType));

        var events = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM platform.outbox_messages
            WHERE event_name = {WholesaleApiHarness.DistributorInvitedEvent} AND payload ->> 'distributorId' = {detail.Id.ToString()}
            """);
        Assert.Equal(1, events[0]);

        var token = await WholesaleApiHarness.LatestTokenAsync(Factory, detail.Id);
        var audits = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM audit.entries
            WHERE tenant_id = {tenant.TenantId} AND action = 'wholesale.distributor.invited' AND resource_id = {detail.Id.ToString()}
            """);
        Assert.Equal(1, audits[0]);
        Assert.Equal(0, await TokenInAuditAsync(token));
        Assert.DoesNotContain(token, await WholesaleApiHarness.BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListCountsAndSearch()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company, "ana@distri.co", "900123456");
        await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company, "luis@otra.co", "800999888");

        var all = await manager.GetFromJsonAsync<DistributorsResponse>(WholesaleApiHarness.DistributorsUrl(tenant.TenantId), Ct);
        var searched = await manager.GetFromJsonAsync<DistributorsResponse>(
            $"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}?search=otra.co", Ct);
        var badStatus = await manager.GetAsync($"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}?status=Expired", Ct);

        Assert.Equal((2, 2, 0, 0, 2), (all!.Total, all.Counts.Invited, all.Counts.Active, all.Counts.Blocked, all.Counts.All));
        Assert.All(all.Items, item => Assert.NotNull(item.Invitation));
        var found = Assert.Single(searched!.Items);
        Assert.Equal("luis@otra.co", found.Email);
        Assert.Equal(1, searched.Counts.All);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badStatus.StatusCode);
        Assert.Equal("validation.failed", await WholesaleApiHarness.CodeOfAsync(badStatus));
    }

    [Fact]
    public async Task ResendRotatesTheToken()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var (distributor, first) = await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company);

        var response = await ResendAsync(manager, tenant.TenantId, distributor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resent = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        var second = await WholesaleApiHarness.LatestTokenAsync(Factory, distributor.Id);
        Assert.NotEqual(first, second);
        Assert.Equal(distributor.Version + 1, resent.Version);
        var events = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM platform.outbox_messages
            WHERE event_name = {WholesaleApiHarness.DistributorInvitedEvent} AND payload ->> 'distributorId' = {distributor.Id.ToString()}
            """);
        Assert.Equal(2, events[0]);
        Assert.Equal(0, await TokenInAuditAsync(second));
    }

    // Review Focus 4: el link viejo es 404, el nuevo abre.
    [Fact]
    public async Task TheOldLinkIs404AfterAResend()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var (distributor, first) = await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company);
        (await ResendAsync(manager, tenant.TenantId, distributor)).EnsureSuccessStatusCode();
        var second = await WholesaleApiHarness.LatestTokenAsync(Factory, distributor.Id);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var old = await anonymous.GetAsync(WholesaleApiHarness.InvitationUrl(first), Ct);
        var fresh = await anonymous.GetAsync(WholesaleApiHarness.InvitationUrl(second), Ct);

        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        Assert.Equal("wholesale.invitation.not_found", await WholesaleApiHarness.CodeOfAsync(old));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task BlockAndUnblock()
    {
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var url = $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}";

        // Invitado: bloquear no está entre sus acciones.
        var early = await PostWithVersionAsync(manager, $"{url}/block", distributor.Version);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("wholesale.distributor.invalid_transition", await WholesaleApiHarness.CodeOfAsync(early));

        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token);
        using (var session = WholesaleApiHarness.CreateSession(Factory, userId))
        {
            var accepted = await session.PostAsJsonAsync(
                $"{WholesaleApiHarness.InvitationUrl(token)}/accept",
                new { mode = "NewTenant", displayName = "Distri Norte", slug = $"distri-{Guid.NewGuid():N}"[..30] },
                Ct);
            accepted.EnsureSuccessStatusCode();
        }

        var active = (await manager.GetFromJsonAsync<DistributorDetailDto>(url, Ct))!;
        // Una tupla con una lista compara la lista por referencia: cada parte se compara por separado.
        Assert.Equal("Active", active.Status);
        Assert.Equal(["edit", "block"], active.AllowedActions);

        var blocked = await PostWithVersionAsync(manager, $"{url}/block", active.Version);
        var blockedDetail = (await blocked.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        var unblocked = await PostWithVersionAsync(manager, $"{url}/unblock", blockedDetail.Version);
        var unblockedDetail = (await unblocked.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;

        Assert.Equal("Blocked", blockedDetail.Status);
        Assert.Equal(["edit", "unblock"], blockedDetail.AllowedActions);
        Assert.Equal("Active", unblockedDetail.Status);
        var audits = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT action AS "Value" FROM audit.entries
            WHERE tenant_id = {wholesaler.TenantId} AND resource_id = {distributor.Id.ToString()}
            ORDER BY occurred_at, id
            """);
        Assert.Equal(
            ["wholesale.distributor.invited", "wholesale.distributor.accepted", "wholesale.distributor.blocked", "wholesale.distributor.unblocked"],
            audits);
    }

    [Fact]
    public async Task DeleteOnlyWhileInvited()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var (invited, token) = await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company);
        var url = $"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}/{invited.Id}";

        var deleted = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Delete, url), invited.Version), Ct);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var link = await anonymous.GetAsync(WholesaleApiHarness.InvitationUrl(token), Ct);
        var gone = await manager.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // Aceptado ya no se borra: 409.
        var (wholesaler, active, activeToken) = await WholesaleApiHarness.InvitedAsync(Factory, "luis@otra.co", "800999888");
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, activeToken);
        using (var session = WholesaleApiHarness.CreateSession(Factory, userId))
        {
            (await session.PostAsJsonAsync(
                $"{WholesaleApiHarness.InvitationUrl(activeToken)}/accept",
                new { mode = "NewTenant", displayName = "Otra Distri", slug = $"otra-{Guid.NewGuid():N}"[..30] },
                Ct)).EnsureSuccessStatusCode();
        }

        using var otherManager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var activeUrl = $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{active.Id}";
        var current = (await otherManager.GetFromJsonAsync<DistributorDetailDto>(activeUrl, Ct))!;
        var refused = await otherManager.SendAsync(
            WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Delete, activeUrl), current.Version), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("wholesale.distributor.invalid_transition", await WholesaleApiHarness.CodeOfAsync(refused));
    }

    private static Task<HttpResponseMessage> ResendAsync(HttpClient manager, Guid tenantId, DistributorDetailDto distributor) =>
        PostWithVersionAsync(
            manager, $"{WholesaleApiHarness.DistributorsUrl(tenantId)}/{distributor.Id}/resend-invitation", distributor.Version);

    private static Task<HttpResponseMessage> PostWithVersionAsync(HttpClient client, string url, long version) =>
        client.SendAsync(WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Post, url), version), Ct);

    private async Task<int> TokenInAuditAsync(string token) =>
        (await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""SELECT count(*)::int AS "Value" FROM audit.entries e WHERE strpos(e::text, {token}) > 0"""))[0];
}
