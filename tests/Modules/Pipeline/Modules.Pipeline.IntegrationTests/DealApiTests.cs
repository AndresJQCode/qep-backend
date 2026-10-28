using System.Net;
using System.Net.Http.Json;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §4.2, §6, D13–D16, D19, D23 y D34: el ciclo de un negocio por la API, con el detalle completo en cada
/// respuesta (BFF), su historial, su auditoría y las fronteras de tenant, permiso, capacidad y versión.</summary>
public sealed class DealApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid CustomerId, string CustomerName, Guid OwnerMemberId);

    private static string DealsUrl(Guid tenantId) => $"{PipelineUrl(tenantId)}/deals";

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString, params string[] permissions)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, customerName) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions.Length == 0 ? ManagePermissions : permissions);
        return new Setup(factory, connectionString, tenant, client, customerId, customerName, owner);
    }

    private static async Task<DealDetailDto> CreateAsync(Setup setup, decimal? amount = 1_500_000m)
    {
        var response = await setup.Client.PostAsJsonAsync(
            DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación sede norte", amount, null, null, null), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static async Task<DealDetailDto> PostAsync(Setup setup, string url, object body)
    {
        var response = await setup.Client.PostAsJsonAsync(url, body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static async Task<Guid> StageIdAsync(Setup setup, string name) =>
        (await setup.Client.GetFromJsonAsync<PipelineStagesDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/stages", Ct))!
            .Stages.Single(stage => stage.Name == name).Id;

    [Fact]
    public async Task CreatingADealReturnsTheFullDetailInTheFirstStageAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;

        var deal = await CreateAsync(setup);

        Assert.Equal(("Dotación sede norte", setup.CustomerId, setup.CustomerName, false), (deal.Title, deal.CustomerId, deal.CustomerName, deal.CustomerIsComplete));
        Assert.Equal((setup.OwnerMemberId, (decimal?)1_500_000m, "COP", true), (deal.OwnerMemberId, deal.Amount, deal.Currency, deal.AmountIsEditable));
        Assert.Equal(("Nuevo", "Open", "Manual", 0, false, 1L), (deal.StageName, deal.StageKind, deal.Source, deal.DaysInStage, deal.IsStalled, deal.Version));
        Assert.False(string.IsNullOrWhiteSpace(deal.OwnerName));
        var row = Assert.Single(deal.History);
        Assert.Equal(((string?)null, "Nuevo", "User", (Guid?)setup.OwnerMemberId), (row.FromStageName, row.ToStageName, row.ActorKind, row.ActorMemberId));
        Assert.Equal(1L, await CountAsync(setup.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'pipeline.deal.created'", ("id", deal.Id.ToString())));
    }

    // D34: sin monto cuenta, no suma; la moneda es la del tenant.
    [Fact]
    public async Task ADealCanBeCreatedWithoutAmount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;

        var deal = await CreateAsync(setup, amount: null);

        Assert.Equal(((decimal?)null, "COP"), (deal.Amount, deal.Currency));
    }

    [Fact]
    public async Task ABadCreateIs422WithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (foreignCustomer, _) = await SeedCustomerAsync(factory, Guid.CreateVersion7(), "Otro");
        var url = DealsUrl(setup.Tenant.TenantId);

        var foreign = await setup.Client.PostAsJsonAsync(url, new CreateDealRequest(foreignCustomer, "Dotación", null, null, null, null), Ct);
        var stranger = await setup.Client.PostAsJsonAsync(url, new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, Guid.CreateVersion7()), Ct);
        var blank = await setup.Client.PostAsJsonAsync(url, new CreateDealRequest(setup.CustomerId, "  ", null, "XYZ", null, null), Ct);

        Assert.Equal(PipelineErrorCodes.DealCustomerNotFound, (await ProblemAsync(foreign)).Code);
        Assert.Equal(PipelineErrorCodes.DealOwnerNotMember, (await ProblemAsync(stranger)).Code);
        var (code, keys) = await ProblemAsync(blank);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["currency", "title"], keys, StringComparer.OrdinalIgnoreCase);
    }

    // Spec 2026-10-24 §3.3 (ruling final #1): nada nuevo se le asigna a un fusionado. Una pestaña vieja que todavía muestra al
    // fusionado crea el negocio a nombre del que quedó, no del fusionado (que ya no sale en ninguna lista).
    [Fact]
    public async Task CreatingADealForAMergedCustomerCreatesItForTheSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (merged, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura G");
        await MarkMergedAsync(connectionString, merged, setup.CustomerId);

        var response = await setup.Client.PostAsJsonAsync(
            DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(merged, "Dotación sede norte", null, null, null, null), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var deal = (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        Assert.Equal((setup.CustomerId, setup.CustomerName), (deal.CustomerId, deal.CustomerName));
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.deals WHERE tenant_id = @tenant AND customer_id = @merged",
            ("tenant", setup.Tenant.TenantId), ("merged", merged)));
    }

    // Review Focus 4: un negocio de otro tenant bajo la ruta propia es 404 (no confirma que existe); la ruta ajena es 403.
    [Fact]
    public async Task AForeignDealIs404AndAnotherTenantsRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setupA = await SetupAsync(factory, connectionString);
        var setupB = await SetupAsync(factory, connectionString);
        using var a = setupA.Client;
        using var b = setupB.Client;
        var deal = await CreateAsync(setupA);

        var underOwnRoute = await b.GetAsync($"{DealsUrl(setupB.Tenant.TenantId)}/{deal.Id}", Ct);
        var underForeignRoute = await b.GetAsync($"{DealsUrl(setupA.Tenant.TenantId)}/{deal.Id}", Ct);
        var fetched = await a.GetFromJsonAsync<DealDetailDto>($"{DealsUrl(setupA.Tenant.TenantId)}/{deal.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, underOwnRoute.StatusCode);
        Assert.Equal(PipelineErrorCodes.DealNotFound, (await ProblemAsync(underOwnRoute)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, underForeignRoute.StatusCode);
        Assert.Equal(deal.Id, fetched!.Id);
    }

    // D13–D15 y D19: libre entre abiertas, perder con motivo del catálogo, reabrir a donde estaba y ganar; cada paso con su fila.
    [Fact]
    public async Task MoveLoseReopenAndWinWalkTheDealAndKeepTheHistory()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);
        var url = $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}";
        var negotiation = await StageIdAsync(setup, "Negociación");
        var price = (await setup.Client.GetFromJsonAsync<ReasonListDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/loss-reasons", Ct))!
            .Items.Single(item => item.Name == "Precio").Id;

        var moved = await PostAsync(setup, $"{url}/move", new MoveDealRequest(negotiation, deal.Version));
        var withoutReason = await setup.Client.PostAsJsonAsync($"{url}/lose", new LoseDealRequest(null, null, moved.Version), Ct);
        var unknownReason = await setup.Client.PostAsJsonAsync($"{url}/lose", new LoseDealRequest(Guid.CreateVersion7(), null, moved.Version), Ct);
        var lost = await PostAsync(setup, $"{url}/lose", new LoseDealRequest(price, " Se fue con la competencia ", moved.Version));
        var reopened = await PostAsync(setup, $"{url}/reopen", new DealVersionRequest(lost.Version));
        var won = await PostAsync(setup, $"{url}/win", new DealVersionRequest(reopened.Version));

        Assert.Equal("Negociación", moved.StageName);
        Assert.Equal(PipelineErrorCodes.DealLossReasonRequired, (await ProblemAsync(withoutReason)).Code);
        Assert.Equal(PipelineErrorCodes.DealLossReasonNotFound, (await ProblemAsync(unknownReason)).Code);
        Assert.Equal(("Perdido", "Precio", "Se fue con la competencia"), (lost.StageName, lost.LossReasonName, lost.LossNote));
        Assert.NotNull(lost.ClosedAt);
        Assert.Equal(("Negociación", (DateTimeOffset?)null, (string?)null), (reopened.StageName, reopened.ClosedAt, reopened.LossReasonName));
        Assert.Equal(("Ganado", "Won"), (won.StageName, won.StageKind));
        Assert.Equal(["Nuevo", "Negociación", "Perdido", "Negociación", "Ganado"], won.History.Select(row => row.ToStageName));
        Assert.Equal(4L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action IN ('pipeline.deal.moved','pipeline.deal.lost','pipeline.deal.reopened','pipeline.deal.won')",
            ("id", deal.Id.ToString())));
    }

    [Fact]
    public async Task MovingToAClosingStageIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);

        var response = await setup.Client.PostAsJsonAsync(
            $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}/move", new MoveDealRequest(await StageIdAsync(setup, "Ganado"), deal.Version), Ct);

        Assert.Equal(PipelineErrorCodes.DealStageNotOpen, (await ProblemAsync(response)).Code);
    }

    // Review Focus 3: el worker (u otra pestaña) tocó el negocio después de que la pantalla lo cargó.
    [Fact]
    public async Task AStaleVersionIs412AndNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);
        await ExecuteAsync(connectionString, "UPDATE pipeline.deals SET version = version + 1 WHERE id = @id", ("id", deal.Id));
        var url = $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}";

        var stale = await setup.Client.PostAsJsonAsync($"{url}/move", new MoveDealRequest(await StageIdAsync(setup, "Calificado"), deal.Version), Ct);
        var noVersion = await setup.Client.PostAsJsonAsync($"{url}/win", new DealVersionRequest(null), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(["version"], (await ProblemAsync(noVersion)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Nuevo", (await setup.Client.GetFromJsonAsync<DealDetailDto>(url, Ct))!.StageName);
    }

    // P5 del plan: el PATCH lleva todos los editables y los reemplaza.
    [Fact]
    public async Task PatchReplacesTheEditableFields()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);
        var (member, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Laura Gómez");

        var response = await setup.Client.PatchAsJsonAsync(
            $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}",
            new UpdateDealRequest(" Dotación 2027 ", member, 2_500m, "usd", new DateOnly(2026, 12, 15), deal.Version),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var patched = (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        Assert.Equal(("Dotación 2027", member, "Laura Gómez"), (patched.Title, patched.OwnerMemberId, patched.OwnerName));
        Assert.Equal(((decimal?)2_500m, "USD", (DateOnly?)new DateOnly(2026, 12, 15), 2L), (patched.Amount, patched.Currency, patched.ExpectedCloseDate, patched.Version));
    }

    // Sólo una membresía Active puede ser dueña: una quitada, suspendida o por aceptar existe en el tenant pero no recibe trabajo.
    [Theory]
    [InlineData("Removed")]
    [InlineData("Suspended")]
    [InlineData("Invited")]
    [InlineData("Expired")]
    public async Task ANonActiveMembershipCannotOwnADealOnCreateNorOnPatch(string state)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup);
        var (inactive, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Marta Ruiz", state: state);

        var create = await setup.Client.PostAsJsonAsync(
            DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, inactive), Ct);
        var patch = await setup.Client.PatchAsJsonAsync(
            $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}",
            new UpdateDealRequest("Dotación", inactive, null, "COP", null, deal.Version), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        Assert.Equal(PipelineErrorCodes.DealOwnerNotMember, (await ProblemAsync(create)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);
        Assert.Equal(PipelineErrorCodes.DealOwnerNotMember, (await ProblemAsync(patch)).Code);
    }

    // Un negocio puede conservar un dueño que dejó de estar activo (los que crea un evento de cotización, o un asesor suspendido
    // después): editar el título con el mismo dueño no exige reasignar. Reasignar a otro inactivo sigue siendo 422.
    [Fact]
    public async Task PatchKeepingAnInactiveOwnerIsAllowedButReassigningToAnInactiveOneIsNot()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (advisor, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Marta Ruiz");
        var (otherInactive, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Pedro Gil", state: "Suspended");
        var create = await setup.Client.PostAsJsonAsync(
            DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, advisor), Ct);
        var deal = (await create.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        await ExecuteAsync(connectionString, "UPDATE tenancy.memberships SET state = 'Suspended' WHERE id = @id", ("id", advisor));

        var sameOwner = await setup.Client.PatchAsJsonAsync(
            $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}",
            new UpdateDealRequest("Dotación renovada", advisor, null, "COP", null, deal.Version), Ct);
        var patched = (await sameOwner.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        var reassigned = await setup.Client.PatchAsJsonAsync(
            $"{DealsUrl(setup.Tenant.TenantId)}/{deal.Id}",
            new UpdateDealRequest("Dotación renovada", otherInactive, null, "COP", null, patched.Version), Ct);

        Assert.Equal(HttpStatusCode.OK, sameOwner.StatusCode);
        Assert.Equal(("Dotación renovada", advisor), (patched.Title, patched.OwnerMemberId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reassigned.StatusCode);
        Assert.Equal(PipelineErrorCodes.DealOwnerNotMember, (await ProblemAsync(reassigned)).Code);
    }

    // Un miembro activo sin nombre ni correo no aparece en el diccionario de nombres, pero sí es miembro: puede ser dueño.
    [Fact]
    public async Task AnActiveMemberWithoutNameOrEmailCanOwnADeal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (unnamed, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, displayName: null);

        var response = await setup.Client.PostAsJsonAsync(
            DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, unnamed), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(unnamed, (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!.OwnerMemberId);
    }

    [Fact]
    public async Task WithoutManageTheWritesAre403AndWithoutTheCapabilityModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);

        var forbidden = await reader.PostAsJsonAsync(DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, null), Ct);
        await DisableModuleAsync(connectionString, setup.Tenant.TenantId, "pipeline");
        var disabled = await setup.Client.PostAsJsonAsync(DealsUrl(setup.Tenant.TenantId), new CreateDealRequest(setup.CustomerId, "Dotación", null, null, null, null), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
    }
}
