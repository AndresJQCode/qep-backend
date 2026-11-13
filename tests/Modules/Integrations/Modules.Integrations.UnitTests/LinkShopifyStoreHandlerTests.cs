using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-15 §4.3 paso 6, el enlace: permiso y módulo <c>shopify</c> en el tenant elegido (doble capa), la
/// instalación del mismo tenant si vino de A (<c>install_tenant_mismatch</c>), canjeada y vigente; tope y nombre; reenlace
/// del mismo tenant o <c>shop_already_linked</c> con el nombre del otro; webhooks que falten, y si
/// Shopify los rechaza la conexión queda <c>NeedsAttention</c> con <c>webhooks_not_registered</c>; la fila de instalación
/// se borra y se audita la variante.
/// </summary>
public sealed class LinkShopifyStoreHandlerTests
{
    private const string Shop = "mi-tienda.myshopify.com";

    private const string AccessToken = "shpat_link-token-SENTINEL-9f8e";

    private const string RefreshToken = "shprt_link-refresh-SENTINEL-9f8e";

    private const string RedirectUri = "https://qep.qcode.co/shopify/callback";

    private static readonly Uri Callback = new("https://qep-api.qcode.co/api/webhooks/shopify");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Bed
    {
        public IntegrationsTestBed Integrations { get; } = new();

        public InMemoryShopifyInstallRepository Installs { get; } = new();

        public FakeShopifyAdminGateway Gateway { get; } = new();

        public FakeConnectionRoutes Routes { get; } = new();

        public FakeTenantDirectory Tenants { get; } = new();

        public LinkShopifyStoreHandler Handler(IExecutionContext? context = null) =>
            new(
                Integrations.Catalog,
                Integrations.Repository,
                Integrations.Routes,
                Routes,
                Installs,
                Gateway,
                Integrations.ShopifyApp,
                Integrations.UnitOfWork,
                Integrations.Audit,
                Integrations.Events,
                Integrations.Protector,
                Integrations.Modules,
                Tenants,
                Integrations.Memberships,
                Integrations.AuthorNames,
                context ?? Integrations.Context(),
                Integrations.Clock,
                new LinkShopifyStoreValidator());

        /// <summary>Una instalación ya canjeada: A con <paramref name="initiatedTenantId"/>, B sin él.</summary>
        public ShopifyInstall SeedExchanged(
            Guid? initiatedTenantId = null, string token = AccessToken, DateTimeOffset? at = null, TimeSpan? tokenLifetime = null)
        {
            var now = at ?? IntegrationsTestBed.Now;
            var install = ShopifyInstall.Start(
                Guid.NewGuid(), Shop, "c3RhdGUtb2YtdGhlLWluc3RhbGwtMDEyMzQ1Njc4OWFi", RedirectUri,
                initiatedTenantId, initiatedTenantId is null ? null : Integrations.MemberId, now);
            install.Claim(ShopifyInstall.ClaimedStatePrefix + "Y2xhaW1lZC1zdGF0ZS0wMTIzNDU2Nzg5YWJjZGVm", now);
            install.MarkExchanged(
                "12345678", "Mi Tienda", "read_orders,read_customers",
                Integrations.Protector.Protect(install.Id, ShopifyFieldKeys.AccessToken, token),
                Integrations.Protector.Protect(install.Id, ShopifyFieldKeys.RefreshToken, RefreshToken),
                now + (tokenLifetime ?? TimeSpan.FromHours(1)),
                now.AddDays(90),
                now);
            Installs.Add(install);
            return install;
        }

        /// <summary>Una conexión shopify del tenant de la cama, con su ruta, como la dejó un enlace anterior.</summary>
        public IntegrationConnection SeedLinked(string name = "Mi Tienda", string token = "shpat_old-token-SENTINEL")
        {
            var connection = IntegrationConnection.Create(
                IntegrationProviders.Shopify,
                Integrations.TenantId,
                name,
                new Dictionary<string, string>
                {
                    [ShopifyFieldKeys.ShopDomain] = Shop,
                    [ShopifyFieldKeys.ShopName] = "Nombre viejo",
                    [ShopifyFieldKeys.ShopId] = "12345678",
                    [ShopifyFieldKeys.Scopes] = "read_orders,read_customers",
                    [ShopifyFieldKeys.WebhookSubscriptionIds] = "gid://shopify/WebhookSubscription/1",
                },
                new Dictionary<string, string> { [ShopifyFieldKeys.AccessToken] = token },
                Integrations.Protector.Protect,
                Integrations.MemberId,
                IntegrationsTestBed.Now.AddDays(-30));
            Integrations.Repository.Connections.Add(connection);
            Routes.Routes[(IntegrationProviders.Shopify.Key, Shop)] =
                new ConnectionRoute(Integrations.TenantId, connection.Id, connection.Status);
            return connection;
        }

        public LinkShopifyStoreCommand Command(Guid installId, string? name = null, Guid? tenantId = null) =>
            new(tenantId ?? Integrations.TenantId, installId, name);

        public string StoredToken(IntegrationConnection connection, string fieldKey = ShopifyFieldKeys.AccessToken)
        {
            var secret = Assert.Single(connection.Secrets, stored => stored.FieldKey == fieldKey);
            Assert.True(Integrations.Protector.TryUnprotect(connection.Id, secret.FieldKey, secret.Protected, out var plaintext));
            return plaintext;
        }
    }

    [Fact]
    public async Task LinkModuleOffThrows403()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();
        bed.Integrations.Modules.Sets[bed.Integrations.TenantId] =
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Shopify]));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, error.Code);
        Assert.Empty(bed.Gateway.Calls);
        Assert.Empty(bed.Integrations.Repository.Connections);
    }

    // Doble capa: el permiso y el tenant de la sesión se revalidan en el handler, antes de leer la instalación.
    [Fact]
    public async Task LinkWithoutPermissionOrFromAnotherTenantThrows403BeforeReadingTheInstall()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();
        bed.Integrations.Permissions = [IntegrationsPermissions.ConnectionRead];

        var withoutPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.Handler().HandleAsync(bed.Command(install.Id), Ct));
        bed.Integrations.Permissions = [IntegrationsPermissions.ConnectionManage];
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.Handler().HandleAsync(bed.Command(install.Id, tenantId: Guid.NewGuid()), Ct));

        Assert.Equal("authorization.denied", withoutPermission.Code);
        Assert.Equal("authorization.denied", otherTenant.Code);
        Assert.Empty(bed.Gateway.Calls);
        Assert.Empty(bed.Installs.Removed);
    }

    [Fact]
    public async Task LinkInstallFromOtherTenantThrowsTenantMismatch()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged(initiatedTenantId: Guid.NewGuid());

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallTenantMismatch, error.Code);
        Assert.Empty(bed.Gateway.Calls);
        Assert.Empty(bed.Integrations.Repository.Connections);
    }

    // A con este mismo tenant se enlaza; B (sin tenant) se puede enlazar desde cualquiera de los del usuario.
    [Fact]
    public async Task LinkInstallFromThisTenantOrWithoutTenantLinks()
    {
        var fromA = new Bed();
        var fromB = new Bed();

        await fromA.Handler().HandleAsync(fromA.Command(fromA.SeedExchanged(fromA.Integrations.TenantId).Id), Ct);
        await fromB.Handler().HandleAsync(fromB.Command(fromB.SeedExchanged().Id), Ct);

        Assert.Single(fromA.Integrations.Repository.Connections);
        Assert.Single(fromB.Integrations.Repository.Connections);
    }

    [Fact]
    public async Task LinkExpiredThrows()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();
        bed.Integrations.Clock.UtcNow = install.ExpiresAt.AddSeconds(1);

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallExpired, error.Code);
        Assert.Empty(bed.Gateway.Calls);
        Assert.Empty(bed.Integrations.Repository.Connections);
    }

    // Una fila sin canjear o un id desconocido son el mismo 404 que el GET: el installId sólo lo revela el canje.
    [Fact]
    public async Task LinkUnknownOrUnexchangedInstallThrowsInstallNotFound()
    {
        var bed = new Bed();
        var started = ShopifyInstall.Start(
            Guid.NewGuid(), Shop, "c3RhcnRlZC1idXQtbm90LWV4Y2hhbmdlZC0wMTIzNDU2", RedirectUri, null, null, IntegrationsTestBed.Now);
        bed.Installs.Add(started);

        var unknown = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.Handler().HandleAsync(bed.Command(Guid.NewGuid()), Ct));
        var unexchanged = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.Handler().HandleAsync(bed.Command(started.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, unknown.Code);
        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, unexchanged.Code);
    }

    [Fact]
    public async Task LinkLimitReachedThrows()
    {
        var bed = new Bed();
        for (var index = 0; index < IntegrationProviders.DefaultMaxConnections; index++)
        {
            bed.Integrations.Repository.Connections.Add(IntegrationConnection.Create(
                IntegrationProviders.Shopify,
                bed.Integrations.TenantId,
                $"Tienda {index}",
                new Dictionary<string, string> { [ShopifyFieldKeys.ShopDomain] = $"tienda-{index}.myshopify.com" },
                new Dictionary<string, string> { [ShopifyFieldKeys.AccessToken] = "shpat_x" },
                bed.Integrations.Protector.Protect,
                bed.Integrations.MemberId,
                IntegrationsTestBed.Now));
        }

        var install = bed.SeedExchanged();

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.LimitReached, error.Code);
        Assert.Equal(20, IntegrationProviders.Shopify.MaxConnections);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task LinkNameTakenThrows()
    {
        var bed = new Bed();
        bed.Integrations.Repository.Connections.Add(IntegrationConnection.Create(
            IntegrationProviders.Shopify,
            bed.Integrations.TenantId,
            "Mi Tienda",
            new Dictionary<string, string> { [ShopifyFieldKeys.ShopDomain] = "otra.myshopify.com" },
            new Dictionary<string, string> { [ShopifyFieldKeys.AccessToken] = "shpat_x" },
            bed.Integrations.Protector.Protect,
            bed.Integrations.MemberId,
            IntegrationsTestBed.Now));
        var install = bed.SeedExchanged();

        // Sin nombre, el de la tienda ("Mi Tienda"), que ya está usado.
        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.NameTaken, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task LinkInvalidNameIsAValidationFailureOnName()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.Handler().HandleAsync(bed.Command(install.Id, name: new string('x', 81)), Ct));

        Assert.Equal(["name"], error.Errors.Select(failure => failure.PropertyName).Distinct());
    }

    [Fact]
    public async Task LinkRegistersOnlyMissingTopics()
    {
        var bed = new Bed();
        bed.Gateway.ExistingWebhooks.Add(new ShopifyWebhookSubscription("gid://shopify/WebhookSubscription/100", "ORDERS_CREATE", Callback));
        // El mismo topic hacia otra URL no es nuestro: no cuenta.
        bed.Gateway.ExistingWebhooks.Add(
            new ShopifyWebhookSubscription("gid://shopify/WebhookSubscription/200", "APP_UNINSTALLED", new Uri("https://otra.example/hooks")));
        var install = bed.SeedExchanged();

        var response = await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.Equal(
            ["list-webhooks", "create-webhook:orders/updated", "create-webhook:orders/paid", "create-webhook:orders/cancelled", "create-webhook:app/uninstalled"],
            bed.Gateway.Calls);
        Assert.All(bed.Gateway.Tokens, token => Assert.Equal(AccessToken, token));
        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        var ids = connection.Fields[ShopifyFieldKeys.WebhookSubscriptionIds].Split(',');
        Assert.Equal(5, ids.Length);
        Assert.Equal("gid://shopify/WebhookSubscription/100", ids[0]);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(nameof(ConnectionStatus.Active), response.Status);
        Assert.Equal(
            ["orders/create", "orders/updated", "orders/paid", "orders/cancelled", "app/uninstalled"],
            ShopifyWebhookTopics.Registered);
    }

    [Fact]
    public async Task LinkCreatesConnectionRouteAndFieldsFromTheInstall()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();

        var response = await bed.Handler().HandleAsync(bed.Command(install.Id, name: "  Tienda principal  "), Ct);

        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        Assert.Equal(connection.Id, response.Id);
        Assert.Equal("Tienda principal", connection.Name);
        Assert.Equal(IntegrationProviders.Shopify.Key, connection.ProviderKey);
        Assert.Equal(Shop, connection.Fields[ShopifyFieldKeys.ShopDomain]);
        Assert.Equal("Mi Tienda", connection.Fields[ShopifyFieldKeys.ShopName]);
        Assert.Equal("12345678", connection.Fields[ShopifyFieldKeys.ShopId]);
        Assert.Equal("read_orders,read_customers", connection.Fields[ShopifyFieldKeys.Scopes]);
        Assert.Equal(AccessToken, bed.StoredToken(connection));
        Assert.Equal(bed.Integrations.MemberId, connection.CreatedBy);
        var route = Assert.Single(bed.Integrations.Routes.Added);
        Assert.Equal((IntegrationProviders.Shopify.Key, Shop, (string?)null, bed.Integrations.TenantId, connection.Id),
            (route.ProviderKey, route.ExternalId, route.AccountId, route.TenantId, route.ConnectionId));
        Assert.Equal(1, bed.Integrations.UnitOfWork.Saves);
        Assert.DoesNotContain(AccessToken, response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkWebhookRejectedLeavesNeedsAttentionWithCode()
    {
        var bed = new Bed();
        bed.Gateway.FailAt = "create-webhook";
        var install = bed.SeedExchanged();

        var response = await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Equal(ConnectionFailureCodes.WebhooksNotRegistered, connection.LastFailureCode);
        Assert.Equal(nameof(ConnectionStatus.NeedsAttention), response.Status);
        Assert.Single(bed.Integrations.Routes.Added);
        Assert.Equal([install.Id], bed.Installs.Removed);
        Assert.Contains(bed.Integrations.Audit.Entries, entry => entry.Action == ConnectionAuditActions.NeedsAttention);
        Assert.Equal(1, bed.Integrations.UnitOfWork.Saves);
    }

    [Fact]
    public async Task LinkWebhookListUnreachableAlsoLeavesNeedsAttention()
    {
        var bed = new Bed();
        bed.Gateway.FailAt = "list-webhooks";
        bed.Gateway.Failure = ShopifyGatewayFailure.Unreachable;
        var install = bed.SeedExchanged();

        await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Equal(ConnectionFailureCodes.WebhooksNotRegistered, connection.LastFailureCode);
    }

    [Fact]
    public async Task LinkSameTenantRelinkReactivatesWithNewToken()
    {
        var bed = new Bed();
        var existing = bed.SeedLinked();
        existing.MarkNeedsAttention(ConnectionFailureCodes.AppUninstalled, IntegrationsTestBed.Now.AddDays(-1));
        var install = bed.SeedExchanged();

        var response = await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        Assert.Same(existing, connection);
        Assert.Equal(existing.Id, response.Id);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(AccessToken, bed.StoredToken(connection));
        Assert.Equal("Mi Tienda", connection.Fields[ShopifyFieldKeys.ShopName]);
        Assert.Equal(5, connection.Fields[ShopifyFieldKeys.WebhookSubscriptionIds].Split(',').Length);
        Assert.Empty(bed.Integrations.Routes.Added);
        Assert.Equal([install.Id], bed.Installs.Removed);
        Assert.Contains(bed.Integrations.Audit.Entries, entry =>
            entry.Action == ConnectionAuditActions.Updated && entry.ConnectionId == existing.Id
            && entry.ChangedFields.Contains(ShopifyFieldKeys.AccessToken));
    }

    // §4.1 dice que el reenlace «la reactiva con el token nuevo»; una conexión pausada también vuelve a Active.
    [Fact]
    public async Task LinkSameTenantRelinkOverPausedReactivates()
    {
        var bed = new Bed();
        var existing = bed.SeedLinked();
        existing.Pause(IntegrationsTestBed.Now.AddDays(-1));
        var install = bed.SeedExchanged();

        var response = await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.Equal(ConnectionStatus.Active, existing.Status);
        Assert.Equal(nameof(ConnectionStatus.Active), response.Status);
        Assert.Equal(AccessToken, bed.StoredToken(existing));
        Assert.Contains(bed.Integrations.Audit.Entries, entry =>
            entry.Action == ConnectionAuditActions.Resumed && entry.ConnectionId == existing.Id);
    }

    // El reenlace no cuenta contra el tope: la conexión ya existe.
    [Fact]
    public async Task LinkSameTenantRelinkIgnoresTheLimitAndItsOwnName()
    {
        var bed = new Bed();
        bed.SeedLinked(name: "Mi Tienda");
        for (var index = 1; index < IntegrationProviders.DefaultMaxConnections; index++)
        {
            bed.Integrations.Repository.Connections.Add(IntegrationConnection.Create(
                IntegrationProviders.Shopify,
                bed.Integrations.TenantId,
                $"Tienda {index}",
                new Dictionary<string, string> { [ShopifyFieldKeys.ShopDomain] = $"tienda-{index}.myshopify.com" },
                new Dictionary<string, string> { [ShopifyFieldKeys.AccessToken] = "shpat_x" },
                bed.Integrations.Protector.Protect,
                bed.Integrations.MemberId,
                IntegrationsTestBed.Now));
        }

        var install = bed.SeedExchanged();

        var response = await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.Equal("Mi Tienda", response.Name);
    }

    [Fact]
    public async Task LinkShopLinkedToOtherTenantReturns422WithTenantName()
    {
        var bed = new Bed();
        var otherTenant = Guid.NewGuid();
        bed.Routes.Routes[(IntegrationProviders.Shopify.Key, Shop)] = new ConnectionRoute(otherTenant, Guid.NewGuid(), ConnectionStatus.NeedsAttention);
        bed.Tenants.Names[otherTenant] = "Distribuidora Andina";
        var install = bed.SeedExchanged();

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyShopAlreadyLinked, error.Code);
        Assert.Contains("Distribuidora Andina", error.Message, StringComparison.Ordinal);
        // El nombre y nada más; ni el id del tenant ni el de la conexión.
        Assert.DoesNotContain(otherTenant.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(bed.Gateway.Calls);
        Assert.Empty(bed.Integrations.Repository.Connections);
        Assert.Empty(bed.Installs.Removed);
    }

    [Theory]
    [InlineData(true, "A")]
    [InlineData(false, "B")]
    public async Task LinkDeletesInstallAndAuditsOnboardingVariant(bool fromQep, string variant)
    {
        var bed = new Bed();
        var install = bed.SeedExchanged(fromQep ? bed.Integrations.TenantId : null);

        var response = await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.Equal([install.Id], bed.Installs.Removed);
        var created = Assert.Single(bed.Integrations.Audit.Entries, entry => entry.Action == ConnectionAuditActions.Created);
        Assert.Equal(response.Id, created.ConnectionId);
        Assert.Equal(bed.Integrations.TenantId, created.TenantId);
        Assert.Equal(bed.Integrations.SubjectId, created.ActorId);
        Assert.Contains(LinkShopifyStoreHandler.OnboardingAuditField, created.ChangedFields);
        Assert.Contains($"{LinkShopifyStoreHandler.OnboardingAuditField}:{variant}", created.ChangedFields);
        Assert.DoesNotContain(created.ChangedFields, field => field.Contains(AccessToken, StringComparison.Ordinal));
        Assert.Equal("onboarding:shopify_oauth", LinkShopifyStoreHandler.OnboardingAuditField);
    }

    // Sin llave activa: 503 antes de tocar Shopify (no se puede guardar el token).
    [Fact]
    public async Task LinkWithoutAnActiveKeyIsServiceUnavailable()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();
        bed.Integrations.Protector.ActiveKeyId = null;

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Empty(bed.Gateway.Calls);
    }

    // El token de la instalación ya no descifra (llave retirada): no hay con qué enlazar; volver a instalar.
    [Fact]
    public async Task LinkWithAnUnreadableTokenThrowsInstallExpired()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();
        bed.Integrations.Protector.Unreadable.Add((install.Id, ShopifyFieldKeys.AccessToken));

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallExpired, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    // Local sin túnel: sin URL de webhooks no hay nada que registrar; la conexión queda activa y la conciliación cubre.
    [Fact]
    public async Task LinkWithoutCallbackUrlSkipsTheWebhooks()
    {
        var bed = new Bed();
        bed.Integrations.ShopifyApp.WebhookCallbackUrl = null;
        var install = bed.SeedExchanged();

        await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.Empty(bed.Gateway.Calls);
        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.False(connection.Fields.ContainsKey(ShopifyFieldKeys.WebhookSubscriptionIds));
    }

    // Spec 2026-11-12 §6.1: la conexión guarda el refresh token de la instalación y los dos vencimientos.
    [Fact]
    public async Task LinkStoresTheRefreshTokenAndBothExpiries()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();

        await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        Assert.Equal(AccessToken, bed.StoredToken(connection));
        Assert.Equal(RefreshToken, bed.StoredToken(connection, ShopifyFieldKeys.RefreshToken));
        Assert.Equal(ShopifyTokenRefresh.Format(IntegrationsTestBed.Now.AddHours(1)), connection.Fields[ShopifyFieldKeys.TokenExpiresAt]);
        Assert.Equal(ShopifyTokenRefresh.Format(IntegrationsTestBed.Now.AddDays(90)), connection.Fields[ShopifyFieldKeys.RefreshTokenExpiresAt]);
        Assert.DoesNotContain("refresh", bed.Gateway.Calls);
    }

    // La instalación vive 7 días y su token, 1 hora: enlazar una vieja refresca el token antes de registrar webhooks.
    [Fact]
    public async Task LinkAnInstallOlderThanItsTokenRefreshesBeforeTheWebhooks()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged(tokenLifetime: TimeSpan.FromMinutes(10));
        var refreshed = bed.Gateway.RefreshedGrant;

        await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.Equal("refresh", bed.Gateway.Calls[0]);
        Assert.Equal([RefreshToken], bed.Gateway.RefreshTokens);
        Assert.NotEmpty(bed.Gateway.Tokens);
        Assert.All(bed.Gateway.Tokens, token => Assert.Equal(refreshed.AccessToken, token));
        Assert.Equal([install.Id], bed.Integrations.UnitOfWork.Locks);
        Assert.Equal([install.Id], bed.Installs.StoredRefreshes);
        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        Assert.Equal(refreshed.AccessToken, bed.StoredToken(connection));
        Assert.Equal(refreshed.RefreshToken, bed.StoredToken(connection, ShopifyFieldKeys.RefreshToken));
        Assert.Equal(ShopifyTokenRefresh.Format(IntegrationsTestBed.Now.AddHours(1)), connection.Fields[ShopifyFieldKeys.TokenExpiresAt]);
    }

    // Otro enlace la refrescó mientras éste esperaba el candado: se usa el par releído y no se vuelve a refrescar.
    [Fact]
    public async Task LinkAnInstallRefreshedByAnotherLinkMeanwhileUsesTheStoredPair()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged(tokenLifetime: TimeSpan.FromMinutes(10));
        bed.Installs.OnFind = found =>
        {
            // La segunda lectura (la de después del candado) ya ve el par que guardó el otro enlace.
            if (bed.Integrations.UnitOfWork.Locks.Count > 0 && found.NeedsTokenRefresh(IntegrationsTestBed.Now))
            {
                found.ApplyRefreshedToken(
                    bed.Integrations.Protector.Protect(found.Id, ShopifyFieldKeys.AccessToken, "shpat_other-link-SENTINEL"),
                    bed.Integrations.Protector.Protect(found.Id, ShopifyFieldKeys.RefreshToken, "shprt_other-link-SENTINEL"),
                    IntegrationsTestBed.Now.AddHours(1),
                    IntegrationsTestBed.Now.AddDays(90));
            }
        };

        await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.DoesNotContain("refresh", bed.Gateway.Calls);
        var connection = Assert.Single(bed.Integrations.Repository.Connections);
        Assert.Equal("shpat_other-link-SENTINEL", bed.StoredToken(connection));
        Assert.Equal("shprt_other-link-SENTINEL", bed.StoredToken(connection, ShopifyFieldKeys.RefreshToken));
    }

    [Fact]
    public async Task LinkWhenTheInstallRefreshIsRejectedFailsWithoutAConnection()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged(tokenLifetime: TimeSpan.FromMinutes(10));
        bed.Gateway.FailAt = "refresh";

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallExpired, error.Code);
        Assert.Empty(bed.Integrations.Repository.Connections);
        Assert.Empty(bed.Integrations.Routes.Added);
        Assert.Empty(bed.Installs.Removed);
        Assert.Equal(["refresh"], bed.Gateway.Calls);
        Assert.DoesNotContain(RefreshToken, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ShopifyGatewayFailure.Unreachable)]
    [InlineData(ShopifyGatewayFailure.Invalid)]
    public async Task LinkWhenTheInstallRefreshIsUnreachableIs503(ShopifyGatewayFailure failure)
    {
        var bed = new Bed();
        var install = bed.SeedExchanged(tokenLifetime: TimeSpan.FromMinutes(10));
        bed.Gateway.FailAt = "refresh";
        bed.Gateway.Failure = failure;

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ProviderUnreachable, error.Code);
        Assert.Empty(bed.Integrations.Repository.Connections);
        Assert.Empty(bed.Installs.Removed);
    }

    // Una fila canjeada antes de este cambio no tiene refresh token: crearía una conexión que se cae en una hora. El
    // dominio ya no deja armarla, así que se quita por reflexión, como quedaría leída de la base.
    [Fact]
    public async Task LinkAnInstallWithoutARefreshTokenIsInstallExpired()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();
        typeof(ShopifyInstall).GetProperty(nameof(ShopifyInstall.ProtectedRefreshToken))!.SetValue(install, null);
        typeof(ShopifyInstall).GetProperty(nameof(ShopifyInstall.AccessTokenExpiresAt))!.SetValue(install, null);

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallExpired, error.Code);
        Assert.Empty(bed.Gateway.Calls);
        Assert.Empty(bed.Integrations.Repository.Connections);
    }

    [Fact]
    public async Task LinkWithAnUnreadableRefreshTokenThrowsInstallExpired()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();
        bed.Integrations.Protector.Unreadable.Add((install.Id, ShopifyFieldKeys.RefreshToken));

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallExpired, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    // D3: el reenlace toma el candado de la conexión antes de cargarla, en su propia transacción.
    [Fact]
    public async Task LinkRelinkLocksTheConnectionBeforeLoadingIt()
    {
        var bed = new Bed();
        var existing = bed.SeedLinked();
        var install = bed.SeedExchanged();
        var lockedBeforeLoad = false;
        bed.Integrations.Repository.OnFind = () => lockedBeforeLoad = bed.Integrations.UnitOfWork.Locks.Contains(existing.Id);

        await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.True(lockedBeforeLoad);
        Assert.Equal([existing.Id], bed.Integrations.UnitOfWork.Locks);
        Assert.Equal(1, bed.Integrations.UnitOfWork.Transactions);
        Assert.Equal(RefreshToken, bed.StoredToken(existing, ShopifyFieldKeys.RefreshToken));
        Assert.Equal(ShopifyTokenRefresh.Format(IntegrationsTestBed.Now.AddHours(1)), existing.Fields[ShopifyFieldKeys.TokenExpiresAt]);
    }

    // D3: una conexión nueva no abre transacción, para que la carrera por la ruta se siga traduciendo.
    [Fact]
    public async Task LinkNewConnectionOpensNoTransaction()
    {
        var bed = new Bed();
        var install = bed.SeedExchanged();

        await bed.Handler().HandleAsync(bed.Command(install.Id), Ct);

        Assert.Equal(0, bed.Integrations.UnitOfWork.Transactions);
        Assert.Empty(bed.Integrations.UnitOfWork.Locks);
    }

    [Fact]
    public void CommandToStringCarriesNoToken()
    {
        var command = new LinkShopifyStoreCommand(Guid.NewGuid(), Guid.NewGuid(), "Tienda");

        Assert.Contains("Tienda", command.ToString(), StringComparison.Ordinal);
    }
}
