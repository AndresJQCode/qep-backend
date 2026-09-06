using Bootstrapper.Authentication;
using Bootstrapper.Messaging;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Observability;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Modules.Audit.Infrastructure;
using Modules.Authorization.Application;
using Modules.Authorization.Infrastructure;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure;
using Modules.Companies.Application;
using Modules.Companies.Infrastructure;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure;
using Modules.Geography.Application;
using Modules.Geography.Infrastructure;
using Modules.Identity.Infrastructure;
using Modules.Notifications.Infrastructure;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure;
using Modules.Reporting.Application;
using Modules.Reporting.Infrastructure;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;
using Modules.Tenancy.Application;
using Modules.Tenancy.Infrastructure;

namespace Bootstrapper;

public static class QepServiceCollectionExtensions
{
    public static IServiceCollection AddQepPlatform(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, ExternalClaimsTransformation>();
        services.AddScoped<IClock, BuildingBlocks.Infrastructure.SystemClock>();
        services.AddScoped<IExecutionContext, HttpExecutionContext>();
        services.AddScoped<IRequestDispatcher, RequestDispatcher>();
        services.AddScoped<
            IQueryHandler<GetTenantSettingsQuery, TenantSettingsDto>,
            GetTenantSettingsHandler>();
        services.AddScoped<
            ICommandHandler<UpdateTenantSettingsCommand, TenantSettingsDto>,
            UpdateTenantSettingsHandler>();
        services.AddScoped<
            ICommandHandler<InviteMemberCommand, MembershipDto>,
            InviteMemberHandler>();
        services.AddScoped<
            ICommandHandler<CreateRoleCommand, Modules.Authorization.Domain.Role>,
            CreateRoleHandler>();
        services.AddScoped<
            ICommandHandler<UpdateRoleCommand, Modules.Authorization.Domain.Role>,
            UpdateRoleHandler>();
        services.AddScoped<ICommandHandler<DeleteRoleCommand, bool>, DeleteRoleHandler>();
        services.AddScoped<
            IQueryHandler<ListTenantRolesQuery, IReadOnlyCollection<TenantRoleDefinition>>,
            ListTenantRolesHandler>();
        services.AddScoped<
            IQueryHandler<ListMembershipsQuery, MembershipListDto>,
            ListMembershipsHandler>();
        services.AddScoped<
            ICommandHandler<SuspendMemberCommand, MembershipListItemDto>,
            SuspendMemberHandler>();
        services.AddScoped<
            ICommandHandler<RemoveMemberCommand, MembershipListItemDto>,
            RemoveMemberHandler>();
        services.AddScoped<
            ICommandHandler<ReactivateMemberCommand, MembershipListItemDto>,
            ReactivateMemberHandler>();
        services.AddScoped<
            ICommandHandler<UpdateMemberRolesCommand, MembershipListItemDto>,
            UpdateMemberRolesHandler>();
        services.AddScoped<
            ICommandHandler<CreateUploadSessionCommand, UploadSessionDto>,
            CreateUploadSessionHandler>();
        services.AddScoped<
            ICommandHandler<CompleteUploadCommand, FileResourceDto>,
            CompleteUploadHandler>();
        services.AddScoped<
            ICommandHandler<CancelUploadCommand, CancelUploadResult>,
            CancelUploadHandler>();
        services.AddScoped<
            ICommandHandler<UpdateFileMetadataCommand, FileResourceDto>,
            UpdateFileMetadataHandler>();
        services.AddScoped<
            ICommandHandler<IssueDownloadUrlCommand, DownloadUrlDto>,
            IssueDownloadUrlHandler>();
        services.AddScoped<
            ICommandHandler<SoftDeleteFileCommand, Modules.Storage.Application.SoftDeleteResult>,
            SoftDeleteFileHandler>();
        services.AddScoped<
            ICommandHandler<PublishFileCommand, FileResourceDto>,
            PublishFileHandler>();
        services.AddScoped<
            ICommandHandler<UnpublishFileCommand, FileResourceDto>,
            UnpublishFileHandler>();
        services.AddScoped<
            IQueryHandler<ListFilesQuery, PagedFilesDto>,
            ListFilesHandler>();
        services.AddScoped<
            IQueryHandler<ListProductsQuery, ProductPage>,
            ListProductsHandler>();
        services.AddScoped<
            IQueryHandler<GetProductQuery, ProductDto>,
            GetProductHandler>();
        services.AddScoped<
            ICommandHandler<ExportProductsCommand, ExportProductsResult>,
            ExportProductsHandler>();
        services.AddScoped<
            ICommandHandler<CreateProductCommand, ProductDto>,
            CreateProductHandler>();
        services.AddScoped<
            ICommandHandler<UpdateProductCommand, ProductDto>,
            UpdateProductHandler>();
        services.AddScoped<
            ICommandHandler<DeactivateProductCommand, ProductDto>,
            DeactivateProductHandler>();
        services.AddScoped<
            ICommandHandler<ActivateProductCommand, ProductDto>,
            ActivateProductHandler>();
        // Los handlers se registran uno por uno, no por escaneo de ensamblado. Un caso de uso
        // nuevo que se olvide acá compila, mapea su endpoint y falla recién en runtime con 500
        // al no poder resolverlo el dispatcher — el mismo modo de falla que un permiso sin su
        // AddPolicy. Los cinco de tasas costaron una corrida entera de las pruebas de
        // integración: 18 de 18 en rojo con InternalServerError.
        services.AddScoped<
            IQueryHandler<ListTaxRatesQuery, IReadOnlyList<TaxRateDto>>,
            ListTaxRatesHandler>();
        services.AddScoped<
            IQueryHandler<GetTaxRateQuery, TaxRateDto>,
            GetTaxRateHandler>();
        services.AddScoped<
            ICommandHandler<CreateTaxRateCommand, TaxRateDto>,
            CreateTaxRateHandler>();
        services.AddScoped<
            ICommandHandler<UpdateTaxRateCommand, TaxRateDto>,
            UpdateTaxRateHandler>();
        services.AddScoped<
            ICommandHandler<DeactivateTaxRateCommand, TaxRateDto>,
            DeactivateTaxRateHandler>();
        services.AddScoped<
            ICommandHandler<ActivateTaxRateCommand, TaxRateDto>,
            ActivateTaxRateHandler>();
        // CAT-06. Los handlers se registran a mano, uno por uno: olvidarse de esta línea deja el
        // endpoint mapeado y el dispatcher sin a quién llamar, y el síntoma es **500, no 404** —
        // no se parece en nada a la causa. Es el mismo defecto que el README documenta para las
        // políticas de permiso.
        services.AddScoped<
            ICommandHandler<DeleteTaxRateCommand, TaxRateDeletedResult>,
            DeleteTaxRateHandler>();
        // EMP. Los siete van aca por la misma razon que los de tasas: el dispatcher resuelve por
        // registro explicito, y un caso de uso que se olvide compila, mapea su endpoint y falla
        // recien en runtime con 500 al no encontrar handler.
        services.AddScoped<
            IQueryHandler<ListCompaniesQuery, IReadOnlyList<CompanyDto>>,
            ListCompaniesHandler>();
        services.AddScoped<
            IQueryHandler<GetCompanyQuery, CompanyDto>,
            GetCompanyHandler>();
        services.AddScoped<
            ICommandHandler<CreateCompanyCommand, CompanyDto>,
            CreateCompanyHandler>();
        services.AddScoped<
            ICommandHandler<UpdateCompanyCommand, CompanyDto>,
            UpdateCompanyHandler>();
        services.AddScoped<
            ICommandHandler<DeactivateCompanyCommand, CompanyDto>,
            DeactivateCompanyHandler>();
        services.AddScoped<
            ICommandHandler<ActivateCompanyCommand, CompanyDto>,
            ActivateCompanyHandler>();
        services.AddScoped<
            ICommandHandler<DeleteCompanyCommand, CompanyDeletedResult>,
            DeleteCompanyHandler>();
        // CLI. Los siete van aca por la misma razon que los de empresas: el dispatcher resuelve
        // por registro explicito, y un caso de uso que se olvide compila, mapea su endpoint y
        // falla recien en runtime con 500 al no encontrar handler.
        services.AddScoped<
            IQueryHandler<ListCustomersQuery, CustomerPage>,
            ListCustomersHandler>();
        services.AddScoped<
            IQueryHandler<GetCustomerQuery, CustomerDto>,
            GetCustomerHandler>();
        services.AddScoped<
            ICommandHandler<CreateCustomerCommand, CustomerDto>,
            CreateCustomerHandler>();
        services.AddScoped<
            ICommandHandler<UpdateCustomerCommand, CustomerDto>,
            UpdateCustomerHandler>();
        // La libreta de direcciones (CLI-DIR-01): cuatro comandos, uno por operacion.
        services.AddScoped<
            ICommandHandler<AddCustomerAddressCommand, CustomerDto>,
            AddCustomerAddressHandler>();
        services.AddScoped<
            ICommandHandler<UpdateCustomerAddressCommand, CustomerDto>,
            UpdateCustomerAddressHandler>();
        services.AddScoped<
            ICommandHandler<RemoveCustomerAddressCommand, CustomerDto>,
            RemoveCustomerAddressHandler>();
        services.AddScoped<
            ICommandHandler<MakeCustomerAddressPrincipalCommand, CustomerDto>,
            MakeCustomerAddressPrincipalHandler>();
        services.AddScoped<
            ICommandHandler<DeactivateCustomerCommand, CustomerDto>,
            DeactivateCustomerHandler>();
        services.AddScoped<
            ICommandHandler<ActivateCustomerCommand, CustomerDto>,
            ActivateCustomerHandler>();
        services.AddScoped<
            ICommandHandler<ImportCustomersCommand, ImportCustomersResponse>,
            ImportCustomersHandler>();
        // Fase 6: la plantilla es una lectura (IQuery), no un comando — no muta nada.
        services.AddScoped<
            IQueryHandler<GetCustomerImportTemplateQuery, CustomerImportTemplateFile>,
            GetCustomerImportTemplateHandler>();
        // El Excel de "filas a corregir" del modal de errores — misma naturaleza de lectura que
        // la plantilla de arriba, sólo que con datos ya cargados.
        services.AddScoped<
            IQueryHandler<ExportFailedCustomerRowsQuery, CustomerImportTemplateFile>,
            ExportFailedCustomerRowsHandler>();
        // La exportacion del padron sí es un comando aunque no mute clientes: sube un archivo al
        // almacenamiento de objetos, encola un correo y deja auditoría — tiene efecto commiteado en
        // una unidad de trabajo, igual que IssueDownloadUrlCommand en Storage.
        services.AddScoped<
            ICommandHandler<ExportCustomersCommand, ExportCustomersResult>,
            ExportCustomersHandler>();
        // El catalogo de clasificaciones de cliente vive en el mismo modulo que Customer pero es
        // un recurso distinto, con sus propios siete handlers — mismo criterio que los cinco de
        // TaxRate frente a Product en Catalog. Registrados a mano, uno por uno: un caso de uso
        // que se olvide compila, mapea su endpoint y falla recien en runtime con 500 al no
        // encontrar handler.
        services.AddScoped<
            IQueryHandler<ListClientClassificationsQuery, IReadOnlyList<ClientClassificationDto>>,
            ListClientClassificationsHandler>();
        services.AddScoped<
            IQueryHandler<GetClientClassificationQuery, ClientClassificationDto>,
            GetClientClassificationHandler>();
        services.AddScoped<
            ICommandHandler<CreateClientClassificationCommand, ClientClassificationDto>,
            CreateClientClassificationHandler>();
        services.AddScoped<
            ICommandHandler<UpdateClientClassificationCommand, ClientClassificationDto>,
            UpdateClientClassificationHandler>();
        services.AddScoped<
            ICommandHandler<DeactivateClientClassificationCommand, ClientClassificationDto>,
            DeactivateClientClassificationHandler>();
        services.AddScoped<
            ICommandHandler<ActivateClientClassificationCommand, ClientClassificationDto>,
            ActivateClientClassificationHandler>();
        services.AddScoped<
            ICommandHandler<DeleteClientClassificationCommand, ClientClassificationDeletedResult>,
            DeleteClientClassificationHandler>();
        // Geography no tiene tenant ni caso de uso de escritura: sólo dos lecturas de datos de
        // referencia DIVIPOLA. Van acá por la misma razón que el resto — el dispatcher resuelve
        // por registro explícito, y un caso de uso que se olvide compila, mapea su endpoint y
        // falla recién en runtime con 500 al no encontrar handler.
        services.AddScoped<
            IQueryHandler<ListDepartmentsQuery, IReadOnlyList<DepartmentDto>>,
            ListDepartmentsHandler>();
        services.AddScoped<
            IQueryHandler<ListCitiesQuery, IReadOnlyList<CityDto>>,
            ListCitiesHandler>();
        // Quotations, fase 1 (borrador: crear, agregar/editar/quitar lineas, editar encabezado).
        // Los seis van aca por la misma razon que el resto: el dispatcher resuelve por registro
        // explicito, y un caso de uso que se olvide compila, mapea su endpoint y falla recien en
        // runtime con 500 al no encontrar handler.
        services.AddScoped<
            ICommandHandler<CreateQuotationCommand, QuotationDto>,
            CreateQuotationHandler>();
        services.AddScoped<
            IQueryHandler<GetQuotationQuery, QuotationDto>,
            GetQuotationHandler>();
        services.AddScoped<
            IQueryHandler<ListQuotationsQuery, QuotationPage>,
            ListQuotationsHandler>();
        services.AddScoped<
            ICommandHandler<UpdateQuotationCommand, QuotationDto>,
            UpdateQuotationHandler>();
        services.AddScoped<
            ICommandHandler<AddQuotationItemCommand, QuotationDto>,
            AddQuotationItemHandler>();
        services.AddScoped<
            ICommandHandler<UpdateQuotationItemCommand, QuotationDto>,
            UpdateQuotationItemHandler>();
        services.AddScoped<
            ICommandHandler<RemoveQuotationItemCommand, QuotationDto>,
            RemoveQuotationItemHandler>();
        services.AddScoped<
            ICommandHandler<SendQuotationCommand, QuotationDto>,
            SendQuotationHandler>();
        services.AddScoped<
            ICommandHandler<VoidQuotationCommand, QuotationDto>,
            VoidQuotationHandler>();
        services.AddScoped<
            IQueryHandler<GetSaleQuery, SaleDto>,
            GetSaleHandler>();
        services.AddScoped<
            ICommandHandler<ConvertQuotationToSaleCommand, SaleDto>,
            ConvertQuotationToSaleHandler>();
        // Reporting. Los ocho van aca por la misma razon que el resto: el dispatcher resuelve por
        // registro explicito, y un caso de uso que se olvide compila, mapea su endpoint y falla
        // recien en runtime con 500 al no encontrar handler.
        //
        // Las cuatro exportaciones son IQueryHandler y no ICommandHandler: a diferencia de
        // ExportCustomersCommand, que sube un archivo y encola un correo, estas solo leen.
        services.AddScoped<
            IQueryHandler<ListSalesReportQuery, ReportPage<SalesReportItemDto>>,
            ListSalesReportHandler>();
        services.AddScoped<
            IQueryHandler<GetSalesReportSummaryQuery, SalesReportSummaryDto>,
            GetSalesReportSummaryHandler>();
        services.AddScoped<
            IQueryHandler<ExportSalesReportQuery, ReportFile>,
            ExportSalesReportHandler>();
        services.AddScoped<
            IQueryHandler<GetQuotationsReportSummaryQuery, QuotationsReportSummaryDto>,
            GetQuotationsReportSummaryHandler>();
        services.AddScoped<
            IQueryHandler<ListQuotationsReportQuery, ReportPage<QuotationsReportItemDto>>,
            ListQuotationsReportHandler>();
        services.AddScoped<
            IQueryHandler<ExportQuotationsReportQuery, ReportFile>,
            ExportQuotationsReportHandler>();
        services.AddScoped<
            IQueryHandler<ListPriceChangeReportQuery, ReportPage<PriceChangeReportItemDto>>,
            ListPriceChangeReportHandler>();
        services.AddScoped<
            IQueryHandler<GetPriceChangeReportSummaryQuery, PriceChangeReportSummaryDto>,
            GetPriceChangeReportSummaryHandler>();
        services.AddScoped<
            IQueryHandler<ExportPriceChangeReportQuery, ReportFile>,
            ExportPriceChangeReportHandler>();
        services.AddScoped<
            IQueryHandler<ListCustomerReportQuery, ReportPage<CustomerReportItemDto>>,
            ListCustomerReportHandler>();
        services.AddScoped<
            IQueryHandler<ExportCustomerReportQuery, ReportFile>,
            ExportCustomerReportHandler>();
        services.AddValidatorsFromAssemblyContaining<UpdateTenantSettingsValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateProductValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateCompanyValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateCustomerValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateQuotationValidator>();
        services.AddValidatorsFromAssemblyContaining<SalesReportFilterValidator>();
        services.AddAuditInfrastructure(configuration);
        services.AddTenancyInfrastructure(configuration);
        services.AddIdentityInfrastructure(configuration);
        services.AddNotificationsInfrastructure(configuration);
        services.AddStorageInfrastructure(configuration);
        services.AddCatalogInfrastructure(configuration);
        services.AddCompaniesInfrastructure(configuration);
        services.AddCustomersInfrastructure(configuration);
        services.AddGeographyInfrastructure(configuration);
        services.AddQuotationsInfrastructure(configuration);
        // Sin AddDbContext y sin inicializador de base: Reporting no tiene tablas propias. Lo
        // unico que registra es el armador de Excel.
        services.AddReportingInfrastructure(configuration);

        // CAT-05 — el único punto donde `catalog` y `storage` se tocan, y es acá a propósito:
        // ningún módulo referencia al otro, el composition root los cablea. Va después de los
        // dos AddXInfrastructure porque el adaptador depende de servicios que ellos registran.
        services.AddScoped<IProductImageLookup, ProductImageLookup>();
        services.AddScoped<IProductExportStorage, ProductExportStorage>();

        // Mismo patrón (CAT-05) entre `customers` y `geography`: ninguno de los dos referencia al
        // otro — CustomersLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules lo
        // impide a propósito — y el composition root cablea el puerto que declara `customers`
        // contra los repositorios que ya registró AddGeographyInfrastructure.
        services.AddScoped<ICustomerGeographyLookup, CustomerGeographyLookup>();

        // Y el mismo patrón entre `customers` y `storage`, para dejar el Excel exportado en el
        // bucket y firmar su enlace de descarga.
        services.AddScoped<ICustomerExportStorage, CustomerExportStorage>();

        // Mismo patrón (CAT-05) entre `companies` y `geography`: ninguno de los dos referencia al
        // otro, y el composition root cablea el puerto que declara `companies` contra los
        // repositorios que ya registró AddGeographyInfrastructure.
        services.AddScoped<ICompanyGeographyLookup, CompanyGeographyLookup>();

        // Mismo patron (CAT-05) entre `quotations` y `customers`/`catalog`: ninguno referencia al
        // otro directo — QuotationsLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules
        // lo impide a proposito — y el composition root cablea los puertos que declara
        // `quotations` contra los repositorios que ya registraron AddCustomersInfrastructure y
        // AddCatalogInfrastructure.
        services.AddScoped<IQuotationCustomerLookup, QuotationCustomerLookup>();
        services.AddScoped<IQuotationAdvisorLookup, QuotationAdvisorLookup>();
        services.AddScoped<IQuotationProductLookup, QuotationProductLookup>();
        // Arma la respuesta de una cotizacion con todo lo que su pantalla muestra, para que el
        // navegador no tenga que pedir cliente, miembros y catalogo por separado.
        services.AddScoped<IQuotationResponseComposer, QuotationResponseComposer>();
        services.AddScoped<IQuotationProductPricingLookup, QuotationProductPricingLookup>();
        services.AddScoped<IQuotationFileLookup, QuotationFileLookup>();

        // Reporting es el caso extremo del mismo patron (CAT-05): el modulo no tiene tablas
        // propias, asi que **todos** sus origenes de datos cruzan una frontera de modulo. Van
        // despues de AddQuotationsInfrastructure, AddCustomersInfrastructure,
        // AddCatalogInfrastructure, AddIdentityInfrastructure y AddTenancyInfrastructure, porque
        // los adaptadores dependen de los DbContext que esos registran — y despues del
        // ICustomerGeographyLookup de arriba, que el de clientes reusa.
        services.AddScoped<ReportingPeopleLookup>();
        services.AddScoped<ReportingClientLookup>();
        services.AddScoped<ISalesReportSource, SalesReportSource>();
        services.AddScoped<IQuotationsReportSource, QuotationsReportSource>();
        services.AddScoped<IPriceChangeReportSource, PriceChangeReportSource>();
        services.AddScoped<ICustomerReportSource, CustomerReportSource>();

        AddAuthorizationCapability(services, configuration);
        services.AddQepObservability(configuration, environment);
        AddAuthentication(services, configuration, environment);
        AddAuthorization(services);
        return services;
    }

    // Registra la capacidad Authorization y el catálogo de roles de sistema de tenancy.
    // Las definiciones de rol se versionan en código; las referencias de rol de la membresía
    // (ADR 0016) mapean a estos permisos. Los roles custom/de base y los globales van después.
    private static void AddAuthorizationCapability(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // El catalogo del codigo sigue siendo singleton: son constantes del build.
        services.AddSingleton<IRoleCatalog, RoleCatalog>();
        // La vista por tenant NO puede serlo: fusiona los roles que el tenant definio, y esos
        // cambian con un PATCH y no con un deploy. Scoped, ademas, es lo que hace que memoizar
        // por request sea correcto — el scope dura lo que el request.
        services.AddScoped<ITenantRoleCatalog, TenantRoleCatalog>();
        services.AddAuthorizationInfrastructure(configuration);
        services.AddScoped<
            Modules.Authorization.Application.IAuthorizationService,
            AuthorizationService>();
        services.AddScoped<IRolePermissionChecker, RolePermissionChecker>();
        services.AddScoped<IRoleReferenceValidator, RoleReferenceValidator>();
        // Las claves de rol van en inglés y sin prefijo de módulo: a diferencia de un permiso,
        // que es propiedad del módulo que protege el caso de uso (`catalog.product.read`), el
        // rol es transversal al tenant y no pertenece a ninguno. El label que ve la persona es
        // `DisplayName`, en español, y el cliente no traduce la clave: la muestra tal cual la
        // manda el catálogo.
        services.AddSingleton(new RoleDefinition(
            "admin",
            "Administrador",
            "Administra la configuración, membresías y capacidades base del tenant.",
            "Tenancy",
            "high",
            [
                TenancyPermissions.SettingsRead,
                TenancyPermissions.SettingsUpdate,
                TenancyPermissions.AdvisorshipInvite,
                TenancyPermissions.AdvisorshipRead,
                TenancyPermissions.AdvisorshipManage,
                TenancyPermissions.AdvisorshipRolesManage,
                StoragePermissions.FileUpload,
                StoragePermissions.FileRead,
                StoragePermissions.FileDelete,
                StoragePermissions.FilePublish,
                CatalogPermissions.ProductRead,
                CatalogPermissions.ProductManage,
                CatalogPermissions.TaxRateRead,
                CatalogPermissions.TaxRateManage,
                CompaniesPermissions.CompanyRead,
                CompaniesPermissions.CompanyManage,
                CustomersPermissions.CustomerRead,
                CustomersPermissions.CustomerManage,
                CustomersPermissions.CustomerImport,
                CustomersPermissions.ClassificationRead,
                CustomersPermissions.ClassificationManage,
                QuotationsPermissions.QuotationRead,
                QuotationsPermissions.QuotationManage,
                SalesPermissions.SaleRead,
                SalesPermissions.SaleManage,
                // Los cuatro reportes. Admin es el unico rol que ve los de cambios de precio y
                // padron de clientes: el primero expone el historial comercial completo del
                // catalogo, y el segundo el padron entero con datos de identificacion.
                ReportingPermissions.SalesRead,
                ReportingPermissions.QuotationRead,
                ReportingPermissions.PriceChangeRead,
                ReportingPermissions.CustomerRead
            ]));
        services.AddSingleton(new RoleDefinition(
            "advisor",
            "Asesor",
            "Gestiona clientes y consulta los datos maestros con los que cotiza.",
            "Tenancy",
            "medium",
            [
                TenancyPermissions.SettingsRead,
                TenancyPermissions.AdvisorshipRead,
                CatalogPermissions.ProductRead,
                // Sólo lectura: cambiar una tasa mueve los totales de toda cotización, así que
                // TaxRateManage es high y queda en admin. Ratificado en el gate CAT-00.
                CatalogPermissions.TaxRateRead,
                // Solo lectura, mismo criterio que producto: una asesora cotiza contra las
                // empresas que ya existen; darlas de alta y desactivarlas es de admin.
                CompaniesPermissions.CompanyRead,
                // Lectura y gestion: dar de alta y editar clientes es el trabajo diario de una
                // asesora, a diferencia de empresas y productos, que son datos maestros que
                // configura el admin. Importar queda afuera — mil clientes de una vez no es la
                // misma autoridad, y el gate CLI-00 pide mapearlo por separado.
                CustomersPermissions.CustomerRead,
                CustomersPermissions.CustomerManage,
                // Mismo criterio que CustomerRead/CustomerManage y no el de CustomerImport:
                // gestionar clasificaciones es trabajo diario de un asesor, no una operacion
                // privilegiada.
                CustomersPermissions.ClassificationRead,
                CustomersPermissions.ClassificationManage,
                // Lectura y gestion: cotizar (crear, agregar lineas, editar el borrador, enviar,
                // anular) es el trabajo diario de la asesora, a diferencia de producto/empresa,
                // que son catalogos maestros que administra otro rol.
                QuotationsPermissions.QuotationRead,
                QuotationsPermissions.QuotationManage,
                // US-12/US-14: enviar una cotizacion sube su PDF a Storage, y convertir en venta
                // sube los comprobantes de pago -- las dos acciones de la asesora que tocan
                // archivos.
                StoragePermissions.FileUpload,
                StoragePermissions.FileRead,
                // Convertir una cotizacion aprobada en venta (US-13 a US-16) es la continuacion
                // natural de cotizar, no una operacion separada que administre otro rol.
                SalesPermissions.SaleRead,
                SalesPermissions.SaleManage,
                // Solo los dos reportes de su trabajo diario. Cambios de precio y padron de
                // clientes quedan en admin: son la vista agregada del negocio, no la operacion.
                ReportingPermissions.SalesRead,
                ReportingPermissions.QuotationRead
            ]));
        services.AddSingleton(new RoleDefinition(
            "billing",
            "Facturación",
            "Consulta la información de clientes necesaria para facturar.",
            "Tenancy",
            "medium",
            [
                TenancyPermissions.SettingsRead,
                CustomersPermissions.CustomerRead,
                // Los tres tercios del alcance de negocio pedido para este rol ("ver clientes,
                // cotizaciones y ventas") ya existen. Sólo lectura en los tres: facturar necesita
                // ver el estado del pago y los comprobantes, no aprobar conversiones ni editar
                // cotizaciones -- eso sigue siendo trabajo de la asesora.
                QuotationsPermissions.QuotationRead,
                SalesPermissions.SaleRead
            ]));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.SettingsRead,
            "Leer configuración",
            "Permite consultar la configuración del tenant.",
            "Tenancy",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.SettingsUpdate,
            "Actualizar configuración",
            "Permite modificar la configuración del tenant.",
            "Tenancy",
            "high"));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.AdvisorshipInvite,
            "Invitar miembros",
            "Permite invitar usuarios al tenant.",
            "Tenancy",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.AdvisorshipRead,
            "Leer miembros",
            "Permite consultar membresías y catálogo de roles/permisos.",
            "Tenancy",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.AdvisorshipManage,
            "Gestionar miembros",
            "Permite suspender, remover y cambiar los roles que tiene un miembro.",
            "Tenancy",
            "high"));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.AdvisorshipRolesManage,
            "Definir roles",
            "Permite crear roles propios y elegir que permisos concede cada uno.",
            "Tenancy",
            "high"));
        services.AddSingleton(new PermissionDefinition(
            StoragePermissions.FileUpload,
            "Subir archivos",
            "Permite crear sesiones de carga y completar uploads.",
            "Storage",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            StoragePermissions.FileRead,
            "Leer archivos",
            "Permite solicitar URLs de descarga.",
            "Storage",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            StoragePermissions.FileDelete,
            "Eliminar archivos",
            "Permite marcar archivos como eliminados.",
            "Storage",
            "high"));
        services.AddSingleton(new PermissionDefinition(
            StoragePermissions.FilePublish,
            "Publicar imágenes",
            "Permite publicar y despublicar imágenes y sus variantes.",
            "Storage",
            "high"));
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.ProductRead,
            "Leer productos",
            "Permite consultar el catálogo de productos del tenant.",
            "Catalog",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.ProductManage,
            "Gestionar productos",
            "Permite crear, editar e inactivar productos.",
            "Catalog",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.TaxRateRead,
            "Leer tasas de impuesto",
            "Permite consultar las tasas de impuesto del tenant.",
            "Catalog",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.TaxRateManage,
            "Gestionar tasas de impuesto",
            "Permite crear, editar e inactivar tasas de impuesto.",
            "Catalog",
            "high"));
        services.AddSingleton(new PermissionDefinition(
            CompaniesPermissions.CompanyRead,
            "Leer empresas",
            "Permite consultar las empresas del tenant.",
            "Companies",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            CompaniesPermissions.CompanyManage,
            "Gestionar empresas",
            "Permite crear, editar, inactivar y reactivar empresas.",
            "Companies",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.CustomerRead,
            "Leer clientes",
            "Permite consultar el listado y el detalle de los clientes del tenant.",
            "Customers",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.CustomerManage,
            "Gestionar clientes",
            "Permite crear, editar, inactivar y reactivar clientes.",
            "Customers",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.CustomerImport,
            "Importar clientes",
            "Permite cargar clientes masivamente desde un archivo Excel.",
            "Customers",
            // Alto y no medio: una carga masiva escribe cientos de registros de datos personales
            // de una sola vez, y el gate CLI-00 todavia tiene abierta la politica de retencion de
            // PII. Separado de manage justamente para poder darlo a menos gente.
            "high"));
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.ClassificationRead,
            "Leer clasificaciones de clientes",
            "Permite consultar el catalogo de clasificaciones de clientes del tenant.",
            "Customers",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.ClassificationManage,
            "Gestionar clasificaciones de clientes",
            "Permite crear, editar, inactivar, reactivar y eliminar clasificaciones de clientes.",
            "Customers",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            QuotationsPermissions.QuotationRead,
            "Leer cotizaciones",
            "Permite consultar el listado y el detalle de las cotizaciones del tenant.",
            "Quotations",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            QuotationsPermissions.QuotationManage,
            "Gestionar cotizaciones",
            "Permite crear y editar cotizaciones en borrador, incluidas sus lineas de producto.",
            "Quotations",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            SalesPermissions.SaleRead,
            "Leer ventas",
            "Permite consultar la venta convertida de una cotizacion.",
            "Quotations",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            SalesPermissions.SaleManage,
            "Gestionar ventas",
            "Permite convertir una cotizacion enviada en venta, con sus comprobantes de pago.",
            "Quotations",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.SalesRead,
            "Reporte de ventas",
            "Permite consultar y exportar el reporte de ventas convertidas del tenant.",
            "Reporting",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.QuotationRead,
            "Reporte de cotizaciones",
            "Permite consultar y exportar el reporte de cotizaciones del tenant.",
            "Reporting",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.PriceChangeRead,
            "Reporte de cambios de precio",
            "Permite consultar y exportar el historico de cambios de precio del catalogo.",
            "Reporting",
            // Medio y no bajo: el historico completo de precios de un catalogo es el margen del
            // negocio visto de costado, y por eso queda solo en admin.
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.CustomerRead,
            "Reporte de clientes",
            "Permite consultar y exportar el padron de clientes (Clientes CUC) del tenant.",
            "Reporting",
            "low"));
    }

    private static void AddAuthentication(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var useDevelopmentStub = QepAuthenticationMode.UseDevelopmentStub(configuration, environment);

        // Fail-closed duro (ADR 0001, ítem requerido #2): el stub confía en los headers que
        // manda el llamador como identidad y permisos auto-declarados, así que tiene que ser
        // imposible habilitarlo fuera de Development, incluso con un override explícito de
        // configuración. Negarse a arrancar es mejor que caer en silencio al proveedor real,
        // que taparía la mala configuración en vez de exponerla.
        if (useDevelopmentStub && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Authentication:UseDevelopmentStub cannot be enabled outside the " +
                $"Development environment (current environment: '{environment.EnvironmentName}'). " +
                "See docs/decisions/0001-development-auth-stub.md.");
        }

        if (useDevelopmentStub)
        {
            services
                .AddAuthentication(DevelopmentAuthenticationHandler.AuthenticationSchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
                    DevelopmentAuthenticationHandler.AuthenticationSchemeName,
                    _ => { });
            return;
        }

        // Validación como resource server del JWT del proveedor externo (ADR 0014/0015,
        // línea base de implementación). Google es el primer proveedor; su client id es
        // la audiencia. Una configuración Authority/Audience explícita y no vacía pisa los
        // valores por defecto de Google.
        var authority = Coalesce(
            configuration["Authentication:Authority"],
            "https://accounts.google.com");
        var audience = Coalesce(
            configuration["Authentication:Audience"],
            configuration["Authentication:Google:ClientId"])
            ?? throw new InvalidOperationException(
                "Authentication:Audience or Authentication:Google:ClientId is required outside Development.");

        // El esquema por defecto es la cookie de sesión — es contra lo que autentica todo
        // endpoint salvo POST /auth/session. "GoogleBearer" está registrado pero
        // deliberadamente NO es el default y NO es alcanzable por ningún fallback que
        // olfatee headers: sólo lo pide explícitamente la política de autorización propia
        // de /auth/session (ver AuthSessionEndpoints). Un id token de Google todavía válido
        // nunca debe servir para autenticar otro endpoint, o eso saltearía la revocación de
        // sesión (suspender un tenant / quitar un miembro revoca la fila de sesión, no el
        // token de Google subyacente, que vive ~1h).
        services
            .AddAuthentication(SessionCookieAuthenticationHandler.AuthenticationSchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionCookieAuthenticationHandler>(
                SessionCookieAuthenticationHandler.AuthenticationSchemeName,
                _ => { })
            .AddJwtBearer("GoogleBearer", options =>
            {
                options.Audience = audience;
                // Mantener intactos los nombres de claim del proveedor (sub, email, email_verified)
                // para que el endpoint /auth/session pueda leerlos directo.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ValidIssuer = authority,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true
                };

                // Escotilla sólo para pruebas (nunca se setea fuera de las de integración): una
                // clave de firma simétrica deja que las pruebas se auto-emitan un JWT con forma
                // de Google sin pegarle al endpoint de discovery OIDC real de Google. Los
                // despliegues reales nunca setean Authentication:TestSigningKey, así que Authority
                // queda cableada al documento de discovery de Google como antes.
                var testSigningKey = configuration["Authentication:TestSigningKey"];
                if (string.IsNullOrEmpty(testSigningKey))
                {
                    options.Authority = authority;
                }
                else
                {
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Convert.FromBase64String(testSigningKey));
                }
            });
    }

    // Devuelve el primer valor no vacío, tratando como ausente la configuración que sólo
    // tiene espacios (por ejemplo el placeholder "" de appsettings.json).
    private static string? Coalesce(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void AddAuthorization(IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(
                TenancyPermissions.SettingsRead,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.SettingsRead))
            .AddPolicy(
                TenancyPermissions.SettingsUpdate,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.SettingsUpdate))
            .AddPolicy(
                TenancyPermissions.AdvisorshipInvite,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.AdvisorshipInvite))
            .AddPolicy(
                TenancyPermissions.AdvisorshipRead,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.AdvisorshipRead))
            .AddPolicy(
                TenancyPermissions.AdvisorshipManage,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.AdvisorshipManage))
            .AddPolicy(
                TenancyPermissions.AdvisorshipRolesManage,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.AdvisorshipRolesManage))
            .AddPolicy(
                StoragePermissions.FileUpload,
                policy => AddPermissionRequirement(policy, StoragePermissions.FileUpload))
            .AddPolicy(
                StoragePermissions.FileRead,
                policy => AddPermissionRequirement(policy, StoragePermissions.FileRead))
            .AddPolicy(
                StoragePermissions.FileDelete,
                policy => AddPermissionRequirement(policy, StoragePermissions.FileDelete))
            .AddPolicy(
                StoragePermissions.FilePublish,
                policy => AddPermissionRequirement(policy, StoragePermissions.FilePublish))
            .AddPolicy(
                CatalogPermissions.ProductRead,
                policy => AddPermissionRequirement(policy, CatalogPermissions.ProductRead))
            .AddPolicy(
                CatalogPermissions.ProductManage,
                policy => AddPermissionRequirement(policy, CatalogPermissions.ProductManage))
            .AddPolicy(
                CompaniesPermissions.CompanyRead,
                policy => AddPermissionRequirement(policy, CompaniesPermissions.CompanyRead))
            .AddPolicy(
                CompaniesPermissions.CompanyManage,
                policy => AddPermissionRequirement(policy, CompaniesPermissions.CompanyManage))
            // La otra mitad del permiso. Sin esta política RequireAuthorization no resuelve y el
            // síntoma es 500, no 403 — un error que no se parece en nada a su causa. Por eso
            // CA-CAT-03-10 verifica que la política resuelva, y no sólo que el permiso figure en
            // el catálogo.
            .AddPolicy(
                CatalogPermissions.TaxRateRead,
                policy => AddPermissionRequirement(policy, CatalogPermissions.TaxRateRead))
            .AddPolicy(
                CatalogPermissions.TaxRateManage,
                policy => AddPermissionRequirement(policy, CatalogPermissions.TaxRateManage))
            .AddPolicy(
                CustomersPermissions.CustomerRead,
                policy => AddPermissionRequirement(policy, CustomersPermissions.CustomerRead))
            .AddPolicy(
                CustomersPermissions.CustomerManage,
                policy => AddPermissionRequirement(policy, CustomersPermissions.CustomerManage))
            .AddPolicy(
                CustomersPermissions.CustomerImport,
                policy => AddPermissionRequirement(policy, CustomersPermissions.CustomerImport))
            // La otra mitad del permiso de clasificaciones. Sin esta politica RequireAuthorization
            // no resuelve y el sintoma es 500, no 403 — mismo gotcha que TaxRateRead/TaxRateManage.
            .AddPolicy(
                CustomersPermissions.ClassificationRead,
                policy => AddPermissionRequirement(policy, CustomersPermissions.ClassificationRead))
            .AddPolicy(
                CustomersPermissions.ClassificationManage,
                policy => AddPermissionRequirement(
                    policy, CustomersPermissions.ClassificationManage))
            // La otra mitad del permiso, para los dos de Quotations. Sin esta politica
            // RequireAuthorization no resuelve y el sintoma es 500, no 403 — mismo gotcha que
            // TaxRateRead/TaxRateManage y ClassificationRead/ClassificationManage.
            .AddPolicy(
                QuotationsPermissions.QuotationRead,
                policy => AddPermissionRequirement(policy, QuotationsPermissions.QuotationRead))
            .AddPolicy(
                QuotationsPermissions.QuotationManage,
                policy => AddPermissionRequirement(policy, QuotationsPermissions.QuotationManage))
            // La otra mitad del permiso, para los dos de Sales -- mismo gotcha.
            .AddPolicy(
                SalesPermissions.SaleRead,
                policy => AddPermissionRequirement(policy, SalesPermissions.SaleRead))
            .AddPolicy(
                SalesPermissions.SaleManage,
                policy => AddPermissionRequirement(policy, SalesPermissions.SaleManage))
            // La otra mitad del permiso, para los cuatro de Reporting. Sin esta politica
            // RequireAuthorization no resuelve y el sintoma es 500, no 403 -- mismo gotcha que
            // TaxRateRead/TaxRateManage, ClassificationRead/ClassificationManage y los de
            // Quotations/Sales.
            .AddPolicy(
                ReportingPermissions.SalesRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.SalesRead))
            .AddPolicy(
                ReportingPermissions.QuotationRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.QuotationRead))
            .AddPolicy(
                ReportingPermissions.PriceChangeRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.PriceChangeRead))
            .AddPolicy(
                ReportingPermissions.CustomerRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.CustomerRead));
    }

    private static void AddPermissionRequirement(
        AuthorizationPolicyBuilder policy,
        string permission)
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(QepClaimTypes.Permission, permission);
    }
}
