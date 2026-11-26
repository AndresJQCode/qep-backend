using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
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
                cityId = await WholesaleApiHarness.MedellinAsync(manager),
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
                cityId = await WholesaleApiHarness.MedellinAsync(manager),
                address = "Calle 10 # 40-20",
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
        // Spec 2026-11-19 §1, foco 5: el token sólo viaja dentro del link, y es el mismo que va al correo.
        Assert.EndsWith($"/wholesale-invitations/{Uri.EscapeDataString(token)}", detail.InvitationUrl, StringComparison.Ordinal);
    }

    // Review Focus 5: la ciudad inexistente se rechaza al invitar, no cuando el distribuidor intenta aceptar.
    [Fact]
    public async Task InviteWithAnUnknownCityIs422CityNotFound()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");

        var response = await PostInviteAsync(manager, tenant, list.Id, company, cityId: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("wholesale.distributor.city_not_found", await WholesaleApiHarness.BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InviteWithoutTheCityIs422OnCityId()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");

        var response = await PostInviteAsync(manager, tenant, list.Id, company, cityId: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("CityId", await WholesaleApiHarness.BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDetailCarriesCityAndAddress()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var (invited, _) = await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company);

        var response = await manager.GetAsync($"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}/{invited.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.StartsWith("Medell", detail.City!.Name, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(detail.City.Id);
        Assert.NotNull(detail.City.DepartmentId);
        Assert.Equal("Antioquia", detail.City.DepartmentName, ignoreCase: true);
        Assert.Equal("Calle 10 # 40-20", detail.Address);
    }

    private static async Task<HttpResponseMessage> PostInviteAsync(
        HttpClient manager, WholesaleTestTenant tenant, Guid priceListId, Guid billingCompanyId, Guid? cityId) =>
        await manager.PostAsJsonAsync(
            WholesaleApiHarness.DistributorsUrl(tenant.TenantId),
            new
            {
                email = "ana@distri.co",
                businessName = "Distribuciones Norte SAS",
                country = "CO",
                identificationType = "NIT",
                identificationNumber = "900123456",
                contactName = "Ana Pérez",
                priceListId,
                advisorId = tenant.MemberId,
                billingCompanyId,
                cityId,
            },
            Ct);

    // Spec 2026-11-19 §1: «Copiar» rota el token y devuelve la URL una vez; el link anterior responde 404. No escribe eventos.
    [Fact]
    public async Task RotateReturnsANewUrlAndTheOldTokenStopsWorking()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var (distributor, first) = await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company);

        var response = await RotateAsync(manager, tenant.TenantId, distributor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal(distributor.Version + 1, rotated.Version);
        const string Marker = "/wholesale-invitations/";
        var url = rotated.InvitationUrl;
        Assert.NotNull(url);
        Assert.Contains(Marker, url, StringComparison.Ordinal);
        var second = Uri.UnescapeDataString(url[(url.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..]);
        Assert.NotEqual(first, second);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var old = await anonymous.GetAsync(WholesaleApiHarness.InvitationUrl(first), Ct);
        var fresh = await anonymous.GetAsync(WholesaleApiHarness.InvitationUrl(second), Ct);
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var events = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM platform.outbox_messages
            WHERE event_name LIKE 'wholesale.distributor-%' AND payload ->> 'distributorId' = {distributor.Id.ToString()}
            """);
        Assert.Equal(1, events[0]); // sólo el de invitar
        var audits = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM audit.entries
            WHERE tenant_id = {tenant.TenantId} AND action = 'wholesale.distributor.invitation_rotated'
              AND resource_id = {distributor.Id.ToString()}
            """);
        Assert.Equal(1, audits[0]);
        Assert.Equal(0, await TokenInAuditAsync(second));
    }

    [Fact]
    public async Task RotateRequiresDistributorManage()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        using var reader = WholesaleApiHarness.CreateReader(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var (distributor, token) = await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company);

        var response = await RotateAsync(reader, tenant.TenantId, distributor);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(WholesaleApiHarness.InvitationUrl(token), Ct)).StatusCode);
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
                $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant" }, Ct);
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
                $"{WholesaleApiHarness.InvitationUrl(activeToken)}/accept", new { mode = "NewTenant" }, Ct)).EnsureSuccessStatusCode();
        }

        using var otherManager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var activeUrl = $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{active.Id}";
        var current = (await otherManager.GetFromJsonAsync<DistributorDetailDto>(activeUrl, Ct))!;
        var refused = await otherManager.SendAsync(
            WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Delete, activeUrl), current.Version), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("wholesale.distributor.invalid_transition", await WholesaleApiHarness.CodeOfAsync(refused));
    }

    // Decisión del owner 2026-11-24: los datos de la invitación se editan mientras está invitado.
    [Fact]
    public async Task UpdateWhileInvitedEditsTheNameTheDocumentTheCityAndTheEmail()
    {
        var (manager, tenant, invited, token) = await InviteForUpdateAsync();
        using var client = manager;
        var otherCity = await WholesaleApiHarness.OtherCityAsync(manager, invited.City!.Id!.Value);

        var response = await PutAsync(manager, tenant, invited, new
        {
            priceListId = invited.PriceList.Id,
            advisorId = invited.Advisor.MemberId,
            billingCompanyId = invited.BillingCompany.Id,
            businessName = "Distribuciones Sur SAS",
            country = "CO",
            identificationType = "NIT",
            identificationNumber = "800111222",
            contactName = "Luis Gómez",
            email = " Luis@Sur.co ",
            cityId = otherCity,
            address = "Carrera 7 # 1-1",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await manager.GetFromJsonAsync<DistributorDetailDto>(DistributorUrl(tenant, invited), Ct))!;
        Assert.Equal(
            ("Distribuciones Sur SAS", "800111222", "Luis Gómez", "luis@sur.co", (Guid?)otherCity, "Carrera 7 # 1-1"),
            (stored.BusinessName, stored.IdentificationNumber, stored.ContactName, stored.Email, stored.City!.Id, stored.Address));
        Assert.Equal(invited.Version + 1, stored.Version);
        // Cambiar el correo no reenvía ni rota: el único evento sigue siendo el de invitar, con el mismo token.
        Assert.Equal(token, await WholesaleApiHarness.LatestTokenAsync(Factory, invited.Id));
        var events = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM platform.outbox_messages
            WHERE event_name = {WholesaleApiHarness.DistributorInvitedEvent} AND payload ->> 'distributorId' = {invited.Id.ToString()}
            """);
        Assert.Equal(1, events[0]);
    }

    [Fact]
    public async Task UpdateToTheDocumentOfAnotherDistributorIs409IdentificationTaken()
    {
        var (manager, tenant, invited, _) = await InviteForUpdateAsync();
        using var client = manager;
        await WholesaleApiHarness.InviteAsync(
            Factory, manager, tenant, invited.PriceList.Id, invited.BillingCompany.Id, "luis@otra.co", "800999888");

        var response = await PutAsync(manager, tenant, invited, new
        {
            priceListId = invited.PriceList.Id,
            advisorId = invited.Advisor.MemberId,
            billingCompanyId = invited.BillingCompany.Id,
            identificationNumber = "800999888",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wholesale.distributor.identification_taken", await WholesaleApiHarness.CodeOfAsync(response));
    }

    [Fact]
    public async Task UpdateWithAnUnknownCityIs422CityNotFound()
    {
        var (manager, tenant, invited, _) = await InviteForUpdateAsync();
        using var client = manager;

        var response = await PutAsync(manager, tenant, invited, new
        {
            priceListId = invited.PriceList.Id,
            advisorId = invited.Advisor.MemberId,
            billingCompanyId = invited.BillingCompany.Id,
            cityId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wholesale.distributor.city_not_found", await WholesaleApiHarness.CodeOfAsync(response));
    }

    [Fact]
    public async Task UpdateWithAStaleIfMatchIs412()
    {
        var (manager, tenant, invited, _) = await InviteForUpdateAsync();
        using var client = manager;

        var response = await PutAsync(manager, tenant, invited with { Version = invited.Version + 1 }, new
        {
            priceListId = invited.PriceList.Id,
            advisorId = invited.Advisor.MemberId,
            billingCompanyId = invited.BillingCompany.Id,
            businessName = "Distribuciones Sur SAS",
        });

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    // Un frontend viejo no manda los campos nuevos: todo se conserva.
    [Fact]
    public async Task UpdateWithoutTheNewFieldsKeepsTheDistributorData()
    {
        var (manager, tenant, invited, _) = await InviteForUpdateAsync();
        using var client = manager;

        var response = await PutAsync(manager, tenant, invited, new
        {
            priceListId = invited.PriceList.Id,
            advisorId = invited.Advisor.MemberId,
            billingCompanyId = invited.BillingCompany.Id,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal(
            (invited.BusinessName, invited.Country, invited.IdentificationType, invited.IdentificationNumber, invited.ContactName,
                invited.Email, invited.City, invited.Address),
            (stored.BusinessName, stored.Country, stored.IdentificationType, stored.IdentificationNumber, stored.ContactName,
                stored.Email, stored.City, stored.Address));
    }

    [Fact]
    public async Task UpdateOfAnActiveDistributorDoesNotChangeItsData()
    {
        var (wholesaler, invited, token) = await WholesaleApiHarness.InvitedAsync(Factory);
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token);
        using (var session = WholesaleApiHarness.CreateSession(Factory, userId))
        {
            (await session.PostAsJsonAsync(
                $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant" }, Ct)).EnsureSuccessStatusCode();
        }

        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var active = (await manager.GetFromJsonAsync<DistributorDetailDto>(DistributorUrl(wholesaler, invited), Ct))!;

        var refused = await PutAsync(manager, wholesaler, active, new
        {
            priceListId = active.PriceList.Id,
            advisorId = active.Advisor.MemberId,
            billingCompanyId = active.BillingCompany.Id,
            businessName = "Otro nombre SAS",
        });
        // El formulario reenvía lo que pintó, con el nombre de la ciudad: nada cambia y se acepta.
        var resent = await PutAsync(manager, wholesaler, active, new
        {
            priceListId = active.PriceList.Id,
            advisorId = active.Advisor.MemberId,
            billingCompanyId = active.BillingCompany.Id,
            businessName = active.BusinessName,
            country = active.Country,
            identificationType = active.IdentificationType,
            identificationNumber = active.IdentificationNumber,
            contactName = active.ContactName,
            email = active.Email,
            cityId = active.City!.Id,
            cityName = active.City.Name,
            address = active.Address,
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("wholesale.distributor.invalid_transition", await WholesaleApiHarness.CodeOfAsync(refused));
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        var stored = (await resent.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal((active.BusinessName, active.Version + 1), (stored.BusinessName, stored.Version));
    }

    // Una aceptación a medias deja el tenant reservado creado y la invitación pendiente: cambiar el documento duplicaría la
    // empresa y el cliente en el reintento.
    [Fact]
    public async Task UpdateOfTheDocumentAfterTheReservedTenantExistsIs409AndWritesNothing()
    {
        var (manager, tenant, invited, _) = await InviteForUpdateAsync();
        using var client = manager;
        await CreateReservedTenantAsync(invited.Id);

        var refused = await PutAsync(manager, tenant, invited, new
        {
            priceListId = invited.PriceList.Id,
            advisorId = invited.Advisor.MemberId,
            billingCompanyId = invited.BillingCompany.Id,
            identificationNumber = "800111222",
            businessName = "Distribuciones Sur SAS",
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("wholesale.distributor.acceptance_in_progress", await WholesaleApiHarness.CodeOfAsync(refused));
        var untouched = (await manager.GetFromJsonAsync<DistributorDetailDto>(DistributorUrl(tenant, invited), Ct))!;
        Assert.Equal(
            (invited.IdentificationNumber, invited.BusinessName, invited.Version),
            (untouched.IdentificationNumber, untouched.BusinessName, untouched.Version));

        var accepted = await PutAsync(manager, tenant, invited, new
        {
            priceListId = invited.PriceList.Id,
            advisorId = invited.Advisor.MemberId,
            billingCompanyId = invited.BillingCompany.Id,
            identificationNumber = invited.IdentificationNumber,
            businessName = "Distribuciones Sur SAS",
        });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var stored = (await accepted.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.Equal(("Distribuciones Sur SAS", invited.Version + 1), (stored.BusinessName, stored.Version));
    }

    // Spec 2026-11-24: la prueba deja de servir cuando deja de corresponder al buzón. Un distribuidor por caso, cada uno con su
    // correo para que ninguna cuenta de otra prueba convierta el intento en un inicio de sesión.
    [Fact]
    public async Task AnEmailProofIsVoidedByCopyResendAndAnEmailChange()
    {
        // «Copiar» rota el token y no manda correo: la prueba anterior no vale con el token nuevo.
        var copiedEmail = NewEmail();
        var (copyManager, copyTenant, copied, _) = await InviteForUpdateAsync(copiedEmail);
        using var first = copyManager;
        var copiedProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, copied.Id);
        Assert.NotNull(copiedProof);
        var rotated = (await (await RotateAsync(copyManager, copyTenant.TenantId, copied))
            .Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        Assert.DoesNotContain("ev=", rotated.InvitationUrl!, StringComparison.Ordinal);
        var copiedToken = Uri.UnescapeDataString(rotated.InvitationUrl!.Split("/wholesale-invitations/")[1]);

        await AssertProofRefusedAsync(copiedToken, copiedEmail, copiedProof);

        // «Reenviar» manda un correo con una prueba nueva: la anterior no vale y la nueva sí.
        var resentEmail = NewEmail();
        var (resendManager, resendTenant, resent, _) = await InviteForUpdateAsync(resentEmail);
        using var second = resendManager;
        var oldProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, resent.Id);
        Assert.NotNull(oldProof);
        (await ResendAsync(resendManager, resendTenant.TenantId, resent)).EnsureSuccessStatusCode();
        var resentToken = await WholesaleApiHarness.LatestTokenAsync(Factory, resent.Id);
        var newProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, resent.Id);
        Assert.NotNull(newProof);
        Assert.NotEqual(oldProof, newProof);

        await AssertProofRefusedAsync(resentToken, resentEmail, oldProof);
        var withNewProof = await SessionWithProofAsync(resentToken, resentEmail, newProof);
        Assert.Equal(HttpStatusCode.OK, withNewProof.StatusCode);

        // Cambiar el correo no rota el token, pero anula la prueba: no vale para el correo viejo ni para el nuevo.
        var previousEmail = NewEmail();
        var changedEmail = NewEmail();
        var (changeManager, changeTenant, changed, changeToken) = await InviteForUpdateAsync(previousEmail);
        using var third = changeManager;
        var changedProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, changed.Id);
        Assert.NotNull(changedProof);
        (await PutAsync(changeManager, changeTenant, changed, new
        {
            priceListId = changed.PriceList.Id,
            advisorId = changed.Advisor.MemberId,
            billingCompanyId = changed.BillingCompany.Id,
            email = changedEmail,
        })).EnsureSuccessStatusCode();

        await AssertProofRefusedAsync(changeToken, previousEmail, changedProof);
        await AssertProofRefusedAsync(changeToken, changedEmail, changedProof);
    }

    private async Task<HttpResponseMessage> SessionWithProofAsync(string token, string email, string emailProof)
    {
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        return await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password",
            new { email, password = "Distri-2026!", termsVersion = WholesaleApiHarness.TermsVersion, emailProof },
            Ct);
    }

    // El mismo 401 que un código errado, y sin usuario creado.
    private async Task AssertProofRefusedAsync(string token, string email, string emailProof)
    {
        var response = await SessionWithProofAsync(token, email, emailProof);

        Assert.Equal(
            (HttpStatusCode.Unauthorized, "identity.credentials.invalid"),
            (response.StatusCode, await WholesaleApiHarness.CodeOfAsync(response)));
        var users = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM identity.users WHERE email = {email}""");
        Assert.Equal(0, users[0]);
    }

    private static string NewEmail() => $"distri-{Guid.NewGuid():N}@distri.co";

    // Lo que deja un intento de aceptación que se cortó después de registrar el tenant.
    private async Task CreateReservedTenantAsync(Guid distributorId)
    {
        var reserved = (await WholesaleApiHarness.RowsAsync<Guid>(
            Factory,
            $"""
            SELECT reserved_tenant_id AS "Value" FROM wholesale.distributor_invitations WHERE distributor_id = {distributorId}
            """))[0];
        await using var scope = Factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        tenancy.Tenants.Add(Tenant.Create(
            new TenantId(reserved),
            $"reserved-{reserved:N}",
            "Distribuidor a medias",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            MembershipId.New(),
            DateTimeOffset.UtcNow));
        await tenancy.SaveChangesAsync(Ct);
    }

    private async Task<(HttpClient Manager, WholesaleTestTenant Tenant, DistributorDetailDto Invited, string Token)> InviteForUpdateAsync(
        string email = "ana@distri.co")
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var (invited, token) = await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company, email);
        return (manager, tenant, invited, token);
    }

    private static string DistributorUrl(WholesaleTestTenant tenant, DistributorDetailDto distributor) =>
        $"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}/{distributor.Id}";

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, WholesaleTestTenant tenant, DistributorDetailDto distributor, object body) =>
        client.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, DistributorUrl(tenant, distributor)) { Content = JsonContent.Create(body) },
                distributor.Version),
            Ct);

    private static Task<HttpResponseMessage> ResendAsync(HttpClient manager, Guid tenantId, DistributorDetailDto distributor) =>
        PostWithVersionAsync(
            manager, $"{WholesaleApiHarness.DistributorsUrl(tenantId)}/{distributor.Id}/resend-invitation", distributor.Version);

    private static Task<HttpResponseMessage> RotateAsync(HttpClient client, Guid tenantId, DistributorDetailDto distributor) =>
        PostWithVersionAsync(
            client, $"{WholesaleApiHarness.DistributorsUrl(tenantId)}/{distributor.Id}/invitation-link", distributor.Version);

    private static Task<HttpResponseMessage> PostWithVersionAsync(HttpClient client, string url, long version) =>
        client.SendAsync(WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Post, url), version), Ct);

    private async Task<int> TokenInAuditAsync(string token) =>
        (await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""SELECT count(*)::int AS "Value" FROM audit.entries e WHERE strpos(e::text, {token}) > 0"""))[0];
}
