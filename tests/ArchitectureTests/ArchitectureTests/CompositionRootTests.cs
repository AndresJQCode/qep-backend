using System.Reflection;
using Bootstrapper;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Authorization.Application;
using Modules.Automations.Application;
using Modules.Customers.Application;
using Modules.Integrations.Application;
using Modules.Messaging.Application;
using Modules.Pipeline.Application;
using Modules.Pos.Application;
using Modules.Shopify.Application;
using Modules.Suppliers.Application;
using Modules.Tenancy.Application;
using ModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;

namespace ArchitectureTests;

/// <summary>
/// El registro de handlers en el composition root es **a mano, uno por uno**. No hay escaneo de
/// ensamblados, así que un caso de uso nuevo cuyo endpoint se mapea pero cuyo handler nadie
/// registra compila, arranca y responde **500** — no 404, no un error de arranque: 500 en
/// producción, con el mensaje `No service for type ICommandHandler&lt;...&gt;`.
///
/// El ledger declaró ese riesgo textualmente al cerrar `CAT-06`, y **volvió a pasar igual**:
/// `PublishFileHandler` y `UnpublishFileHandler` nunca se registraron, así que el `PUT` y el
/// `DELETE` de `/files/{id}/publication` respondieron 500 desde el día que se escribieron. Y como
/// `imageUrl` de producto sólo tiene valor si el archivo fue publicado, la mitad de lectura de
/// `CAT-05b` estuvo muerta sin que ninguna prueba lo notara.
///
/// Advertirlo en un documento ya se demostró insuficiente. Esta prueba lo convierte en regla.
/// </summary>
public sealed class CompositionRootTests
{
    [Fact]
    public void EveryCommandAndQueryHasItsHandlerRegistered()
    {
        var registeredServices = BuildPlatformServices()
            .Select(descriptor => descriptor.ServiceType)
            .ToHashSet();

        var unregistered = MessageTypes()
            .Where(message => !registeredServices.Contains(HandlerTypeFor(message)))
            .Select(message => $"{message.Message.Name} -> {HandlerTypeFor(message).Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unregistered.Length == 0,
            "Estos casos de uso no tienen su handler registrado en QepServiceCollectionExtensions, "
                + "así que su endpoint responde 500: "
                + string.Join(", ", unregistered));
    }

    /// <summary>
    /// Sin al menos un mensaje encontrado, la prueba de arriba pasaría por vacía y no verificaría
    /// nada. Esto ancla que el descubrimiento por reflexión sigue viendo los ensamblados.
    /// </summary>
    [Fact]
    public void MessageDiscoveryFindsTheApplicationAssemblies()
    {
        Assert.NotEmpty(ApplicationAssemblies());
        Assert.NotEmpty(MessageTypes());
    }

    private static Type HandlerTypeFor(MessageType message) =>
        (message.IsCommand ? typeof(ICommandHandler<,>) : typeof(IQueryHandler<,>))
            .MakeGenericType(message.Message, message.Response);

    /// <summary>
    /// El contenedor puede construir de verdad lo que decide permisos.
    /// </summary>
    /// <remarks>
    /// Los demas casos de este archivo miran **que quedo registrado**; este **construye**. Hace
    /// falta porque <c>ITenantRoleCatalog</c> es la unica pieza scoped que depende de un
    /// DbContext propio y de un singleton a la vez: un registro mal armado ahi no rompe el
    /// build ni aparece en la lista de descriptores, aparece como 500 en el primer request
    /// autenticado — que es cada request.
    ///
    /// Y las pruebas de integracion, que lo cubririan levantando la app, necesitan Docker.
    /// </remarks>
    [Fact]
    public void TheContainerCanBuildTheTenantRoleCatalog()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var catalog = scope.ServiceProvider.GetRequiredService<ITenantRoleCatalog>();

        Assert.IsType<TenantRoleCatalog>(catalog);
    }

    /// <summary>
    /// La vista por tenant NO puede ser singleton: fusiona los roles que el tenant definio, y
    /// esos cambian con un PUT, no con un deploy. Registrada como singleton, el primer
    /// tenant en preguntar dejaria su catalogo cacheado para todo el proceso — y para todos
    /// los demas tenants.
    /// </summary>
    [Fact]
    public void TheTenantRoleCatalogIsScopedAndTheSystemOneIsNot()
    {
        var services = BuildPlatformServices();

        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(d => d.ServiceType == typeof(ITenantRoleCatalog)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Singleton,
            services.Single(d => d.ServiceType == typeof(IRoleCatalog)).Lifetime);
    }

    /// <summary>
    /// Spec 2026-10-07, criterio 4: un permiso sin módulo declarado se enmascara siempre, así que
    /// olvidarlo deja a todos sin ese permiso en silencio. Como el registro es a mano, esta prueba
    /// lo convierte en regla: toda constante de toda clase <c>*Permissions</c> de los ensamblados
    /// Application tiene su <see cref="PermissionDefinition"/> con <c>RequiredModules</c> no nulo.
    /// </summary>
    [Fact]
    public void EveryPermissionConstantDeclaresItsModules()
    {
        var definitions = RegisteredPermissionDefinitions()
            .GroupBy(definition => definition.Permission, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var unmapped = PermissionConstants()
            .Where(permission =>
                !definitions.TryGetValue(permission, out var definition) || definition.RequiredModules is null)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unmapped.Length == 0,
            "Estos permisos no tienen PermissionDefinition con RequiredModules en "
                + "QepServiceCollectionExtensions, así que se enmascaran siempre: "
                + string.Join(", ", unmapped));
    }

    /// <summary>
    /// <see cref="ModuleEntitlementMask"/> indexa por nombre con <c>ToDictionary</c>: un permiso
    /// registrado dos veces tumbaría el singleton al construirse. Esta prueba lo nombra antes.
    /// </summary>
    [Fact]
    public void EveryRegisteredPermissionStringIsUnique()
    {
        var duplicated = RegisteredPermissionDefinitions()
            .GroupBy(definition => definition.Permission, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            duplicated.Length == 0,
            "Estos permisos están registrados más de una vez como PermissionDefinition: "
                + string.Join(", ", duplicated));
    }

    /// <summary>Ningún permiso de un rol de sistema sale del fallback de metadata de RoleCatalog.</summary>
    [Fact]
    public void NoSystemRolePermissionComesFromTheMetadataFallback()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Empty(catalog.ListPermissions()
            .Where(permission => permission.RequiredModules is null)
            .Select(permission => permission.Permission));
    }

    /// <summary>Ancla: sin esto, las dos de arriba pasarían por vacías. Son las 35 del spec de
    /// entitlements, las 7 de POS, las tres <c>operator.*</c> y las dos <c>integrations.*</c> de los
    /// specs de 2026-10-08, las dos <c>messaging.*</c> del spec 2026-10-09, las dos de Customers
    /// (<c>customers.tag.manage</c>, <c>customers.note.moderate</c>), <c>messaging.campaign.manage</c>
    /// del spec 2026-10-11 y <c>messaging.quick_reply.manage</c> del spec 2026-10-12 las tres <c>pipeline.*</c> del spec 2026-10-13 <c>messaging.template.manage</c> del spec 2026-10-14 <c>messaging.resolution_reason.manage</c> del spec 2026-10-15 y <c>messaging.business_hours.manage</c> del spec 2026-10-21 y las dos <c>automations.chatbot.*</c> del mismo spec y <c>customers.customer.merge</c> del spec 2026-10-24.</summary>
    [Fact]
    public void PermissionDiscoveryFindsTheSixtyFourConstants()
    {
        // 46 + los dos de Messaging (spec 2026-10-09 §6.3) + los dos de Customers (spec 2026-10-11 §4.3) + el de campañas (§6.4)
        // + el de respuestas rápidas (spec 2026-10-12 §3.5) + los tres del embudo (spec 2026-10-13 D23)
        // + el de plantillas (spec 2026-10-14 D-T5) + el de motivos de resolución (spec 2026-10-15 D-R8)
        // + el de horarios de atención (spec 2026-10-21 D-H8) + los dos de chatbots (spec 2026-10-21 D-C10)
        // + el del cliente de la venta de POS (spec 2026-10-14, decisión 4)
        // + los dos de proveedores (suppliers, spec 2026-10-23) + el de fusionar clientes (spec 2026-10-24 D-F1).
        Assert.Equal(64, PermissionConstants().Length);
    }

    /// <summary>Spec 2026-10-11 §6.4 y decisión 16: de núcleo (el módulo lo revisa cada handler) y de fábrica sólo en <c>admin</c>:
    /// una campaña llega a miles de personas en nombre de la empresa y el costo es por mensaje.</summary>
    [Fact]
    public void OnlyTheAdminRoleCarriesTheCampaignPermissionAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Contains(MessagingPermissions.CampaignManage, catalog.PermissionsFor("admin"));
        Assert.DoesNotContain(MessagingPermissions.CampaignManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(MessagingPermissions.CampaignManage, catalog.PermissionsFor("billing"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == MessagingPermissions.CampaignManage);
        Assert.Equal("Messaging", definition.Category);
        Assert.Empty(definition.RequiredModules!);
    }

    /// <summary>Spec 2026-10-12 §3.5 y D-Q6: de núcleo (el módulo lo revisa cada handler) y de fábrica sólo en <c>admin</c>: lo que el
    /// asesor manda en nombre de la empresa lo decide quien administra.</summary>
    [Fact]
    public void OnlyTheAdminRoleCarriesTheQuickReplyPermissionAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("messaging.quick_reply.manage", MessagingPermissions.QuickReplyManage);
        Assert.Contains(MessagingPermissions.QuickReplyManage, catalog.PermissionsFor("admin"));
        Assert.DoesNotContain(MessagingPermissions.QuickReplyManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(MessagingPermissions.QuickReplyManage, catalog.PermissionsFor("billing"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == MessagingPermissions.QuickReplyManage);
        Assert.Equal("Messaging", definition.Category);
        Assert.Empty(definition.RequiredModules!);
    }

    /// <summary>Spec 2026-10-15 D-R8: de núcleo y de fábrica en <c>admin</c> y <c>advisor</c> (decisión del owner: los asesores también
    /// gestionan el catálogo). Permiso aparte de <c>conversation.manage</c> para que un rol custom pueda no llevarlo.</summary>
    [Fact]
    public void AdminAndAdvisorCarryTheResolutionReasonPermissionAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("messaging.resolution_reason.manage", MessagingPermissions.ResolutionReasonManage);
        Assert.Contains(MessagingPermissions.ResolutionReasonManage, catalog.PermissionsFor("admin"));
        Assert.Contains(MessagingPermissions.ResolutionReasonManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(MessagingPermissions.ResolutionReasonManage, catalog.PermissionsFor("billing"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == MessagingPermissions.ResolutionReasonManage);
        Assert.Equal("Messaging", definition.Category);
        Assert.Empty(definition.RequiredModules!);
    }

    /// <summary>Spec 2026-10-24 D-F1: aparte de <c>customer.manage</c> para que un rol custom pueda no llevarlo; de fábrica en
    /// <c>admin</c> y <c>advisor</c>, no en <c>billing</c> ni <c>cashier</c>.</summary>
    [Fact]
    public void AdminAndAdvisorCarryTheCustomerMergePermission()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("customers.customer.merge", CustomersPermissions.CustomerMerge);
        Assert.Contains(CustomersPermissions.CustomerMerge, catalog.PermissionsFor("admin"));
        Assert.Contains(CustomersPermissions.CustomerMerge, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(CustomersPermissions.CustomerMerge, catalog.PermissionsFor("billing"));
        Assert.DoesNotContain(CustomersPermissions.CustomerMerge, catalog.PermissionsFor("cashier"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == CustomersPermissions.CustomerMerge);
        Assert.Equal("Customers", definition.Category);
        Assert.Equal([ModuleKeys.Customers], definition.RequiredModules);
    }

    /// <summary>Spec 2026-10-21 D-H8: de núcleo y de fábrica sólo en <c>admin</c>: el horario decide cuándo atiende el asistente en nombre
    /// de la empresa, y lo administra quien administra, no el asesor.</summary>
    [Fact]
    public void AdminCarriesBusinessHoursManageAndAdvisorDoesNot()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("messaging.business_hours.manage", MessagingPermissions.BusinessHoursManage);
        Assert.Contains(MessagingPermissions.BusinessHoursManage, catalog.PermissionsFor("admin"));
        Assert.DoesNotContain(MessagingPermissions.BusinessHoursManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(MessagingPermissions.BusinessHoursManage, catalog.PermissionsFor("billing"));
        Assert.DoesNotContain(MessagingPermissions.BusinessHoursManage, catalog.PermissionsFor("cashier"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == MessagingPermissions.BusinessHoursManage);
        Assert.Equal("Messaging", definition.Category);
        Assert.Empty(definition.RequiredModules!);
    }

    /// <summary>Spec 2026-10-14 D-T5: de núcleo (el módulo lo revisa cada handler) y de fábrica sólo en <c>admin</c>: lo que la empresa
    /// manda en su nombre y lo que Meta cobra lo decide quien administra.</summary>
    [Fact]
    public void OnlyTheAdminRoleCarriesTheTemplateManagePermissionAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("messaging.template.manage", MessagingPermissions.TemplateManage);
        Assert.Contains(MessagingPermissions.TemplateManage, catalog.PermissionsFor("admin"));
        Assert.DoesNotContain(MessagingPermissions.TemplateManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(MessagingPermissions.TemplateManage, catalog.PermissionsFor("billing"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == MessagingPermissions.TemplateManage);
        Assert.Equal("Messaging", definition.Category);
        Assert.Empty(definition.RequiredModules!);
    }

    /// <summary>
    /// Spec 2026-10-08 (Integraciones), «Permisos»: los dos de fábrica en admin y núcleo
    /// (<c>RequiredModules</c> vacío): Integrations no se apaga; lo que se filtra por módulo es el
    /// catálogo de proveedores.
    /// </summary>
    [Fact]
    public void TheAdminRoleCarriesTheIntegrationsPermissionsAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] integrationsPermissions =
            [IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage];

        Assert.All(integrationsPermissions, permission =>
            Assert.Contains(permission, catalog.PermissionsFor("admin")));
        var definitions = catalog.ListPermissions()
            .Where(definition => integrationsPermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Integrations", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }

    /// <summary>Spec 2026-10-09 §6.3 (decisión 5): en <c>admin</c> y <c>advisor</c>, de núcleo; el
    /// módulo lo revisa cada handler con <c>TenantModuleGuard</c>.</summary>
    [Fact]
    public void TheAdminAndAdvisorRolesCarryTheMessagingPermissionsAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] messagingPermissions =
            [MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage];

        foreach (var role in new[] { "admin", "advisor" })
        {
            Assert.All(messagingPermissions, permission => Assert.Contains(permission, catalog.PermissionsFor(role)));
        }

        var definitions = catalog.ListPermissions()
            .Where(definition => messagingPermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Messaging", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }

    /// <summary>Spec 2026-10-13 D32 y P1 del plan: de núcleo (cada handler revisa la capacidad pipeline); <c>admin</c> con los tres,
    /// <c>advisor</c> sin los ajustes, <c>billing</c> y <c>cashier</c> sin ninguno.</summary>
    [Fact]
    public void ThePipelinePermissionsAreCoreAndGoToAdminAndAdvisor()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] pipelinePermissions =
            [PipelinePermissions.DealRead, PipelinePermissions.DealManage, PipelinePermissions.SettingsManage];

        Assert.Equal(["pipeline.deal.read", "pipeline.deal.manage", "pipeline.settings.manage"], pipelinePermissions);
        Assert.All(pipelinePermissions, permission => Assert.Contains(permission, catalog.PermissionsFor("admin")));
        Assert.Contains(PipelinePermissions.DealRead, catalog.PermissionsFor("advisor"));
        Assert.Contains(PipelinePermissions.DealManage, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(PipelinePermissions.SettingsManage, catalog.PermissionsFor("advisor"));
        foreach (var role in new[] { "billing", "cashier" })
        {
            Assert.All(pipelinePermissions, permission => Assert.DoesNotContain(permission, catalog.PermissionsFor(role)));
        }

        var definitions = catalog.ListPermissions()
            .Where(definition => pipelinePermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(3, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Pipeline", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }

    /// <summary>Spec 2026-10-21 D-C10: de núcleo (con <c>[automations]</c> el 403 saldría <c>authorization.denied</c> y no
    /// <c>tenancy.module_not_enabled</c>; cada handler revisa la capacidad) y de fábrica sólo en <c>admin</c>.</summary>
    [Fact]
    public void TheAutomationsPermissionsAreCoreAndGoOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] automationsPermissions = [AutomationsPermissions.ChatbotRead, AutomationsPermissions.ChatbotManage];

        Assert.Equal(["automations.chatbot.read", "automations.chatbot.manage"], automationsPermissions);
        Assert.All(automationsPermissions, permission => Assert.Contains(permission, catalog.PermissionsFor("admin")));
        foreach (var role in new[] { "advisor", "billing", "cashier" })
        {
            Assert.All(automationsPermissions, permission => Assert.DoesNotContain(permission, catalog.PermissionsFor(role)));
        }

        var definitions = catalog.ListPermissions()
            .Where(definition => automationsPermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Automations", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }

    /// <summary>Spec 2026-10-08 §2: el admin de fábrica los lleva; el filtro decide dónde valen.</summary>
    [Fact]
    public void TheAdminRoleCarriesTheOperatorPermissionsAsCoreOperatorCategory()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] operatorPermissions =
            [OperatorPermissions.TenantsRead, OperatorPermissions.ModulesManage, OperatorPermissions.TenantsManage];

        Assert.All(operatorPermissions, permission =>
            Assert.Contains(permission, catalog.PermissionsFor("admin")));
        Assert.All(
            catalog.ListPermissions().Where(definition => operatorPermissions.Contains(definition.Permission)),
            definition =>
            {
                Assert.Equal("Operator", definition.Category);
                Assert.Empty(definition.RequiredModules!);
            });
    }

    /// <summary>
    /// Spec POS, «Arquitectura»: los seis <c>pos.*</c> de la caja mapean exactamente a <c>[pos]</c>; <c>pos.sale.customer</c> tiene su propia prueba (exige además <c>customers</c>). La prueba
    /// genérica sólo exige que <c>RequiredModules</c> no sea nulo, así que un <c>[]</c> o un módulo
    /// equivocado pasarían sin esta.
    /// </summary>
    [Fact]
    public void EveryPosPermissionRequiresExactlyThePosModule()
    {
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);
        string[] posPermissions =
        [
            PosPermissions.SaleRead,
            PosPermissions.SaleCreate,
            PosPermissions.SaleVoid,
            PosPermissions.SaleDiscount,
            PosPermissions.RegisterOperate,
            PosPermissions.RegisterRead,
        ];

        foreach (var permission in posPermissions)
        {
            var modules = definitions[permission].RequiredModules;
            Assert.NotNull(modules);
            Assert.Equal([ModuleKeys.Pos], modules.ToArray());
        }
    }

    /// <summary>
    /// Spec 2026-10-14, decisión 4: elegir o crear el cliente de la venta exige <c>pos</c> y también <c>customers</c>
    /// (en ese orden): sin Clientes, el enmascarado quita el permiso y la caja vende a Consumidor final. Va a admin y
    /// cajero, no a asesor ni facturación.
    /// </summary>
    [Fact]
    public void ThePosCustomerPermissionRequiresPosAndCustomersAndGoesToAdminAndCashier()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        var definition = RegisteredPermissionDefinitions()
            .Single(item => item.Permission == PosPermissions.SaleCustomer);

        Assert.Equal([ModuleKeys.Pos, ModuleKeys.Customers], definition.RequiredModules!.ToArray());
        Assert.Contains(PosPermissions.SaleCustomer, catalog.PermissionsFor("admin"));
        Assert.Contains(PosPermissions.SaleCustomer, catalog.PermissionsFor("cashier"));
        Assert.DoesNotContain(PosPermissions.SaleCustomer, catalog.PermissionsFor("advisor"));
        Assert.DoesNotContain(PosPermissions.SaleCustomer, catalog.PermissionsFor("billing"));
    }

    /// <summary>
    /// Spec 2026-10-23 D-14 y B3: los dos permisos de proveedores exigen sólo el módulo <c>suppliers</c>, van a admin y a
    /// facturación (que paga a los proveedores) y no a asesor ni cajero.
    /// </summary>
    [Fact]
    public void TheSuppliersPermissionsRequireTheSuppliersModuleAndGoToAdminAndBilling()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        foreach (var permission in new[] { SuppliersPermissions.Read, SuppliersPermissions.Manage })
        {
            Assert.Equal("Suppliers", definitions[permission].Category);
            Assert.Equal([ModuleKeys.Suppliers], definitions[permission].RequiredModules!.ToArray());
            Assert.Contains(permission, catalog.PermissionsFor("admin"));
            Assert.Contains(permission, catalog.PermissionsFor("billing"));
            Assert.DoesNotContain(permission, catalog.PermissionsFor("advisor"));
            Assert.DoesNotContain(permission, catalog.PermissionsFor("cashier"));
        }
    }

    /// <summary>
    /// Spec de Shopify §5.2: el webhook de Shopify encola y el worker procesa. Sin el worker registrado, o con un
    /// puerto del importador sin adaptador, el webhook responde 200 y nadie procesa nunca, sin error en ningún lado. Esto arma
    /// el contenedor con <c>ValidateOnBuild</c> —lo mismo que hace el host en Development— y resuelve el worker y el importador.
    /// </summary>
    [Fact]
    public void TheContainerValidatesAndBuildsTheShopifyWebhookWorker()
    {
        var services = BuildPlatformServices();
        // Lo que el host web agrega por su cuenta antes de AddQepPlatform: sin esto, ValidateOnBuild fallaría por servicios que
        // en producción sí están.
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Modules.Shopify.Infrastructure.Workers.ShopifyWebhookDeliveryWorker");
        using var scope = provider.CreateScope();
        Assert.IsType<ShopifyOrderImporter>(scope.ServiceProvider.GetRequiredService<IShopifyOrderImporter>());
        // La reacción de §6.3 (ShopifyOrderChanges) reemplaza la vacía. Sin ella, pagos y cancelaciones de Shopify sobre
        // pedidos importados se guardan en el espejo y nunca llegan a Quotations.
        Assert.IsType<ShopifyOrderChanges>(scope.ServiceProvider.GetRequiredService<IShopifyOrderChangeReaction>());
        // Spec §5.3: el cron de la conciliación y «Sincronizar ahora». Sin el worker, lo que el webhook pierde (abandonado
        // a los 8 intentos o descartado con 200) y los Waiting no se recuperan nunca, sin error en ningún lado.
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Modules.Shopify.Infrastructure.Workers.ShopifyReconcileWorker");
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICommandHandler<SyncShopifyStoreCommand, ShopifySyncResult>>());
        // Spec 2026-10-26 D-C6: sin el consumidor, confirmar un sinónimo publica su evento y ningún pedido se corrige nunca.
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Modules.Shopify.Infrastructure.Workers.CitySynonymEventsWorker");
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ShopifyCitySynonymEventApplier>());
        // Y sin el worker que corrige, los trabajos sembrados quedan Pending para siempre: el procesador necesita los dos puertos
        // que registra Bootstrapper (el sinónimo de Geography y el escritor sobre Quotations y Customers).
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Modules.Shopify.Infrastructure.Workers.ShopifyCityCorrectionWorker");
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ShopifyCityCorrectionProcessor>());
    }

    /// <summary>
    /// Fallo P2-R31 (el mismo amarre que <c>ExternalApiUrlPolicyTests.TheNamedClientIsBoundToThePolicyHandler</c> de la tarea 10): el
    /// cliente del bloque HTTP del chatbot se registra en el composition root con el handler de la política anti-SSRF. Sin él, el handler
    /// por defecto sigue redirecciones con la clave, usa el proxy del sistema y conecta a 10/8 o a 169.254.169.254 sin que nada se ponga
    /// rojo: las pruebas de extremo a extremo reemplazan el handler primario.
    /// </summary>
    [Fact]
    public void TheChatbotHttpClientIsBoundToThePolicyHandler()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(Modules.Automations.Infrastructure.Http.HttpBlockExecutor.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.NotNull(primary.ConnectCallback);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseProxy);
        using var scope = provider.CreateScope();
        Assert.IsType<Modules.Automations.Infrastructure.Http.HttpBlockExecutor>(
            scope.ServiceProvider.GetRequiredService<Modules.Automations.Application.Http.IHttpBlockExecutor>());
    }

    [Fact]
    public void TheContainerCanBuildTheEntitlementMask()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ModuleEntitlementMask>());
    }

    private static PermissionDefinition[] RegisteredPermissionDefinitions() =>
        BuildPlatformServices()
            .Where(descriptor => descriptor.ServiceType == typeof(PermissionDefinition))
            .Select(descriptor => (PermissionDefinition)descriptor.ImplementationInstance!)
            .ToArray();

    private static string[] PermissionConstants() =>
        ApplicationAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type =>
                type is { IsAbstract: true, IsSealed: true } &&
                type.Name.EndsWith("Permissions", StringComparison.Ordinal))
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static ServiceCollection BuildPlatformServices()
    {
        var services = new ServiceCollection();
        services.AddQepPlatform(MinimalConfiguration(), new TestHostEnvironment());
        return services;
    }

    // Valores de juguete: esta prueba mira **qué quedó registrado**, no construye ningún
    // servicio, así que nada de esto se conecta a nada. Sólo tiene que alcanzar para que los
    // AddXInfrastructure no aborten mientras registran.
    private static IConfiguration MinimalConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] =
                    "Host=localhost;Port=5432;Database=architecture_tests;Username=x;Password=x",
                ["Storage:R2:AccountId"] = "account",
                ["Storage:R2:AccessKeyId"] = "key",
                ["Storage:R2:SecretAccessKey"] = "secret",
                ["Storage:R2:Bucket"] = "bucket",
                ["Notifications:EmailProvider"] = "log",
            })
            .Build();

    private static MessageType[] MessageTypes() =>
        ApplicationAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces().Select(contract => (type, contract)))
            .Where(pair => pair.contract.IsGenericType)
            .Select(pair => new
            {
                pair.type,
                definition = pair.contract.GetGenericTypeDefinition(),
                response = pair.contract.GetGenericArguments()[0],
            })
            .Where(pair =>
                pair.definition == typeof(ICommand<>) || pair.definition == typeof(IQuery<>))
            .Select(pair => new MessageType(
                pair.type, pair.response, pair.definition == typeof(ICommand<>)))
            .ToArray();

    // Se cargan por patrón de archivo y no por un tipo ancla de cada módulo: un ancla hay que
    // acordarse de agregarla al abrir un módulo nuevo, y justamente el olvido es lo que esta
    // prueba existe para atrapar.
    private static Assembly[] ApplicationAssemblies() =>
        Directory
            .GetFiles(AppContext.BaseDirectory, "Modules.*.Application.dll")
            .Select(Assembly.LoadFrom)
            .ToArray();

    private sealed record MessageType(Type Message, Type Response, bool IsCommand);

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ArchitectureTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
