using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Endpoints» (las tres lecturas) y «Visibilidad para un tenant»: sólo lo visible,
/// nada borrado por estar oculto, orden estable, 403 antes de leer y 404 dentro del tenant.
/// </summary>
public sealed class ReadHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCatalogShowsTheVisibleProvidersWithTheirFieldsAndCount()
    {
        var bed = new IntegrationsTestBed();
        bed.ShowEverything();
        bed.Seed("Norte");
        bed.Seed("Sur");
        bed.Seed("De otro tenant", tenantId: Guid.CreateVersion7());

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        var zenvia = Assert.Single(catalog.Providers, provider => provider.Key == "zenvia");
        Assert.Equal("Zenvia (WhatsApp)", zenvia.DisplayName);
        Assert.Equal("Messaging", zenvia.Category);
        Assert.Equal(20, zenvia.MaxConnections);
        Assert.Equal(2, zenvia.ConnectionCount);
        Assert.Equal(["apiToken", "fromNumber"], zenvia.Fields.Select(field => field.Key));
        Assert.Equal(["Secret", "Phone"], zenvia.Fields.Select(field => field.Kind));
        Assert.All(zenvia.Fields, field => Assert.True(field.Required));
    }

    [Fact]
    public async Task TheCatalogIsEmptyButPresentWhenNoConsumingModuleIsOn()
    {
        var bed = new IntegrationsTestBed();
        // Ni quotations (Zenvia) ni messaging (whatsapp-cloud).
        bed.Modules.Sets[bed.TenantId] = TenantModuleSet.FromStored(
            TenantModuleKeys.All.Except([TenantModuleKeys.Quotations, TenantModuleKeys.Messaging]));

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        Assert.Empty(catalog.Providers);
    }

    [Fact]
    public async Task TheDevelopmentStubSeesTheWholeCatalog()
    {
        var bed = new IntegrationsTestBed();

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        Assert.Equal(["zenvia", "whatsapp-cloud"], catalog.Providers.Select(provider => provider.Key));
    }

    // Spec §5.2: cada proveedor trae onboarding; los campos internos no salen del catálogo.
    [Fact]
    public async Task TheCatalogCarriesOnboardingAndHidesInternalFields()
    {
        var bed = new IntegrationsTestBed();
        bed.MetaApp.Configured = true;

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        var zenvia = Assert.Single(catalog.Providers, provider => provider.Key == "zenvia");
        Assert.Equal("Form", zenvia.Onboarding.Kind);
        Assert.Null(zenvia.Onboarding.AppId);
        var whatsapp = Assert.Single(catalog.Providers, provider => provider.Key == "whatsapp-cloud");
        Assert.Equal("MetaEmbeddedSignup", whatsapp.Onboarding.Kind);
        Assert.Equal("100200300", whatsapp.Onboarding.AppId);
        Assert.Equal("400500600", whatsapp.Onboarding.ConfigId);
        Assert.Equal("v24.0", whatsapp.Onboarding.GraphApiVersion);
        Assert.Equal(["displayPhoneNumber", "verifiedName"], whatsapp.Fields.Select(field => field.Key));
    }

    // Spec §5.2: con Form, appId, configId y graphApiVersion no viajan; la forma es { "kind": "Form" }.
    [Fact]
    public async Task AFormOnboardingSerializesAsKindOnly()
    {
        var bed = new IntegrationsTestBed();

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        var zenvia = Assert.Single(catalog.Providers, provider => provider.Key == "zenvia");
        Assert.Equal("{\"kind\":\"Form\"}", JsonSerializer.Serialize(zenvia.Onboarding, JsonSerializerOptions.Web));
    }

    // D-M3: sin Meta:App (sólo fuera de producción) whatsapp-cloud no sale.
    [Fact]
    public async Task WithoutMetaAppWhatsAppCloudIsNotInTheCatalog()
    {
        var bed = new IntegrationsTestBed();
        bed.MetaApp.Configured = false;

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        Assert.DoesNotContain(catalog.Providers, provider => provider.Key == "whatsapp-cloud");
    }

    [Fact]
    public async Task ReadingNeedsTheReadPermissionAndTheRouteTenant()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        var otherTenant = bed.Context(Guid.CreateVersion7());
        bed.Permissions = [];
        var noPermission = bed.Context();

        foreach (var context in new IExecutionContext[] { otherTenant, noPermission })
        {
            Assert.Equal("authorization.denied", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                bed.CatalogHandler(context).HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct))).Code);
            Assert.Equal("authorization.denied", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                bed.ListHandler(context).HandleAsync(new ListConnectionsQuery(bed.TenantId), Ct))).Code);
            Assert.Equal("authorization.denied", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                bed.GetHandler(context).HandleAsync(new GetConnectionQuery(bed.TenantId, connection.Id), Ct))).Code);
        }
    }

    [Fact]
    public async Task TheListIsOrderedByProviderThenNameAndCarriesNoSecretValue()
    {
        var bed = new IntegrationsTestBed();
        bed.Seed("sur");
        bed.Seed("Norte");
        bed.Seed("alto");

        var list = await bed.ListHandler().HandleAsync(new ListConnectionsQuery(bed.TenantId), Ct);

        Assert.Equal(["alto", "Norte", "sur"], list.Items.Select(item => item.Name));
        var first = list.Items[0];
        Assert.Equal("Active", first.Status);
        Assert.Equal(IntegrationsTestBed.FromNumber, first.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal(["apiToken"], first.Secrets.Keys);
        Assert.True(first.Secrets[ZenviaFieldKeys.ApiToken].Configured);
        Assert.True(first.Secrets[ZenviaFieldKeys.ApiToken].Readable);
        Assert.Equal(IntegrationsTestBed.Now, first.Secrets[ZenviaFieldKeys.ApiToken].UpdatedAt);
        Assert.Equal(bed.MemberId, first.CreatedBy.MemberId);
        Assert.Equal(IntegrationsTestBed.AuthorName, first.CreatedBy.DisplayName);
        Assert.DoesNotContain(IntegrationsTestBed.Token, JsonSerializer.Serialize(list), StringComparison.Ordinal);
    }

    // Spec, «Visibilidad»: lo de un proveedor oculto no se borra ni se pausa, sólo no se muestra.
    [Fact]
    public async Task TheListHidesConnectionsOfHiddenProvidersWithoutTouchingThem()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.HideQuotations();

        var list = await bed.ListHandler().HandleAsync(new ListConnectionsQuery(bed.TenantId), Ct);

        Assert.Empty(list.Items);
        Assert.Same(connection, Assert.Single(bed.Repository.Connections));
        Assert.Equal(ConnectionStatus.Active, connection.Status);
    }

    [Fact]
    public async Task AnUnreadableSecretIsConfiguredButNotReadable()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Protector.Unreadable.Add((connection.Id, ZenviaFieldKeys.ApiToken));

        var response = await bed.GetHandler().HandleAsync(new GetConnectionQuery(bed.TenantId, connection.Id), Ct);

        Assert.True(response.Secrets[ZenviaFieldKeys.ApiToken].Configured);
        Assert.False(response.Secrets[ZenviaFieldKeys.ApiToken].Readable);
    }

    // Un id de otro tenant, pedido por la ruta del propio, responde igual que uno inexistente.
    [Fact]
    public async Task AnIdOfAnotherTenantIsNotFoundInsideTheRouteTenant()
    {
        var bed = new IntegrationsTestBed();
        var foreign = bed.Seed(tenantId: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.GetHandler().HandleAsync(new GetConnectionQuery(bed.TenantId, foreign.Id), Ct));

        Assert.Equal("integrations.connection.not_found", error.Code);
    }

    [Fact]
    public async Task AConnectionOfAHiddenProviderIsModuleNotEnabled()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.HideQuotations();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.GetHandler().HandleAsync(new GetConnectionQuery(bed.TenantId, connection.Id), Ct));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
    }

    [Fact]
    public async Task AnAuthorWhoseMembershipIsGoneHasNoName()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.AuthorNames.Names.Clear();

        var response = await bed.GetHandler().HandleAsync(new GetConnectionQuery(bed.TenantId, connection.Id), Ct);

        Assert.Equal(bed.MemberId, response.CreatedBy.MemberId);
        Assert.Null(response.CreatedBy.DisplayName);
    }
}
