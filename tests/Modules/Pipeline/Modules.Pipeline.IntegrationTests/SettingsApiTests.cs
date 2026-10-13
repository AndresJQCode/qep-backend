using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §6 (ajustes), D11, D12, D31 y P8 del plan: las etapas y los dos catálogos, sembrados en el primer
/// acceso, reemplazados enteros, con versión en las etapas y auditados.</summary>
public sealed class SettingsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string StagesUrl(Guid tenantId) => $"{PipelineUrl(tenantId)}/settings/stages";

    private static async Task<(RegisteredTenant Tenant, HttpClient Client)> TenantAsync(QepApiFactory factory, string connectionString, params string[] permissions)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        return (tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions));
    }

    private static List<StageRequest> Layout(PipelineStagesDto stages) =>
        [.. stages.Stages.Select(stage => new StageRequest(stage.Id, stage.Name, stage.Kind, stage.Probability, stage.StallDays, stage.IsQuotationStage))];

    [Fact]
    public async Task TheFirstReadSeedsTheDefaultLayout()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, ReadPermissions);
        using var _ = client;

        var stages = await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct);

        Assert.NotNull(stages);
        Assert.Equal(1L, stages.Version);
        Assert.Equal(["Nuevo", "Calificado", "Cotizado", "Negociación", "Ganado", "Perdido"], stages.Stages.Select(stage => stage.Name));
        Assert.Equal(["Open", "Open", "Open", "Open", "Won", "Lost"], stages.Stages.Select(stage => stage.Kind));
        Assert.Equal("Cotizado", Assert.Single(stages.Stages, stage => stage.IsQuotationStage).Name);
    }

    [Fact]
    public async Task ReplacingTheStagesRenamesAddsBumpsTheVersionAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var stages = (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!;
        var layout = Layout(stages);
        layout[0] = layout[0] with { Name = "Prospecto" };
        layout.Insert(3, new StageRequest(null, "Demostración", "Open", 40, 4, false));

        var response = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, layout), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replaced = await response.Content.ReadFromJsonAsync<PipelineStagesDto>(Ct);
        Assert.NotNull(replaced);
        Assert.Equal(2L, replaced.Version);
        Assert.Equal(stages.Stages[0].Id, replaced.Stages[0].Id);
        Assert.Equal(
            ["Prospecto", "Calificado", "Cotizado", "Demostración", "Negociación", "Ganado", "Perdido"],
            replaced.Stages.Select(stage => stage.Name));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.stages.replaced' AND source = 'pipeline'",
            ("t", tenant.TenantId)));
    }

    [Fact]
    public async Task AStaleVersionIs412AndNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var stages = (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!;
        var layout = Layout(stages);
        layout[0] = layout[0] with { Name = "Prospecto" };
        (await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, layout), Ct)).EnsureSuccessStatusCode();
        layout[0] = layout[0] with { Name = "Otro" };

        var stale = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, layout), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(stale)).Code);
        Assert.Equal("Prospecto", (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!.Stages[0].Name);
    }

    // Review Focus 2: borrar la etapa de un negocio no deja negocios huérfanos ni media disposición aplicada.
    [Fact]
    public async Task RemovingAStageWithDealsIs422AndTheLayoutStays()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var stages = (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!;
        using (var scope = factory.Services.CreateScope())
        {
            var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenant.TenantId, Ct);
            scope.ServiceProvider.GetRequiredService<IDealRepository>().Add(Deal.Create(
                tenant.TenantId, pipeline, Guid.CreateVersion7(), Guid.CreateVersion7(), "Dotación", null, "COP", null,
                DealSource.Manual, null, DealActor.ForSystem, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        var layout = Layout(stages);
        layout.RemoveAt(0);

        var response = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, layout), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(PipelineErrorCodes.StageHasDeals, (await ProblemAsync(response)).Code);
        Assert.Equal(6, (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!.Stages.Count);
    }

    [Fact]
    public async Task ABrokenLayoutIs422LayoutInvalidAndABlankNameIsValidationFailed()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var stages = (await client.GetFromJsonAsync<PipelineStagesDto>(StagesUrl(tenant.TenantId), Ct))!;
        var twoQuotationStages = Layout(stages);
        twoQuotationStages[0] = twoQuotationStages[0] with { IsQuotationStage = true };
        var blankName = Layout(stages);
        blankName[1] = blankName[1] with { Name = "  " };

        var broken = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, twoQuotationStages), Ct);
        var blank = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(stages.Version, blankName), Ct);
        var noVersion = await client.PutAsJsonAsync(StagesUrl(tenant.TenantId), new ReplaceStagesRequest(null, Layout(stages)), Ct);

        Assert.Equal(PipelineErrorCodes.StageLayoutInvalid, (await ProblemAsync(broken)).Code);
        var (blankCode, blankKeys) = await ProblemAsync(blank);
        Assert.Equal("validation.failed", blankCode);
        Assert.Contains("stages[1].name", blankKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["version"], (await ProblemAsync(noVersion)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
    }

    // P8: el catálogo se reemplaza entero; el que deja de venir se desactiva y no vuelve en el GET.
    [Fact]
    public async Task TheReasonCatalogsAreSeededAndReplacedIndependently()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, client) = await TenantAsync(factory, connectionString, AllPermissions);
        using var _ = client;
        var lossUrl = $"{PipelineUrl(tenant.TenantId)}/settings/loss-reasons";
        var dismissUrl = $"{PipelineUrl(tenant.TenantId)}/settings/dismiss-reasons";
        var loss = (await client.GetFromJsonAsync<ReasonListDto>(lossUrl, Ct))!;

        var response = await client.PutAsJsonAsync(lossUrl, new ReplaceReasonsRequest(
            [new ReasonRequest(loss.Items[1].Id, "Competencia directa"), new ReasonRequest(null, "Tiempo de entrega")]), Ct);

        Assert.Equal(("Loss", 5), (loss.Kind, loss.Items.Count));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replaced = (await response.Content.ReadFromJsonAsync<ReasonListDto>(Ct))!;
        Assert.Equal(["Competencia directa", "Tiempo de entrega"], replaced.Items.Select(item => item.Name));
        Assert.Equal(loss.Items[1].Id, replaced.Items[0].Id);
        Assert.Equal(["Competencia directa", "Tiempo de entrega"], (await client.GetFromJsonAsync<ReasonListDto>(lossUrl, Ct))!.Items.Select(item => item.Name));
        Assert.Equal(["Soporte", "Recompra", "Proveedor", "Spam", "Otro"], (await client.GetFromJsonAsync<ReasonListDto>(dismissUrl, Ct))!.Items.Select(item => item.Name));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.loss_reasons.replaced'", ("t", tenant.TenantId)));
    }

    [Fact]
    public async Task WithoutTheCapabilityOrThePermissionOrFromAnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        using var withoutCapability = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AllPermissions);
        using var reader = CreateClient(factory, other.OwnerUserId, other.TenantId, ReadPermissions);
        await EnablePipelineAsync(connectionString, other.TenantId);

        var disabled = await withoutCapability.GetAsync(StagesUrl(tenant.TenantId), Ct);
        var noPermission = await reader.PutAsJsonAsync(StagesUrl(other.TenantId), new ReplaceStagesRequest(1, []), Ct);
        var foreignRoute = await reader.GetAsync(StagesUrl(tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRoute.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(foreignRoute)).Code);
    }
}
