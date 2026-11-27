using System.Reflection;
using Bootstrapper;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Ai.Application;
using Modules.ApiAccess.Application;
using Modules.Authorization.Application;
using Modules.Automations.Application;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Customers.Application;
using Modules.Geography.Application;
using Modules.Hr.Application;
using Modules.Integrations.Application;
using Modules.Loans.Application;
using Modules.Messaging.Application;
using Modules.Payroll.Application;
using Modules.Pipeline.Application;
using Modules.Pos.Application;
using Modules.Purchases.Application;
using Modules.Reporting.Application;
using Modules.Scheduling.Application;
using Modules.ServiceContracts.Application;
using Modules.Shopify.Application;
using Modules.Siigo.Application;
using Modules.Store.Application;
using Modules.Suppliers.Application;
using Modules.Support.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Application;
using Modules.Wms.Application;
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
    /// Spec 2026-11-22 D-14: el horario de atención es un contrato de BuildingBlocks que implementa Mensajería. Si su registro faltara o
    /// se armara mal, el aviso de «sin asignar» de Soporte fallaría en el primer tick del worker, no al compilar.
    /// </summary>
    [Fact]
    public void TheContainerResolvesTheBusinessHoursProvider()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var hours = scope.ServiceProvider.GetRequiredService<BuildingBlocks.Application.BusinessHours.IBusinessHoursProvider>();

        // The class is internal to Messaging.Infrastructure: it is told by name.
        Assert.Equal("BusinessHoursProvider", hours.GetType().Name);
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

    /// <summary>
    /// Spec 2026-11-16 (catálogo con entidad y acción): la matriz de roles ubica cada permiso por su
    /// <see cref="PermissionShape"/>. Un permiso sin forma quedaría fuera de la matriz sin que nada falle.
    /// </summary>
    [Fact]
    public void EveryRegisteredPermissionDeclaresItsShape()
    {
        var missing = RegisteredPermissionDefinitions()
            .Where(definition => definition.Shape is null
                || string.IsNullOrWhiteSpace(definition.Shape.Entity)
                || string.IsNullOrWhiteSpace(definition.Shape.EntityLabel)
                || string.IsNullOrWhiteSpace(definition.Shape.Action))
            .Select(definition => definition.Permission)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Estos permisos no declaran Shape: " + string.Join(", ", missing));
    }

    /// <summary>Una celda de la matriz es (entidad, acción): dos permisos en la misma celda no se pueden pintar.</summary>
    [Fact]
    public void NoEntityRepeatsAnAction()
    {
        var repeated = RegisteredPermissionDefinitions()
            .Where(definition => definition.Shape is not null)
            .GroupBy(definition => (definition.Shape!.Entity, definition.Shape.Action))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Entity}/{group.Key.Action}: "
                + string.Join(" + ", group.Select(definition => definition.Permission).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(repeated.Length == 0, "Estas entidades repiten acción: " + string.Join("; ", repeated));
    }

    /// <summary>Una fila de la matriz cuelga de un solo módulo.</summary>
    [Fact]
    public void EachEntityLivesInOneCategory()
    {
        var split = RegisteredPermissionDefinitions()
            .Where(definition => definition.Shape is not null)
            .GroupBy(definition => definition.Shape!.Entity, StringComparer.Ordinal)
            .Where(group => group.Select(definition => definition.Category).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(split.Length == 0, "Estas entidades están en más de una categoría: " + string.Join(", ", split));
    }

    /// <summary>Una fila de la matriz tiene un solo nombre.</summary>
    [Fact]
    public void EveryEntityHasOneLabel()
    {
        var split = RegisteredPermissionDefinitions()
            .Where(definition => definition.Shape is not null)
            .GroupBy(definition => definition.Shape!.Entity, StringComparer.Ordinal)
            .Where(group => group.Select(definition => definition.Shape!.EntityLabel).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(split.Length == 0, "Estas entidades tienen más de un nombre: " + string.Join(", ", split));
    }

    /// <summary>
    /// Sin etiqueta, <see cref="PermissionVocabulary"/> devuelve la clave y la pantalla mostraría
    /// <c>void</c> o <c>Pipeline</c>: toda acción y toda categoría registradas tienen nombre.
    /// </summary>
    [Fact]
    public void EveryActionAndCategoryHasALabel()
    {
        var definitions = RegisteredPermissionDefinitions();
        var unlabeled = definitions
            .Where(definition => definition.Shape is not null)
            .Select(definition => definition.Shape!.Action)
            .Where(action => !PermissionVocabulary.ActionLabels.ContainsKey(action))
            .Select(action => "acción " + action)
            .Concat(definitions
                .Select(definition => definition.Category)
                .Where(category => !PermissionVocabulary.CategoryLabels.ContainsKey(category))
                .Select(category => "categoría " + category))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(unlabeled.Length == 0, "Sin etiqueta: " + string.Join(", ", unlabeled));
    }

    /// <summary>
    /// Dos entidades de una misma categoría no pueden compartir etiqueta: la matriz las pintaría como
    /// dos filas indistinguibles.
    /// </summary>
    [Fact]
    public void NoTwoEntitiesOfACategoryShareALabel()
    {
        var clashes = RegisteredPermissionDefinitions()
            .Where(definition => definition.Shape is not null)
            .Select(definition => (definition.Category, definition.Shape!.Entity, definition.Shape.EntityLabel))
            .Distinct()
            .GroupBy(row => (row.Category, row.EntityLabel))
            .Where(group => group.Select(row => row.Entity).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => $"{group.Key.Category}/{group.Key.EntityLabel}: "
                + string.Join(", ", group.Select(row => row.Entity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(clashes.Length == 0, "Etiqueta repetida en una categoría: " + string.Join(" | ", clashes));
    }

    /// <summary>La clave de una entidad es <c>módulo.entidad</c> y la de una acción, un verbo en snake_case.</summary>
    [Fact]
    public void EveryEntityKeyIsModuleDotEntity()
    {
        var shapes = RegisteredPermissionDefinitions()
            .Where(definition => definition.Shape is not null)
            .Select(definition => definition.Shape!)
            .ToArray();

        var badEntities = shapes
            .Select(shape => shape.Entity)
            .Where(entity => !System.Text.RegularExpressions.Regex.IsMatch(entity, @"^[a-z]+\.[a-z_]+$"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var badActions = shapes
            .Select(shape => shape.Action)
            .Where(action => !System.Text.RegularExpressions.Regex.IsMatch(action, "^[a-z_]+$"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(badEntities.Length == 0, "Entidades mal formadas: " + string.Join(", ", badEntities));
        Assert.True(badActions.Length == 0, "Acciones mal formadas: " + string.Join(", ", badActions));
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

    /// <summary>La cuenta vive sólo en el Assert: el nombre ya no la lleva, para que no se desfase.
    /// Ancla: sin esto, las dos de arriba pasarían por vacías. Son las 35 del spec de
    /// entitlements, las 7 de POS, las tres <c>operator.*</c> y las dos <c>integrations.*</c> de los
    /// specs de 2026-10-08, las dos <c>messaging.*</c> del spec 2026-10-09, las dos de Customers
    /// (<c>customers.tag.manage</c>, <c>customers.note.moderate</c>), <c>messaging.campaign.manage</c>
    /// del spec 2026-10-11 y <c>messaging.quick_reply.manage</c> del spec 2026-10-12 las tres <c>pipeline.*</c> del spec 2026-10-13 <c>messaging.template.manage</c> del spec 2026-10-14 <c>messaging.resolution_reason.manage</c> del spec 2026-10-15 y <c>messaging.business_hours.manage</c> del spec 2026-10-21 y las dos <c>automations.chatbot.*</c> del mismo spec, <c>customers.customer.merge</c> del spec 2026-10-24, <c>customers.data_consent.revoke</c> y las dos
    /// <c>operator.cities.*</c> del spec 2026-10-26 y las dos <c>billing.service.*</c> del spec 2026-10-29 y <c>developer.api_key.manage</c> del spec 2026-10-30 y las dos <c>loans.loan.*</c> del spec 2026-10-31 y <c>store.settings.manage</c> del spec 2026-11-03 (la bandeja reusa <c>quotations.order.manage</c>, que ya se contaba) y <c>customers.customer.delete</c> del spec 2026-11-10 y las dos <c>ai.agent.*</c> del spec 2026-10-29 y las tres <c>support.*</c> del spec 2026-11-14 y las dos <c>purchases.order.*</c> del spec 2026-11-17 y las tres <c>wholesale.*</c> del spec 2026-11-17 (mayorista) y las dos <c>siigo.*</c> del spec 2026-11-18 y las cinco <c>hr.*</c> del spec 2026-11-20 y las cuatro <c>payroll.*</c> del spec 2026-11-21 y las cinco <c>scheduling.*</c> del spec 2026-11-21 (Agenda) y las dieciséis <c>wms.*</c> del spec WMS 2026-10-31 §11.5 y <c>wms.task.manage</c> del spec 2026-11-26.</summary>
    [Fact]
    public void PermissionDiscoveryFindsEveryPermissionConstant()
    {
        // 46 + los dos de Messaging (spec 2026-10-09 §6.3) + los dos de Customers (spec 2026-10-11 §4.3) + el de campañas (§6.4)
        // + el de respuestas rápidas (spec 2026-10-12 §3.5) + los tres del embudo (spec 2026-10-13 D23)
        // + el de plantillas (spec 2026-10-14 D-T5) + el de motivos de resolución (spec 2026-10-15 D-R8)
        // + el de horarios de atención (spec 2026-10-21 D-H8) + los dos de chatbots (spec 2026-10-21 D-C10)
        // + el del cliente de la venta de POS (spec 2026-10-14, decisión 4)
        // + los dos de proveedores (suppliers, spec 2026-10-23) + el de fusionar clientes (spec 2026-10-24 D-F1)
        // + el de revocar la autorización de datos (spec 2026-10-26 D-9)
        // + los dos de ciudades del operador (spec 2026-10-26 §6)
        // + los dos de cobros recurrentes (billing, spec 2026-10-29)
        // + el de claves de API (developer.api_key.manage, spec 2026-10-30)
        // + los dos de préstamos (loans, spec 2026-10-31)
        // + el de uso de la plataforma (operator.usage.read)
        // + el de la tienda en línea (store.settings.manage, spec 2026-11-03; quotations.order.manage ya estaba y .Distinct no lo cuenta dos veces)
        // + el de borrar clientes (customers.customer.delete, spec 2026-11-10 D-8)
        // + los dos de la unidad tributaria del operador (operator.tax_units.*, spec 2026-11-10)
        // + los tres de costo y margen (spec 2026-11-10 §5)
        // + los dos de agentes IA (ai.agent.read|manage, spec 2026-10-29)
        // + los tres de soporte (support.*, spec 2026-11-14)
        // + los dos del copiloto (ai.copilot.use|manage, spec 2026-11-17)
        // + los dos de compras (purchases.order.read|manage, spec 2026-11-17)
        // + los tres de mayorista (spec 2026-11-17)
        // + los dos de Siigo (siigo.settings.manage, siigo.invoice.issue, spec 2026-11-18)
        // + el de las fallas de las llamadas a Siigo (siigo.calls.read, spec 2026-11-21)
        // + the five hr.* permissions (spec 2026-11-20).
        // + the four payroll.* permissions (spec 2026-11-21).
        // + los cinco de la Agenda (scheduling.*, spec 2026-11-21).
        // + los dos de parámetros legales del operador (operator.legal_parameters.read|manage, spec 2026-11-22).
        // + the two hr.medical.* permissions (spec 2026-11-23).
        // + los dieciséis de bodegas (wms, spec WMS 2026-10-31 §11.5).
        // + el de supervisar tareas de bodega (wms.task.manage, spec 2026-11-26).
        Assert.Equal(131, PermissionConstants().Length);
    }

    /// <summary>Spec 2026-10-26 §6: categoría <c>Operator</c> y de núcleo, como las tres <c>operator.*</c> de la consola. Las lleva
    /// el rol global <c>admin</c>; <c>OperatorPermissionFilter</c> las deja vivas sólo en el tenant operador.</summary>
    [Fact]
    public void OnlyTheAdminRoleCarriesTheCityOperatorPermissions()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] cityPermissions = [GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage];

        Assert.Equal(["operator.cities.read", "operator.cities.manage"], cityPermissions);
        Assert.All(cityPermissions, permission =>
        {
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
            var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == permission);
            Assert.Equal("Operator", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }

    /// <summary>Reporte de uso de la plataforma: categoría <c>Operator</c> y de núcleo, como las demás <c>operator.*</c>. Sólo la lleva
    /// el rol global <c>admin</c>; <c>OperatorPermissionFilter</c> la deja viva sólo en el tenant operador.</summary>
    [Fact]
    public void OnlyTheAdminRoleCarriesTheUsagePermission()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("operator.usage.read", OperatorPermissions.UsageRead);
        Assert.Contains(OperatorPermissions.UsageRead, FactoryPermissions(provider, "admin"));
        Assert.DoesNotContain(OperatorPermissions.UsageRead, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(OperatorPermissions.UsageRead, FactoryPermissions(provider, "billing"));
        Assert.DoesNotContain(OperatorPermissions.UsageRead, FactoryPermissions(provider, "cashier"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == OperatorPermissions.UsageRead);
        Assert.Equal("Operator", definition.Category);
        Assert.Empty(definition.RequiredModules!);
    }

    /// <summary>Spec 2026-11-10 §5: el costo y el margen son dato comercial sensible, de fábrica sólo en <c>admin</c>.</summary>
    [Fact]
    public void OnlyTheAdminRoleCarriesTheCostAndMarginPermissions()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] permissions =
            [CatalogPermissions.ProductCostRead, CatalogPermissions.ProductCostManage, ReportingPermissions.MarginRead];

        Assert.Equal(["catalog.product_cost.read", "catalog.product_cost.manage", "reporting.margin.read"], permissions);
        Assert.All(permissions, permission =>
        {
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        });
        var all = catalog.ListPermissions();
        Assert.Equal([ModuleKeys.Catalog],
            Assert.Single(all, d => d.Permission == CatalogPermissions.ProductCostRead).RequiredModules!);
        Assert.Equal([ModuleKeys.Catalog],
            Assert.Single(all, d => d.Permission == CatalogPermissions.ProductCostManage).RequiredModules!);
        Assert.Equal([ModuleKeys.Reporting, ModuleKeys.Catalog],
            Assert.Single(all, d => d.Permission == ReportingPermissions.MarginRead).RequiredModules!);
    }

    /// <summary>Sin la política, <c>RequireAuthorization</c> no resuelve y el síntoma es un 500, no un 403.</summary>
    [Fact]
    public async Task TheCostAndMarginPoliciesAreRegistered()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var permission in new[]
                 {
                     CatalogPermissions.ProductCostRead, CatalogPermissions.ProductCostManage, ReportingPermissions.MarginRead,
                 })
        {
            Assert.NotNull(await policies.GetPolicyAsync(permission));
        }
    }

    /// <summary>Spec 2026-10-11 §6.4 y decisión 16: de núcleo (el módulo lo revisa cada handler) y de fábrica sólo en <c>admin</c>:
    /// una campaña llega a miles de personas en nombre de la empresa y el costo es por mensaje.</summary>
    [Fact]
    public void OnlyTheAdminRoleCarriesTheCampaignPermissionAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Contains(MessagingPermissions.CampaignManage, FactoryPermissions(provider, "admin"));
        Assert.DoesNotContain(MessagingPermissions.CampaignManage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(MessagingPermissions.CampaignManage, FactoryPermissions(provider, "billing"));
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
        Assert.Contains(MessagingPermissions.QuickReplyManage, FactoryPermissions(provider, "admin"));
        Assert.DoesNotContain(MessagingPermissions.QuickReplyManage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(MessagingPermissions.QuickReplyManage, FactoryPermissions(provider, "billing"));
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
        Assert.Contains(MessagingPermissions.ResolutionReasonManage, FactoryPermissions(provider, "admin"));
        Assert.Contains(MessagingPermissions.ResolutionReasonManage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(MessagingPermissions.ResolutionReasonManage, FactoryPermissions(provider, "billing"));
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
        Assert.Contains(CustomersPermissions.CustomerMerge, FactoryPermissions(provider, "admin"));
        Assert.Contains(CustomersPermissions.CustomerMerge, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(CustomersPermissions.CustomerMerge, FactoryPermissions(provider, "billing"));
        Assert.DoesNotContain(CustomersPermissions.CustomerMerge, FactoryPermissions(provider, "cashier"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == CustomersPermissions.CustomerMerge);
        Assert.Equal("Customers", definition.Category);
        Assert.Equal([ModuleKeys.Customers], definition.RequiredModules);
    }

    /// <summary>Spec 2026-11-10 D-8: borrar clientes es definitivo, así que de fábrica sólo lo lleva <c>admin</c>.</summary>
    [Fact]
    public void OnlyAdminCarriesTheCustomerDeletePermission()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("customers.customer.delete", CustomersPermissions.CustomerDelete);
        Assert.Contains(CustomersPermissions.CustomerDelete, FactoryPermissions(provider, "admin"));
        Assert.DoesNotContain(CustomersPermissions.CustomerDelete, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(CustomersPermissions.CustomerDelete, FactoryPermissions(provider, "billing"));
        Assert.DoesNotContain(CustomersPermissions.CustomerDelete, FactoryPermissions(provider, "cashier"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == CustomersPermissions.CustomerDelete);
        Assert.Equal("Customers", definition.Category);
        Assert.Equal("high", definition.RiskLevel);
        Assert.Equal([ModuleKeys.Customers], definition.RequiredModules);
    }

    /// <summary>Spec 2026-10-26 D-9: registrar la revocación de una autorización de datos es un permiso aparte; de fábrica en
    /// <c>admin</c> y <c>advisor</c>, no en <c>billing</c> ni <c>cashier</c>.</summary>
    [Fact]
    public void AdminAndAdvisorCarryTheDataConsentRevokePermission()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("customers.data_consent.revoke", CustomersPermissions.DataConsentRevoke);
        Assert.Contains(CustomersPermissions.DataConsentRevoke, FactoryPermissions(provider, "admin"));
        Assert.Contains(CustomersPermissions.DataConsentRevoke, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(CustomersPermissions.DataConsentRevoke, FactoryPermissions(provider, "billing"));
        Assert.DoesNotContain(CustomersPermissions.DataConsentRevoke, FactoryPermissions(provider, "cashier"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == CustomersPermissions.DataConsentRevoke);
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
        Assert.Contains(MessagingPermissions.BusinessHoursManage, FactoryPermissions(provider, "admin"));
        Assert.DoesNotContain(MessagingPermissions.BusinessHoursManage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(MessagingPermissions.BusinessHoursManage, FactoryPermissions(provider, "billing"));
        Assert.DoesNotContain(MessagingPermissions.BusinessHoursManage, FactoryPermissions(provider, "cashier"));
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
        Assert.Contains(MessagingPermissions.TemplateManage, FactoryPermissions(provider, "admin"));
        Assert.DoesNotContain(MessagingPermissions.TemplateManage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(MessagingPermissions.TemplateManage, FactoryPermissions(provider, "billing"));
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
            Assert.Contains(permission, FactoryPermissions(provider, "admin")));
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
            Assert.All(messagingPermissions, permission => Assert.Contains(permission, FactoryPermissions(provider, role)));
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
        Assert.All(pipelinePermissions, permission => Assert.Contains(permission, FactoryPermissions(provider, "admin")));
        Assert.Contains(PipelinePermissions.DealRead, FactoryPermissions(provider, "advisor"));
        Assert.Contains(PipelinePermissions.DealManage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(PipelinePermissions.SettingsManage, FactoryPermissions(provider, "advisor"));
        foreach (var role in new[] { "billing", "cashier" })
        {
            Assert.All(pipelinePermissions, permission => Assert.DoesNotContain(permission, FactoryPermissions(provider, role)));
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
        Assert.All(automationsPermissions, permission => Assert.Contains(permission, FactoryPermissions(provider, "admin")));
        foreach (var role in new[] { "advisor", "billing", "cashier" })
        {
            Assert.All(automationsPermissions, permission => Assert.DoesNotContain(permission, FactoryPermissions(provider, role)));
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

    /// <summary>Spec 2026-10-29 §1: de núcleo (con <c>[ai]</c> el 403 saldría <c>authorization.denied</c> y no
    /// <c>tenancy.module_not_enabled</c>; cada handler revisa la capacidad) y de fábrica sólo en <c>admin</c>: un agente habla con los
    /// clientes en nombre de la empresa.</summary>
    [Fact]
    public void TheAiPermissionsAreCoreAndGoOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] aiPermissions = [AiPermissions.AgentRead, AiPermissions.AgentManage];

        Assert.Equal(["ai.agent.read", "ai.agent.manage"], aiPermissions);
        Assert.All(aiPermissions, permission => Assert.Contains(permission, FactoryPermissions(provider, "admin")));
        foreach (var role in new[] { "advisor", "billing", "cashier" })
        {
            Assert.All(aiPermissions, permission => Assert.DoesNotContain(permission, FactoryPermissions(provider, role)));
        }

        var definitions = catalog.ListPermissions()
            .Where(definition => aiPermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Ai", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }

    /// <summary>La otra mitad de un permiso nuevo: sin su política, <c>RequireAuthorization</c> no resuelve y la ruta responde 500, no 403
    /// (CLAUDE.md, «Un permiso nuevo necesita dos mitades»).</summary>
    [Fact]
    public async Task TheAiPermissionsHaveTheirPolicies()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var policies = provider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();

        foreach (var permission in new[] { AiPermissions.AgentRead, AiPermissions.AgentManage })
        {
            Assert.NotNull(await policies.GetPolicyAsync(permission));
        }
    }

    /// <summary>Spec 2026-11-17 (copiloto), D-C14 y OC-1: de núcleo como los de agentes (cada handler revisa la capacidad
    /// <c>copilot</c>), de fábrica sólo en <c>admin</c> (cada pregunta gasta saldo de la cuenta del tenant) y con su forma en la matriz
    /// de <c>/roles</c>.</summary>
    [Fact]
    public void TheCopilotPermissionsAreCoreAndGoOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] copilotPermissions = [AiPermissions.CopilotUse, AiPermissions.CopilotManage];

        Assert.Equal(["ai.copilot.use", "ai.copilot.manage"], copilotPermissions);
        Assert.All(copilotPermissions, permission => Assert.Contains(permission, FactoryPermissions(provider, "admin")));
        foreach (var role in new[] { "advisor", "billing", "cashier" })
        {
            Assert.All(copilotPermissions, permission => Assert.DoesNotContain(permission, FactoryPermissions(provider, role)));
        }

        var definitions = catalog.ListPermissions()
            .Where(definition => copilotPermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Ai", definition.Category);
            Assert.Empty(definition.RequiredModules!);
            Assert.Equal("ai.copilot", definition.Shape!.Entity);
        });
    }

    /// <summary>La otra mitad de los permisos del copiloto: sin su política la ruta responde 500, no 403.</summary>
    [Fact]
    public async Task TheCopilotPermissionsHaveTheirPolicies()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var policies = provider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();

        foreach (var permission in new[] { AiPermissions.CopilotUse, AiPermissions.CopilotManage })
        {
            Assert.NotNull(await policies.GetPolicyAsync(permission));
        }
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
            Assert.Contains(permission, FactoryPermissions(provider, "admin")));
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
        var definition = RegisteredPermissionDefinitions()
            .Single(item => item.Permission == PosPermissions.SaleCustomer);

        Assert.Equal([ModuleKeys.Pos, ModuleKeys.Customers], definition.RequiredModules!.ToArray());
        Assert.Contains(PosPermissions.SaleCustomer, FactoryPermissions(provider, "admin"));
        Assert.Contains(PosPermissions.SaleCustomer, FactoryPermissions(provider, "cashier"));
        Assert.DoesNotContain(PosPermissions.SaleCustomer, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(PosPermissions.SaleCustomer, FactoryPermissions(provider, "billing"));
    }

    /// <summary>
    /// Spec 2026-10-23 D-14 y B3: los dos permisos de proveedores exigen sólo el módulo <c>suppliers</c>, van a admin y a
    /// facturación (que paga a los proveedores) y no a asesor ni cajero.
    /// </summary>
    [Fact]
    public void TheSuppliersPermissionsRequireTheSuppliersModuleAndGoToAdminAndBilling()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        foreach (var permission in new[] { SuppliersPermissions.Read, SuppliersPermissions.Manage })
        {
            Assert.Equal("Suppliers", definitions[permission].Category);
            Assert.Equal([ModuleKeys.Suppliers], definitions[permission].RequiredModules!.ToArray());
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.Contains(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }
    }

    /// <summary>
    /// Spec cobros-recurrentes (owner, 2026-10-29): los dos permisos de cobros exigen el módulo <c>billing</c> (nunca se muestran
    /// permisos que el tenant no tiene: apagado, el editor de roles los oculta y la API responde un 403 pelado) y van a admin y
    /// a facturación (que cobra los servicios recurrentes); no a asesor ni cajero.
    /// </summary>
    [Fact]
    public void TheServiceContractsPermissionsRequireTheBillingModuleAndGoToAdminAndBilling()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        foreach (var permission in new[] { ServiceContractsPermissions.Read, ServiceContractsPermissions.Manage })
        {
            Assert.Equal("Contratos de servicio", definitions[permission].Category);
            Assert.Equal([ModuleKeys.ServiceContracts], definitions[permission].RequiredModules!.ToArray());
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.Contains(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }
    }

    /// <summary>
    /// Spec de préstamos (2026-10-31): los dos permisos exigen el módulo <c>loans</c> (apagado, el editor de roles los oculta y la API
    /// responde un 403 pelado) y van a admin y a facturación; no a asesor ni cajero.
    /// </summary>
    [Fact]
    public void TheLoansPermissionsRequireTheLoansModuleAndGoToAdminAndBilling()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        foreach (var permission in new[] { LoansPermissions.Read, LoansPermissions.Manage })
        {
            Assert.Equal("Préstamos", definitions[permission].Category);
            Assert.Equal([ModuleKeys.Loans], definitions[permission].RequiredModules!.ToArray());
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.Contains(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }
    }

    /// <summary>Spec WMS 2026-10-31 §11.5 y spec 2026-11-26 §6.11: diecisiete permisos con [wms] (la máscara los quita con el módulo
    /// apagado), categoría Bodegas, todos en admin y en ninguna plantilla de rol: el resto es rol custom del tenant.</summary>
    [Fact]
    public void TheWmsPermissionsRequireTheWmsModuleAndGoOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions().ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        (string Permission, string Entity, string EntityLabel, string Action)[] expected =
        [
            (WmsPermissions.WarehouseManage, "wms.warehouse", "Bodegas y ubicaciones", "manage"),
            (WmsPermissions.OwnerManage, "wms.owner", "Dueños del inventario", "manage"),
            (WmsPermissions.ProfileManage, "wms.profile", "Perfiles de variante", "manage"),
            (WmsPermissions.DocumentRead, "wms.document", "Documentos de inventario", "read"),
            (WmsPermissions.StockRead, "wms.stock", "Existencias", "read"),
            (WmsPermissions.StockExport, "wms.stock", "Existencias", "export"),
            (WmsPermissions.AdjustmentPropose, "wms.adjustment", "Ajustes de inventario", "propose"),
            (WmsPermissions.AdjustmentApprove, "wms.adjustment", "Ajustes de inventario", "approve"),
            (WmsPermissions.PurchaseOrderManage, "wms.purchase_order", "Órdenes de compra", "manage"),
            (WmsPermissions.TransferManage, "wms.transfer", "Traslados", "manage"),
            (WmsPermissions.TransferDispatch, "wms.transfer", "Traslados", "dispatch"),
            (WmsPermissions.ReceiptPerform, "wms.receipt", "Recepción de mercancía", "perform"),
            (WmsPermissions.PutawayPerform, "wms.putaway", "Ubicación de mercancía", "perform"),
            (WmsPermissions.DiscrepancyResolve, "wms.discrepancy", "Discrepancias de recepción", "resolve"),
            (WmsPermissions.CountPerform, "wms.count", "Conteos", "perform"),
            (WmsPermissions.CountApprove, "wms.count", "Conteos", "approve"),
            (WmsPermissions.TaskManage, "wms.task", "Tareas de bodega", "manage"),
        ];
        Assert.Equal(
            typeof(WmsPermissions).GetFields().Select(field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal),
            expected.Select(row => row.Permission).Order(StringComparer.Ordinal));
        var templates = provider.GetServices<RoleTemplate>().ToArray();
        Assert.NotEmpty(templates);
        foreach (var (permission, entity, entityLabel, action) in expected)
        {
            var definition = definitions[permission];
            Assert.Equal("Bodegas", definition.Category);
            Assert.Equal([ModuleKeys.Wms], definition.RequiredModules!.ToArray());
            Assert.Equal((entity, entityLabel, action), (definition.Shape!.Entity, definition.Shape.EntityLabel, definition.Shape.Action));
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.All(templates, template => Assert.DoesNotContain(permission, template.Permissions));
        }
    }

    /// <summary>
    /// Spec 2026-11-20 (RRHH fase 1): los cinco permisos exigen el módulo <c>hr</c> (apagado, el editor de roles los oculta y la
    /// API responde un 403 pelado) y van sólo a admin; ni asesor, ni facturación, ni cajero.
    /// </summary>
    [Fact]
    public void TheHrPermissionsRequireTheHrModuleAndGoOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        (string Permission, string Entity, string EntityLabel, string Action)[] expected =
        [
            (HrPermissions.EmployeeRead, "hr.employee", "Empleados", "read"),
            (HrPermissions.EmployeeManage, "hr.employee", "Empleados", "manage"),
            (HrPermissions.ContractRead, "hr.contract", "Contratos laborales", "read"),
            (HrPermissions.ContractManage, "hr.contract", "Contratos laborales", "manage"),
            (HrPermissions.SettingsManage, "hr.settings", "Configuración de RRHH", "manage"),
            (HrPermissions.MedicalRead, "hr.medical", "Exámenes médicos", "read"),
            (HrPermissions.MedicalManage, "hr.medical", "Exámenes médicos", "manage"),
        ];
        Assert.Equal(
            [
                "hr.employee.read", "hr.employee.manage", "hr.contract.read", "hr.contract.manage", "hr.settings.manage",
                "hr.medical.read", "hr.medical.manage",
            ],
            expected.Select(row => row.Permission));
        foreach (var (permission, entity, entityLabel, action) in expected)
        {
            var definition = definitions[permission];
            Assert.Equal("Recursos humanos", definition.Category);
            Assert.Equal([ModuleKeys.Hr], definition.RequiredModules!.ToArray());
            Assert.Equal((entity, entityLabel, action), (definition.Shape!.Entity, definition.Shape.EntityLabel, definition.Shape.Action));
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }
    }

    /// <summary>
    /// Spec 2026-11-21 (nómina): los cuatro permisos exigen el módulo <c>payroll</c> y van sólo a admin; ni asesor, ni facturación,
    /// ni cajero. Todos ven plata.
    /// </summary>
    [Fact]
    public void ThePayrollPermissionsRequireThePayrollModuleAndGoOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        (string Permission, string Entity, string EntityLabel, string Action)[] expected =
        [
            (PayrollPermissions.PeriodRead, "payroll.period", "Períodos de nómina", "read"),
            (PayrollPermissions.PeriodManage, "payroll.period", "Períodos de nómina", "manage"),
            (PayrollPermissions.PeriodApprove, "payroll.period", "Períodos de nómina", "approve"),
            (PayrollPermissions.SettingsManage, "payroll.settings", "Configuración de nómina", "manage"),
        ];
        Assert.Equal(
            ["payroll.period.read", "payroll.period.manage", "payroll.period.approve", "payroll.settings.manage"],
            expected.Select(row => row.Permission));
        foreach (var (permission, entity, entityLabel, action) in expected)
        {
            var definition = definitions[permission];
            Assert.Equal("Nómina", definition.Category);
            Assert.Equal([ModuleKeys.Payroll], definition.RequiredModules!.ToArray());
            Assert.Equal((entity, entityLabel, action), (definition.Shape!.Entity, definition.Shape.EntityLabel, definition.Shape.Action));
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }
    }

    /// <summary>
    /// Spec 2026-11-21 (Agenda): los cinco permisos exigen el módulo <c>scheduling</c> (apagado, el editor de roles los oculta y la API
    /// responde un 403 pelado) y de fábrica van a <c>admin</c> y a las dos plantillas de la Agenda; ni asesor, ni facturación, ni
    /// cajero. Configurar servicios, reglas y estructura (<c>settings.manage</c>) es sólo de admin.
    /// </summary>
    [Fact]
    public void TheSchedulingPermissionsRequireTheModuleAndGoToAdminAndTheSchedulingRoles()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        (string Permission, string Entity, string EntityLabel, string Action)[] expected =
        [
            (SchedulingPermissions.AppointmentRead, "scheduling.appointment", "Citas", "read"),
            (SchedulingPermissions.AppointmentManage, "scheduling.appointment", "Citas", "manage"),
            (SchedulingPermissions.AvailabilityManage, "scheduling.availability", "Franjas y bloqueos", "manage"),
            (SchedulingPermissions.AgendaReadAll, "scheduling.agenda", "Todas las agendas", "read"),
            (SchedulingPermissions.SettingsManage, "scheduling.settings", "Configuración de la agenda", "manage"),
        ];
        Assert.Equal(
            [
                "scheduling.appointment.read", "scheduling.appointment.manage", "scheduling.availability.manage",
                "scheduling.agenda.read_all", "scheduling.settings.manage",
            ],
            expected.Select(row => row.Permission));
        foreach (var (permission, entity, entityLabel, action) in expected)
        {
            var definition = definitions[permission];
            Assert.Equal("Agenda", definition.Category);
            Assert.Equal([ModuleKeys.Scheduling], definition.RequiredModules!.ToArray());
            Assert.Equal((entity, entityLabel, action), (definition.Shape!.Entity, definition.Shape.EntityLabel, definition.Shape.Action));
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }

        string[] manager = [.. FactoryPermissions(provider, "scheduling_manager")];
        string[] schedulingProvider = [.. FactoryPermissions(provider, "scheduling_provider")];
        Assert.Equal(
            new[]
            {
                SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage,
                SchedulingPermissions.AvailabilityManage, SchedulingPermissions.AgendaReadAll,
            }.Order(StringComparer.Ordinal),
            manager.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage, SchedulingPermissions.AvailabilityManage,
            }.Order(StringComparer.Ordinal),
            schedulingProvider.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(SchedulingPermissions.SettingsManage, manager);
        Assert.DoesNotContain(SchedulingPermissions.SettingsManage, schedulingProvider);
        Assert.DoesNotContain(SchedulingPermissions.AgendaReadAll, schedulingProvider);
    }

    /// <summary>
    /// Las plantillas de la Agenda conceden sólo permisos de la Agenda: con el módulo apagado sus roles no son asignables, y con él
    /// prendido sí. Si llevaran un permiso de núcleo (por ejemplo el de Clientes), un tenant sin Agenda vería «Gestor de agenda» en su
    /// lista (<c>RoleAssignability</c>, spec 2026-11-15). Por eso la búsqueda y el alta de clientes de la cita irán por la propia
    /// Agenda, como en el POS (spec 2026-10-14, decisión 4).
    /// </summary>
    [Fact]
    public void TheSchedulingRolesAreAssignableOnlyWithTheSchedulingModule()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var mask = provider.GetRequiredService<ModuleEntitlementMask>();
        var withScheduling = Modules.Tenancy.Domain.TenantModuleSet.FromStored(ModuleKeys.All);
        var withoutScheduling = Modules.Tenancy.Domain.TenantModuleSet.FromStored(ModuleKeys.All.Where(key => key != ModuleKeys.Scheduling));

        foreach (var role in new[] { "scheduling_manager", "scheduling_provider" })
        {
            var permissions = FactoryPermissions(provider, role);
            Assert.All(permissions, permission => Assert.StartsWith("scheduling.", permission));
            Assert.True(RoleAssignability.IsAssignable(permissions, withScheduling, isOperatorTenant: false, mask));
            Assert.False(RoleAssignability.IsAssignable(permissions, withoutScheduling, isOperatorTenant: false, mask));
        }
    }

    /// <summary>La otra mitad de un permiso nuevo: sin su política, <c>RequireAuthorization</c> no resuelve y la ruta responde 500, no 403.</summary>
    [Fact]
    public async Task TheSchedulingPermissionsHaveTheirPolicies()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var policies = provider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();

        foreach (var permission in new[]
        {
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage, SchedulingPermissions.AvailabilityManage,
            SchedulingPermissions.AgendaReadAll, SchedulingPermissions.SettingsManage,
        })
        {
            Assert.NotNull(await policies.GetPolicyAsync(permission));
        }
    }

    /// <summary>
    /// Spec 2026-11-17 (compras, P-3 propuesta): los dos permisos exigen el módulo <c>purchases</c> (apagado, el editor de roles
    /// los oculta y la API responde un 403 pelado) y van sólo a admin; ni asesor, ni facturación, ni cajero.
    /// </summary>
    [Fact]
    public void ThePurchasesPermissionsRequireThePurchasesModuleAndGoOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

        Assert.Equal("purchases.order.read", PurchasesPermissions.OrderRead);
        Assert.Equal("purchases.order.manage", PurchasesPermissions.OrderManage);
        foreach (var permission in new[] { PurchasesPermissions.OrderRead, PurchasesPermissions.OrderManage })
        {
            Assert.Equal("Compras", definitions[permission].Category);
            Assert.Equal([ModuleKeys.Purchases], definitions[permission].RequiredModules!.ToArray());
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }
    }

    /// <summary>
    /// Decisión del owner (2026-11-24): facturación ve las empresas y configura Siigo. De Integraciones sólo lee: crear, editar y
    /// desconectar una conexión daría también WhatsApp, Instagram y Shopify, así que sigue siendo de admin.
    /// </summary>
    [Fact]
    public void TheBillingRoleReadsCompaniesAndConnectionsWithoutManagingThem()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var billing = FactoryPermissions(provider, "billing");

        Assert.Contains(CompaniesPermissions.CompanyRead, billing);
        Assert.DoesNotContain(CompaniesPermissions.CompanyManage, billing);
        Assert.Contains(IntegrationsPermissions.ConnectionRead, billing);
        Assert.DoesNotContain(IntegrationsPermissions.ConnectionManage, billing);
    }

    /// <summary>
    /// Spec 2026-11-18 (integración Siigo): los permisos exigen el módulo <c>siigo</c>; admin y facturación llevan todos
    /// (decisiones del owner, 2026-11-24 y 2026-11-25). Asesor y cajero, ninguno.
    /// </summary>
    [Fact]
    public void TheSiigoPermissionsRequireTheSiigoModuleAndGoToAdminAndBilling()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);
        var all = new[] { SiigoPermissions.SettingsManage, SiigoPermissions.InvoiceIssue, SiigoPermissions.CallsRead };

        Assert.Equal(["siigo.settings.manage", "siigo.invoice.issue", "siigo.calls.read"], all);
        foreach (var permission in all)
        {
            Assert.Equal([ModuleKeys.Siigo], definitions[permission].RequiredModules!.ToArray());
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "advisor"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }

        Assert.Contains(SiigoPermissions.InvoiceIssue, FactoryPermissions(provider, "billing"));
        Assert.Contains(SiigoPermissions.SettingsManage, FactoryPermissions(provider, "billing"));
        // Decisión del owner (2026-11-25): quien factura ve las fallas de sus llamadas, aunque lleven datos del cliente.
        Assert.Contains(SiigoPermissions.CallsRead, FactoryPermissions(provider, "billing"));
    }

    /// <summary>
    /// Spec 2026-11-17 (mayorista, P-3 aprobada): los tres permisos exigen el módulo <c>wholesale</c> y van a admin; el asesor
    /// sólo ve los distribuidores. Facturación y cajero, ninguno.
    /// </summary>
    [Fact]
    public void TheWholesalePermissionsRequireTheWholesaleModuleAndGoToAdminAndReadToAdvisor()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);
        var all = new[] { WholesalePermissions.DistributorRead, WholesalePermissions.DistributorManage };

        Assert.Equal(["wholesale.distributor.read", "wholesale.distributor.manage"], all);
        foreach (var permission in all)
        {
            Assert.Equal("Mayorista", definitions[permission].Category);
            Assert.Equal([ModuleKeys.Wholesale], definitions[permission].RequiredModules!.ToArray());
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }

        Assert.Contains(WholesalePermissions.DistributorRead, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(WholesalePermissions.DistributorManage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(WholesalePermissions.PriceListManage, FactoryPermissions(provider, "advisor"));
    }

    /// <summary>
    /// Spec 2026-11-22 (D-10): las listas de precios son de Catalog. Un solo permiso, sin el módulo mayorista (el POS, la tienda y
    /// las cotizaciones las usan sin él), sólo en admin, y Mayorista lo nombra con el mismo literal porque no referencia Catalog.
    /// </summary>
    [Fact]
    public void ThePriceListPermissionIsCatalogsAndGoesToAdminOnly()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        var definition = Assert.Single(
            RegisteredPermissionDefinitions(), item => item.Permission == CatalogPermissions.PriceListManage);

        Assert.Equal("catalog.price_list.manage", CatalogPermissions.PriceListManage);
        Assert.Equal(CatalogPermissions.PriceListManage, WholesalePermissions.PriceListManage);
        Assert.Equal("Catalog", definition.Category);
        Assert.Equal([ModuleKeys.Catalog], definition.RequiredModules!.ToArray());
        Assert.Contains(CatalogPermissions.PriceListManage, FactoryPermissions(provider, "admin"));
        foreach (var role in new[] { "advisor", "billing", "cashier" })
        {
            Assert.DoesNotContain(CatalogPermissions.PriceListManage, FactoryPermissions(provider, role));
        }

        Assert.DoesNotContain(RegisteredPermissionDefinitions(), item => item.Permission == "wholesale.price_list.manage");
    }

    /// <summary>
    /// Spec 2026-11-14 (soporte, C1): los tres permisos exigen el módulo <c>support</c>; admin los lleva todos y el asesor lee y
    /// gestiona tickets, pero no configura. Facturación y cajero, ninguno.
    /// </summary>
    [Fact]
    public void TheSupportPermissionsRequireTheSupportModuleAdminGetsAllThreeAndAdvisorReadAndManage()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definitions = RegisteredPermissionDefinitions()
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);
        var all = new[] { SupportPermissions.Read, SupportPermissions.Manage, SupportPermissions.SettingsManage };

        Assert.Equal(["support.ticket.read", "support.ticket.manage", "support.settings.manage"], all);
        foreach (var permission in all)
        {
            Assert.Equal("Soporte", definitions[permission].Category);
            Assert.Equal([ModuleKeys.Support], definitions[permission].RequiredModules!.ToArray());
            Assert.Contains(permission, FactoryPermissions(provider, "admin"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "billing"));
            Assert.DoesNotContain(permission, FactoryPermissions(provider, "cashier"));
        }

        Assert.Equal("low", definitions[SupportPermissions.Read].RiskLevel);
        Assert.Equal("medium", definitions[SupportPermissions.Manage].RiskLevel);
        Assert.Equal("medium", definitions[SupportPermissions.SettingsManage].RiskLevel);
        Assert.Contains(SupportPermissions.Read, FactoryPermissions(provider, "advisor"));
        Assert.Contains(SupportPermissions.Manage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(SupportPermissions.SettingsManage, FactoryPermissions(provider, "advisor"));
    }

    /// <summary>Spec 2026-11-22 (tareas internas, §5.6): <c>support.task.use</c> es de riesgo bajo, exige el módulo Soporte y lo llevan
    /// de fábrica los cuatro roles, porque la meta es que cualquier miembro cree y trabaje tareas.</summary>
    [Fact]
    public void TheTaskUsePermissionIsLowNeedsSupportAndEveryFactoryRoleHasIt()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definition = RegisteredPermissionDefinitions().Single(d => d.Permission == SupportPermissions.TaskUse);

        Assert.Equal("support.task.use", SupportPermissions.TaskUse);
        Assert.Equal("Usar tareas internas", definition.DisplayName);
        Assert.Equal("Soporte", definition.Category);
        Assert.Equal("low", definition.RiskLevel);
        Assert.Equal([ModuleKeys.Support], definition.RequiredModules!.ToArray());
        Assert.Equal(new PermissionShape("support.task", "Tareas internas", "use"), definition.Shape);
        foreach (var role in new[] { "admin", "advisor", "billing", "cashier" })
        {
            Assert.Contains(SupportPermissions.TaskUse, FactoryPermissions(provider, role));
        }
    }

    /// <summary>La otra mitad de los permisos de soporte, más las dos políticas de visibilidad (D5 y D15): <c>TicketView</c> acepta
    /// leer o gestionar y <c>SettingsView</c> además configurar. Sin ellas, la ruta responde 500, no 403.</summary>
    [Fact]
    public async Task TheSupportPoliciesAreRegistered()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var permission in new[] { SupportPermissions.Read, SupportPermissions.Manage, SupportPermissions.SettingsManage })
        {
            var policy = await policies.GetPolicyAsync(permission);
            Assert.NotNull(policy);
            Assert.Equal([permission], AcceptedPermissions(policy));
        }

        Assert.Equal("support.ticket.view", SupportPolicies.TicketView);
        Assert.Equal("support.settings.view", SupportPolicies.SettingsView);
        var ticketView = await policies.GetPolicyAsync(SupportPolicies.TicketView);
        Assert.NotNull(ticketView);
        Assert.Equal([SupportPermissions.Read, SupportPermissions.Manage], AcceptedPermissions(ticketView));
        var settingsView = await policies.GetPolicyAsync(SupportPolicies.SettingsView);
        Assert.NotNull(settingsView);
        Assert.Equal(
            [SupportPermissions.Read, SupportPermissions.Manage, SupportPermissions.SettingsManage],
            AcceptedPermissions(settingsView));

        // Spec 2026-11-22 (tareas internas) §5.6 y D10: actuar sobre un ticket lo decide la puerta; la política sólo deja pasar a
        // quien gestiona o usa tareas. Ver acepta además leer.
        Assert.Equal("support.ticket.act", SupportPolicies.TicketAct);
        Assert.Equal("support.ticket.scoped_view", SupportPolicies.TicketScopedView);
        var ticketAct = await policies.GetPolicyAsync(SupportPolicies.TicketAct);
        Assert.NotNull(ticketAct);
        Assert.Equal([SupportPermissions.Manage, SupportPermissions.TaskUse], AcceptedPermissions(ticketAct));
        var scopedView = await policies.GetPolicyAsync(SupportPolicies.TicketScopedView);
        Assert.NotNull(scopedView);
        Assert.Equal([SupportPermissions.Read, SupportPermissions.Manage, SupportPermissions.TaskUse], AcceptedPermissions(scopedView));
    }

    private static string[] AcceptedPermissions(AuthorizationPolicy policy) =>
        Assert.Single(policy.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement>(),
                requirement => requirement.ClaimType == Bootstrapper.Authentication.QepClaimTypes.Permission)
            .AllowedValues!.ToArray();

    /// <summary>
    /// Spec de la tienda en línea (2026-11-03, owner): <c>store.settings.manage</c> exige el módulo <c>store</c> y va SOLO a admin. La bandeja
    /// reusa <c>quotations.order.manage</c>, que no se redefine.
    /// </summary>
    [Fact]
    public void TheStoreSettingsPermissionRequiresTheStoreModuleAndGoesOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var definition = Assert.Single(
            RegisteredPermissionDefinitions(), candidate => candidate.Permission == StorePermissions.SettingsManage);

        Assert.Equal("store.settings.manage", StorePermissions.SettingsManage);
        Assert.Equal("Tienda", definition.Category);
        Assert.Equal([ModuleKeys.Store], definition.RequiredModules!.ToArray());
        Assert.Contains(StorePermissions.SettingsManage, FactoryPermissions(provider, "admin"));
        foreach (var role in new[] { "advisor", "billing", "cashier" })
        {
            Assert.DoesNotContain(StorePermissions.SettingsManage, FactoryPermissions(provider, role));
        }
    }

    /// <summary>Spec 2026-11-14 §3: la detección de ediciones de Shopify reemplaza la vacía. Sin ella, una edición en Shopify se
    /// guarda en el espejo y nunca llega a la bandeja ni al pedido, sin error.</summary>
    [Fact]
    public void TheEditDetectorIsTheRealOne()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.IsType<ShopifyOrderEdits>(scope.ServiceProvider.GetRequiredService<IShopifyOrderEditDetector>());
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
        // Spec 2026-11-24 §11.1.1: sin el consumidor, la dirección cambiada en «Editar pedido» se publica y nunca llega a Shopify.
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Modules.Shopify.Infrastructure.Workers.QuotationOrderEventsWorker");
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ShopifyShippingPartyChangedApplier>());
    }

    /// <summary>
    /// Spec 2026-11-17 §5.4: los puertos de productos de Shopify tienen adaptador en el composition root. Sin alguno, la importación
    /// y los webhooks de productos no arrancan; con <c>ValidateOnBuild</c> el contenedor lo dice antes.
    /// </summary>
    [Fact]
    public void TheProductPortsAreRegistered()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifyCatalogWriter>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifyProductImageStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifyImageFetcher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifyReauthorization>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifyProductsSource>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifyProductPayloadParser>());
        Assert.NotNull(provider.GetRequiredService<IShopifyProductSyncMetrics>());
    }

    /// <summary>Spec 2026-11-17 (onboarding) §4: el asistente de la tienda crea la empresa por Companies desde el composition
    /// root. Sin el adaptador, el <c>POST …/setup/company</c> no arranca.</summary>
    [Fact]
    public void TheSetupPortsAreRegistered()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifySetupCompanies>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifyMemberDirectory>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IShopifyStoreReferences>());
    }

    /// <summary>
    /// Spec cobros-recurrentes §3.3: sin el worker registrado, o con un puerto del procesador sin adaptador, ningún cobro se genera
    /// después de crear el servicio y ningún cliente recibe su aviso, sin error en ningún lado. Mismo armado que el de Shopify.
    /// </summary>
    [Fact]
    public void TheContainerValidatesAndBuildsTheServiceContractsReminderWorker()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Modules.ServiceContracts.Infrastructure.Reminders.ServiceContractsReminderWorker");
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IServiceContractsReminderProcessor>());
    }

    /// <summary>
    /// Plan 2026-11-21, Tarea 12: sin el worker registrado, o con un puerto sin adaptador de Bootstrapper (el envío por Mensajería y el
    /// cliente con su consentimiento), ningún recordatorio de cita sale, sin error en ningún lado. Mismo armado que el de Contratos de servicio.
    /// </summary>
    [Fact]
    public void TheContainerValidatesAndBuildsTheSchedulingReminderWorker()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Modules.Scheduling.Infrastructure.Reminders.AppointmentReminderWorker");
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Modules.Scheduling.Application.IAppointmentReminderSender>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Modules.Scheduling.Application.ISchedulingReminderRecipients>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Modules.Scheduling.Application.ISchedulingReminderTemplateStatus>());
    }

    /// <summary>
    /// Sin el worker registrado, o con un puerto del procesador sin adaptador, ninguna cuota de préstamo se genera al cerrar su
    /// período, sin error en ningún lado. Mismo armado que el de Contratos de servicio.
    /// </summary>
    [Fact]
    public void TheContainerValidatesAndBuildsTheLoansGenerationWorker()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Modules.Loans.Infrastructure.Generation.LoansGenerationWorker");
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Modules.Loans.Application.ILoansGenerationProcessor>());
    }

    /// <summary>
    /// WMS-D: los cuatro workers del ledger (publicador de saldos, reconciliación, particiones y limpieza) y la entrega del export se
    /// construyen desde el contenedor con validación de ámbitos. Son singleton y usan scopes propios: uno que pidiera un servicio
    /// scoped directo se cae acá y no al arrancar en producción. La entrega no es un hosted service: es una definición que drena
    /// <c>NotificationsDeliveryWorker</c> (spec 2026-11-13, D-7). Mismo armado que el de Contratos de servicio.
    /// </summary>
    [Fact]
    public void TheContainerValidatesAndBuildsTheWmsWorkers()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var hosted = provider.GetServices<IHostedService>().Select(service => service.GetType().FullName).ToArray();
        foreach (var worker in new[]
        {
            "Modules.Wms.Infrastructure.Workers.BalanceChangePublisher",
            "Modules.Wms.Infrastructure.Workers.BalanceReconciliationWorker",
            "Modules.Wms.Infrastructure.Workers.PartitionMaintenanceWorker",
            "Modules.Wms.Infrastructure.Workers.WmsHousekeepingWorker",
            "Modules.Notifications.Infrastructure.Messaging.NotificationsDeliveryWorker",
        })
        {
            Assert.Contains(worker, hosted);
        }

        var definitionType = services
            .Select(descriptor => descriptor.ServiceType)
            .First(type => type.FullName == "Modules.Notifications.Infrastructure.Messaging.OutboxDeliveryWorker");
        Assert.Contains(
            provider.GetServices(definitionType),
            definition => definition!.GetType().FullName == "Modules.Notifications.Infrastructure.Messaging.WmsExportDeliveryWorker");
    }

    /// <summary>
    /// Spec 2026-11-13 (Valkey), D-7; plan P10: los timbres los toca el interceptor de EF al confirmar un <c>INSERT</c> en una tabla de
    /// cola. Un <c>DbContext</c> sin el interceptor escribiría en su cola sin despertar a nadie hasta la red de seguridad, sin error. Se
    /// exige en todos los nuestros, no sólo en los que mapean una tabla de cola: el SQL crudo (<c>ExecuteSql</c>) inserta en tablas que el
    /// modelo no mapea.
    /// </summary>
    [Fact]
    public void EveryDbContextThatMapsADoorbellTableHasTheInterceptor()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        var contextTypes = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(type)
                && type.Namespace?.StartsWith("Modules.", StringComparison.Ordinal) == true)
            .Distinct()
            .ToList();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotEmpty(contextTypes);
        var mapsOutbox = false;
        foreach (var type in contextTypes)
        {
            var context = (Microsoft.EntityFrameworkCore.DbContext)scope.ServiceProvider.GetRequiredService(type);
            var options = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>(context);
            var interceptors = options
                .FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()?.Interceptors ?? [];
            Assert.True(
                interceptors.OfType<BuildingBlocks.Infrastructure.QueueDoorbellInterceptor>().Any(),
                $"{type.FullName} no tiene QueueDoorbellInterceptor: sus inserciones en una cola no timbran.");
            mapsOutbox |= context.Model.GetEntityTypes().Any(entity =>
                Microsoft.EntityFrameworkCore.RelationalEntityTypeExtensions.GetTableName(entity) == "outbox_messages");
        }

        Assert.True(mapsOutbox, "Ningún DbContext mapea platform.outbox_messages: la prueba no estaría mirando los productores.");
    }

    /// <summary>
    /// Spec 2026-11-22 §9.1: el grupo personal de un miembro se llama <c>{tenant}:@member:{id}</c> y el hub lo une desde las claims. Si algún
    /// autorizador declarara un prefijo que empieza por <c>@</c>, un cliente podría suscribirse a ese grupo y leer avisos ajenos.
    /// </summary>
    [Fact]
    public void NoAuthorizerPrefixStartsWithAt()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var authorizers = scope.ServiceProvider.GetServices<BuildingBlocks.Application.Realtime.IRealtimeTopicAuthorizer>().ToList();

        Assert.NotEmpty(authorizers);
        Assert.All(authorizers, authorizer => Assert.False(
            authorizer.Prefix.StartsWith('@'),
            $"{authorizer.GetType().Name} declara el prefijo '{authorizer.Prefix}': los tópicos '@…' son grupos personales."));
    }

    /// <summary>
    /// Observabilidad fase 2: sin el muestreador registrado los gauges de cola no publican nada y nadie se entera; y como los gauges
    /// son singleton, ValidateScopes caza que dependan de un servicio scoped (IClock lo es).
    /// </summary>
    [Fact]
    public void TheContainerValidatesAndBuildsTheQueueSampler()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service.GetType().FullName == "Bootstrapper.Observability.QueueSamplerWorker");
        Assert.NotNull(provider.GetRequiredService<Bootstrapper.Observability.QueueGauges>());
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
        // Spec 2026-11-03 D-8: el pool suelta una conexión ociosa a los 4 s, antes que el keep-alive de cualquier salto (Cloudflare, ingress,
        // NAT del hairpin), y la recicla a los 5 min aunque no esté ociosa. Con el default de .NET (1 min, vida sin límite) el bloque
        // reutilizaba un socket que alguien ya había cerrado y recibía connection_reset (producción, 2026-11-06).
        Assert.Equal(TimeSpan.FromSeconds(4), primary.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), primary.PooledConnectionLifetime);
        using var scope = provider.CreateScope();
        Assert.IsType<Modules.Automations.Infrastructure.Http.HttpBlockExecutor>(
            scope.ServiceProvider.GetRequiredService<Modules.Automations.Application.Http.IHttpBlockExecutor>());
    }

    /// <summary>
    /// Plan 2026-11-12, 5.2 y C-14: el contenedor arma un adaptador del modelo para cada proveedor, y el cliente HTTP de Claude no tiene
    /// los loggers de <see cref="HttpClient"/> (escribirían el header con la clave del tenant) ni sigue redirecciones.
    /// </summary>
    [Fact]
    public void EveryAiProviderHasAModelClientAndTheClaudeHttpClientHasNoLoggers()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IAiModelClientResolver>();
        foreach (var aiProvider in Modules.Ai.Domain.AiProviders.All)
        {
            Assert.NotNull(resolver.For(aiProvider));
        }

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(Modules.Ai.Infrastructure.Model.Anthropic.ClaudeModelClient.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            Assert.DoesNotContain("Logging", delegating.GetType().Name, StringComparison.Ordinal);
            handler = delegating.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    /// <summary>Spec 2026-10-29 §9.1, O-2: el cliente HTTP de OpenAI tampoco tiene loggers (la clave va en <c>Authorization</c>) ni sigue
    /// redirecciones.</summary>
    [Fact]
    public void TheOpenAiHttpClientHasNoLoggersAndNoRedirects()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(Modules.Ai.Infrastructure.Model.OpenAi.OpenAiModelClient.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            Assert.DoesNotContain("Logging", delegating.GetType().Name, StringComparison.Ordinal);
            handler = delegating.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    /// <summary>Spec 2026-11-18 (integración Siigo): el cliente HTTP del probador de Siigo tampoco tiene loggers (la clave de
    /// acceso va en el cuerpo de <c>/auth</c> y el token en <c>Authorization</c>) ni sigue redirecciones. El nombre va literal porque
    /// <c>SiigoConnectionTester</c> es interno de Integrations.Infrastructure.</summary>
    [Fact]
    public void TheSiigoTesterHttpClientHasNoLoggersAndNoRedirects()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler("integrations.siigo");
        while (handler is DelegatingHandler delegating)
        {
            Assert.DoesNotContain("Logging", delegating.GetType().Name, StringComparison.Ordinal);
            handler = delegating.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    /// <summary>Spec 2026-11-18 §7: el cliente de la API de Siigo (<c>siigo</c>) no tiene loggers —el log de <c>HttpClient</c> escribiría
    /// el <c>Authorization</c> y la URL con la identificación del cliente— ni sigue redirecciones: la clave de acceso viaja en el cuerpo de
    /// <c>/auth</c>. El nombre va literal porque <c>SiigoApiClient</c> es interno de Siigo.Infrastructure.</summary>
    [Fact]
    public void TheSiigoHttpClientHasNoLoggersAndNoRedirects()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("siigo");
        while (handler is DelegatingHandler delegating)
        {
            Assert.DoesNotContain("Logging", delegating.GetType().Name, StringComparison.Ordinal);
            handler = delegating.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    /// <summary>
    /// Plan fase 2, C2.4 (spec 2026-11-17 copiloto §6): un contexto de ejecución que no sale de los claims de un request sólo lo crea el
    /// ejecutor de herramientas del copiloto, y sólo él lo instala en un scope. Cualquier otro lugar que lo hiciera podría correr consultas
    /// con los permisos de alguien que no las pidió. Se lee del IL: un <c>new</c> o un <c>Enter</c> en otro tipo pone esto en rojo.
    /// </summary>
    [Fact]
    public void OnlyTheCopilotExecutorAndTheSiigoBatchInvokerCreateADelegatedContext()
    {
        var bootstrapper = typeof(QepServiceCollectionExtensions).Assembly;
        var delegated = bootstrapper.GetType("Bootstrapper.Copilot.DelegatedExecutionContext", throwOnError: true)!;
        var scope = bootstrapper.GetType("Bootstrapper.Copilot.DelegatedExecutionScope", throwOnError: true)!;
        var executor = bootstrapper.GetType("Bootstrapper.Copilot.CopilotToolExecutor", throwOnError: true)!;
        // Spec 2026-11-21: el único otro que corre un handler como un miembro fuera de un request. Factura cada pedido de una tanda con
        // los permisos que quien la pidió tiene al procesarlo.
        var siigoBatchInvoker = bootstrapper.GetType("Bootstrapper.SiigoBatchItemInvoker", throwOnError: true)!;

        var creators = IlScanner.CallersOf(bootstrapper, method => method is ConstructorInfo && method.DeclaringType == delegated);
        var enterers = IlScanner.CallersOf(bootstrapper, method => method.DeclaringType == scope && method.Name == "Enter");

        Assert.NotEmpty(creators);
        Assert.NotEmpty(enterers);
        Assert.All(creators.Concat(enterers), caller => Assert.True(
            IlScanner.IsWithin(caller, executor) || IlScanner.IsWithin(caller, siigoBatchInvoker),
            $"{caller.DeclaringType?.FullName}.{caller.Name} creates or installs a delegated context."));
    }

    /// <summary>Plan fase 2, C2.4: fuera de un scope del ejecutor del copiloto, <c>IExecutionContext</c> sigue siendo el de los claims del
    /// request, y por scope (un singleton serviría el contexto de otro).</summary>
    [Fact]
    public void OutsideADelegatedScopeTheExecutionContextIsTheHttpOne()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal(ServiceLifetime.Scoped, services.Single(descriptor => descriptor.ServiceType == typeof(IExecutionContext)).Lifetime);
        Assert.Equal("HttpExecutionContext", scope.ServiceProvider.GetRequiredService<IExecutionContext>().GetType().Name);
    }

    /// <summary>Plan fase 2, C2.3: el trabajo del copiloto se arma con su ejecutor de herramientas, que vive en Bootstrapper.</summary>
    [Fact]
    public void TheContainerBuildsTheCopilotJobProcessor()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CopilotJobProcessor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICopilotToolExecutor>());
    }

    /// <summary>Plan fase 2, C2.5: cada adaptador del copiloto registrado una vez, con nombre único (el ejecutor los indexa por nombre). Los
    /// de reportes, siete.</summary>
    [Fact]
    public void EveryCopilotToolIsRegisteredOnceWithAUniqueName()
    {
        var services = BuildPlatformServices();
        using var provider = services.BuildServiceProvider();
        var contract = typeof(QepServiceCollectionExtensions).Assembly.GetType("Bootstrapper.Copilot.ICopilotTool", throwOnError: true)!;

        var names = provider.GetServices(contract)
            .Select(tool => ((CopilotToolContract)contract.GetProperty("Contract")!.GetValue(tool)!).Name)
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Superset(
            new HashSet<string>(
                ["get_orders_summary", "list_orders_report", "get_quotations_summary", "list_quotations_report", "get_customers_summary",
                 "get_margin_summary", "get_price_changes_summary"],
                StringComparer.Ordinal),
            names.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Spec 2026-10-29 §12: el worker de modelo y el procesador de las pruebas se arman con todos sus puertos (las fuentes de las
    /// herramientas son adaptadores de Bootstrapper: sin su registro, el primer trabajo daría 500 en vez de una respuesta).</summary>
    [Fact]
    public void TheContainerBuildsTheAiModelWorkerAndTheTestJobProcessor()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AgentTestJobProcessor>());
        var worker = typeof(Modules.Ai.Infrastructure.AiInfrastructureExtensions).Assembly.GetType("Modules.Ai.Infrastructure.AiModelWorker", throwOnError: true)!;
        Assert.IsAssignableFrom<IHostedService>(provider.GetRequiredService(worker));
    }

    /// <summary>
    /// Spec 2026-10-25 D-E5: <c>RunUnitWriter</c> es un parámetro opcional de <c>RunProcessor</c>, así que sin su registro el contenedor
    /// arma el procesador igual, sin registro de unidades, y ninguna corrida deja su fila en <c>run_units</c> sin error en ningún lado.
    /// </summary>
    [Fact]
    public void TheContainerBuildsTheRunProcessorWithItsRunUnitWriter()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var processor = scope.ServiceProvider.GetRequiredService<Modules.Automations.Application.Engine.RunProcessor>();

        var writer = typeof(Modules.Automations.Application.Engine.RunProcessor)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(Modules.Automations.Application.Engine.RunUnitWriter));
        Assert.NotNull(writer.GetValue(processor));
    }

    /// <summary>Spec 2026-10-31: el arranque manual del asistente se resuelve del contenedor con todos sus puertos.</summary>
    [Fact]
    public void TheContainerBuildsTheManualRunStarter()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Modules.Automations.Application.Engine.ManualRunStarter>());
    }

    /// <summary>Spec 2026-11-19 (chatbot CRM action): the CRM port and catalog of Automations resolve to their Bootstrapper adapters, over
    /// the system entry points of Customers and Pipeline; the engine and the editor builder resolve with them.</summary>
    [Fact]
    public void TheContainerBuildsTheChatbotCrmAction()
    {
        var services = BuildPlatformServices();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal("AutomationCrm", scope.ServiceProvider.GetRequiredService<IAutomationCrm>().GetType().Name);
        Assert.Equal("AutomationsCrmDirectory", scope.ServiceProvider.GetRequiredService<IAutomationsCrmDirectory>().GetType().Name);
        Assert.IsType<Modules.Customers.Application.ChatbotCustomerTags>(
            scope.ServiceProvider.GetRequiredService<Modules.Customers.Application.IChatbotCustomerTags>());
        Assert.IsType<Modules.Pipeline.Application.ChatbotDealMover>(
            scope.ServiceProvider.GetRequiredService<Modules.Pipeline.Application.IChatbotDealMover>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Modules.Automations.Application.Engine.RunEffects>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ChatbotEditorBuilder>());
    }

    /// <summary>Spec 2026-11-25 (roles por tenant): sólo <c>admin</c> se define en código. Una definición más volvería a reservar
    /// permisos por clave, y un rol del tenant con esa clave la pisaría.</summary>
    [Fact]
    public void OnlyAdminIsASystemRole()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("admin", Assert.Single(catalog.ListRoles()).Role);
    }

    /// <summary>Las cinco plantillas con las que se siembra cada tenant, con los mismos nombres de siempre.</summary>
    [Fact]
    public void TheFiveTemplatesAreRegisteredWithTodaysNames()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();

        var templates = provider.GetServices<RoleTemplate>().ToArray();

        Assert.Equal(
            new (string, string)[]
            {
                (RoleTemplateKeys.Advisor, "Asesor"),
                (RoleTemplateKeys.Billing, "Facturación"),
                (RoleTemplateKeys.Cashier, "Cajero"),
                (RoleTemplateKeys.SchedulingManager, "Gestor de agenda"),
                (RoleTemplateKeys.SchedulingProvider, "Profesional de agenda"),
            },
            templates
                .Select(template => (template.Key, template.DisplayName))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    /// <summary>El catálogo de permisos sólo ofrece lo que concede algún rol de sistema: un permiso de una plantilla que
    /// <c>admin</c> no llevara no se podría marcar ni guardar (<c>authorization.role.permission_unknown</c>).</summary>
    [Fact]
    public void EveryRoleTemplateIsASubsetOfAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var offered = provider.GetRequiredService<IRoleCatalog>().ListPermissions()
            .Select(permission => permission.Permission)
            .ToHashSet(StringComparer.Ordinal);
        var templates = provider.GetServices<RoleTemplate>().ToArray();

        Assert.NotEmpty(templates);
        Assert.All(templates, template => Assert.Empty(
            template.Permissions.Where(permission => !offered.Contains(permission))
                .Select(permission => $"{template.Key}: {permission}")));
    }

    [Fact]
    public void TheContainerCanBuildTheEntitlementMask()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ModuleEntitlementMask>());
    }

    /// <summary>Spec 2026-10-30: las claves de API las administra solo quien administra la empresa, y la pantalla solo existe con el módulo
    /// <c>developer</c>. Sin la capacidad el permiso ni se ofrece en el editor de roles.</summary>
    [Fact]
    public void TheApiKeyPermissionRequiresTheDeveloperModuleAndGoesOnlyToAdmin()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Equal("developer.api_key.manage", ApiAccessPermissions.ApiKeyManage);
        Assert.Contains(ApiAccessPermissions.ApiKeyManage, FactoryPermissions(provider, "admin"));
        Assert.DoesNotContain(ApiAccessPermissions.ApiKeyManage, FactoryPermissions(provider, "advisor"));
        Assert.DoesNotContain(ApiAccessPermissions.ApiKeyManage, FactoryPermissions(provider, "billing"));
        Assert.DoesNotContain(ApiAccessPermissions.ApiKeyManage, FactoryPermissions(provider, "cashier"));
        var definition = Assert.Single(catalog.ListPermissions(), candidate => candidate.Permission == ApiAccessPermissions.ApiKeyManage);
        Assert.Equal("Developer", definition.Category);
        Assert.Equal([ModuleKeys.Developer], definition.RequiredModules);
    }

    /// <summary>Un scope que nombra un módulo que no existe nunca se ofrecería, y uno repetido se pisaría en el catálogo: las dos cosas
    /// pasan en silencio, así que se amarran aquí.</summary>
    [Fact]
    public void EveryApiScopeDeclaresARealModule()
    {
        var scopes = RegisteredApiScopes();

        Assert.NotEmpty(scopes);
        Assert.All(scopes, scope => Modules.Tenancy.Domain.TenantModuleKey.Parse(scope.RequiredModule));
        Assert.Equal(scopes.Length, scopes.Select(scope => scope.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheTemplatesSendScopeIsRestrictableAndBelongsToMessaging()
    {
        var scope = Assert.Single(RegisteredApiScopes(), candidate => candidate.Key == MessagingScopes.TemplatesSend);

        Assert.Equal("whatsapp.templates.send", scope.Key);
        Assert.Equal(ModuleKeys.Messaging.Value, scope.RequiredModule);
        Assert.True(scope.RestrictableByConnection);
    }

    /// <summary>Lo que concede un rol de fábrica: <c>admin</c> sale del catálogo de sistema y los demás de la plantilla con la que se
    /// siembra cada tenant.</summary>
    private static IReadOnlyCollection<string> FactoryPermissions(IServiceProvider provider, string role) =>
        role == "admin"
            ? provider.GetRequiredService<IRoleCatalog>().PermissionsFor(role)
            : provider.GetServices<RoleTemplate>().Single(template => template.Key == role).Permissions;

    private static ApiScopeDefinition[] RegisteredApiScopes() =>
        BuildPlatformServices()
            .Where(descriptor => descriptor.ServiceType == typeof(ApiScopeDefinition))
            .Select(descriptor => (ApiScopeDefinition)descriptor.ImplementationInstance!)
            .ToArray();

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
                // La lista curada de Ai (C-3): sin ella, armar algo que lea el catálogo de modelos lanza al validar las opciones.
                ["Ai:DefaultProvider"] = "anthropic",
                ["Ai:Providers:anthropic:Models:0:Id"] = "claude-opus-5-5",
                ["Ai:Providers:anthropic:Models:0:Label"] = "Claude Opus 5.5",
                ["Ai:Providers:anthropic:Models:0:IsDefault"] = "true",
                ["Ai:Providers:anthropic:Models:0:Effort"] = "medium",
                ["Ai:Providers:openai:Models:0:Id"] = "gpt-6-luna",
                ["Ai:Providers:openai:Models:0:Label"] = "GPT-6 Luna",
                ["Ai:Providers:openai:Models:0:IsDefault"] = "true",
                ["Ai:Providers:openai:Models:0:Available"] = "false",
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
