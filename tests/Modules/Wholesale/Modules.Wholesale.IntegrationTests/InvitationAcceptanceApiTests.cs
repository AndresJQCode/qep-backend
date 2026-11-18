using System.Net;
using System.Net.Http.Json;
using Api;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>
/// El link de la invitación de punta a punta (contrato «Invitación», D-3, D-4, D-16, D-17, D-20): resumen anónimo, las dos
/// sesiones, los tenants vinculables y la aceptación con sus escrituras entre módulos. Cada prueba usa un correo propio para
/// que el usuario de una no exista en otra del mismo host.
/// </summary>
public sealed class InvitationAcceptanceApiTests(SharedWholesaleHost host) : IClassFixture<SharedWholesaleHost>
{
    private const string Password = "Distri-2026!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleApiHarness.QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task TheSummaryIsAnonymousAndShowsPendingThenAccepted()
    {
        var email = NewEmail();
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var pending = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        (await AcceptNewTenantAsync(userId, token)).EnsureSuccessStatusCode();
        var accepted = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);

        Assert.Equal(
            ("Mayorista API Tests", "Distribuciones Norte SAS", "Ana Pérez", email, "pending", false),
            (pending!.WholesalerName, pending.BusinessName, pending.ContactName, pending.Email, pending.Status, pending.HasPassword));
        Assert.Equal(distributor.Invitation.ExpiresAt, pending.ExpiresAt);
        Assert.Equal(("accepted", true), (accepted!.Status, accepted.HasPassword));
    }

    [Fact]
    public async Task AnExpiredLinkIs410()
    {
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        await WholesaleApiHarness.ExecuteAsync(
            Factory,
            $"""
            UPDATE wholesale.distributor_invitations SET expires_at = now() - interval '1 minute'
            WHERE distributor_id = {distributor.Id}
            """);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var summary = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);
        var session = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password", new { password = Password }, Ct);

        Assert.Equal("expired", summary!.Status);
        Assert.Equal(HttpStatusCode.Gone, session.StatusCode);
        Assert.Equal("wholesale.invitation.expired", await WholesaleApiHarness.CodeOfAsync(session));
    }

    [Fact]
    public async Task APasswordSessionProvisionsTheUserAndOpensASession()
    {
        var email = NewEmail();
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var missingBody = await anonymous.PostAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password",
            new StringContent("{", System.Text.Encoding.UTF8, "application/json"),
            Ct);
        var response = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password", new { password = Password }, Ct);
        var wrongPassword = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password", new { password = "Otra-Clave-99" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, missingBody.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<SessionResponse>(Ct))!;
        Assert.Equal(email, session.Email);
        Assert.Empty(session.ActiveTenantIds);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any());
        // Con contraseña ya creada, una distinta no abre sesión.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal("identity.credentials.invalid", await WholesaleApiHarness.CodeOfAsync(wrongPassword));
    }

    // D-4: Google sólo con el correo invitado y verificado.
    [Fact]
    public async Task AGoogleSessionNeedsTheInvitedVerifiedEmail()
    {
        var email = NewEmail();
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        using var other = WholesaleApiHarness.CreateGoogle(Factory, NewEmail());
        using var unverified = WholesaleApiHarness.CreateGoogle(Factory, email, verified: false);
        using var invited = WholesaleApiHarness.CreateGoogle(Factory, $"  {email.ToUpperInvariant()} ");

        var mismatch = await other.PostAsync($"{WholesaleApiHarness.InvitationUrl(token)}/session", null, Ct);
        var notVerified = await unverified.PostAsync($"{WholesaleApiHarness.InvitationUrl(token)}/session", null, Ct);
        var ok = await invited.PostAsync($"{WholesaleApiHarness.InvitationUrl(token)}/session", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        Assert.Equal("wholesale.invitation.email_mismatch", await WholesaleApiHarness.CodeOfAsync(mismatch));
        Assert.Equal(HttpStatusCode.Forbidden, notVerified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(email, (await ok.Content.ReadFromJsonAsync<SessionResponse>(Ct))!.Email);
    }

    // D-21 y Review Focus: el alta del distribuidor no lee Registration:PublicTenantSignupEnabled. Host propio: cambia la
    // configuración.
    [Fact]
    public async Task NewTenantCreatesTheEightModulesWithoutShopifyEvenWithSignupDisabled()
    {
        await using var database = await WholesaleApiHarness.StartDatabaseAsync();
        await using var factory = new WholesaleApiHarness.QepApiFactory(
            database.GetConnectionString(),
            settings: new Dictionary<string, string> { ["Registration:PublicTenantSignupEnabled"] = "false" });
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(factory, token, Password);
        using var session = WholesaleApiHarness.CreateSession(factory, userId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept",
            new { mode = "NewTenant", displayName = "Distri Norte", slug = NewSlug() },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.True(accepted.Created);
        var modules = await WholesaleApiHarness.RowsAsync<string>(
            factory,
            $"""
            SELECT module_key AS "Value" FROM tenancy.tenant_modules WHERE tenant_id = {accepted.TenantId} ORDER BY module_key
            """);
        Assert.Equal(
            ["catalog", "companies", "customers", "orders", "purchases", "quotations", "reporting", "suppliers"],
            modules);
        Assert.DoesNotContain(TenantModuleKeys.Shopify.Value, modules);
        var owner = await WholesaleApiHarness.RowsAsync<int>(
            factory,
            $"""
            SELECT count(*)::int AS "Value" FROM tenancy.tenants t
            JOIN tenancy.memberships m ON m.id = t.owner_membership_id AND m.tenant_id = t.id
            WHERE t.id = {accepted.TenantId} AND m.user_id = {userId}
            """);
        Assert.Equal(1, owner[0]);
    }

    [Fact]
    public async Task NewTenantCreatesTheLinkedSupplierInTheDistributorAndTheCustomerInTheWholesaler()
    {
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);

        var response = await AcceptNewTenantAsync(userId, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal(("Distri Norte", true), (accepted.TenantName, accepted.Created));
        var suppliers = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {accepted.TenantId} AND linked_tenant_id = {wholesaler.TenantId}
            """);
        Assert.Equal(1, suppliers[0]);
        var customers = await WholesaleApiHarness.RowsAsync<Guid>(
            Factory,
            $"""
            SELECT id AS "Value" FROM customers.customers
            WHERE tenant_id = {wholesaler.TenantId} AND identification_number = '900123456'
            """);
        var customer = Assert.Single(customers);

        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var detail = (await manager.GetFromJsonAsync<DistributorDetailDto>(
            $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}", Ct))!;
        Assert.Equal(("Active", accepted.TenantId, (Guid?)customer), (detail.Status, detail.DistributorTenant!.Id, detail.CustomerId));

        // D-16 y ruling W9: repetirlo es la misma respuesta, sin crear nada.
        var again = (await (await AcceptNewTenantAsync(userId, token)).Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal((accepted.TenantId, false), (again.TenantId, again.Created));
        Assert.Empty(again.EnabledModules);
    }

    [Fact]
    public async Task LinkTenantEnablesSuppliersAndPurchasesAndKeepsTheRest()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory, [TenantModuleKeys.Catalog, TenantModuleKeys.Customers]);
        var (wholesaler, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, WholesaleApiHarness.EmailOf(mine));
        using var session = WholesaleApiHarness.CreateSession(Factory, mine.SubjectId, mine.TenantId);

        var linkable = await session.GetFromJsonAsync<LinkableTenantsResponse>(
            $"{WholesaleApiHarness.InvitationUrl(token)}/linkable-tenants", Ct);
        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant", tenantId = mine.TenantId }, Ct);

        var option = Assert.Single(linkable!.Tenants);
        Assert.Equal((mine.TenantId, true, (string?)null), (option.TenantId, option.Linkable, option.IneligibleReason));
        Assert.Equal(["suppliers", "purchases"], option.ModulesToEnable);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal((mine.TenantId, false), (accepted.TenantId, accepted.Created));
        Assert.Equal(["suppliers", "purchases"], accepted.EnabledModules);
        var modules = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT module_key AS "Value" FROM tenancy.tenant_modules WHERE tenant_id = {mine.TenantId} ORDER BY module_key
            """);
        Assert.Equal(["catalog", "customers", "purchases", "suppliers"], modules);
        var supplier = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {mine.TenantId} AND linked_tenant_id = {wholesaler.TenantId}
            """);
        Assert.Equal(1, supplier[0]);
    }

    [Fact]
    public async Task AnotherAccountIs403AndNothingIsWritten()
    {
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var stranger = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var session = WholesaleApiHarness.CreateSession(Factory, stranger.SubjectId, stranger.TenantId);
        var slug = NewSlug();

        var accept = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant", displayName = "Ajena", slug }, Ct);
        var linkable = await session.GetAsync($"{WholesaleApiHarness.InvitationUrl(token)}/linkable-tenants", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);
        Assert.Equal("wholesale.invitation.email_mismatch", await WholesaleApiHarness.CodeOfAsync(accept));
        Assert.Equal(HttpStatusCode.Forbidden, linkable.StatusCode);
        var tenants = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM tenancy.tenants WHERE slug = {slug}""");
        Assert.Equal(0, tenants[0]);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var detail = (await manager.GetFromJsonAsync<DistributorDetailDto>(
            $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}", Ct))!;
        Assert.Equal("Invited", detail.Status);
    }

    [Fact]
    public async Task AnAcceptWithoutModeIs422WithTheField()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        using var session = WholesaleApiHarness.CreateSession(Factory, userId);

        var noMode = await session.PostAsJsonAsync($"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { }, Ct);
        var noTenant = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noMode.StatusCode);
        Assert.Contains("\"Mode\"", await WholesaleApiHarness.BodyAsync(noMode), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTenant.StatusCode);
        Assert.Contains("\"TenantId\"", await WholesaleApiHarness.BodyAsync(noTenant), StringComparison.Ordinal);
    }

    // Review Focus 1: doble clic o dos pestañas. Un tenant, un proveedor, un cliente, y las dos respuestas sobre el mismo tenant.
    [Fact]
    public async Task TwoConcurrentAcceptsCreateOneTenantOneSupplierOneCustomer()
    {
        var (wholesaler, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail(), "901222333");
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        var slug = NewSlug();

        var responses = await Task.WhenAll(AcceptNewTenantAsync(userId, token, slug), AcceptNewTenantAsync(userId, token, slug));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var accepted = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct)));
        Assert.Equal(accepted[0]!.TenantId, accepted[1]!.TenantId);
        var tenants = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM tenancy.tenants WHERE slug = {slug}""");
        var suppliers = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {accepted[0]!.TenantId} AND linked_tenant_id = {wholesaler.TenantId}
            """);
        var customers = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM customers.customers
            WHERE tenant_id = {wholesaler.TenantId} AND identification_number = '901222333'
            """);
        Assert.Equal((1, 1, 1), (tenants[0], suppliers[0], customers[0]));
    }

    // El token no queda en la auditoría ni en el log de fallas (platform.request_failures), aunque una aceptación falle.
    [Fact]
    public async Task TheTokenNeverAppearsInAuditOrRequestLogs()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var stranger = await WholesaleApiHarness.NewTenantAsync(Factory);
        using (var session = WholesaleApiHarness.CreateSession(Factory, stranger.SubjectId, stranger.TenantId))
        {
            var refused = await session.PostAsJsonAsync(
                $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant", displayName = "X", slug = NewSlug() }, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var anonymous = WholesaleApiHarness.CreateAnonymous(Factory))
        {
            var weak = await anonymous.PostAsJsonAsync(
                $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password", new { password = "1" }, Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        }

        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        (await AcceptNewTenantAsync(userId, token)).EnsureSuccessStatusCode();

        var masked = await WholesaleApiHarness.EventuallyAsync(
            () => WholesaleApiHarness.RowsAsync<string>(
                Factory,
                $"""
                SELECT path AS "Value" FROM platform.request_failures
                WHERE path LIKE '/api/v1/wholesale-invitations/%' ORDER BY occurred_at
                """),
            rows => rows.Count >= 2);
        Assert.Contains("/api/v1/wholesale-invitations/***/accept", masked);
        Assert.Contains("/api/v1/wholesale-invitations/***/session-with-password", masked);
        var leaks = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT ((SELECT count(*) FROM platform.request_failures f WHERE strpos(f::text, {token}) > 0)
                  + (SELECT count(*) FROM audit.entries e WHERE strpos(e::text, {token}) > 0))::int AS "Value"
            """);
        Assert.Equal(0, leaks[0]);
    }

    private static string NewEmail() => $"distri-{Guid.NewGuid():N}@distri.co";

    // Slug de 30: dentro del tope de Tenancy y único por prueba.
    private static string NewSlug() => $"distri-{Guid.NewGuid():N}"[..30];

    private async Task<HttpResponseMessage> AcceptNewTenantAsync(Guid userId, string token, string? slug = null)
    {
        using var session = WholesaleApiHarness.CreateSession(Factory, userId);
        return await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept",
            new { mode = "NewTenant", displayName = "Distri Norte", slug = slug ?? NewSlug() },
            Ct);
    }
}
