using Bootstrapper.ApiKeys;
using Bootstrapper.Authentication;
using Bootstrapper.Copilot;
using Bootstrapper.Deployment;
using Bootstrapper.Messaging;
using Bootstrapper.Realtime;
using Bootstrapper.Seeding;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.LegalParameters;
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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Infrastructure;
using Modules.Audit.Infrastructure;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;
using Modules.Authorization.Infrastructure;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Infrastructure;
using Modules.Automations.Infrastructure.Http;
using Modules.Buyers.Infrastructure;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure;
using Modules.Companies.Application;
using Modules.Companies.Infrastructure;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure;
using Modules.Geography.Application;
using Modules.Geography.Infrastructure;
using Modules.Hr.Application;
using Modules.Hr.Infrastructure;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.ExternalApi;
using Modules.Loans.Infrastructure;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure;
using Modules.Notifications.Infrastructure;
using Modules.Payroll.Application;
using Modules.Payroll.Infrastructure;
using Modules.Pipeline.Application;
using Modules.Pipeline.Infrastructure;
using Modules.Platform.Application;
using Modules.Platform.Infrastructure;
using Modules.Pos.Application;
using Modules.Pos.Infrastructure;
using Modules.Purchases.Application;
using Modules.Purchases.Infrastructure;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure;
using Modules.Reporting.Application;
using Modules.Reporting.Infrastructure;
using Modules.Scheduling.Application;
using Modules.Scheduling.Infrastructure;
using Modules.ServiceContracts.Infrastructure;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure;
using Modules.Siigo.Application;
using Modules.Siigo.Application.CallFailures;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Application.Products;
using Modules.Siigo.Application.Settings;
using Modules.Siigo.Infrastructure;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;
using Modules.Store.Infrastructure;
using Modules.Suppliers.Application;
using Modules.Suppliers.Infrastructure;
using Modules.Support.Infrastructure;
using Modules.Tenancy.Application;
using Modules.Tenancy.Infrastructure;
using Modules.Wholesale.Application;
using Modules.Wholesale.Infrastructure;
using Modules.Wms.Infrastructure;
using LoansPermissions = Modules.Loans.Application.LoansPermissions;
using WmsPermissions = Modules.Wms.Application.WmsPermissions;
using ModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;
using ServiceContractsPermissions = Modules.ServiceContracts.Application.ServiceContractsPermissions;
using StorePermissions = Modules.Store.Application.StorePermissions;
using SupportPermissions = Modules.Support.Application.SupportPermissions;
using SupportPolicies = Modules.Support.Application.SupportPolicies;

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
        // Plan fase 2, C2.4: el de los claims del request, salvo en un scope del ejecutor del copiloto, que trae el del miembro.
        services.AddQepExecutionContext();
        // API externa (spec 2026-10-30 §4.3): los handlers externos leen a quien llama de este puerto, no de IExecutionContext.
        services.AddScoped<IApiCaller, HttpApiCaller>();
        services.AddScoped<IExternalCallNotes, HttpExternalCallNotes>();
        // Límites de la API externa (spec 2026-10-30 §6.1): todo tiene valor por defecto y no hay
        // configuración nueva en Backend-prod, pero un 0 o un negativo tumba el arranque (ApiAccessOptionsValidator). El limitador por clave es singleton: el estado de las ventanas vive en él.
        services.AddOptions<ApiAccessOptions>()
            .Bind(configuration.GetSection(ApiAccessOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ApiAccessOptions>, ApiAccessOptionsValidator>();
        // Sus particiones salen de SharedRateLimitPartitions, que registra AddQepValkey: en memoria o en Valkey según
        // Valkey:Areas:RateLimiting (spec 2026-11-13, D-3).
        services.AddSingleton<ApiKeyRateLimiter>();
        // Valkey (spec 2026-11-13, D-1): cliente, opciones, métricas, gateway, presencia y fábrica de limitadores.
        services.AddQepValkey(configuration);
        services.AddScoped<IRequestDispatcher, RequestDispatcher>();
        services.AddScoped<
            IQueryHandler<GetTenantSettingsQuery, TenantSettingsDto>,
            GetTenantSettingsHandler>();
        services.AddScoped<
            ICommandHandler<UpdateTenantSettingsCommand, TenantSettingsDto>,
            UpdateTenantSettingsHandler>();
        services.AddScoped<
            IQueryHandler<GetPriceRoundingQuery, PriceRoundingSettingsDto>,
            GetPriceRoundingHandler>();
        services.AddScoped<
            ICommandHandler<UpdatePriceRoundingCommand, PriceRoundingSettingsDto>,
            UpdatePriceRoundingHandler>();
        services.AddScoped<
            ICommandHandler<SetTenantLogoCommand, TenantSettingsDto>,
            SetTenantLogoHandler>();
        services.AddScoped<
            ICommandHandler<RemoveTenantLogoCommand, TenantSettingsDto>,
            RemoveTenantLogoHandler>();
        // Spec 2026-10-08 §5: la consola de operador.
        services.AddScoped<
            IQueryHandler<ListOperatorTenantsQuery, OperatorTenantPageDto>,
            ListOperatorTenantsHandler>();
        services.AddScoped<
            IQueryHandler<GetOperatorTenantQuery, OperatorTenantDetailDto>,
            GetOperatorTenantHandler>();
        services.AddScoped<
            IQueryHandler<GetPlatformUsageQuery, PlatformUsageDto>,
            GetPlatformUsageHandler>();
        services.AddScoped<IPlatformUsageSource, PlatformUsageSource>();
        services.AddScoped<
            ICommandHandler<ChangeTenantModulesCommand, OperatorTenantDetailDto>,
            ChangeTenantModulesHandler>();
        services.AddScoped<
            ICommandHandler<ChangeTenantProviderCommand, OperatorTenantDetailDto>,
            ChangeTenantProviderHandler>();
        services.AddScoped<
            ICommandHandler<ChangeTenantStatusCommand, OperatorTenantDetailDto>,
            ChangeTenantStatusHandler>();
        services.AddScoped<
            IQueryHandler<ListTenantHistoryQuery, OperatorHistoryPageDto>,
            ListTenantHistoryHandler>();
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
            ICommandHandler<UpdateMemberProfileCommand, MembershipListItemDto>,
            UpdateMemberProfileHandler>();
        // Notice phone (spec 2026-11-22 section 5.5): the caller's own membership. The validator enters with
        // AddValidatorsFromAssemblyContaining.
        services.AddScoped<
            IQueryHandler<GetMyMembershipQuery, MyMembershipDto>,
            GetMyMembershipHandler>();
        services.AddScoped<
            ICommandHandler<UpdateMyPhoneCommand, MyMembershipDto>,
            UpdateMyPhoneHandler>();
        services.AddScoped<
            ICommandHandler<CreateUploadSessionCommand, UploadSessionDto>,
            CreateUploadSessionHandler>();
        services.AddScoped<
            ICommandHandler<CompleteUploadCommand, FileResourceDto>,
            CompleteUploadHandler>();
        // Soporte fase 3 (plan F31): la subida sin miembro. No es un handler ni tiene endpoint en Storage: la usa sólo el adaptador de
        // Soporte en Bootstrapper (SupportAttachmentStorage).
        services.AddScoped<IStorageSystemUploads, StorageSystemUploads>();
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
            IQueryHandler<GetCurrenciesInUseQuery, IReadOnlyList<string>>,
            GetCurrenciesInUseHandler>();
        services.AddScoped<
            ICommandHandler<CreateProductCommand, ProductDto>,
            CreateProductHandler>();
        services.AddScoped<
            ICommandHandler<UpdateProductCommand, ProductDto>,
            UpdateProductHandler>();
        services.AddScoped<
            ICommandHandler<DeleteProductCommand, ProductDeletedResult>,
            DeleteProductHandler>();
        services.AddScoped<
            ICommandHandler<CopyPriceScalesCommand, IReadOnlyList<ProductDto>>,
            CopyPriceScalesHandler>();
        services.AddScoped<
            ICommandHandler<SetProductsStoreVisibilityCommand, ProductStoreVisibilityResult>,
            SetProductsStoreVisibilityHandler>();
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
            IQueryHandler<GetTaxRateOptionsQuery, TaxRateOptionsDto>,
            GetTaxRateOptionsHandler>();
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
        // La consola de la unidad tributaria del operador: también a mano, y sin el registro el síntoma es 500.
        services.AddScoped<IQueryHandler<ListTaxUnitsQuery, TaxUnitsDto>, ListTaxUnitsHandler>();
        services.AddScoped<ICommandHandler<SetTaxUnitValueCommand, TaxUnitValueDto>, SetTaxUnitValueHandler>();
        // La biblioteca de opciones de Catalog. Mismo registro a mano que las tasas.
        services.AddScoped<IQueryHandler<ListOptionsQuery, OptionsResponse>, ListOptionsHandler>();
        services.AddScoped<ICommandHandler<CreateOptionCommand, OptionResponse>, CreateOptionHandler>();
        services.AddScoped<ICommandHandler<UpdateOptionCommand, OptionResponse>, UpdateOptionHandler>();
        services.AddScoped<ICommandHandler<DeleteOptionCommand, OptionDeletedResult>, DeleteOptionHandler>();
        services.AddScoped<IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategoryDto>>, ListCategoriesHandler>();
        services.AddScoped<ICommandHandler<CreateCategoryCommand, CategoryDto>, CreateCategoryHandler>();
        services.AddScoped<ICommandHandler<UpdateCategoryCommand, CategoryDto>, UpdateCategoryHandler>();
        services.AddScoped<ICommandHandler<DeleteCategoryCommand, CategoryDeletedResult>, DeleteCategoryHandler>();
        services.AddScoped<
            ICommandHandler<ReplacePosQuickAccessCommand, IReadOnlyList<CategoryDto>>,
            ReplacePosQuickAccessHandler>();
        // Spec 2026-11-22 (listas de precios por canal). Los validadores entran con AddValidatorsFromAssemblyContaining del módulo.
        services.AddScoped<IQueryHandler<ListPriceListsQuery, PriceListsResponse>, ListPriceListsHandler>();
        services.AddScoped<IQueryHandler<GetPriceListQuery, PriceListDetailDto>, GetPriceListHandler>();
        services.AddScoped<ICommandHandler<CreatePriceListCommand, PriceListDetailDto>, CreatePriceListHandler>();
        services.AddScoped<ICommandHandler<UpdatePriceListCommand, PriceListDetailDto>, UpdatePriceListHandler>();
        services.AddScoped<ICommandHandler<DeletePriceListCommand, bool>, DeletePriceListHandler>();
        services.AddScoped<
            IQueryHandler<ListPriceListOverridesQuery, PriceListOverridesPageDto>, ListPriceListOverridesHandler>();
        services.AddScoped<
            ICommandHandler<UpdatePriceListOverridesCommand, PriceListOverridesWriteDto>, UpdatePriceListOverridesHandler>();
        services.AddScoped<IQueryHandler<GetChannelPricesQuery, ChannelPricesDto>, GetChannelPricesHandler>();
        services.AddScoped<ICommandHandler<UpdateChannelPricesCommand, ChannelPricesDto>, UpdateChannelPricesHandler>();
        services.AddScoped<IPriceListUsage, CatalogPriceListUsage>();
        services.AddScoped<IPriceListPricing, PriceListPricing>();
        services.AddScoped<Modules.Wholesale.Application.IWholesalePriceLists, WholesalePriceLists>();
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
        // Suppliers, spec 2026-10-23. Los ocho a mano, como los demás: un handler olvidado compila y
        // responde 500 en runtime.
        services.AddScoped<
            IQueryHandler<ListSuppliersQuery, SupplierPage>,
            ListSuppliersHandler>();
        services.AddScoped<
            IQueryHandler<ListSupplierCategoriesQuery, IReadOnlyList<string>>,
            ListSupplierCategoriesHandler>();
        services.AddScoped<
            IQueryHandler<GetSupplierQuery, SupplierResponse>,
            GetSupplierHandler>();
        services.AddScoped<
            ICommandHandler<CreateSupplierCommand, SupplierResponse>,
            CreateSupplierHandler>();
        services.AddScoped<
            ICommandHandler<UpdateSupplierCommand, SupplierResponse>,
            UpdateSupplierHandler>();
        services.AddScoped<
            ICommandHandler<ActivateSupplierCommand, SupplierResponse>,
            ActivateSupplierHandler>();
        services.AddScoped<
            ICommandHandler<DeactivateSupplierCommand, SupplierResponse>,
            DeactivateSupplierHandler>();
        services.AddScoped<
            ICommandHandler<ExportSuppliersCommand, ExportSuppliersResult>,
            ExportSuppliersHandler>();
        // ApiAccess, spec 2026-10-30: administración de claves de API. A mano, como los demás: un handler olvidado
        // compila y responde 500 en runtime.
        services.AddScoped<
            IQueryHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyDto>>,
            ListApiKeysHandler>();
        services.AddScoped<
            IQueryHandler<ListApiKeyRequestsQuery, ApiKeyRequestPage>,
            ListApiKeyRequestsHandler>();
        services.AddScoped<
            IQueryHandler<ListApiScopesQuery, ApiScopeCatalogDto>,
            ListApiScopesHandler>();
        services.AddScoped<
            ICommandHandler<CreateApiKeyCommand, CreatedApiKeyDto>,
            CreateApiKeyHandler>();
        services.AddScoped<
            ICommandHandler<RevokeApiKeyCommand, ApiKeyDto>,
            RevokeApiKeyHandler>();
        // ApiAccess no ve a los módulos: cada uno registra aquí los scopes que ofrece, y el catálogo los filtra por
        // los módulos que el tenant tiene prendidos. Las conexiones entran por un puerto con adaptador en Bootstrapper.
        services.AddScoped<ApiScopeCatalog>();
        services.AddScoped<IApiKeyConnectionDirectory, ApiKeyConnectionDirectory>();
        services.AddScoped<IApiMessageStates, ApiMessageStates>();
        services.AddScoped<IApiKeyVerifier, ApiKeyVerifier>();
        services.AddSingleton(new ApiScopeDefinition(
            MessagingScopes.TemplatesSend,
            "Enviar plantillas",
            "Incluye consultar las plantillas aprobadas.",
            "WhatsApp",
            ModuleKeys.Messaging.Value,
            RestrictableByConnection: true));
        // ServiceContracts, spec cobros-recurrentes 2026-10-29. A mano y con ruta completa: importar el namespace
        // choca con Messaging (AssigneesDto, entre otros). Un handler olvidado compila y responde 500.
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.GetCustomerProfileQuery, Modules.ServiceContracts.Application.ProfileDto>,
            Modules.ServiceContracts.Application.GetCustomerProfileHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.UpsertCustomerProfileCommand, Modules.ServiceContracts.Application.ProfileDto>,
            Modules.ServiceContracts.Application.UpsertCustomerProfileHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.ListAssigneesQuery, Modules.ServiceContracts.Application.AssigneesDto>,
            Modules.ServiceContracts.Application.ListAssigneesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.ListServicesQuery, Modules.ServiceContracts.Application.ServicePageDto>,
            Modules.ServiceContracts.Application.ListServicesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.GetServiceQuery, Modules.ServiceContracts.Application.ServiceDetailDto>,
            Modules.ServiceContracts.Application.GetServiceHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.PreviewServiceChargesQuery, Modules.ServiceContracts.Application.ChargesPreviewDto>,
            Modules.ServiceContracts.Application.PreviewServiceChargesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.CreateServiceCommand, Modules.ServiceContracts.Application.ServiceDetailDto>,
            Modules.ServiceContracts.Application.CreateServiceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.UpdateServiceCommand, Modules.ServiceContracts.Application.ServiceDetailDto>,
            Modules.ServiceContracts.Application.UpdateServiceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.ChangeServiceStatusCommand, Modules.ServiceContracts.Application.ServiceDetailDto>,
            Modules.ServiceContracts.Application.ChangeServiceStatusHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.DeleteServiceCommand, bool>,
            Modules.ServiceContracts.Application.DeleteServiceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.UpdatePriceReviewCommand, Modules.ServiceContracts.Application.ServiceDetailDto>,
            Modules.ServiceContracts.Application.UpdatePriceReviewHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.SchedulePriceChangeCommand, Modules.ServiceContracts.Application.ServiceDetailDto>,
            Modules.ServiceContracts.Application.SchedulePriceChangeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.CancelPriceScheduleCommand, Modules.ServiceContracts.Application.ServiceDetailDto>,
            Modules.ServiceContracts.Application.CancelPriceScheduleHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.KeepPriceCommand, Modules.ServiceContracts.Application.ServiceDetailDto>,
            Modules.ServiceContracts.Application.KeepPriceHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.ListPriceReviewsQuery, Modules.ServiceContracts.Application.PriceReviewPageDto>,
            Modules.ServiceContracts.Application.ListPriceReviewsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.ConfirmIpcReviewsCommand, Modules.ServiceContracts.Application.ConfirmIpcResponseDto>,
            Modules.ServiceContracts.Application.ConfirmIpcReviewsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.ListIpcRatesQuery, Modules.ServiceContracts.Application.IpcRateListDto>,
            Modules.ServiceContracts.Application.ListIpcRatesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.SaveIpcRateCommand, Modules.ServiceContracts.Application.IpcRateDto>,
            Modules.ServiceContracts.Application.SaveIpcRateHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.ListChargesQuery, Modules.ServiceContracts.Application.ChargePageDto>,
            Modules.ServiceContracts.Application.ListChargesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.GetChargeQuery, Modules.ServiceContracts.Application.ChargeDto>,
            Modules.ServiceContracts.Application.GetChargeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.PayChargeCommand, Modules.ServiceContracts.Application.ChargeDto>,
            Modules.ServiceContracts.Application.PayChargeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.UpdateChargePaymentCommand, Modules.ServiceContracts.Application.ChargeDto>,
            Modules.ServiceContracts.Application.UpdateChargePaymentHandler>();
        // Los armadores de DTO que comparten los handlers de servicios y (Tarea 8) los de cobros.
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.GetServiceContractsSettingsQuery, Modules.ServiceContracts.Application.ServiceContractsSettingsDto>,
            Modules.ServiceContracts.Application.GetServiceContractsSettingsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.UpdateServiceContractsSettingsCommand, Modules.ServiceContracts.Application.ServiceContractsSettingsDto>,
            Modules.ServiceContracts.Application.UpdateServiceContractsSettingsHandler>();
        services.AddScoped<Modules.ServiceContracts.Application.ChargeDtoComposer>();
        services.AddScoped<Modules.ServiceContracts.Application.ServiceDetailComposer>();
        services.AddScoped<Modules.ServiceContracts.Application.PriceReviewReader>();
        // Importación desde Excel (§3.2b): la vista previa y la importación comparten el analizador.
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.PreviewServiceContractsImportCommand, Modules.ServiceContracts.Application.ImportPreviewDto>,
            Modules.ServiceContracts.Application.PreviewServiceContractsImportHandler>();
        services.AddScoped<
            ICommandHandler<Modules.ServiceContracts.Application.ImportServiceContractsCommand, Modules.ServiceContracts.Application.ImportResultDto>,
            Modules.ServiceContracts.Application.ImportServiceContractsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.ServiceContracts.Application.GetServiceContractsImportTemplateQuery, Modules.ServiceContracts.Application.ServiceContractsImportTemplateFile>,
            Modules.ServiceContracts.Application.GetServiceContractsImportTemplateHandler>();
        services.AddScoped<Modules.ServiceContracts.Application.ServiceContractsImportAnalyzer>();

        // Loans, spec de préstamos 2026-10-31. A mano y con ruta completa, como ServiceContracts: un handler olvidado responde 500.
        services.AddScoped<
            IQueryHandler<Modules.Loans.Application.ListLoanAssigneesQuery, Modules.Loans.Application.LoanAssigneesDto>,
            Modules.Loans.Application.ListLoanAssigneesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Loans.Application.ListLoansQuery, Modules.Loans.Application.LoanPageDto>,
            Modules.Loans.Application.ListLoansHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Loans.Application.GetLoanQuery, Modules.Loans.Application.LoanDetailDto>,
            Modules.Loans.Application.GetLoanHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.CreateLoanCommand, Modules.Loans.Application.LoanDetailDto>,
            Modules.Loans.Application.CreateLoanHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.UpdateLoanCommand, Modules.Loans.Application.LoanDetailDto>,
            Modules.Loans.Application.UpdateLoanHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.DeleteLoanCommand, bool>,
            Modules.Loans.Application.DeleteLoanHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Loans.Application.ListLoanInstallmentsQuery, Modules.Loans.Application.LoanInstallmentPageDto>,
            Modules.Loans.Application.ListLoanInstallmentsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.PayInstallmentCommand, Modules.Loans.Application.LoanDetailDto>,
            Modules.Loans.Application.PayInstallmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.UpdateInstallmentPaymentCommand, Modules.Loans.Application.LoanDetailDto>,
            Modules.Loans.Application.UpdateInstallmentPaymentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.RemoveInstallmentPaymentCommand, Modules.Loans.Application.LoanDetailDto>,
            Modules.Loans.Application.RemoveInstallmentPaymentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.PreviewCapitalPaymentCommand, Modules.Loans.Application.LoanCapitalPaymentPreviewDto>,
            Modules.Loans.Application.PreviewCapitalPaymentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.AddCapitalPaymentCommand, Modules.Loans.Application.LoanDetailDto>,
            Modules.Loans.Application.AddCapitalPaymentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Loans.Application.VoidCapitalPaymentCommand, Modules.Loans.Application.LoanDetailDto>,
            Modules.Loans.Application.VoidCapitalPaymentHandler>();
        // Lo reutilizan los mutadores de préstamos, cuotas y abonos.
        services.AddScoped<Modules.Loans.Application.LoanDtoComposer>();

        // Hr, spec 2026-11-20. A mano y con ruta completa, como Loans: un handler olvidado responde 500.
        services.AddScoped<SalaryRules>();
        services.AddScoped<
            IQueryHandler<GetSalaryRulesQuery, SalaryRulesDto>,
            GetSalaryRulesHandler>();
        // Lo que comparten los casos de uso de la configuración: empresas, «¿está lista?» y el armado de los DTO.
        services.AddScoped<Modules.Hr.Application.HrCompanyRefs>();
        services.AddScoped<Modules.Hr.Application.HrSetup>();
        services.AddScoped<Modules.Hr.Application.HrSettingsComposer>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.GetHrSettingsQuery, Modules.Hr.Application.HrSettingsDto>,
            Modules.Hr.Application.GetHrSettingsHandler>();
        // Hr, birthday notices (spec 2026-11-26): the setting of who receives the daily digest.
        services.AddScoped<Modules.Hr.Application.BirthdayNotices.BirthdayNoticesComposer>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.BirthdayNotices.GetBirthdayNoticesQuery, Modules.Hr.Application.BirthdayNotices.BirthdayNoticesDto>,
            Modules.Hr.Application.BirthdayNotices.GetBirthdayNoticesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.BirthdayNotices.SaveBirthdayNoticesCommand, Modules.Hr.Application.BirthdayNotices.BirthdayNoticesDto>,
            Modules.Hr.Application.BirthdayNotices.SaveBirthdayNoticesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.CreateSiteCommand, Modules.Hr.Application.SiteDto>,
            Modules.Hr.Application.CreateSiteHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.UpdateSiteCommand, Modules.Hr.Application.SiteDto>,
            Modules.Hr.Application.UpdateSiteHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.CreateWorkplaceCommand, Modules.Hr.Application.WorkplaceDto>,
            Modules.Hr.Application.CreateWorkplaceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.UpdateWorkplaceCommand, Modules.Hr.Application.WorkplaceDto>,
            Modules.Hr.Application.UpdateWorkplaceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.CreateNamedSettingCommand, object>,
            Modules.Hr.Application.CreateNamedSettingHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.UpdateNamedSettingCommand, object>,
            Modules.Hr.Application.UpdateNamedSettingHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.SetPositionExamFrequencyCommand, Modules.Hr.Application.PositionDto>,
            Modules.Hr.Application.MedicalExams.SetPositionExamFrequencyHandler>();
        // Exámenes médicos (spec 2026-11-23): lo que comparten las lecturas y escrituras, y un handler por ruta.
        services.AddScoped<Modules.Hr.Application.MedicalExams.EmployeeMedicalExamsComposer>();
        services.AddScoped<Modules.Hr.Application.MedicalExams.MedicalExamWrites>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.MedicalExams.GetEmployeeMedicalExamsQuery, Modules.Hr.Application.MedicalExams.EmployeeMedicalExamsDto>,
            Modules.Hr.Application.MedicalExams.GetEmployeeMedicalExamsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.MedicalExams.GetMedicalExamOverviewQuery, Modules.Hr.Application.MedicalExams.MedicalExamOverviewDto>,
            Modules.Hr.Application.MedicalExams.GetMedicalExamOverviewHandler>();
        services.AddScoped<Modules.Hr.Application.MedicalExams.MedicalExamImportAnalyzer>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.MedicalExams.GetMedicalExamImportTemplateQuery, Modules.Hr.Application.MedicalExams.MedicalExamImportTemplateFile>,
            Modules.Hr.Application.MedicalExams.GetMedicalExamImportTemplateHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.PreviewMedicalExamImportCommand, Modules.Hr.Application.MedicalExams.MedicalExamImportPreviewDto>,
            Modules.Hr.Application.MedicalExams.PreviewMedicalExamImportHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.ImportMedicalExamsCommand, Modules.Hr.Application.MedicalExams.MedicalExamImportResultDto>,
            Modules.Hr.Application.MedicalExams.ImportMedicalExamsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.OpenMedicalExamUploadSessionCommand, Modules.Hr.Application.HrUploadSessionDto>,
            Modules.Hr.Application.MedicalExams.OpenMedicalExamUploadSessionHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.SetMedicalExamDocumentCommand, Modules.Hr.Application.MedicalExams.EmployeeMedicalExamsDto>,
            Modules.Hr.Application.MedicalExams.SetMedicalExamDocumentHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.MedicalExams.GetMedicalExamDocumentQuery, Modules.Hr.Application.HrDownloadUrlDto>,
            Modules.Hr.Application.MedicalExams.GetMedicalExamDocumentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.RecordMedicalExamCommand, Modules.Hr.Application.MedicalExams.EmployeeMedicalExamsDto>,
            Modules.Hr.Application.MedicalExams.RecordMedicalExamHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.UpdateMedicalExamCommand, Modules.Hr.Application.MedicalExams.EmployeeMedicalExamsDto>,
            Modules.Hr.Application.MedicalExams.UpdateMedicalExamHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.DeleteMedicalExamCommand, Modules.Hr.Application.MedicalExams.EmployeeMedicalExamsDto>,
            Modules.Hr.Application.MedicalExams.DeleteMedicalExamHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.MedicalExams.MarkExitNotAttendedCommand, Modules.Hr.Application.MedicalExams.EmployeeMedicalExamsDto>,
            Modules.Hr.Application.MedicalExams.MarkExitNotAttendedHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.ArchiveSettingCommand, object>,
            Modules.Hr.Application.ArchiveSettingHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.DeleteSettingCommand, bool>,
            Modules.Hr.Application.DeleteSettingHandler>();
        // Empleados (spec 2026-11-20): lo que comparten los casos de uso (armado de la respuesta, historial de acciones, contrato
        // nuevo) y un handler por ruta.
        services.AddScoped<Modules.Hr.Application.EmployeeDetailComposer>();
        services.AddScoped<Modules.Hr.Application.ActivityWriter>();
        services.AddScoped<Modules.Hr.Application.NewContractFactory>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.ListEmployeesQuery, EmployeePageDto>,
            Modules.Hr.Application.ListEmployeesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.GetEmployeeQuery, EmployeeDetailDto>,
            Modules.Hr.Application.GetEmployeeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.CreateEmployeeCommand, EmployeeDetailDto>,
            Modules.Hr.Application.CreateEmployeeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.UpdateEmployeeCommand, EmployeeDetailDto>,
            Modules.Hr.Application.UpdateEmployeeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.UpdateEmployeePaymentCommand, EmployeeDetailDto>,
            Modules.Hr.Application.UpdateEmployeePaymentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.DeactivateEmployeeCommand, EmployeeDetailDto>,
            Modules.Hr.Application.DeactivateEmployeeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.DeleteEmployeeCommand, bool>,
            Modules.Hr.Application.DeleteEmployeeHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.ListAvailableMembersQuery, IReadOnlyList<Modules.Hr.Application.MemberRefDto>>,
            Modules.Hr.Application.ListAvailableMembersHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.ListEmployeeActivityQuery, ActivityPageDto>,
            Modules.Hr.Application.ListEmployeeActivityHandler>();
        // El contrato del empleado (spec 2026-11-20): lo que comparten las escrituras y un handler por ruta.
        services.AddScoped<Modules.Hr.Application.ContractWrites>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.AddSalaryCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.AddSalaryHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.RewriteSalaryCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.RewriteSalaryHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.AddAssignmentCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.AddAssignmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.RenewContractCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.RenewContractHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.ConvertContractToIndefiniteCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.ConvertContractToIndefiniteHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.ChangeContractTypeCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.ChangeContractTypeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.TerminateContractCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.TerminateContractHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.ReincorporateEmployeeCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.ReincorporateEmployeeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.SetHrDocumentCommand, Modules.Hr.Application.EmployeeDetailDto>,
            Modules.Hr.Application.SetHrDocumentHandler>();
        // Los PDF de contratos y registros (spec 2026-11-20): la sesión de subida y la URL firmada de descarga.
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.OpenHrUploadSessionCommand, Modules.Hr.Application.HrUploadSessionDto>,
            Modules.Hr.Application.OpenHrUploadSessionHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.GetHrFileDownloadUrlQuery, Modules.Hr.Application.HrDownloadUrlDto>,
            Modules.Hr.Application.GetHrFileDownloadUrlHandler>();
        // La consola de valores legales del operador (spec 2026-11-22). Sus handlers no revisan el módulo `hr`: el tenant operador
        // puede no tenerlo. La escritura (y la carga por Excel, después) pasa por LegalParameterWriter; la pantalla la arma el composer.
        services.AddScoped<Modules.Hr.Application.LegalParameterWriter>();
        services.AddScoped<Modules.Hr.Application.LegalParametersComposer>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.ListLegalParametersQuery, Modules.Hr.Application.LegalParametersDto>,
            Modules.Hr.Application.ListLegalParametersHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.ListLegalParameterHistoryQuery, Modules.Hr.Application.LegalParameterHistoryPageDto>,
            Modules.Hr.Application.ListLegalParameterHistoryHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.SetLegalParameterCommand, Modules.Hr.Application.LegalParametersDto>,
            Modules.Hr.Application.SetLegalParameterHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.DeleteLegalParameterCommand, Modules.Hr.Application.LegalParametersDto>,
            Modules.Hr.Application.DeleteLegalParameterHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.SetLegalBracketsCommand, Modules.Hr.Application.LegalParametersDto>,
            Modules.Hr.Application.SetLegalBracketsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.DeleteLegalBracketsCommand, Modules.Hr.Application.LegalParametersDto>,
            Modules.Hr.Application.DeleteLegalBracketsHandler>();
        // La carga anual por Excel (spec 2026-11-22): plantilla, vista previa y confirmación comparten análisis y escriben por el mismo escritor.
        services.AddScoped<Modules.Hr.Application.LegalParameterImportAnalyzer>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.GetLegalParameterImportTemplateQuery, Modules.Hr.Application.LegalParameterImportTemplateFile>,
            Modules.Hr.Application.GetLegalParameterImportTemplateHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.PreviewLegalParameterImportCommand, Modules.Hr.Application.LegalParameterImportPreviewDto>,
            Modules.Hr.Application.PreviewLegalParameterImportHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.ImportLegalParametersCommand, Modules.Hr.Application.LegalParameterImportResultDto>,
            Modules.Hr.Application.ImportLegalParametersHandler>();

        // La importación de empleados por Excel (spec 2026-11-20): plantilla, vista previa y confirmación comparten análisis.
        services.AddScoped<Modules.Hr.Application.EmployeeImportCatalogs>();
        services.AddScoped<Modules.Hr.Application.EmployeeImportAnalyzer>();
        services.AddScoped<Modules.Hr.Application.IHrPhoneNormalizer, HrPhoneNormalizer>();
        services.AddScoped<Modules.Tenancy.Application.IMemberPhoneDirectory, MemberPhoneDirectory>();
        services.AddScoped<Modules.Tenancy.Application.MembershipPhoneReader>();
        services.AddScoped<
            IQueryHandler<Modules.Hr.Application.GetEmployeeImportTemplateQuery, Modules.Hr.Application.EmployeeImportTemplateFile>,
            Modules.Hr.Application.GetEmployeeImportTemplateHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.PreviewEmployeeImportCommand, Modules.Hr.Application.EmployeeImportPreviewDto>,
            Modules.Hr.Application.PreviewEmployeeImportHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Hr.Application.ImportEmployeesCommand, Modules.Hr.Application.EmployeeImportResultDto>,
            Modules.Hr.Application.ImportEmployeesHandler>();

        // Payroll, spec 2026-11-21. A mano y con ruta completa, como Hr: un handler olvidado responde 500.
        services.AddScoped<Modules.Payroll.Application.PayrollCompanies>();
        services.AddScoped<Modules.Payroll.Application.ConceptCatalog>();
        services.AddScoped<Modules.Payroll.Application.PayrollSettingsComposer>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.ListPayrollCompaniesQuery, IReadOnlyList<Modules.Payroll.Application.PayrollCompanyDto>>,
            Modules.Payroll.Application.ListPayrollCompaniesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.GetPayrollSettingsQuery, Modules.Payroll.Application.PayrollSettingsDto>,
            Modules.Payroll.Application.GetPayrollSettingsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.UpdatePayrollSettingsCommand, Modules.Payroll.Application.PayrollSettingsDto>,
            Modules.Payroll.Application.UpdatePayrollSettingsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.CreateConceptCommand, Modules.Payroll.Application.PayrollConceptDto>,
            Modules.Payroll.Application.CreateConceptHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.UpdateConceptCommand, Modules.Payroll.Application.PayrollConceptDto>,
            Modules.Payroll.Application.UpdateConceptHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.ArchiveConceptCommand, Modules.Payroll.Application.PayrollConceptDto>,
            Modules.Payroll.Application.ArchiveConceptHandler>();
        // Períodos: el compositor y los actores los comparten todos los casos de uso del período.
        services.AddScoped<Modules.Payroll.Application.PeriodActors>();
        services.AddScoped<Modules.Payroll.Application.PeriodComposer>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.EnsureCurrentPeriodsCommand, Modules.Payroll.Application.PayrollPeriodListDto>,
            Modules.Payroll.Application.EnsureCurrentPeriodsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.OpenNextPeriodCommand, Modules.Payroll.Application.PayrollPeriodListDto>,
            Modules.Payroll.Application.OpenNextPeriodHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.ListPeriodsQuery, Modules.Payroll.Application.PayrollPeriodListDto>,
            Modules.Payroll.Application.ListPeriodsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.GetPeriodQuery, Modules.Payroll.Application.PayrollPeriodDetailDto>,
            Modules.Payroll.Application.GetPeriodHandler>();
        services.AddScoped<Modules.Payroll.Application.PayrollEmployees>();
        services.AddScoped<Modules.Payroll.Application.RecurringAccumulation>();
        services.AddScoped<Modules.Payroll.Application.RecurringConceptComposer>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.GetPeriodEmployeeQuery, Modules.Payroll.Application.PayrollEmployeeDetailDto>,
            Modules.Payroll.Application.GetPeriodEmployeeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.UpdateEmployeeInputsCommand, Modules.Payroll.Application.PayrollEmployeeDetailDto>,
            Modules.Payroll.Application.UpdateEmployeeInputsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.ListRecurringConceptsQuery, IReadOnlyList<Modules.Payroll.Application.RecurringConceptDto>>,
            Modules.Payroll.Application.ListRecurringConceptsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.CreateRecurringConceptCommand, IReadOnlyList<Modules.Payroll.Application.RecurringConceptDto>>,
            Modules.Payroll.Application.CreateRecurringConceptHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.UpdateRecurringConceptCommand, IReadOnlyList<Modules.Payroll.Application.RecurringConceptDto>>,
            Modules.Payroll.Application.UpdateRecurringConceptHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.ToggleRecurringConceptCommand, IReadOnlyList<Modules.Payroll.Application.RecurringConceptDto>>,
            Modules.Payroll.Application.ToggleRecurringConceptHandler>();
        services.AddScoped<Modules.Payroll.Application.VacationInputBuilder>();
        services.AddScoped<Modules.Payroll.Application.VacationRules>();
        services.AddScoped<Modules.Payroll.Application.VacationComposer>();
        services.AddScoped<Modules.Payroll.Application.VacationLocks>();
        services.AddScoped<Modules.Payroll.Application.VacationPayments>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.CreateVacationCommand, IReadOnlyList<Modules.Payroll.Application.VacationDto>>,
            Modules.Payroll.Application.CreateVacationHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.UpdateVacationCommand, IReadOnlyList<Modules.Payroll.Application.VacationDto>>,
            Modules.Payroll.Application.UpdateVacationHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.DeleteVacationCommand, IReadOnlyList<Modules.Payroll.Application.VacationDto>>,
            Modules.Payroll.Application.DeleteVacationHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.ListVacationsQuery, IReadOnlyList<Modules.Payroll.Application.VacationDto>>,
            Modules.Payroll.Application.ListVacationsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.PreviewVacationQuery, Modules.Payroll.Application.VacationPreviewDto>,
            Modules.Payroll.Application.PreviewVacationHandler>();
        services.AddScoped<Modules.Payroll.Application.LiquidationPlanner>();
        services.AddScoped<Modules.Payroll.Application.PeriodApprovals>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.LiquidatePeriodCommand, Modules.Payroll.Application.PayrollPeriodDetailDto>,
            Modules.Payroll.Application.LiquidatePeriodHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.ApprovePeriodCommand, Modules.Payroll.Application.PayrollPeriodDetailDto>,
            Modules.Payroll.Application.ApprovePeriodHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.ClosePeriodCommand, Modules.Payroll.Application.PayrollPeriodDetailDto>,
            Modules.Payroll.Application.ClosePeriodHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.ReopenPeriodCommand, Modules.Payroll.Application.PayrollPeriodDetailDto>,
            Modules.Payroll.Application.ReopenPeriodHandler>();
        services.AddScoped<Modules.Payroll.Application.InputsImportAnalyzer>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.GetInputsImportTemplateQuery, Modules.Payroll.Application.InputsImportTemplateFile>,
            Modules.Payroll.Application.GetInputsImportTemplateHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.PreviewInputsImportCommand, Modules.Payroll.Application.InputsImportPreviewDto>,
            Modules.Payroll.Application.PreviewInputsImportHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Payroll.Application.ImportInputsCommand, Modules.Payroll.Application.PayrollPeriodDetailDto>,
            Modules.Payroll.Application.ImportInputsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Payroll.Application.ExportPeriodQuery, Modules.Payroll.Application.PayrollExportFile>,
            Modules.Payroll.Application.ExportPeriodHandler>();

        // Scheduling, spec 2026-11-21. A mano y con ruta completa, como Hr: un handler olvidado responde 500.
        services.AddScoped<Modules.Scheduling.Application.SchedulingFirstAccess>();
        services.AddScoped<Modules.Scheduling.Application.ResourceScopeResolver>();
        services.AddScoped<Modules.Scheduling.Application.SchedulingComposer>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.GetSetupQuery, Modules.Scheduling.Application.SetupDto>,
            Modules.Scheduling.Application.GetSetupHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.ListLocationsQuery, IReadOnlyList<Modules.Scheduling.Application.LocationDto>>,
            Modules.Scheduling.Application.ListLocationsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.ListResourcesQuery, IReadOnlyList<Modules.Scheduling.Application.ResourceDto>>,
            Modules.Scheduling.Application.ListResourcesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.ListServicesQuery, IReadOnlyList<Modules.Scheduling.Application.ServiceDto>>,
            Modules.Scheduling.Application.ListServicesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.GetSettingsQuery, Modules.Scheduling.Application.SettingsDto>,
            Modules.Scheduling.Application.GetSettingsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.CreateLocationCommand, Modules.Scheduling.Application.LocationDto>,
            Modules.Scheduling.Application.CreateLocationHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.UpdateLocationCommand, Modules.Scheduling.Application.LocationDto>,
            Modules.Scheduling.Application.UpdateLocationHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.ArchiveLocationCommand, Modules.Scheduling.Application.LocationDto>,
            Modules.Scheduling.Application.ArchiveLocationHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.CreateResourceCommand, Modules.Scheduling.Application.ResourceDto>,
            Modules.Scheduling.Application.CreateResourceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.UpdateResourceCommand, Modules.Scheduling.Application.ResourceDto>,
            Modules.Scheduling.Application.UpdateResourceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.ArchiveResourceCommand, Modules.Scheduling.Application.ResourceDto>,
            Modules.Scheduling.Application.ArchiveResourceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.CreateServiceCommand, Modules.Scheduling.Application.ServiceDto>,
            Modules.Scheduling.Application.CreateServiceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.UpdateServiceCommand, Modules.Scheduling.Application.ServiceDto>,
            Modules.Scheduling.Application.UpdateServiceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.ArchiveServiceCommand, Modules.Scheduling.Application.ServiceDto>,
            Modules.Scheduling.Application.ArchiveServiceHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.UpdateSettingsCommand, Modules.Scheduling.Application.SettingsDto>,
            Modules.Scheduling.Application.UpdateSettingsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.ListSessionsQuery, IReadOnlyList<Modules.Scheduling.Application.PublishedSessionDto>>,
            Modules.Scheduling.Application.ListSessionsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.ListBlocksQuery, IReadOnlyList<Modules.Scheduling.Application.BlockDto>>,
            Modules.Scheduling.Application.ListBlocksHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.CreateBlockCommand, Modules.Scheduling.Application.BlockDto>,
            Modules.Scheduling.Application.CreateBlockHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.UpdateBlockCommand, Modules.Scheduling.Application.BlockDto>,
            Modules.Scheduling.Application.UpdateBlockHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.ArchiveBlockCommand, Modules.Scheduling.Application.BlockDto>,
            Modules.Scheduling.Application.ArchiveBlockHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.PublishSessionCommand, Modules.Scheduling.Application.PublishedSessionDto>,
            Modules.Scheduling.Application.PublishSessionHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.RemovePublishedSessionCommand, Modules.Scheduling.Application.PublishedSessionRemovedResult>,
            Modules.Scheduling.Application.RemovePublishedSessionHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.ListClosuresQuery, IReadOnlyList<Modules.Scheduling.Application.ClosureDto>>,
            Modules.Scheduling.Application.ListClosuresHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.CreateClosureCommand, Modules.Scheduling.Application.ClosureDto>,
            Modules.Scheduling.Application.CreateClosureHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.DeleteClosureCommand, Modules.Scheduling.Application.ClosureDeletedResult>,
            Modules.Scheduling.Application.DeleteClosureHandler>();
        services.AddScoped<Modules.Scheduling.Application.AvailabilityLoader>();
        services.AddScoped<Modules.Scheduling.Application.AvailabilitySearch>();
        services.AddScoped<Modules.Scheduling.Application.AppointmentBooking>();
        services.AddScoped<Modules.Scheduling.Application.AppointmentCancellation>();
        services.AddScoped<Modules.Scheduling.Application.AppointmentRescheduling>();
        services.AddScoped<Modules.Scheduling.Application.IChannelScheduling, Modules.Scheduling.Application.ChannelScheduling>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.ListAvailabilityQuery, Modules.Scheduling.Application.AvailabilityDto>,
            Modules.Scheduling.Application.ListAvailabilityHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.SearchAppointmentCustomersQuery, IReadOnlyList<Modules.Scheduling.Application.AppointmentCustomerDto>>,
            Modules.Scheduling.Application.SearchAppointmentCustomersHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.CreateAppointmentCustomerCommand, Modules.Scheduling.Application.AppointmentCustomerDto>,
            Modules.Scheduling.Application.CreateAppointmentCustomerHandler>();
        services.AddScoped<Modules.Scheduling.Application.AppointmentComposer>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.BookAppointmentCommand, Modules.Scheduling.Application.AppointmentDto>,
            Modules.Scheduling.Application.BookAppointmentHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.GetAppointmentQuery, Modules.Scheduling.Application.AppointmentDto>,
            Modules.Scheduling.Application.GetAppointmentHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.ListCalendarQuery, Modules.Scheduling.Application.CalendarDto>,
            Modules.Scheduling.Application.ListCalendarHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Scheduling.Application.GetDeskQuery, Modules.Scheduling.Application.DeskDto>,
            Modules.Scheduling.Application.GetDeskHandler>();
        services.AddScoped<Modules.Scheduling.Application.AppointmentTransitions>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.ConfirmAppointmentCommand, Modules.Scheduling.Application.AppointmentDto>,
            Modules.Scheduling.Application.ConfirmAppointmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.CancelAppointmentCommand, Modules.Scheduling.Application.AppointmentDto>,
            Modules.Scheduling.Application.CancelAppointmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.MarkAttendedCommand, Modules.Scheduling.Application.AppointmentDto>,
            Modules.Scheduling.Application.MarkAttendedHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.MarkNoShowCommand, Modules.Scheduling.Application.AppointmentDto>,
            Modules.Scheduling.Application.MarkNoShowHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Scheduling.Application.RescheduleAppointmentCommand, Modules.Scheduling.Application.AppointmentDto>,
            Modules.Scheduling.Application.RescheduleAppointmentHandler>();
        // La carga común de abonar, previsualizar y anular: genera los períodos cerrados antes de decidir.
        services.AddScoped<Modules.Loans.Application.CapitalPaymentSupport>();

        // Purchases, spec 2026-11-17. A mano: un handler olvidado responde 500.
        services.AddScoped<
            IQueryHandler<Modules.Purchases.Application.ListPurchaseOrdersQuery, Modules.Purchases.Application.PurchaseOrdersResponse>,
            Modules.Purchases.Application.ListPurchaseOrdersHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Purchases.Application.GetPurchaseOrderQuery, Modules.Purchases.Application.PurchaseOrderDetailDto>,
            Modules.Purchases.Application.GetPurchaseOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Purchases.Application.CreatePurchaseOrderCommand, Modules.Purchases.Application.PurchaseOrderDetailDto>,
            Modules.Purchases.Application.CreatePurchaseOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Purchases.Application.PreviewPurchaseOrderCommand, Modules.Purchases.Application.PurchaseOrderPreviewDto>,
            Modules.Purchases.Application.PreviewPurchaseOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Purchases.Application.UpdatePurchaseOrderCommand, Modules.Purchases.Application.PurchaseOrderDetailDto>,
            Modules.Purchases.Application.UpdatePurchaseOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Purchases.Application.DeletePurchaseOrderCommand, bool>,
            Modules.Purchases.Application.DeletePurchaseOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Purchases.Application.TransitionPurchaseOrderCommand, Modules.Purchases.Application.PurchaseOrderDetailDto>,
            Modules.Purchases.Application.TransitionPurchaseOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Purchases.Application.ConfirmPurchaseOrderCommand, Modules.Purchases.Application.PurchaseOrderDetailDto>,
            Modules.Purchases.Application.ConfirmPurchaseOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Purchases.Application.ResendPurchaseOrderEmailCommand, Modules.Purchases.Application.PurchaseOrderDetailDto>,
            Modules.Purchases.Application.ResendPurchaseOrderEmailHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Purchases.Application.GetPurchaseOrderPdfQuery, Modules.Purchases.Application.PurchaseOrderPdfFile>,
            Modules.Purchases.Application.GetPurchaseOrderPdfHandler>();
        services.AddScoped<Modules.Purchases.Application.PurchaseOrderComposer>();
        // El documento de la orden lo comparten la descarga del PDF y el correo al proveedor.
        services.AddScoped<
            Modules.Purchases.Application.IPurchaseOrderDocumentBuilder,
            Modules.Purchases.Application.PurchaseOrderDocumentBuilder>();
        services.AddScoped<Modules.Purchases.Application.PurchaseOrderEmailDelivery>();
        // El armado de líneas que comparten el alta, la edición y la vista previa (D-19).
        services.AddScoped<Modules.Purchases.Application.PurchaseOrderLineBuilder>();
        // Mayorista fase 3: el catálogo del mayorista de un proveedor vinculado y el aplicador de sus eventos (lo llama el
        // procesador del inbox de Compras en su scope por mensaje; los adaptadores de los puertos mayoristas van más abajo).
        services.AddScoped<
            IQueryHandler<Modules.Purchases.Application.ListSupplierCatalogQuery, Modules.Purchases.Application.SupplierCatalogPageDto>,
            Modules.Purchases.Application.ListSupplierCatalogHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Purchases.Application.ListSupplierCatalogCategoriesQuery, Modules.Purchases.Application.SupplierCatalogCategoriesDto>,
            Modules.Purchases.Application.ListSupplierCatalogCategoriesHandler>();
        services.AddScoped<Modules.Purchases.Application.WholesaleOrderEventApplier>();

        // Wholesale, spec 2026-11-17. A mano: un handler olvidado responde 500.
        // Spec 2026-11-22: las listas pasaron a Catalog; queda la vista previa del distribuidor.
        services.AddScoped<
            ICommandHandler<Modules.Wholesale.Application.PreviewPriceListCommand, Modules.Wholesale.Application.PriceListPreviewDto>,
            Modules.Wholesale.Application.PreviewPriceListHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wholesale.Application.ListWholesaleCategoriesQuery, Modules.Wholesale.Application.WholesaleCategoriesResponse>,
            Modules.Wholesale.Application.ListWholesaleCategoriesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wholesale.Application.ListWholesaleProductsQuery, Modules.Wholesale.Application.WholesaleProductsResponse>,
            Modules.Wholesale.Application.ListWholesaleProductsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wholesale.Application.ListDistributorsQuery, Modules.Wholesale.Application.DistributorsResponse>,
            Modules.Wholesale.Application.ListDistributorsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wholesale.Application.GetDistributorQuery, Modules.Wholesale.Application.DistributorDetailDto>,
            Modules.Wholesale.Application.GetDistributorHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wholesale.Application.GetDistributorOptionsQuery, Modules.Wholesale.Application.DistributorOptionsDto>,
            Modules.Wholesale.Application.GetDistributorOptionsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wholesale.Application.InviteDistributorCommand, Modules.Wholesale.Application.DistributorDetailDto>,
            Modules.Wholesale.Application.InviteDistributorHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wholesale.Application.UpdateDistributorCommand, Modules.Wholesale.Application.DistributorDetailDto>,
            Modules.Wholesale.Application.UpdateDistributorHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wholesale.Application.TransitionDistributorCommand, Modules.Wholesale.Application.DistributorDetailDto>,
            Modules.Wholesale.Application.TransitionDistributorHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wholesale.Application.DeleteDistributorCommand, bool>,
            Modules.Wholesale.Application.DeleteDistributorHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wholesale.Application.RotateInvitationLinkCommand, Modules.Wholesale.Application.DistributorDetailDto>,
            Modules.Wholesale.Application.RotateInvitationLinkHandler>();
        services.AddScoped<Modules.Wholesale.Application.DistributorComposer>();
        // La invitación vista desde el link: sin IExecutionContext, el tenant del mayorista sale de la invitación.
        services.AddScoped<Modules.Wholesale.Application.IDistributorInvitationService, Modules.Wholesale.Application.DistributorInvitationService>();
        // Fase 3: la única puerta entre tenants al catálogo del mayorista (relación, visibilidad y precio de lista).
        services.AddScoped<Modules.Wholesale.Application.WholesaleOffers>();

        // Support, spec 2026-11-14. A mano y con ruta completa, como Loans: un handler olvidado compila, mapea su endpoint y
        // responde 500.
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListTicketTypesQuery, IReadOnlyList<Modules.Support.Application.SupportTicketTypeDto>>,
            Modules.Support.Application.ListTicketTypesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.CreateTicketTypeCommand, Modules.Support.Application.SupportTicketTypeDto>,
            Modules.Support.Application.CreateTicketTypeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.UpdateTicketTypeCommand, Modules.Support.Application.SupportTicketTypeDto>,
            Modules.Support.Application.UpdateTicketTypeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.DeleteTicketTypeCommand, Modules.Support.Application.DeleteTicketTypeResult>,
            Modules.Support.Application.DeleteTicketTypeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ReorderTicketTypesCommand, IReadOnlyList<Modules.Support.Application.SupportTicketTypeDto>>,
            Modules.Support.Application.ReorderTicketTypesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetSupportSettingsQuery, Modules.Support.Application.SupportSettingsDto>,
            Modules.Support.Application.GetSupportSettingsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.UpdateSupportSettingsCommand, Modules.Support.Application.SupportSettingsDto>,
            Modules.Support.Application.UpdateSupportSettingsHandler>();
        // Fase 3: el formulario público, versionado (plan F14); los documentos legales del portal, versionados por tipo (spec
        // 2026-11-15 §7).
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetFormSettingsQuery, Modules.Support.Application.SupportFormSettingsDto>,
            Modules.Support.Application.GetFormSettingsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.UpdateFormSettingsCommand, Modules.Support.Application.SupportFormSettingsDto>,
            Modules.Support.Application.UpdateFormSettingsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetLegalDocumentsQuery, Modules.Support.Application.SupportLegalDocumentsDto>,
            Modules.Support.Application.GetLegalDocumentsHandler>();
        services.AddScoped<
            IQueryHandler<
                Modules.Support.Application.GetLegalDocumentHistoryQuery, Modules.Support.Application.SupportLegalDocumentHistoryDto>,
            Modules.Support.Application.GetLegalDocumentHistoryHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetLegalDocumentVersionQuery, Modules.Support.Application.SupportLegalDocumentDto>,
            Modules.Support.Application.GetLegalDocumentVersionHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.PublishLegalDocumentCommand, Modules.Support.Application.SupportLegalDocumentDto>,
            Modules.Support.Application.PublishLegalDocumentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.CreateTicketCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.CreateTicketHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetTicketQuery, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.GetTicketHandler>();
        services.AddScoped<
            IQueryHandler<
                Modules.Support.Application.GetLegalAcceptanceSnapshotQuery,
                Modules.Support.Application.SupportLegalAcceptanceSnapshotDto>,
            Modules.Support.Application.GetLegalAcceptanceSnapshotHandler>();
        services.AddScoped<
            ICommandHandler<
                Modules.Support.Application.GetTicketAttachmentDownloadCommand, Modules.Support.Application.SupportAttachmentDownloadDto>,
            Modules.Support.Application.GetTicketAttachmentDownloadHandler>();
        // Fase 4b (H13): el PDF de una respuesta formal desde la consola.
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.GetReplyDocumentDownloadCommand, Modules.Support.Application.SupportDocumentLinkDto>,
            Modules.Support.Application.GetReplyDocumentDownloadHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListSupportAssigneesQuery, Modules.Support.Application.SupportAssigneesDto>,
            Modules.Support.Application.ListSupportAssigneesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ChangeTicketStatusCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.ChangeTicketStatusHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.AssignTicketCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.AssignTicketHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.TakeTicketCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.TakeTicketHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ExtendTicketCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.ExtendTicketHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ReissueTicketPinCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.ReissueTicketPinHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ChangeTicketTypeCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.ChangeTicketTypeHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.LinkTicketCustomerCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.LinkTicketCustomerHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ChangeTicketPriorityCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.ChangeTicketPriorityHandler>();
        // Spec 2026-11-22: la fecha límite de una tarea. El validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ChangeTicketDueDateCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.ChangeTicketDueDateHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListTicketsQuery, Modules.Support.Application.SupportTicketPageDto>,
            Modules.Support.Application.ListTicketsHandler>();
        // Fase 4b (H24): el reporte de Soporte.
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetSupportReportQuery, Modules.Support.Application.SupportReportDto>,
            Modules.Support.Application.GetSupportReportHandler>();
        // Fase 2 (Mensajería).
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.CreateTicketFromConversationCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.CreateTicketFromConversationHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListConversationTicketsQuery, Modules.Support.Application.SupportConversationTicketsDto>,
            Modules.Support.Application.ListConversationTicketsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListTicketMessagesQuery, Modules.Support.Application.SupportTicketMessagesDto>,
            Modules.Support.Application.ListTicketMessagesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.PinTicketMessageCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.PinTicketMessageHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ReleaseTicketMessageCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.ReleaseTicketMessageHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.SetTicketFollowThreadCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.SetTicketFollowThreadHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListUnclassifiedQuery, Modules.Support.Application.SupportUnclassifiedDto>,
            Modules.Support.Application.ListUnclassifiedHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.AssignUnclassifiedCommand, Modules.Support.Application.SupportUnclassifiedDto>,
            Modules.Support.Application.AssignUnclassifiedHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.DismissUnclassifiedCommand, Modules.Support.Application.SupportUnclassifiedDto>,
            Modules.Support.Application.DismissUnclassifiedHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetReplyOptionsQuery, Modules.Support.Application.SupportReplyOptionsDto>,
            Modules.Support.Application.GetReplyOptionsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.SendTicketReplyCommand, Modules.Support.Application.SupportReplyResultDto>,
            Modules.Support.Application.SendTicketReplyHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.AddTicketNoteCommand, Modules.Support.Application.SupportTicketNoteDto>,
            Modules.Support.Application.AddTicketNoteHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListTicketNotesQuery, Modules.Support.Application.SupportTicketNotesDto>,
            Modules.Support.Application.ListTicketNotesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.OpenNoteAttachmentCommand, Modules.Support.Application.SupportNoteAttachmentSessionDto>,
            Modules.Support.Application.OpenNoteAttachmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.CompleteNoteAttachmentCommand, Modules.Support.Application.SupportNoteAttachmentStatusDto>,
            Modules.Support.Application.CompleteNoteAttachmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.GetNoteFileDownloadCommand, Modules.Support.Application.SupportAttachmentDownloadDto>,
            Modules.Support.Application.GetNoteFileDownloadHandler>();
        // Spec 2026-11-22 (tareas internas) §5.2: el alta y los candidatos a responsable. El validador entra con
        // AddValidatorsFromAssemblyContaining<UpdateSupportSettingsValidator>.
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.CreateTaskCommand, Modules.Support.Application.SupportTicketDetailDto>,
            Modules.Support.Application.CreateTaskHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListTaskAssigneesQuery, Modules.Support.Application.SupportAssigneesDto>,
            Modules.Support.Application.ListTaskAssigneesHandler>();
        services.AddScoped<Modules.Support.Application.SupportTicketDtoComposer>();
        // Spec 2026-11-22 (tareas internas) §5.6: la puerta única de todo ticket; una instancia por request memoiza el acceso.
        services.AddScoped<Modules.Support.Application.SupportTicketScope>();
        // Spec 2026-11-22 §8.6: the settings DTO with the unassigned-alert block (names, options and the schedule that rules).
        services.AddScoped<Modules.Support.Application.SupportSettingsComposer>();

        // Wms, spec WMS 2026-10-31. A mano y con ruta completa, como Loans: un handler olvidado responde 500.
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.ListWarehousesQuery, Modules.Wms.Application.WarehousesDto>,
            Modules.Wms.Application.ListWarehousesHandler>();
        services.AddValidatorsFromAssemblyContaining<Modules.Wms.Application.CreateWarehouseValidator>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.RenderLocationLabelsCommand, Modules.Wms.Application.LocationLabelsPdf>,
            Modules.Wms.Application.RenderLocationLabelsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.CreateWarehouseCommand, Modules.Wms.Application.WarehouseDto>,
            Modules.Wms.Application.CreateWarehouseHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.UpdateWarehouseCommand, Modules.Wms.Application.WarehouseDto>,
            Modules.Wms.Application.UpdateWarehouseHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.ListLocationsQuery, Modules.Wms.Application.LocationsDto>,
            Modules.Wms.Application.ListLocationsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.CreateLocationCommand, Modules.Wms.Application.LocationDto>,
            Modules.Wms.Application.CreateLocationHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.UpdateLocationCommand, Modules.Wms.Application.LocationDto>,
            Modules.Wms.Application.UpdateLocationHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.DeleteLocationCommand, bool>,
            Modules.Wms.Application.DeleteLocationHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.GetScannableLocationsQuery, Modules.Wms.Application.ScannableLocationsDto>,
            Modules.Wms.Application.GetScannableLocationsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.ListOwnersQuery, Modules.Wms.Application.OwnersDto>,
            Modules.Wms.Application.ListOwnersHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.CreateOwnerCommand, Modules.Wms.Application.OwnerDto>,
            Modules.Wms.Application.CreateOwnerHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.UpdateOwnerCommand, Modules.Wms.Application.OwnerDto>,
            Modules.Wms.Application.UpdateOwnerHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.ListUomsQuery, Modules.Wms.Application.UomsDto>,
            Modules.Wms.Application.ListUomsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.CreateUomCommand, Modules.Wms.Application.UomDto>,
            Modules.Wms.Application.CreateUomHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.GetVariantProfilesQuery, Modules.Wms.Application.VariantProfilesDto>,
            Modules.Wms.Application.GetVariantProfilesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.UpdateVariantProfileCommand, Modules.Wms.Application.VariantProfileDto>,
            Modules.Wms.Application.UpdateVariantProfileHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.GetStockQuery, Modules.Wms.Application.StockPageDto>,
            Modules.Wms.Application.GetStockHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.GetStockSummaryQuery, Modules.Wms.Application.StockSummaryDto>,
            Modules.Wms.Application.GetStockSummaryHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.ListStockMovementsQuery, Modules.Wms.Application.MovementPageDto>,
            Modules.Wms.Application.ListStockMovementsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.ListAdjustmentsQuery, Modules.Wms.Application.AdjustmentPageDto>,
            Modules.Wms.Application.ListAdjustmentsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.ProposeAdjustmentCommand, Modules.Wms.Application.AdjustmentDto>,
            Modules.Wms.Application.ProposeAdjustmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.ApproveAdjustmentCommand, Modules.Wms.Application.AdjustmentDto>,
            Modules.Wms.Application.ApproveAdjustmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.PostAdjustmentCommand, Modules.Wms.Application.AdjustmentDto>,
            Modules.Wms.Application.PostAdjustmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.RejectAdjustmentCommand, Modules.Wms.Application.AdjustmentDto>,
            Modules.Wms.Application.RejectAdjustmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.ReverseMovementCommand, Modules.Wms.Application.MovementDto>,
            Modules.Wms.Application.ReverseMovementHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.PreviewInitialBalancesCommand, Modules.Wms.Application.InitialBalancePreviewDto>,
            Modules.Wms.Application.PreviewInitialBalancesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.CommitInitialBalancesCommand, Modules.Wms.Application.InitialBalanceCommitDto>,
            Modules.Wms.Application.CommitInitialBalancesHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.ExportStockCommand, Modules.Wms.Application.StockExportAcceptedDto>,
            Modules.Wms.Application.ExportStockHandler>();
        // Tareas de bodega (spec 2026-11-26 §6.11).
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.CreateMoveTaskCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.CreateMoveTaskHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.GetTaskQuery, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.GetTaskHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.ListTasksQuery, Modules.Wms.Application.TaskPageDto>,
            Modules.Wms.Application.ListTasksHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.PublishTaskCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.PublishTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.HoldTaskCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.HoldTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.AssignTaskCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.AssignTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.UnassignTaskCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.UnassignTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.ChangeTaskPriorityCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.ChangeTaskPriorityHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.CancelTaskCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.CancelTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.CancelTaskLineCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.CancelTaskLineHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.GetTaskBoardQuery, Modules.Wms.Application.TaskBoardDto>,
            Modules.Wms.Application.GetTaskBoardHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.ListTaskAssigneesQuery, Modules.Wms.Application.TaskAssigneesDto>,
            Modules.Wms.Application.ListTaskAssigneesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Wms.Application.GetMyTasksQuery, Modules.Wms.Application.MyTasksDto>,
            Modules.Wms.Application.GetMyTasksHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.NextTaskCommand, Modules.Wms.Application.NextTaskResultDto>,
            Modules.Wms.Application.NextTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.ClaimTaskCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.ClaimTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.HeartbeatTaskCommand, Modules.Wms.Application.TaskLeaseDto>,
            Modules.Wms.Application.HeartbeatTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.UnclaimTaskCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.UnclaimTaskHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Wms.Application.ConfirmTaskLineCommand, Modules.Wms.Application.TaskDto>,
            Modules.Wms.Application.ConfirmTaskLineHandler>();
        // Pos (spec 2026-10-07). Registro a mano, uno por uno, como los demás: un handler olvidado
        // responde 500 y CompositionRootTests lo detecta.
        services.AddScoped<
            IQueryHandler<GetRegisterContextQuery, RegisterContextResponse>,
            GetRegisterContextHandler>();
        services.AddScoped<
            ICommandHandler<OpenCashSessionCommand, PosOpenSessionResponse>,
            OpenCashSessionHandler>();
        services.AddScoped<
            ICommandHandler<CloseCashSessionCommand, PosSessionSummaryResponse>,
            CloseCashSessionHandler>();
        services.AddScoped<
            IQueryHandler<SearchPosProductsQuery, PosPage<PosCatalogProductResponse>>,
            SearchPosProductsHandler>();
        services.AddScoped<IQueryHandler<ListPosCategoriesQuery, PosCategoriesResponse>, ListPosCategoriesHandler>();
        services.AddScoped<
            IQueryHandler<FindPosProductByCodeQuery, PosProductResponse>,
            FindPosProductByCodeHandler>();
        services.AddScoped<
            ICommandHandler<PreviewPosSaleCommand, PosPreviewResponse>,
            PreviewPosSaleHandler>();
        services.AddScoped<
            ICommandHandler<CreatePosSaleCommand, PosSaleCreation>,
            CreatePosSaleHandler>();
        services.AddScoped<
            IQueryHandler<GetPosSaleQuery, PosSaleResponse>,
            GetPosSaleHandler>();
        services.AddScoped<
            IQueryHandler<ListPosSalesQuery, PosPage<PosSaleListItemResponse>>,
            ListPosSalesHandler>();
        services.AddScoped<
            ICommandHandler<VoidPosSaleCommand, PosSaleResponse>,
            VoidPosSaleHandler>();
        services.AddScoped<
            IQueryHandler<ListCashSessionsQuery, PosPage<PosSessionSummaryResponse>>,
            ListCashSessionsHandler>();
        services.AddScoped<
            IQueryHandler<GetCashSessionQuery, PosSessionSummaryResponse>,
            GetCashSessionHandler>();
        services.AddScoped<
            IQueryHandler<SearchPosCustomersQuery, PosCustomerOptionsResponse>,
            SearchPosCustomersHandler>();
        services.AddScoped<
            ICommandHandler<CreatePosCustomerCommand, PosCustomerOptionResponse>,
            CreatePosCustomerHandler>();
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
            IQueryHandler<GetCustomerMergePreviewQuery, CustomerMergePreviewResponse>,
            GetCustomerMergePreviewHandler>();
        // Spec 2026-10-26 D-8: el estado y el historial de la autorización de datos.
        services.AddScoped<
            IQueryHandler<GetCustomerDataConsentQuery, CustomerDataConsentDto>,
            GetCustomerDataConsentHandler>();
        // Spec 2026-10-26 D-5 (medium) y D-9: la revocación de un miembro. El validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<
            ICommandHandler<RevokeDataConsentCommand, CustomerDataConsentDto>,
            RevokeDataConsentHandler>();
        services.AddScoped<
            ICommandHandler<MergeCustomersCommand, CustomerMergeResult>,
            MergeCustomersHandler>();
        // Spec 2026-11-10 (borrar clientes) §4.1: el borrado en lote.
        services.AddScoped<
            ICommandHandler<BulkDeleteCustomersCommand, BulkDeleteCustomersResult>,
            BulkDeleteCustomersHandler>();
        services.AddScoped<
            IQueryHandler<FindCustomerIdentificationMatchesQuery, CustomerIdentificationMatchesResponse>,
            FindCustomerIdentificationMatchesHandler>();
        services.AddScoped<
            IQueryHandler<ListCustomerAnomaliesQuery, CustomerAnomaliesResponse>,
            ListCustomerAnomaliesHandler>();
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
        // La libreta de perfiles de facturación (spec 2026-11-24 §3), gemela de la de direcciones.
        services.AddScoped<
            ICommandHandler<AddCustomerBillingProfileCommand, CustomerDto>,
            AddCustomerBillingProfileHandler>();
        services.AddScoped<
            ICommandHandler<UpdateCustomerBillingProfileCommand, CustomerDto>,
            UpdateCustomerBillingProfileHandler>();
        services.AddScoped<
            ICommandHandler<RemoveCustomerBillingProfileCommand, CustomerDto>,
            RemoveCustomerBillingProfileHandler>();
        services.AddScoped<
            ICommandHandler<MakeCustomerBillingProfilePrincipalCommand, CustomerDto>,
            MakeCustomerBillingProfilePrincipalHandler>();
        services.AddScoped<
            ICommandHandler<DeactivateCustomerCommand, CustomerDto>,
            DeactivateCustomerHandler>();
        services.AddScoped<
            ICommandHandler<ActivateCustomerCommand, CustomerDto>,
            ActivateCustomerHandler>();
        services.AddScoped<
            ICommandHandler<SetCustomerCompanyCommand, CustomerDto>,
            SetCustomerCompanyHandler>();
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
        // Spec 2026-10-11 §4: notas, etiquetas y consentimiento de marketing. Registro explícito, como todo el módulo.
        services.AddScoped<
            IQueryHandler<ListCustomerNotesQuery, IReadOnlyList<CustomerNoteDto>>,
            ListCustomerNotesHandler>();
        services.AddScoped<
            ICommandHandler<CreateCustomerNoteCommand, CustomerNoteDto>,
            CreateCustomerNoteHandler>();
        services.AddScoped<
            ICommandHandler<UpdateCustomerNoteCommand, CustomerNoteDto>,
            UpdateCustomerNoteHandler>();
        services.AddScoped<
            ICommandHandler<DeleteCustomerNoteCommand, CustomerNoteDeletedResult>,
            DeleteCustomerNoteHandler>();
        services.AddScoped<
            IQueryHandler<ListTagsQuery, IReadOnlyList<TagDto>>,
            ListTagsHandler>();
        services.AddScoped<
            ICommandHandler<CreateTagCommand, TagDto>,
            CreateTagHandler>();
        services.AddScoped<
            ICommandHandler<UpdateTagCommand, TagDto>,
            UpdateTagHandler>();
        services.AddScoped<
            ICommandHandler<DeleteTagCommand, TagDeletedResult>,
            DeleteTagHandler>();
        services.AddScoped<
            IQueryHandler<ListCustomerTagsQuery, IReadOnlyList<TagDto>>,
            ListCustomerTagsHandler>();
        services.AddScoped<
            ICommandHandler<SetCustomerTagsCommand, IReadOnlyList<TagDto>>,
            SetCustomerTagsHandler>();
        services.AddScoped<
            ICommandHandler<SetMarketingConsentCommand, MarketingConsentDto>,
            SetMarketingConsentHandler>();
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
            IQueryHandler<ListCountriesQuery, IReadOnlyList<CountryDto>>,
            ListCountriesHandler>();
        services.AddScoped<
            IQueryHandler<ListCitiesQuery, IReadOnlyList<CityDto>>,
            ListCitiesHandler>();
        // Spec 2026-11-20: regiones y ciudades por país (Colombia por DIVIPOLA, el resto por Region/RegionCity).
        services.AddScoped<
            IQueryHandler<ListRegionsQuery, IReadOnlyList<RegionOptionDto>>,
            ListRegionsHandler>();
        services.AddScoped<
            IQueryHandler<ListRegionCitiesQuery, IReadOnlyList<RegionCityOptionDto>>,
            ListRegionCitiesHandler>();
        // Spec 2026-10-26 §6: las lecturas de la consola de ciudades del operador. La política exige el permiso y el handler
        // revalida con OperatorAuthorization; el validador entra con AddValidatorsFromAssemblyContaining de abajo.
        services.AddScoped<IQueryHandler<GetCitySummaryQuery, CitySummaryDto>, GetCitySummaryHandler>();
        services.AddScoped<IQueryHandler<ListUnresolvedCitiesQuery, UnresolvedCityPageDto>, ListUnresolvedCitiesHandler>();
        services.AddScoped<IQueryHandler<GetUnresolvedCityQuery, UnresolvedCityDetailDto>, GetUnresolvedCityHandler>();
        // Confirmar, descartar y restaurar, y el impacto de confirmar (que no escribe).
        services.AddScoped<IQueryHandler<PreviewResolveImpactQuery, ResolveImpactDto>, PreviewResolveImpactHandler>();
        services.AddScoped<ICommandHandler<ResolveUnresolvedCityCommand, CitySynonymDto>, ResolveUnresolvedCityHandler>();
        services.AddScoped<UnresolvedCityTriage>();
        services.AddScoped<ICommandHandler<DiscardUnresolvedCityCommand, UnresolvedCityListItemDto>, DiscardUnresolvedCityHandler>();
        services.AddScoped<ICommandHandler<RestoreUnresolvedCityCommand, UnresolvedCityListItemDto>, RestoreUnresolvedCityHandler>();
        // Los sinónimos: la lista, el impacto de corregir (que no escribe), corregir, borrar y reintentar; y el recorrido de la
        // historia previa. Sin el registro de uno, su ruta responde 500.
        services.AddScoped<IQueryHandler<ListCitySynonymsQuery, CitySynonymPageDto>, ListCitySynonymsHandler>();
        services.AddScoped<IQueryHandler<PreviewSynonymCorrectionImpactQuery, CorrectImpactDto>, PreviewSynonymCorrectionImpactHandler>();
        services.AddScoped<ICommandHandler<CorrectCitySynonymCommand, CitySynonymDto>, CorrectCitySynonymHandler>();
        services.AddScoped<ICommandHandler<DeleteCitySynonymCommand, bool>, DeleteCitySynonymHandler>();
        services.AddScoped<ICommandHandler<RetryCitySynonymCorrectionsCommand, CitySynonymDto>, RetryCitySynonymCorrectionsHandler>();
        services.AddScoped<ICommandHandler<RunCityBackfillCommand, CityBackfillResultDto>, RunCityBackfillHandler>();
        // Quotations, fase 1 (borrador: crear, agregar/editar/quitar lineas, editar encabezado).
        // Los seis van aca por la misma razon que el resto: el dispatcher resuelve por registro
        // explicito, y un caso de uso que se olvide compila, mapea su endpoint y falla recien en
        // runtime con 500 al no encontrar handler.
        services.AddScoped<
            ICommandHandler<CreateQuotationCommand, QuotationDto>,
            CreateQuotationHandler>();
        // Lo comparten la creación y su cálculo previo sin id (spec 2026-10-18 §7.3).
        services.AddScoped<QuotationDraftBuilder>();
        services.AddScoped<
            IQueryHandler<GetQuotationQuery, QuotationDto>,
            GetQuotationHandler>();
        services.AddScoped<
            IQueryHandler<ListQuotationsQuery, QuotationPage>,
            ListQuotationsHandler>();
        // Comando y no query desde la exportación asíncrona: encola un job (spec 2026-09-12).
        services.AddScoped<
            ICommandHandler<ExportQuotationsCommand, ExportJobAccepted>,
            ExportQuotationsHandler>();
        services.AddScoped<
            IQueryHandler<ListQuotationHistoryQuery, IReadOnlyList<QuotationHistoryEntryDto>>,
            ListQuotationHistoryHandler>();
        // El log de la aplicacion. Registrados a mano igual que el resto: un caso de uso que se
        // olvide compila, mapea su endpoint y falla recien en runtime con 500.
        services.AddScoped<
            IQueryHandler<ListRequestFailuresQuery, RequestFailurePage>,
            ListRequestFailuresHandler>();
        services.AddScoped<
            ICommandHandler<PurgeRequestFailuresCommand, PurgeRequestFailuresResult>,
            PurgeRequestFailuresHandler>();

        services.AddScoped<
            ICommandHandler<ChangeQuotationClientCommand, QuotationDto>,
            ChangeQuotationClientHandler>();
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
            ICommandHandler<BatchUpdateQuotationItemsCommand, QuotationDto>,
            BatchUpdateQuotationItemsHandler>();
        // El guardado de una vez del editor y su cálculo previo. Los validadores no se registran
        // acá —los levanta el escaneo de ensamblado—, pero los handlers sí: el dispatcher resuelve
        // por registro explícito y uno que falte compila, mapea su endpoint y falla recién en
        // runtime con 500.
        services.AddScoped<
            ICommandHandler<SaveQuotationCommand, QuotationDto>,
            SaveQuotationHandler>();
        services.AddScoped<
            IQueryHandler<PreviewQuotationQuery, QuotationDto>,
            PreviewQuotationHandler>();
        // El cálculo de una cotización nueva sin id (spec 2026-10-18 §7): mismo armado que la creación.
        services.AddScoped<
            IQueryHandler<PreviewNewQuotationQuery, QuotationDto>,
            PreviewNewQuotationHandler>();
        services.AddScoped<
            ICommandHandler<SendQuotationCommand, QuotationDto>,
            SendQuotationHandler>();
        services.AddScoped<
            ICommandHandler<MarkQuotationSentCommand, QuotationDto>,
            MarkQuotationSentHandler>();
        services.AddScoped<
            ICommandHandler<VoidQuotationCommand, QuotationDto>,
            VoidQuotationHandler>();
        services.AddScoped<
            ICommandHandler<ExportQuotationPdfCommand, QuotationPdfExportDto>,
            ExportQuotationPdfHandler>();
        services.AddScoped<
            IQueryHandler<GetOrderQuery, OrderDetailDto>,
            GetOrderHandler>();
        services.AddScoped<
            IQueryHandler<PreviewQuotationConversionQuery, QuotationConversionPreviewDto>,
            PreviewQuotationConversionHandler>();
        services.AddScoped<
            IQueryHandler<ListOrdersQuery, OrderPage>,
            ListOrdersHandler>();
        services.AddScoped<
            ICommandHandler<ExportOrdersCommand, ExportJobAccepted>,
            ExportOrdersHandler>();
        services.AddScoped<
            IQueryHandler<GetOrderByIdQuery, OrderDetailDto>,
            GetOrderByIdHandler>();
        services.AddScoped<
            ICommandHandler<CancelOrderCommand, OrderDto>,
            CancelOrderHandler>();
        services.AddScoped<
            ICommandHandler<InvoiceOrderCommand, OrderDto>,
            InvoiceOrderHandler>();
        services.AddScoped<
            ICommandHandler<RevertOrderInvoicingCommand, OrderDto>,
            RevertOrderInvoicingHandler>();
        // Las acciones del pedido de spec 2026-11-10: pagado, preparado y archivado, con sus deshacer.
        services.AddScoped<ICommandHandler<MarkOrderPaidCommand, OrderDto>, MarkOrderPaidHandler>();
        services.AddScoped<ICommandHandler<UnmarkOrderPaidCommand, OrderDto>, UnmarkOrderPaidHandler>();
        services.AddScoped<ICommandHandler<PrepareOrderCommand, OrderDto>, PrepareOrderHandler>();
        // Mayorista fase 3 (D-1, D-2): aprobar el pedido de un distribuidor.
        services.AddScoped<ICommandHandler<ApproveWholesaleOrderCommand, OrderDto>, ApproveWholesaleOrderHandler>();
        services.AddScoped<ICommandHandler<UndoOrderPreparationCommand, OrderDto>, UndoOrderPreparationHandler>();
        services.AddScoped<ICommandHandler<ArchiveOrderCommand, OrderDto>, ArchiveOrderHandler>();
        services.AddScoped<ICommandHandler<UnarchiveOrderCommand, OrderDto>, UnarchiveOrderHandler>();
        // Spec 2026-11-15 §4: el asesor cambia o quita la cuenta de comprador del pedido de la tienda. El validador entra con
        // AddValidatorsFromAssemblyContaining del módulo.
        services.AddScoped<
            ICommandHandler<SetOrderBuyerAccountCommand, OrderDetailDto>,
            SetOrderBuyerAccountHandler>();
        services.AddScoped<
            ICommandHandler<RemoveOrderBuyerAccountCommand, OrderDetailDto>,
            RemoveOrderBuyerAccountHandler>();
        // «Enviar el pedido por WhatsApp»: el PDF del pedido con la plantilla propia del pedido.
        services.AddScoped<
            ICommandHandler<SendOrderCommand, OrderDetailDto>,
            SendOrderHandler>();
        // La fachada con la que Siigo manda la factura por WhatsApp, con las reglas de canal del envío del pedido.
        services.AddScoped<IInvoiceWhatsAppDelivery, InvoiceWhatsAppDelivery>();
        // «Descargar PDF» del pedido: el mismo documento del envío, como enlace firmado.
        services.AddScoped<
            ICommandHandler<ExportOrderPdfCommand, OrderPdfExportDto>,
            ExportOrderPdfHandler>();
        services.AddScoped<
            ICommandHandler<ConvertQuotationToOrderCommand, OrderDetailDto>,
            ConvertQuotationToOrderHandler>();
        services.AddScoped<
            ICommandHandler<AddOrderPaymentProofsCommand, OrderDto>,
            AddOrderPaymentProofsHandler>();
        services.AddScoped<
            ICommandHandler<AddOrderItemsCommand, OrderDetailDto>,
            AddOrderItemsHandler>();
        services.AddScoped<
            ICommandHandler<RemoveOrderPaymentProofCommand, OrderDto>,
            RemoveOrderPaymentProofHandler>();
        services.AddScoped<
            ICommandHandler<SaveOrderEditsCommand, OrderDetailDto>,
            SaveOrderEditsHandler>();
        services.AddScoped<
            IQueryHandler<PreviewOrderEditsQuery, OrderDetailDto>,
            PreviewOrderEditsHandler>();
        // «Reconocer este nombre» (spec de Shopify D-S25); el validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<
            ICommandHandler<RecognizeOrderCarrierCommand, OrderDetailDto>,
            RecognizeOrderCarrierHandler>();
        // Pedido manual y su cálculo previo (spec 2026-10-15 §5.2); el armador lo comparten con el
        // servicio de pedidos externos.
        services.AddScoped<OrderDraftBuilder>();
        services.AddScoped<
            ICommandHandler<CreateOrderCommand, OrderDetailDto>,
            CreateOrderHandler>();
        services.AddScoped<
            IQueryHandler<PreviewOrderQuery, OrderDetailDto>,
            PreviewOrderHandler>();
        // Pedidos desde integraciones (spec 2026-10-15 §5.4): el puerto que el adaptador de Shopify
        // consume; ningún consumidor referencia Modules.Quotations.Application.
        services.AddScoped<IExternalOrderWriter, ExternalOrderService>();
        // Spec 2026-11-18 §5.5: el pedido visto por un facturador externo (Siigo) y la pregunta «¿hay una factura externa abierta?» que
        // la marca manual hace antes de marcar. Quotations declara los dos puertos; el segundo lo implementa Bootstrapper sobre siigo.invoices.
        services.AddScoped<IExternalOrderInvoicing, ExternalOrderInvoicingService>();
        services.AddScoped<IExternalInvoiceStatus, SiigoOpenInvoiceCheck>();
        // Spec 2026-10-29 §8.3: el borrador de cotización sin sesión que pide un agente IA (create_quotation_draft); Ai lo
        // consume por un adaptador (AiQuotationDrafts).
        services.AddScoped<IExternalQuotationWriter, ExternalQuotationService>();
        // El puerto de la tienda pública hacia el motor de pedidos (spec de la tienda §5): cotiza el carrito y crea el
        // pedido al «Tomar». Store lo consume por un adaptador; no referencia Modules.Quotations.Application.
        services.AddScoped<IStoreOrderGateway, StoreOrderGateway>();
        // Mayorista fase 3: el pedido de la orden de compra vinculada (lo usa el adaptador de Compras) y el precio de lista del
        // distribuidor para la línea que el mayorista agrega al editar (D-5).
        services.AddScoped<IWholesaleOrderGateway, WholesaleOrderGateway>();
        services.AddScoped<IWholesaleLinePricing, QuotationsWholesaleLinePricing>();
        // El alta los corre en el orden en que se registran. El de roles va primero (spec 2026-11-25): un fallo de Quotations sube,
        // y no puede dejar al tenant sin sus roles.
        services.AddScoped<Modules.Tenancy.Application.INewTenantProvisioning, TenantRolesProvisioning>();
        services.AddScoped<Modules.Tenancy.Application.INewTenantProvisioning, QuotationsNewTenantProvisioning>();
        // Los pedidos del comprador de la tienda (spec 2026-11-09): listar, leer, anular y enlazar.
        services.AddScoped<IBuyerOrderGateway, BuyerOrderGateway>();
        // La limpieza de cotizaciones al borrar variantes (spec 2026-11-08): el puerto que consume un adaptador de
        // Bootstrapper dentro de la transacción de Catalog; nadie más referencia Modules.Quotations.Application.
        services.AddScoped<IQuotationVariantDeletion, QuotationVariantDeletion>();
        // La homologación de columnas del Excel de pedidos (spec 2026-09-24). A mano, como el
        // resto: un handler que falte compila, mapea su endpoint y falla recién en runtime con 500.
        services.AddScoped<
            IQueryHandler<GetOrdersExportLayoutQuery, OrdersExportLayoutDto>,
            GetOrdersExportLayoutHandler>();
        services.AddScoped<
            ICommandHandler<UpdateOrdersExportLayoutCommand, OrdersExportLayoutDto>,
            UpdateOrdersExportLayoutHandler>();
        // The tenant's minimum purchase (spec 2026-10-08, D7), registered by hand like the rest.
        services.AddScoped<
            IQueryHandler<GetQuotationSettingsQuery, QuotationSettingsDto>,
            GetQuotationSettingsHandler>();
        services.AddScoped<
            ICommandHandler<UpdateQuotationSettingsCommand, QuotationSettingsDto>,
            UpdateQuotationSettingsHandler>();
        // El contenido configurable del PDF (spec 2026-11-16 §4.2); el validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<IQueryHandler<GetPdfContentQuery, PdfContentDto>, GetPdfContentHandler>();
        services.AddScoped<ICommandHandler<UpdatePdfContentCommand, PdfContentDto>, UpdatePdfContentHandler>();
        // Los ajustes del pedido por tenant (spec 2026-11-10 §6); el validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<
            IQueryHandler<GetOrderSettingsQuery, OrderSettingsDto>,
            GetOrderSettingsHandler>();
        services.AddScoped<
            ICommandHandler<UpdateOrderSettingsCommand, OrderSettingsDto>,
            UpdateOrderSettingsHandler>();
        // Los canales de WhatsApp del tenant y su elección desde Configuración (spec 2026-10-21 §4.4, §5.2).
        services.AddScoped<
            IQueryHandler<GetWhatsAppChannelsQuery, WhatsAppChannelsDto>,
            GetWhatsAppChannelsHandler>();
        services.AddScoped<
            ICommandHandler<ChooseWhatsAppChannelCommand, WhatsAppChannelsDto>,
            ChooseWhatsAppChannelHandler>();
        // Transportadoras y el IVA del envío (spec 2026-10-17 §4.3), a mano como el resto.
        services.AddScoped<IQueryHandler<ListCarriersQuery, CarrierSettingsDto>, ListCarriersHandler>();
        services.AddScoped<ICommandHandler<CreateCarrierCommand, CarrierDto>, CreateCarrierHandler>();
        services.AddScoped<ICommandHandler<UpdateCarrierCommand, CarrierDto>, UpdateCarrierHandler>();
        services.AddScoped<ICommandHandler<SetCarrierActiveCommand, CarrierDto>, SetCarrierActiveHandler>();
        services.AddScoped<ICommandHandler<ReorderCarriersCommand, CarrierSettingsDto>, ReorderCarriersHandler>();
        services.AddScoped<ICommandHandler<SetFreightTaxRateCommand, CarrierSettingsDto>, SetFreightTaxRateHandler>();
        services.AddScoped<ICommandHandler<SetFreightChargeModesCommand, CarrierSettingsDto>, SetFreightChargeModesHandler>();
        services.AddScoped<IQueryHandler<GetShippingOptionsQuery, ShippingOptionsDto>, GetShippingOptionsHandler>();
        // Reporting. Los diez van aca por la misma razon que el resto: el dispatcher resuelve por
        // registro explicito, y un caso de uso que se olvide compila, mapea su endpoint y falla
        // recien en runtime con 500 al no encontrar handler.
        services.AddScoped<
            IQueryHandler<ListOrdersReportQuery, ReportPage<OrdersReportItemDto>>,
            ListOrdersReportHandler>();
        services.AddScoped<
            IQueryHandler<GetOrdersReportSummaryQuery, OrdersReportSummaryDto>,
            GetOrdersReportSummaryHandler>();
        services.AddScoped<
            IQueryHandler<GetQuotationsReportSummaryQuery, QuotationsReportSummaryDto>,
            GetQuotationsReportSummaryHandler>();
        services.AddScoped<
            IQueryHandler<ListQuotationsReportQuery, ReportPage<QuotationsReportItemDto>>,
            ListQuotationsReportHandler>();
        services.AddScoped<
            IQueryHandler<ListPriceChangeReportQuery, ReportPage<PriceChangeReportItemDto>>,
            ListPriceChangeReportHandler>();
        services.AddScoped<
            IQueryHandler<GetPriceChangeReportSummaryQuery, PriceChangeReportSummaryDto>,
            GetPriceChangeReportSummaryHandler>();
        services.AddScoped<
            IQueryHandler<ListCustomerReportQuery, ReportPage<CustomerReportItemDto>>,
            ListCustomerReportHandler>();
        services.AddScoped<
            IQueryHandler<GetCustomerReportSummaryQuery, CustomerReportSummaryDto>,
            GetCustomerReportSummaryHandler>();
        services.AddScoped<
            IQueryHandler<ListMarginReportQuery, ReportPage<MarginReportProductDto>>,
            ListMarginReportHandler>();
        services.AddScoped<
            IQueryHandler<GetMarginReportSummaryQuery, MarginReportSummaryDto>,
            GetMarginReportSummaryHandler>();
        // Integrations (spec 2026-10-08). A mano, como el resto: un handler que falte compila, mapea
        // su endpoint y falla recién en runtime con 500.
        services.AddScoped<
            IQueryHandler<GetIntegrationsCatalogQuery, IntegrationsCatalogResponse>,
            GetIntegrationsCatalogHandler>();
        services.AddScoped<
            IQueryHandler<ListConnectionsQuery, ConnectionsResponse>,
            ListConnectionsHandler>();
        services.AddScoped<
            IQueryHandler<GetConnectionQuery, ConnectionResponse>,
            GetConnectionHandler>();
        services.AddScoped<
            ICommandHandler<CreateConnectionCommand, ConnectionResponse>,
            CreateConnectionHandler>();
        services.AddScoped<
            ICommandHandler<UpdateConnectionCommand, ConnectionResponse>,
            UpdateConnectionHandler>();
        services.AddScoped<
            ICommandHandler<TestConnectionCommand, ConnectionResponse>,
            TestConnectionHandler>();
        services.AddScoped<
            ICommandHandler<PauseConnectionCommand, ConnectionResponse>,
            PauseConnectionHandler>();
        services.AddScoped<
            ICommandHandler<ResumeConnectionCommand, ConnectionResponse>,
            ResumeConnectionHandler>();
        services.AddScoped<
            ICommandHandler<DeleteConnectionCommand, bool>,
            DeleteConnectionHandler>();
        services.AddScoped<
            ICommandHandler<CompleteWhatsAppSignupCommand, ConnectionResponse>,
            CompleteWhatsAppSignupHandler>();
        // Integrations (spec 2026-10-14 §8.1): conectar Instagram con el code de Business Login. El validador entra con
        // AddValidatorsFromAssemblyContaining del módulo, más abajo.
        services.AddScoped<
            ICommandHandler<ConnectInstagramCommand, ConnectionResponse>,
            ConnectInstagramHandler>();
        // Integrations (spec 2026-10-15 §4.3): los dos inicios de la instalación de Shopify. Los validadores entran con
        // AddValidatorsFromAssemblyContaining del módulo, más abajo.
        services.AddScoped<
            ICommandHandler<StartShopifyInstallForTenantCommand, ShopifyAuthorizeResponse>,
            StartShopifyInstallForTenantHandler>();
        services.AddScoped<
            ICommandHandler<StartShopifyInstallCommand, ShopifyAuthorizeResponse>,
            StartShopifyInstallHandler>();
        // El canje del code y la lectura de la instalación (§4.3 pasos 4 y 5).
        services.AddScoped<
            ICommandHandler<CompleteShopifyInstallCommand, ShopifyInstallCompletedResponse>,
            CompleteShopifyInstallHandler>();
        services.AddScoped<
            IQueryHandler<GetShopifyInstallQuery, ShopifyInstallResponse>,
            GetShopifyInstallHandler>();
        // El enlace de la tienda al tenant elegido (§4.3 paso 6).
        services.AddScoped<
            ICommandHandler<LinkShopifyStoreCommand, ConnectionResponse>,
            LinkShopifyStoreHandler>();
        // Messaging (spec 2026-10-09): el webhook de Meta, anónimo y firmado.
        services.AddScoped<IQueryHandler<VerifyWebhookQuery, string?>, VerifyWebhookHandler>();
        services.AddScoped<ICommandHandler<ReceiveWebhookCommand, bool>, ReceiveWebhookHandler>();
        // Messaging (spec 2026-10-09 §8.7): lista, detalle e hilo. Cada handler a mano: sin registro, 500.
        services.AddValidatorsFromAssemblyContaining<ListConversationsValidator>();
        services.AddScoped<IQueryHandler<ListConversationsQuery, ConversationPageDto>, ListConversationsHandler>();
        services.AddScoped<IQueryHandler<GetConversationQuery, ConversationSummary>, GetConversationHandler>();
        services.AddScoped<IQueryHandler<GetConversationAutomationQuery, ConversationAutomationDetailsDto>, GetConversationAutomationHandler>();
        services.AddScoped<IQueryHandler<ListMessagesQuery, MessagePageDto>, ListMessagesHandler>();
        // API externa de Messaging (spec 2026-10-30 §5.2): las dos lecturas y el envío de la clave de API.
        services.AddScoped<IQueryHandler<ListExternalConnectionsQuery, IReadOnlyList<ExternalConnectionDto>>, ListExternalConnectionsHandler>();
        services.AddScoped<IQueryHandler<ListExternalTemplatesQuery, IReadOnlyList<ExternalTemplateDto>>, ListExternalTemplatesHandler>();
        services.AddScoped<IQueryHandler<GetExternalMessageQuery, ExternalMessageDto>, GetExternalMessageHandler>();
        services.AddScoped<ICommandHandler<SendExternalTemplateCommand, ExternalMessageResult>, SendExternalTemplateHandler>();
        // Messaging (spec 2026-10-09 §8.8): búsqueda en el historial.
        services.AddScoped<IQueryHandler<SearchMessagesQuery, SearchPageDto>, SearchMessagesHandler>();
        // Messaging (spec 2026-10-09 §8.3): envío de texto idempotente por clientId.
        services.AddScoped<ICommandHandler<SendMessageCommand, MessageDto>, SendMessageHandler>();
        // Messaging (spec 2026-10-11 §6): la nota de voz, con las mismas guardas que el texto.
        services.AddScoped<ICommandHandler<SendAudioMessageCommand, MessageDto>, SendAudioMessageHandler>();
        // Messaging-media-reply R1: la sesión de subida del adjunto del asesor (imagen o documento).
        services.AddScoped<ICommandHandler<CreateMediaUploadSessionCommand, MediaUploadSessionDto>, CreateMediaUploadSessionHandler>();
        // Messaging-media-reply R3: la confirmación del adjunto. El validador entra con AddValidatorsFromAssemblyContaining de arriba.
        services.AddScoped<ICommandHandler<SendMediaMessageCommand, MessageDto>, SendMediaMessageHandler>();
        // Messaging (spec 2026-10-11 §5): plantillas. Conexiones y catálogo para leer; enviar una plantilla y abrir una conversación con ella.
        services.AddScoped<IQueryHandler<ListMessagingConnectionsQuery, IReadOnlyList<MessagingConnectionDto>>, ListMessagingConnectionsHandler>();
        services.AddScoped<IQueryHandler<ListTemplatesQuery, IReadOnlyList<MessageTemplateDto>>, ListTemplatesHandler>();
        services.AddScoped<ICommandHandler<SendTemplateMessageCommand, MessageDto>, SendTemplateMessageHandler>();
        services.AddScoped<ICommandHandler<StartConversationCommand, StartConversationResult>, StartConversationHandler>();
        // Spec 2026-11-17: abrir la conversación del cliente sin enviar. El validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<ICommandHandler<OpenCustomerConversationCommand, OpenCustomerConversationResult>, OpenCustomerConversationHandler>();
        // Messaging (spec 2026-10-11 §6.2): campañas. Cada handler a mano: sin registro, 500.
        services.AddScoped<IQueryHandler<ListCampaignsQuery, CampaignPageDto<CampaignSummaryDto>>, ListCampaignsHandler>();
        services.AddScoped<IQueryHandler<GetCampaignQuery, CampaignDetailDto>, GetCampaignHandler>();
        services.AddScoped<IQueryHandler<ListCampaignRecipientsQuery, CampaignPageDto<CampaignRecipientDto>>, ListCampaignRecipientsHandler>();
        services.AddScoped<IQueryHandler<PreviewAudienceQuery, AudiencePreviewDto>, PreviewAudienceHandler>();
        services.AddScoped<ICommandHandler<CreateCampaignCommand, CampaignDetailDto>, CreateCampaignHandler>();
        services.AddScoped<ICommandHandler<UpdateCampaignCommand, CampaignDetailDto>, UpdateCampaignHandler>();
        services.AddScoped<ICommandHandler<DeleteCampaignCommand, bool>, DeleteCampaignHandler>();
        services.AddScoped<ICommandHandler<LaunchCampaignCommand, CampaignDetailDto>, LaunchCampaignHandler>();
        services.AddScoped<ICommandHandler<CancelCampaignCommand, CampaignDetailDto>, CancelCampaignHandler>();
        services.AddScoped<ICommandHandler<RetryCampaignCommand, CampaignDetailDto>, RetryCampaignHandler>();
        // Messaging (spec 2026-11-09): el mensaje programado de una conversación. Cada handler a mano: sin registro, 500 (lo cubre
        // ScheduledMessageApiTests.EveryRouteIsRegisteredAndAnswersItsStatus). Los validadores entran con AddValidatorsFromAssemblyContaining
        // de arriba. Los cinco de gestión arman a mano su ScheduledMessageManager (es internal de Application): no se registra.
        services.AddScoped<ScheduledMessageMapper>();
        services.AddScoped<IQueryHandler<GetScheduledMessageQuery, ScheduledMessageEnvelopeDto>, GetScheduledMessageHandler>();
        services.AddScoped<IQueryHandler<GetScheduledMessageMediaQuery, MediaStreamDto?>, GetScheduledMessageMediaHandler>();
        services.AddScoped<ICommandHandler<CreateScheduledMessageCommand, ScheduleMessageResult>, CreateScheduledMessageHandler>();
        services.AddScoped<ICommandHandler<CreateScheduledVoiceNoteCommand, ScheduleMessageResult>, CreateScheduledVoiceNoteHandler>();
        services.AddScoped<ICommandHandler<UpdateScheduledMessageCommand, ScheduledMessageDto>, UpdateScheduledMessageHandler>();
        services.AddScoped<ICommandHandler<CancelScheduledMessageCommand, bool>, CancelScheduledMessageHandler>();
        services.AddScoped<ICommandHandler<SendScheduledMessageNowCommand, ScheduledMessageDto>, SendScheduledMessageNowHandler>();
        services.AddScoped<ICommandHandler<RetryScheduledMessageCommand, ScheduledMessageDto>, RetryScheduledMessageHandler>();
        services.AddScoped<ICommandHandler<DiscardScheduledMessageCommand, bool>, DiscardScheduledMessageHandler>();
        // Messaging (spec 2026-10-12 §3.3): respuestas rápidas. Cada handler a mano: sin registro, 500.
        services.AddScoped<IQueryHandler<ListQuickRepliesQuery, QuickReplyListDto>, ListQuickRepliesHandler>();
        services.AddScoped<ICommandHandler<CreateQuickReplyCommand, QuickReplyDto>, CreateQuickReplyHandler>();
        services.AddScoped<ICommandHandler<UpdateQuickReplyCommand, QuickReplyDto>, UpdateQuickReplyHandler>();
        services.AddScoped<ICommandHandler<DeleteQuickReplyCommand, bool>, DeleteQuickReplyHandler>();
        // Spec 2026-10-29: el orden personal; el validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<ICommandHandler<SaveQuickReplyOrderCommand, QuickReplyListDto>, SaveQuickReplyOrderHandler>();
        // Spec 2026-10-15 §3.4: el catálogo de motivos de resolución. Sin estos registros, 500.
        services.AddScoped<IQueryHandler<ListResolutionReasonsQuery, IReadOnlyList<ResolutionReasonDto>>, ListResolutionReasonsHandler>();
        services.AddScoped<ICommandHandler<RenameResolutionReasonCommand, ResolutionReasonDto>, RenameResolutionReasonHandler>();
        services.AddScoped<ICommandHandler<ArchiveResolutionReasonCommand, bool>, ArchiveResolutionReasonHandler>();
        // Spec 2026-10-21: el catálogo de horarios de atención. Sin registrar el handler, la ruta responde 500.
        services.AddScoped<IQueryHandler<ListBusinessHoursQuery, BusinessHoursListDto>, ListBusinessHoursHandler>();
        // Spec 2026-10-28: reporte de mensajería; el validador entra con AddValidatorsFromAssemblyContaining de arriba.
        services.AddScoped<IQueryHandler<GetMessagingReportSummaryQuery, MessagingReportSummaryDto>, GetMessagingReportSummaryHandler>();
        services.AddScoped<ICommandHandler<CreateBusinessHoursCommand, BusinessHoursDto>, CreateBusinessHoursHandler>();
        services.AddScoped<ICommandHandler<UpdateBusinessHoursCommand, BusinessHoursDto>, UpdateBusinessHoursHandler>();
        services.AddScoped<ICommandHandler<DeleteBusinessHoursCommand, bool>, DeleteBusinessHoursHandler>();
        services.AddScoped<ICommandHandler<DuplicateBusinessHoursCommand, BusinessHoursDto>, DuplicateBusinessHoursHandler>();
        services.AddScoped<ICommandHandler<SetDefaultBusinessHoursCommand, BusinessHoursDto>, SetDefaultBusinessHoursHandler>();
        // Spec 2026-10-29 §5: estado y reintento de la importación del historial por coexistencia. Sin estos registros, 500.
        services.AddScoped<IQueryHandler<GetHistorySyncQuery, HistorySyncDto>, GetHistorySyncHandler>();
        services.AddScoped<ICommandHandler<RetryHistorySyncCommand, HistorySyncDto>, RetryHistorySyncHandler>();
        // Messaging (spec 2026-10-14 §4.3): la pantalla de plantillas de una conexión y su refresco contra Meta.
        services.AddScoped<IQueryHandler<GetTemplateManagementQuery, TemplateManagementDto>, GetTemplateManagementHandler>();
        services.AddScoped<ICommandHandler<SyncTemplatesCommand, TemplateManagementDto>, SyncTemplatesHandler>();
        // Messaging (spec 2026-10-14 §4.3): crear una plantilla libre y mandarla a aprobación.
        services.AddScoped<ICommandHandler<CreateTemplateCommand, ManagedTemplateDto>, CreateTemplateHandler>();
        // Spec 2026-11-17: la sesión de subida de la imagen del encabezado; el validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<ICommandHandler<CreateTemplateHeaderImageSessionCommand, TemplateHeaderImageSessionDto>, CreateTemplateHeaderImageSessionHandler>();
        // Messaging (spec 2026-10-14 §4.3): aprovisionar, adoptar o reintentar la plantilla de cotización de una conexión.
        services.AddScoped<ICommandHandler<ProvisionQuotationTemplateCommand, ManagedTemplateDto>, ProvisionQuotationTemplateHandler>();
        services.AddScoped<ICommandHandler<ProvisionBillingTemplatesCommand, IReadOnlyList<ManagedTemplateDto>>, ProvisionBillingTemplatesHandler>();
        services.AddScoped<IQueryHandler<GetQuotationTemplateDraftQuery, SystemTemplateDraftDto>, GetQuotationTemplateDraftHandler>();
        services.AddScoped<IQueryHandler<GetBillingTemplatesDraftQuery, BillingTemplatesDraftDto>, GetBillingTemplatesDraftHandler>();
        // Messaging (spec 2026-11-09 D3): la plantilla de pedido, calcada de la de cotización; el validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<ICommandHandler<ProvisionOrderTemplateCommand, ManagedTemplateDto>, ProvisionOrderTemplateHandler>();
        services.AddScoped<IQueryHandler<GetOrderTemplateDraftQuery, SystemTemplateDraftDto>, GetOrderTemplateDraftHandler>();
        services.AddScoped<ICommandHandler<ProvisionInvoiceTemplateCommand, ManagedTemplateDto>, ProvisionInvoiceTemplateHandler>();
        services.AddScoped<IQueryHandler<GetInvoiceTemplateDraftQuery, SystemTemplateDraftDto>, GetInvoiceTemplateDraftHandler>();
        // Soporte fase 2: la plantilla «Aviso de respuesta», calcada de la de pedido sin encabezado.
        services.AddScoped<ICommandHandler<ProvisionSupportReplyTemplateCommand, ManagedTemplateDto>, ProvisionSupportReplyTemplateHandler>();
        services.AddScoped<IQueryHandler<GetSupportReplyTemplateDraftQuery, SystemTemplateDraftDto>, GetSupportReplyTemplateDraftHandler>();
        // Soporte fase 4b: la plantilla «Respuesta formal», calcada de la de pedido con el PDF de encabezado.
        services.AddScoped<ICommandHandler<ProvisionSupportFormalReplyTemplateCommand, ManagedTemplateDto>, ProvisionSupportFormalReplyTemplateHandler>();
        services.AddScoped<IQueryHandler<GetSupportFormalReplyTemplateDraftQuery, SystemTemplateDraftDto>, GetSupportFormalReplyTemplateDraftHandler>();
        // Soporte fase 4b: la plantilla de la encuesta, con los tres botones fijos.
        services.AddScoped<ICommandHandler<ProvisionSupportSurveyTemplateCommand, ManagedTemplateDto>, ProvisionSupportSurveyTemplateHandler>();
        services.AddScoped<IQueryHandler<GetSupportSurveyTemplateDraftQuery, SystemTemplateDraftDto>, GetSupportSurveyTemplateDraftHandler>();
        // Mayorista: la plantilla del aviso de invitación de distribuidor, calcada de la «Aviso de respuesta».
        services.AddScoped<ICommandHandler<ProvisionWholesaleInvitationNoticeTemplateCommand, ManagedTemplateDto>, ProvisionWholesaleInvitationNoticeTemplateHandler>();
        services.AddScoped<IQueryHandler<GetWholesaleInvitationNoticeTemplateDraftQuery, SystemTemplateDraftDto>, GetWholesaleInvitationNoticeTemplateDraftHandler>();
        // Support task notices: the assignment notice template, sibling of the wholesale invitation notice.
        services.AddScoped<ICommandHandler<ProvisionTaskNoticeTemplateCommand, ManagedTemplateDto>, ProvisionTaskNoticeTemplateHandler>();
        services.AddScoped<IQueryHandler<GetTaskNoticeTemplateDraftQuery, SystemTemplateDraftDto>, GetTaskNoticeTemplateDraftHandler>();
        services.AddScoped<ICommandHandler<ProvisionHrBirthdayNoticeTemplateCommand, ManagedTemplateDto>, ProvisionHrBirthdayNoticeTemplateHandler>();
        services.AddScoped<IQueryHandler<GetHrBirthdayNoticeTemplateDraftQuery, SystemTemplateDraftDto>, GetHrBirthdayNoticeTemplateDraftHandler>();
        // Agenda: la plantilla del recordatorio de cita, calcada de la del aviso de Mayorista.
        services.AddScoped<ICommandHandler<ProvisionAppointmentReminderTemplateCommand, ManagedTemplateDto>, ProvisionAppointmentReminderTemplateHandler>();
        services.AddScoped<IQueryHandler<GetAppointmentReminderTemplateDraftQuery, SystemTemplateDraftDto>, GetAppointmentReminderTemplateDraftHandler>();
        // Messaging (spec 2026-10-14 §4.3): borrar una plantilla en Meta y en el espejo.
        services.AddScoped<ICommandHandler<DeleteTemplateCommand, bool>, DeleteTemplateHandler>();
        // Spec 2026-11-20: cambiar la imagen del encabezado sin pasar por Meta. El validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<ICommandHandler<ReplaceTemplateHeaderImageCommand, ManagedTemplateDto>, ReplaceTemplateHeaderImageHandler>();
        // Messaging (spec 2026-10-09 §8.4–§8.5): marcar leído, resolver y reabrir.
        services.AddScoped<ReadReceipts>();
        services.AddScoped<ICommandHandler<MarkConversationReadCommand, bool>, MarkConversationReadHandler>();
        // El indicador «escribiendo» del asesor hacia WhatsApp e Instagram.
        services.AddScoped<ICommandHandler<SendTypingIndicatorCommand, bool>, SendTypingIndicatorHandler>();
        // Spec 2026-10-14: marcar no leída, espejo del read.
        services.AddScoped<ICommandHandler<MarkConversationUnreadCommand, bool>, MarkConversationUnreadHandler>();
        services.AddScoped<ICommandHandler<MarkUnreadFromMessageCommand, bool>, MarkUnreadFromMessageHandler>();
        services.AddScoped<ICommandHandler<ResolveConversationCommand, ConversationSummary>, ResolveConversationHandler>();
        services.AddScoped<ICommandHandler<ReopenConversationCommand, ConversationSummary>, ReopenConversationHandler>();
        // Messaging (spec 2026-10-10 §8.4): tomar, transferir y liberar. Cada handler a mano: sin registro, 500.
        services.AddScoped<ConversationAssignmentSteps>();
        services.AddScoped<ICommandHandler<TakeConversationCommand, ConversationSummary>, TakeConversationHandler>();
        services.AddScoped<ICommandHandler<TransferConversationCommand, ConversationSummary>, TransferConversationHandler>();
        services.AddScoped<ICommandHandler<ReleaseConversationCommand, ConversationSummary>, ReleaseConversationHandler>();
        // Spec 2026-10-24 §4.2: cambiar el cliente principal de la conversación.
        services.AddScoped<ICommandHandler<ChangeConversationCustomerCommand, ConversationSummary>, ChangeConversationCustomerHandler>();
        // Spec 2026-10-21: pausar y devolver el asistente. Sin registro, 500.
        services.AddScoped<ICommandHandler<PauseAutomationCommand, ConversationSummary>, PauseAutomationHandler>();
        services.AddScoped<ICommandHandler<ResumeAutomationCommand, ConversationSummary>, ResumeAutomationHandler>();
        // Spec 2026-10-31: iniciar el asistente desde el hilo. Sin registro, 500.
        services.AddScoped<ICommandHandler<StartAutomationCommand, ConversationSummary>, StartAutomationHandler>();
        services.AddScoped<ICommandHandler<ContinueAutomationCommand, ConversationSummary>, ContinueAutomationHandler>();
        // Spec 2026-10-26 §3.2: bloquear y desbloquear el contacto en Meta. Sin registro, 500.
        services.AddScoped<ICommandHandler<BlockContactCommand, ConversationSummary>, BlockContactHandler>();
        services.AddScoped<ICommandHandler<UnblockContactCommand, ConversationSummary>, UnblockContactHandler>();
        // Messaging (spec 2026-10-09 §8.6): servir el medio ya copiado con la sesión de QEP.
        services.AddScoped<IQueryHandler<GetMediaQuery, MediaStreamDto?>, GetMediaHandler>();
        // Messaging (spec 2026-10-10 §5.1, D-A1): el selector de transferir.
        services.AddScoped<IQueryHandler<ListAssigneesQuery, AssigneesDto>, ListAssigneesHandler>();
        // Pipeline (spec 2026-10-13 §6): ajustes. Cada handler a mano: sin registro, 500 (CompositionRootTests).
        services.AddValidatorsFromAssemblyContaining<ReplacePipelineStagesValidator>();
        services.AddScoped<IQueryHandler<GetPipelineStagesQuery, PipelineStagesDto>, GetPipelineStagesHandler>();
        services.AddScoped<ICommandHandler<ReplacePipelineStagesCommand, PipelineStagesDto>, ReplacePipelineStagesHandler>();
        services.AddScoped<IQueryHandler<GetPipelineReasonsQuery, ReasonListDto>, GetPipelineReasonsHandler>();
        services.AddScoped<ICommandHandler<ReplacePipelineReasonsCommand, ReasonListDto>, ReplacePipelineReasonsHandler>();
        // Pipeline (spec 2026-10-13 §6): negocios.
        services.AddScoped<IQueryHandler<GetDealQuery, DealDetailDto>, GetDealHandler>();
        services.AddScoped<ICommandHandler<CreateDealCommand, DealDetailDto>, CreateDealHandler>();
        services.AddScoped<ICommandHandler<UpdateDealCommand, DealDetailDto>, UpdateDealHandler>();
        services.AddScoped<ICommandHandler<MoveDealCommand, DealDetailDto>, MoveDealHandler>();
        services.AddScoped<ICommandHandler<WinDealCommand, DealDetailDto>, WinDealHandler>();
        services.AddScoped<ICommandHandler<LoseDealCommand, DealDetailDto>, LoseDealHandler>();
        services.AddScoped<ICommandHandler<ReopenDealCommand, DealDetailDto>, ReopenDealHandler>();
        services.AddScoped<ICommandHandler<LinkDealQuotationCommand, DealDetailDto>, LinkDealQuotationHandler>();
        services.AddScoped<ICommandHandler<LinkDealOrderCommand, DealDetailDto>, LinkDealOrderHandler>();
        services.AddScoped<ICommandHandler<SetPrimaryQuotationCommand, DealDetailDto>, SetPrimaryQuotationHandler>();
        // Pipeline (D21 del spec del frontend): los posibles dueños de un negocio, para el selector del formulario.
        services.AddScoped<IQueryHandler<GetDealOwnersQuery, IReadOnlyList<DealOwnerDto>>, GetDealOwnersHandler>();
        // Pipeline (spec 2026-10-13 §6): el tablero.
        services.AddScoped<IQueryHandler<GetBoardQuery, PipelineBoardDto>, GetBoardHandler>();
        // Pipeline (spec 2026-10-13 §5.1): la decisión de triage.
        services.AddScoped<ICommandHandler<DecideTriageCommand, TriageResultDto>, DecideTriageHandler>();
        // Pipeline (spec 2026-10-13 D36): el resumen de negocios de la ficha del cliente.
        services.AddScoped<IQueryHandler<GetCustomerDealsSummaryQuery, CustomerDealsSummaryDto>, GetCustomerDealsSummaryHandler>();
        // Automations (spec 2026-10-21 §4.4): lista, creación, lectura, edición, borrador, validación, publicación, pausa, reanudación,
        // duplicado, borrado, exportar e importar el chatbot. A mano, como el resto: sin registro, 500 (CompositionRootTests).
        services.AddValidatorsFromAssemblyContaining<CreateChatbotValidator>();
        services.AddScoped<IQueryHandler<ListChatbotsQuery, ChatbotListResponse>, ListChatbotsHandler>();
        services.AddScoped<IQueryHandler<GetChatbotQuery, ChatbotEditorDto>, GetChatbotHandler>();
        services.AddScoped<IQueryHandler<ListSelectableConnectionsQuery, IReadOnlyList<SelectableConnectionDto>>, ListSelectableConnectionsHandler>();
        services.AddScoped<IQueryHandler<ListExternalApisQuery, IReadOnlyList<ExternalApiRefDto>>, ListExternalApisHandler>();
        services.AddScoped<IQueryHandler<ListRunsQuery, IReadOnlyList<RunDto>>, ListRunsHandler>();
        services.AddScoped<ICommandHandler<CreateChatbotCommand, ChatbotEditorDto>, CreateChatbotHandler>();
        services.AddScoped<ICommandHandler<UpdateChatbotCommand, ChatbotEditorDto>, UpdateChatbotHandler>();
        services.AddScoped<ICommandHandler<SaveChatbotDraftCommand, DraftSavedDto>, SaveChatbotDraftHandler>();
        services.AddScoped<IQueryHandler<ValidateChatbotQuery, IReadOnlyList<FlowIssueDto>>, ValidateChatbotHandler>();
        services.AddScoped<ICommandHandler<PublishChatbotCommand, ChatbotEditorDto>, PublishChatbotHandler>();
        services.AddScoped<ICommandHandler<PauseChatbotCommand, ChatbotEditorDto>, PauseChatbotHandler>();
        services.AddScoped<ICommandHandler<ResumeChatbotCommand, ChatbotEditorDto>, ResumeChatbotHandler>();
        services.AddScoped<IQueryHandler<ListChatbotVersionsQuery, ChatbotVersionListResponse>, ListChatbotVersionsHandler>();
        services.AddScoped<ICommandHandler<RestoreChatbotVersionCommand, ChatbotEditorDto>, RestoreChatbotVersionHandler>();
        services.AddScoped<ICommandHandler<DuplicateChatbotCommand, ChatbotEditorDto>, DuplicateChatbotHandler>();
        services.AddScoped<ICommandHandler<DeleteChatbotCommand, bool>, DeleteChatbotHandler>();
        services.AddScoped<IQueryHandler<ExportChatbotQuery, ChatbotExportDto>, ExportChatbotHandler>();
        services.AddScoped<ICommandHandler<ImportChatbotCommand, ChatbotEditorDto>, ImportChatbotHandler>();
        // Automations (spec 2026-11-18): subir y completar el archivo de un bloque «Mensaje».
        services.AddScoped<ICommandHandler<CreateChatbotMediaSessionCommand, ChatbotMediaSessionDto>, CreateChatbotMediaSessionHandler>();
        services.AddScoped<ICommandHandler<CompleteChatbotMediaCommand, ChatbotMediaFileDto>, CompleteChatbotMediaHandler>();
        // Automations (spec 2026-10-21 D-C8): el catálogo de plantillas, guardar un chatbot como plantilla y borrarla.
        services.AddScoped<IQueryHandler<ListChatbotTemplatesQuery, TemplateCatalogDto>, ListChatbotTemplatesHandler>();
        services.AddScoped<ICommandHandler<SaveChatbotAsTemplateCommand, TemplateDto>, SaveChatbotAsTemplateHandler>();
        services.AddScoped<ICommandHandler<DeleteChatbotTemplateCommand, bool>, DeleteChatbotTemplateHandler>();
        // Automations (spec 2026-10-21 §8, D-S1..D-S3): el simulador del editor sobre el borrador.
        services.AddScoped<ICommandHandler<StartSimulationCommand, SimulationDto>, StartSimulationHandler>();
        services.AddScoped<ICommandHandler<SendSimulationMessageCommand, SimulationDto>, SendSimulationMessageHandler>();
        services.AddScoped<ICommandHandler<DeleteSimulationCommand, bool>, DeleteSimulationHandler>();
        services.AddValidatorsFromAssemblyContaining<UpdateTenantSettingsValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateProductValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateCompanyValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateCustomerValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateSupplierValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateApiKeyValidator>();
        services.AddValidatorsFromAssemblyContaining<Modules.ServiceContracts.Application.UpsertCustomerProfileValidator>();
        services.AddValidatorsFromAssemblyContaining<Modules.Loans.Application.CreateLoanValidator>();
        // Hr: se escanea el ensamblado (sólo registra validadores públicos, y todos los de Hr lo son), porque HrPermissions es
        // estática y no sirve de argumento de tipo.
        services.AddValidatorsFromAssembly(typeof(HrPermissions).Assembly);
        // Payroll: igual que Hr, PayrollPermissions es estática y no sirve de argumento de tipo.
        services.AddValidatorsFromAssembly(typeof(PayrollPermissions).Assembly);
        // Scheduling: igual que Hr, por ensamblado (SchedulingPermissions es estática y no sirve de argumento de tipo).
        services.AddValidatorsFromAssembly(typeof(SchedulingPermissions).Assembly);
        services.AddValidatorsFromAssemblyContaining<Modules.Purchases.Application.CreatePurchaseOrderValidator>();
        services.AddValidatorsFromAssemblyContaining<Modules.Wholesale.Application.PreviewPriceListValidator>();
        services.AddValidatorsFromAssemblyContaining<Modules.Support.Application.UpdateSupportSettingsValidator>();
        services.AddValidatorsFromAssemblyContaining<Modules.Store.Application.UpdateStoreSettingsValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateQuotationValidator>();
        services.AddValidatorsFromAssemblyContaining<OrdersReportFilterValidator>();
        services.AddValidatorsFromAssemblyContaining<OpenCashSessionValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateConnectionValidator>();
        services.AddValidatorsFromAssemblyContaining<ListUnresolvedCitiesValidator>();
        services.AddValidatorsFromAssemblyContaining<NewPasswordValidator>();
        services.AddAuditInfrastructure(configuration);
        services.AddTenancyInfrastructure(configuration);
        services.AddIdentityInfrastructure(configuration);
        services.AddNotificationsInfrastructure(configuration);
        services.AddStorageInfrastructure(configuration);
        services.AddCatalogInfrastructure(configuration);
        services.AddCompaniesInfrastructure(configuration);
        services.AddSuppliersInfrastructure(configuration);
        services.AddApiAccessInfrastructure(configuration);
        services.AddServiceContractsInfrastructure(configuration);
        services.AddLoansInfrastructure(configuration);
        services.AddHrInfrastructure(configuration);
        // Después de AddHrInfrastructure y no en AddQepValkey: envuelve el ILegalParameterSource que ésta registra (spec 2026-11-22).
        services.AddLegalParameterCache(configuration);
        services.AddPayrollInfrastructure(configuration);
        services.AddSchedulingInfrastructure(configuration);
        services.AddPurchasesInfrastructure(configuration);
        services.AddWholesaleInfrastructure(configuration);
        services.AddSupportInfrastructure(configuration);
        services.AddStoreInfrastructure(configuration);
        services.AddBuyersInfrastructure(configuration);
        services.AddWmsInfrastructure(configuration);
        services.AddCustomersInfrastructure(configuration);
        services.AddGeographyInfrastructure(configuration);
        services.AddQuotationsInfrastructure(configuration);
        // Sin AddDbContext y sin inicializador de base: Reporting no tiene tablas propias, y hoy
        // no registra nada. Se llama igual para que el modulo se cablee como los demas.
        services.AddReportingInfrastructure(configuration);
        services.AddPlatformInfrastructure(configuration);

        // Pos (spec 2026-10-07): su único vecino directo es Tenancy; productos, empresas y
        // cajeros entran por adaptadores que se registran más abajo, con los demás.
        services.AddPosInfrastructure(configuration);

        // Integrations (spec 2026-10-08): núcleo, dueño de las conexiones del tenant con plataformas
        // externas. Sólo ve Tenancy; el nombre del autor de una conexión entra por un adaptador que se
        // registra más abajo, con los demás.
        services.AddIntegrationsInfrastructure(configuration);

        // Messaging (spec 2026-10-09): la bandeja de WhatsApp. Sólo ve Tenancy y Audit; conexiones,
        // clientes y almacenamiento entran por adaptadores que se registran más abajo.
        services.AddMessagingInfrastructure(configuration);

        // La presencia de la bandeja (IMessagingPresence, IMessagingPresenceReader) la registra AddQepValkey: con
        // Valkey:Areas:Presence es compartida entre pods, y apagada es el registro local de siempre.

        // Pipeline (spec 2026-10-13): el embudo de ventas. Sólo ve Tenancy (y Audit en Infrastructure); conversaciones,
        // clientes y nombres de miembros entran por adaptadores que se registran más abajo.
        services.AddPipelineInfrastructure(configuration);

        // Automations (spec 2026-10-21 §4.1): los chatbots. Sólo ve Tenancy (y Audit en Infrastructure); conversaciones, salida del
        // bot, conexiones y la «API externa» entran por adaptadores que se registran más abajo.
        services.AddAutomationsInfrastructure(configuration);
        // Spec 2026-10-29 §4.3: la ocupación neutral de las conexiones (messaging.connection_bots), sincronizada en la transacción del
        // chatbot. SQL entre esquemas: va acá, no en Automations.
        services.AddScoped<IChatbotConnectionSlots, ChatbotConnectionSlots>();

        // Ai (spec 2026-10-29 D-A1, plan 2026-11-12): los agentes IA. Sólo ve Tenancy; conversaciones, conexiones y datos de negocio
        // entran por adaptadores en Bootstrapper. A mano, como el resto: sin registro, 500 (CompositionRootTests).
        services.AddAiInfrastructure(configuration);
        services.AddScoped<IQueryHandler<ListAiModelsQuery, AiModelCatalogDto>, ListAiModelsHandler>();
        services.AddScoped<IQueryHandler<GetAiSettingsQuery, AiSettingsDto>, GetAiSettingsHandler>();
        // Plan 2026-11-12, C-1: la clave de cada proveedor es su conexión en Integrations; Ai no la ve, así que el adaptador va acá.
        services.AddScoped<IAiProviderCredentials, AiProviderCredentials>();
        // Spec 2026-10-29 §5: los agentes. Los validadores entran con AddValidatorsFromAssemblyContaining.
        services.AddScoped<IQueryHandler<ListAgentsQuery, AgentListResponse>, ListAgentsHandler>();
        services.AddScoped<IQueryHandler<ListAgentTemplatesQuery, IReadOnlyList<AgentTemplateDto>>, ListAgentTemplatesHandler>();
        services.AddScoped<IQueryHandler<GetAgentQuery, AgentEditorDto>, GetAgentHandler>();
        services.AddScoped<IQueryHandler<ExportAgentQuery, AgentExportDto>, ExportAgentHandler>();
        services.AddScoped<IQueryHandler<ListAiConnectionsQuery, IReadOnlyList<AiConnectionDto>>, ListAiConnectionsHandler>();
        services.AddScoped<ICommandHandler<CreateAgentCommand, AgentEditorDto>, CreateAgentHandler>();
        services.AddScoped<ICommandHandler<ImportAgentCommand, AgentEditorDto>, ImportAgentHandler>();
        services.AddScoped<ICommandHandler<CreateAgentFromTemplateCommand, AgentEditorDto>, CreateAgentFromTemplateHandler>();
        services.AddScoped<ICommandHandler<UpdateAgentCommand, AgentEditorDto>, UpdateAgentHandler>();
        services.AddScoped<ICommandHandler<SaveAgentDraftCommand, AgentDraftSavedDto>, SaveAgentDraftHandler>();
        services.AddScoped<ICommandHandler<PublishAgentCommand, AgentEditorDto>, PublishAgentHandler>();
        services.AddScoped<ICommandHandler<PauseAgentCommand, AgentEditorDto>, PauseAgentHandler>();
        services.AddScoped<ICommandHandler<ResumeAgentCommand, AgentEditorDto>, ResumeAgentHandler>();
        services.AddScoped<ICommandHandler<DeleteAgentCommand, bool>, DeleteAgentHandler>();
        // Plan 2026-11-12 Tarea 9: el historial de sesiones y la actividad del agente en una conversación.
        services.AddScoped<IQueryHandler<ListAgentSessionsQuery, AgentSessionListResponse>, ListAgentSessionsHandler>();
        services.AddScoped<IQueryHandler<GetConversationAgentActivityQuery, AgentActivityDto>, GetConversationAgentActivityHandler>();
        // Spec 2026-10-29 §7: el playground.
        services.AddScoped<ICommandHandler<CreateAgentTestCommand, AgentTestDto>, CreateAgentTestHandler>();
        services.AddScoped<ICommandHandler<SendAgentTestMessageCommand, AgentTestDto>, SendAgentTestMessageHandler>();
        services.AddScoped<IQueryHandler<GetAgentTestQuery, AgentTestDto>, GetAgentTestHandler>();
        services.AddScoped<ICommandHandler<DeleteAgentTestCommand, bool>, DeleteAgentTestHandler>();
        // Spec 2026-11-17 (copiloto) §8: configuración y estado.
        services.AddScoped<IQueryHandler<GetCopilotSettingsQuery, CopilotSettingsDto>, GetCopilotSettingsHandler>();
        services.AddScoped<ICommandHandler<UpdateCopilotSettingsCommand, CopilotSettingsDto>, UpdateCopilotSettingsHandler>();
        services.AddScoped<IQueryHandler<GetCopilotStatusQuery, CopilotStatusDto>, GetCopilotStatusHandler>();
        // Spec 2026-11-17 (copiloto) §8: los hilos privados del miembro.
        services.AddScoped<IQueryHandler<ListCopilotThreadsQuery, CopilotThreadPage>, ListCopilotThreadsHandler>();
        services.AddScoped<IQueryHandler<GetCopilotThreadQuery, CopilotThreadDto>, GetCopilotThreadHandler>();
        services.AddScoped<ICommandHandler<CreateCopilotThreadCommand, CopilotThreadDto>, CreateCopilotThreadHandler>();
        services.AddScoped<ICommandHandler<SendCopilotMessageCommand, CopilotThreadDto>, SendCopilotMessageHandler>();
        services.AddScoped<ICommandHandler<RetryCopilotMessageCommand, CopilotThreadDto>, RetryCopilotMessageHandler>();
        services.AddScoped<ICommandHandler<RenameCopilotThreadCommand, CopilotThreadDto>, RenameCopilotThreadHandler>();
        services.AddScoped<ICommandHandler<DeleteCopilotThreadCommand, bool>, DeleteCopilotThreadHandler>();
        services.AddValidatorsFromAssemblyContaining<CreateAgentValidator>();
        // Spec 2026-10-29 §8.3: las fuentes de las herramientas de lectura y las respuestas rápidas, por adaptadores sobre Catalog,
        // Quotations y Mensajería (Ai no ve ninguno de los tres).
        services.AddScoped<IAiCatalogSearch, AiCatalogSearch>();
        // Spec 2026-11-17 (copiloto) D-C3: las herramientas del copiloto corren como el miembro, sobre las consultas de otros módulos.
        services.AddCopilotTools();
        services.AddScoped<IAiOrderStatusReader, AiOrderStatusReader>();
        services.AddScoped<IAiQuickReplies, AiQuickReplies>();
        // Spec 2026-10-29 §8.3 y P-4: create_quotation_draft escribe en Quotations y valida la asesora del agente, por adaptadores.
        services.AddScoped<IAiQuotationDrafts, AiQuotationDrafts>();
        // Spec 2026-11-23 §7.2: la Agenda del agente; Ai no referencia Scheduling, Mensajería ni Clientes.
        services.AddScoped<IAiScheduling, AiScheduling>();
        services.AddScoped<IAiQuotationAdvisors, AiQuotationAdvisors>();
        // Plan 2026-11-17 A3: las cuentas de cobro del editor del agente, con la regla de la tienda (ActiveBillingAccountOptions).
        services.AddScoped<IAiQuotationBillingAccounts, AiQuotationBillingAccounts>();
        // Spec 2026-10-29 D-A3 y §4.3: conexiones, miembros y la ocupación neutral, por adaptadores (Ai no ve Mensajería).
        services.AddScoped<IAiConnections, AiConnections>();
        services.AddScoped<IAiMembers, AiMembers>();
        // Spec 2026-11-17 (copiloto) D-C12, plan C3.4: nombres por id de usuario para el consumo por miembro y el contexto volátil.
        services.AddScoped<ICopilotMemberNames, CopilotMemberNames>();
        services.AddScoped<IAgentConnectionSlots, AgentConnectionSlots>();
        // Spec 2026-11-17 D-B18: qué chatbots publicados usan cada agente (SQL sobre automations; Ai no ve Automations).
        services.AddScoped<IAiChatbotUsage, AiChatbotUsage>();
        services.AddScoped<IAiChatbotNames, AiChatbotNames>();
        // Spec 2026-11-17 §6.5: el barrido de delegaciones huérfanas de Mensajería, que corre el worker de Ai.
        services.AddScoped<IAiDelegationSweep, AiDelegationSweep>();
        // Spec 2026-10-29 §6.1–§6.6 (plan 2026-11-12, Tarea 7): la cola, la conversación, el envío y el horario de los agentes en canales
        // reales, por adaptadores sobre Mensajería.
        services.AddScoped<IAiInbox, AiInbox>();
        services.AddScoped<IAiConversations, AiConversations>();
        services.AddScoped<IAiBotOutbound, AiBotOutbound>();
        services.AddScoped<IAiBusinessHours, AiBusinessHours>();
        // Plan 2026-11-12 (correcciones de la revisión): no se borra una conexión que atiende un bot. Integrations no ve Mensajería.
        services.AddScoped<IConnectionBotOccupancy, IntegrationsConnectionBotOccupancy>();

        // Shopify (spec 2026-10-15): el espejo de pedidos de las tiendas. Sólo ve Tenancy (y Audit para su auditoría atómica);
        // conexiones, clientes, productos, ciudades y pedidos entran por adaptadores que se registran más abajo.
        services.AddShopifyInfrastructure(configuration);

        // Siigo (spec 2026-11-18): la factura electrónica de un pedido en Siigo. Sólo ve Tenancy (y Audit para su auditoría
        // atómica); conexión, pedido, cliente, producto, ciudad y empresa entran por adaptadores que llegan con cada caso de uso.
        services.AddSiigoInfrastructure(configuration);
        // §5.3 y §7 (Tarea 6): la configuración de cada conexión (siigo.settings.manage, política ya registrada abajo) y las listas de
        // Siigo que sus selectores leen en vivo. Los puertos de Siigo hacia Integrations, Companies y Catalog (SiigoPorts.cs) se unen acá:
        // Siigo no referencia ninguno de esos módulos (SiigoLayerTests).
        services.AddScoped<IQueryHandler<GetSiigoSettingsQuery, SiigoSettingsDto>, GetSiigoSettingsHandler>();
        services.AddScoped<ICommandHandler<AssignSiigoCompanyCommand, SiigoSettingsDto>, AssignSiigoCompanyHandler>();
        services.AddScoped<ICommandHandler<UpdateSiigoDocumentCommand, SiigoSettingsDto>, UpdateSiigoDocumentHandler>();
        services.AddScoped<ICommandHandler<UpdateSiigoTaxesCommand, SiigoSettingsDto>, UpdateSiigoTaxesHandler>();
        services.AddScoped<ICommandHandler<UpdateSiigoPaymentsCommand, SiigoSettingsDto>, UpdateSiigoPaymentsHandler>();
        // Spec 2026-11-20: el paso «Vendedores» (vínculo asesor → usuario de Siigo) y su vinculación por correo.
        services.AddScoped<ICommandHandler<UpdateSiigoSellersCommand, SiigoSettingsDto>, UpdateSiigoSellersHandler>();
        services.AddScoped<ICommandHandler<AutoLinkSiigoSellersByEmailCommand, SiigoSellerAutoLinkResultDto>, AutoLinkSiigoSellersByEmailHandler>();
        services.AddScoped<ISiigoMemberDirectory, SiigoMemberDirectory>();
        services.AddScoped<IQueryHandler<ListSiigoOptionsQuery, IReadOnlyList<SiigoOptionDto>>, ListSiigoOptionsHandler>();
        // Paso 6 (Tarea 7): los vínculos de las variantes con los productos de Siigo.
        services.AddScoped<IQueryHandler<ListSiigoVariantLinksQuery, SiigoVariantLinksPageDto>, ListSiigoVariantLinksHandler>();
        services.AddScoped<IQueryHandler<SearchSiigoProductsQuery, SiigoProductCandidatesPageDto>, SearchSiigoProductsHandler>();
        services.AddScoped<ICommandHandler<LinkSiigoVariantCommand, SiigoVariantLinkDto>, LinkSiigoVariantHandler>();
        services.AddScoped<ICommandHandler<UnlinkSiigoVariantCommand, SiigoCoverageDto>, UnlinkSiigoVariantHandler>();
        services.AddScoped<ICommandHandler<AutoLinkSiigoVariantsByCodeCommand, SiigoAutoLinkResultDto>, AutoLinkSiigoVariantsByCodeHandler>();
        services.AddScoped<ISiigoVariantCatalog, SiigoVariantCatalog>();
        services.AddValidatorsFromAssemblyContaining<UpdateSiigoDocumentValidator>();
        services.AddScoped<ISiigoConnectionDirectory, SiigoConnectionDirectory>();
        services.AddScoped<ISiigoCompanyDirectory, SiigoCompanyDirectory>();
        services.AddScoped<ISiigoTaxRateDirectory, SiigoTaxRateDirectory>();
        // Spec 2026-11-18 §5.5: el pedido para el armador de la factura, con la ciudad en códigos DANE.
        services.AddScoped<ISiigoGeographyLookup, SiigoGeographyLookup>();
        services.AddScoped<ISiigoOrderSource, SiigoOrderSource>();
        // Spec 2026-11-21: enviar el PDF de la factura por WhatsApp. Siigo no ve Quotations: el puerto reenvía a su fachada.
        services.AddScoped<ISiigoInvoiceWhatsApp, SiigoInvoiceWhatsApp>();
        // Tareas 8 a 10: vista previa, generar, consultar, estampar y resolver la factura de un pedido.
        services.AddScoped<IQueryHandler<PreviewSiigoInvoiceQuery, SiigoInvoicePreviewDto>, PreviewSiigoInvoiceHandler>();
        services.AddScoped<IQueryHandler<SiigoInvoiceAvailabilityQuery, SiigoInvoiceAvailabilityDto>, SiigoInvoiceAvailabilityHandler>();
        services.AddScoped<IQueryHandler<GetSiigoInvoicesQuery, IReadOnlyList<SiigoInvoiceDto>>, GetSiigoInvoicesHandler>();
        // Spec 2026-11-21: las llamadas a Siigo que fallaron. El validador entra con AddValidatorsFromAssemblyContaining del módulo.
        services.AddScoped<IQueryHandler<ListSiigoCallFailuresQuery, SiigoCallFailurePageDto>, ListSiigoCallFailuresHandler>();
        services.AddScoped<IQueryHandler<GetSiigoCallFailureQuery, SiigoCallFailureDetailDto>, GetSiigoCallFailureHandler>();
        services.AddScoped<IQueryHandler<GetSiigoInvoicePdfQuery, SiigoInvoicePdfDto>, GetSiigoInvoicePdfHandler>();
        services.AddScoped<ICommandHandler<IssueSiigoInvoiceCommand, IReadOnlyList<SiigoInvoiceDto>>, IssueSiigoInvoiceHandler>();
        services.AddScoped<ICommandHandler<StampSiigoInvoiceCommand, SiigoInvoiceDto>, StampSiigoInvoiceHandler>();
        services.AddScoped<ICommandHandler<ResolveSiigoInvoiceCommand, SiigoInvoiceDto>, ResolveSiigoInvoiceHandler>();
        services.AddScoped<ICommandHandler<ConfirmSiigoAnnulmentCommand, IReadOnlyList<SiigoInvoiceDto>>, ConfirmSiigoAnnulmentHandler>();
        services.AddScoped<ICommandHandler<SendSiigoInvoiceWhatsAppCommand, SiigoInvoiceWhatsAppSent>, SendSiigoInvoiceWhatsAppHandler>();
        // Spec 2026-11-21: la tanda de facturación. El invocador corre el handler de arriba como quien la pidió.
        services.AddScoped<ICommandHandler<CreateSiigoInvoiceBatchCommand, SiigoInvoiceBatchDto>, CreateSiigoInvoiceBatchHandler>();
        services.AddScoped<IQueryHandler<GetSiigoInvoiceBatchQuery, SiigoInvoiceBatchDto>, GetSiigoInvoiceBatchHandler>();
        services.AddScoped<IQueryHandler<ListSiigoInvoiceBatchesQuery, SiigoInvoiceBatchPageDto>, ListSiigoInvoiceBatchesHandler>();
        services.AddScoped<ISiigoBatchItemInvoker, SiigoBatchItemInvoker>();
        // §5.1: el webhook firmado. La firma usa el client secret de la app de Integrations por un puerto propio.
        services.AddSingleton<IShopifyAppSecret, ShopifyAppSecret>();
        services.AddScoped<ICommandHandler<ReceiveShopifyWebhookCommand, ShopifyWebhookReceipt>, ReceiveShopifyWebhookHandler>();
        // §5.3: «Sincronizar ahora» (quotations.order.manage, política ya registrada por Quotations) y la versión de la Admin API
        // de la conciliación, la misma Shopify:App:ApiVersion que usa Integrations para el enlace.
        services.AddScoped<ICommandHandler<SyncShopifyStoreCommand, ShopifySyncResult>, SyncShopifyStoreHandler>();
        // §6.1 y §7: la configuración de la tienda (integrations.connection.read|manage) y la bandeja
        // (quotations.order.read|manage); las cuatro políticas ya las registran Integrations y Quotations, sin permisos nuevos.
        services.AddScoped<IQueryHandler<ListShopifyStoresQuery, IReadOnlyList<ShopifyStoreSummaryDto>>, ListShopifyStoresHandler>();
        services.AddScoped<IQueryHandler<GetShopifyStoreQuery, ShopifyStoreDto>, GetShopifyStoreHandler>();
        services.AddScoped<IQueryHandler<GetShopifyDocumentSourcesQuery, ShopifyDocumentSourcesDto>, GetShopifyDocumentSourcesHandler>();
        services.AddScoped<ICommandHandler<UpdateShopifyStoreCommand, ShopifyStoreDto>, UpdateShopifyStoreHandler>();
        // Spec 2026-11-17 (onboarding) §4.1: el asistente de la tienda (integrations.connection.manage, política de Integrations).
        services.AddScoped<IQueryHandler<GetShopifySetupQuery, ShopifySetupDto>, GetShopifySetupHandler>();
        services.AddScoped<ICommandHandler<AssignShopifySetupCompanyCommand, ShopifySetupCompanyResultDto>, AssignShopifySetupCompanyHandler>();
        services.AddScoped<IQueryHandler<ListShopifyOrdersQuery, ShopifyOrderPageDto>, ListShopifyOrdersHandler>();
        services.AddScoped<IQueryHandler<GetShopifyOrdersSummaryQuery, ShopifyOrdersSummaryDto>, GetShopifyOrdersSummaryHandler>();
        services.AddScoped<ICommandHandler<RetryShopifyOrderCommand, ShopifyOrderRowDto>, RetryShopifyOrderHandler>();
        services.AddScoped<ICommandHandler<ReviewShopifyOrderWarningsCommand, ShopifyOrderRowDto>, ReviewShopifyOrderWarningsHandler>();
        services.AddScoped<ShopifyOrderRowMapper>();
        services.AddValidatorsFromAssemblyContaining<UpdateShopifyStoreValidator>();
        services.AddSingleton<IShopifyApiVersion, ShopifyApiVersion>();
        // §5.2 y §6.2: los puertos de Shopify hacia Integrations, Customers, Catalog, Geography, Quotations, Companies y
        // Tenancy/Identity (ShopifyPorts.cs). Shopify no referencia ninguno de esos módulos: el composition root los une. El
        // escritor de pedidos abre un scope propio por llamada (DiscardPendingChanges de Quotations).
        services.AddScoped<IShopifyConnectionDirectory, ShopifyConnectionDirectory>();
        services.AddScoped<IShopifyConnectionHealth, ShopifyConnectionHealth>();
        services.AddScoped<IShopifyCustomerDirectory, ShopifyCustomerDirectory>();
        services.AddScoped<IShopifyProductLookup, ShopifyProductLookup>();
        services.AddScoped<IShopifyGeographyLookup, ShopifyGeographyLookup>();
        // Spec 2026-10-26 §4.2: la ciudad que no casa va a la bandeja de Geography.
        services.AddScoped<IShopifyUnresolvedCityReporter, ShopifyUnresolvedCityReporter>();
        services.AddScoped<IShopifyShippingLookup, ShopifyShippingLookup>();
        services.AddScoped<IShopifyOrderWriter, ShopifyOrderWriter>();
        services.AddScoped<IShopifyMemberDirectory, ShopifyMemberDirectory>();
        services.AddScoped<IShopifyBillingAccounts, ShopifyBillingAccounts>();
        services.AddScoped<IShopifyOrderNumbers, ShopifyOrderNumbers>();
        services.AddScoped<IShopifyTenantConnections, ShopifyTenantConnections>();
        services.AddScoped<IShopifyStoreReferences, ShopifyStoreReferences>();
        // Spec 2026-11-24 §11.1.4: la otra dirección, Quotations → Shopify: «Editar pedido» pregunta si Shopify puede recibir la
        // dirección antes de guardarla.
        services.AddScoped<IExternalOrderDeliveryCheck, ExternalOrderDeliveryCheck>();
        // Spec 2026-11-17 §5.4: productos desde Shopify. El escritor enrola un CatalogDbContext de un scope hijo en la transacción
        // de Shopify; la imagen va por la subida de servidor de Storage, fuera de esa transacción; la reautorización es el inicio A
        // de la instalación de Integrations.
        services.AddScoped<IShopifyCatalogWriter, ShopifyCatalogWriter>();
        services.AddScoped<IShopifyProductImageStore, ShopifyProductImageStore>();
        services.AddScoped<IShopifyReauthorization, ShopifyReauthorization>();
        // Spec 2026-11-17 (onboarding) §4.2: el asistente crea la empresa con el caso de uso de Companies.
        services.AddScoped<IShopifySetupCompanies, ShopifySetupCompanies>();
        // §6.2: el importador y, con él, el procesador de entregas y su worker. Sin estas tres líneas el webhook
        // responde 200, encola y nadie procesa nunca; CompositionRootTests arma el contenedor con ValidateOnBuild y resuelve el
        // worker para que eso no pase en silencio.
        services.AddScoped<ShopifyCustomerResolver>();
        services.AddScoped<IShopifyOrderImporter, ShopifyOrderImporter>();
        // §6.3: pago y cancelación de los pedidos ya importados. Va antes de AddShopifyWebhookProcessing, que sólo
        // registra la reacción vacía si nadie registró otra (TryAdd).
        services.AddScoped<IShopifyOrderChangeReaction, ShopifyOrderChanges>();
        // Spec 2026-11-14 §3: las ediciones de Shopify sobre pedidos importados, con el mismo patrón (TryAdd en
        // AddShopifyWebhookProcessing). Los resolvedores de líneas y envío son los de la importación.
        services.AddScoped<ShopifyLineResolver>();
        services.AddScoped<ShopifyShippingResolver>();
        services.AddScoped<ShopifyEditPlanner>();
        services.AddScoped<ShopifyOrderEdits>();
        services.AddScoped<IShopifyOrderEditDetector>(provider => provider.GetRequiredService<ShopifyOrderEdits>());
        services.AddShopifyWebhookProcessing();
        services.AddShopifyWebhookWorker();
        // Spec 2026-10-26 D-C6: el consumidor de los eventos de sinónimos de Geography, que siembra los trabajos de corrección, y
        // el worker que los corrige, con sus dos puertos: el sinónimo de Geography y el escritor sobre Quotations y Customers (los
        // dos abren un scope propio por llamada).
        services.AddScoped<IShopifyCitySynonymLookup, ShopifyCitySynonymLookup>();
        services.AddScoped<IShopifyCityCorrectionWriter, ShopifyCityCorrectionWriter>();
        services.AddShopifyCityCorrections();
        // Spec 2026-11-24 §11.1.1: la dirección cambiada en «Editar pedido» (evento de Quotations en el outbox) se encola como
        // envío a Shopify.
        services.AddShopifyQuotationOrderEvents();
        // Spec 2026-10-26 D-C3b: lo que la consola de ciudades lee de pedidos, clientes, huellas, tenants y usuarios, que
        // Geography no referencia. Scoped como el GeographyDbContext sobre el que corre.
        services.AddScoped<ICityCorrectionInsights, CityCorrectionInsights>();
        // Lo que la consola le hace a lo de Shopify: reintentar los trabajos fallidos de un sinónimo y recorrer la historia previa
        // (D-C12, S6). Los dos puertos son de Geography y Geography no referencia Shopify.
        services.AddScoped<ICityCorrectionJobs, CityCorrectionJobsAdapter>();
        services.AddScoped<ShopifyUnresolvedCityBackfill>();
        services.AddScoped<IHistoricalCityBackfill, HistoricalCityBackfillAdapter>();
        // §5.3: el cron de la conciliación, cada Shopify:Reconcile:IntervalMinutes.
        services.AddShopifyReconcileWorker();
        // Spec 2026-11-13: las asociaciones de líneas personalizadas (nombres de variantes de Catalog).
        services.AddShopifyCustomLineMappings();
        // Spec 2026-11-14 §6: la bandeja de cambios de Shopify (consultas, aceptar, rechazar y revisar).
        services.AddShopifyOrderEditDecisions();
        // Spec 2026-11-14 §5: el worker que manda a Shopify las correcciones (orderUpdate).
        services.AddShopifyOrderPushes();
        // Spec 2026-11-17 §5.2: la importación de productos (procesador, cola y worker), sobre los adaptadores de Catalog y Storage
        // registrados arriba.
        services.AddShopifyProductSync();

        // CAT-05 — el único punto donde `catalog` y `storage` se tocan, y es acá a propósito:
        // ningún módulo referencia al otro, el composition root los cablea. Va después de los
        // dos AddXInfrastructure porque el adaptador depende de servicios que ellos registran.
        services.AddScoped<IProductImageLookup, ProductImageLookup>();
        // Variantes: «¿alguna línea vende esta variante?» mira Quotations y Pos, que
        // Catalog no referencia. Scoped como el CatalogDbContext sobre el que corre, para que la
        // consulta comparta la transacción del PUT.
        services.AddScoped<IVariantUsageChecker, CatalogVariantUsageChecker>();
        // Productos desde Shopify (spec 2026-11-17 §5.4): el camino de sistema de Catalog, sin miembro. Scoped como el
        // CatalogDbContext sobre el que guarda, para que el adaptador de Shopify lo enrole en su transacción.
        services.AddScoped<IExternalProductWriter, ExternalProductWriter>();
        // Borrado de producto (spec 2026-11-08): congelar y quitar las líneas de Quotations y las de las solicitudes
        // nuevas de la tienda, que Catalog no referencia. Scoped como el CatalogDbContext sobre el que corre, para que
        // todo comparta la transacción del DELETE.
        services.AddScoped<IProductDeletionCleanup, CatalogProductDeletionCleanup>();
        // Fusión de clientes (spec 2026-10-24 §4): mover las referencias de Quotations, Pos, Pipeline, Messaging y
        // Shopify, que Customers no referencia. Scoped como el CustomersDbContext sobre el que corre, para que los UPDATE
        // compartan la transacción de la fusión.
        services.AddScoped<ICustomerMergeReferences, CustomerMergeReferences>();
        // Borrado de clientes (spec 2026-11-10 §4.2 y §4.3): los bloqueos y la limpieza en los otros módulos. Scoped como el
        // CustomersDbContext sobre el que corre, para que todo quede en la transacción del lote.
        services.AddScoped<ICustomerDeletionReferences, CustomerDeletionReferences>();

        services.AddScoped<IProductExportStorage, ProductExportStorage>();

        // Pos (spec 2026-10-07): Catalog, Companies y Tenancy/Identity entran por adaptadores
        // (PosLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules).
        services.AddScoped<IPosProductLookup, PosProductLookup>();
        services.AddScoped<IPosVariantCostLookup, CatalogVariantCostLookup>();
        // El pedido congela el mismo costo con su propio puerto (spec costo de producto §4.3); mismo adaptador.
        services.AddScoped<IOrderVariantCostLookup, CatalogVariantCostLookup>();
        services.AddScoped<IPosCategoryLookup, PosCategoryLookup>();
        services.AddScoped<IPosCompanyLookup, PosCompanyLookup>();
        // Hr: la empresa empleadora y la sede entran por su propio puerto (HrLayerTests.NoHrLayerReferencesCompanies).
        services.AddScoped<Modules.Hr.Application.IHrCompanyLookup, HrCompanyLookup>();
        // Payroll lee los valores legales de Hr por su propio puerto (PayrollLayerTests.NoPayrollLayerReferencesHr).
        services.AddScoped<Modules.Payroll.Application.IPayrollLegalValues, PayrollLegalValues>();
        // La consola de valores legales de Hr sabe qué nóminas ya se calcularon por este puerto (PayrollLayerTests.NoPayrollLayerReferencesHr):
        // Payroll expone sus períodos calculados y este adaptador los traduce al puerto de Hr.
        services.AddScoped<Modules.Hr.Application.ILegalParameterUsage, LegalParameterUsage>();
        // Empresas, empleados y contratos de Hr, y el nombre de quien actúa, también por puertos propios de Payroll.
        services.AddScoped<Modules.Payroll.Application.IPayrollHrDirectory, PayrollHrDirectory>();
        services.AddScoped<Modules.Payroll.Application.IPayrollHolidayCalendar, PayrollHolidayCalendar>();
        services.AddScoped<Modules.Payroll.Application.IPayrollMemberDirectory, PayrollMemberDirectory>();
        services.AddScoped<IPosCashierLookup, PosCashierLookup>();
        services.AddScoped<IPosCustomerLookup, PosCustomerLookup>();

        // Integrations (P23): el nombre de quien creó la conexión sale de Tenancy e Identity.
        services.AddScoped<IConnectionAuthorNames, IntegrationsConnectionAuthorNames>();

        // Messaging (spec 2026-10-09 §6.4): conexiones de WhatsApp por Integrations. El único punto donde
        // los dos módulos se tocan.
        services.AddScoped<IMessagingConnectionDirectory, MessagingConnectionDirectory>();
        // Messaging (spec 2026-10-09 §6.4): clientes por teléfono y nombres de miembros, por adaptadores.
        services.AddScoped<IMessagingCustomerDirectory, MessagingCustomerDirectory>();
        services.AddSingleton<IDialablePhoneNumbers, DialablePhoneNumbers>();
        services.AddScoped<IMessagingMemberNames, MessagingMemberNames>();
        // Spec 2026-11-22: la campana de avisos. Los nombres se resuelven al leer, con la regla de Mensajería.
        services.AddScoped<Modules.Notifications.Application.IInAppNotificationNames, InAppNotificationNames>();
        // Spec 2026-11-22 D-1: the task notice worker asks whether the member has QEP open (the app hub's presence).
        services.AddScoped<Modules.Notifications.Application.ITaskNoticePresence, TaskNoticePresence>();
        // Task-notices spec 2026-11-22 §5.4: the escalation reads the ticket (Support), the recipient (Tenancy, HR, Identity) and sends
        // the WhatsApp template (Messaging) only through these narrow ports.
        services.AddScoped<Modules.Notifications.Application.ITaskNoticeState, TaskNoticeState>();
        services.AddScoped<Modules.Notifications.Application.ITaskNoticeRecipients, TaskNoticeRecipients>();
        services.AddScoped<Modules.Notifications.Application.ITaskNoticeWhatsApp, TaskNoticeWhatsAppSender>();
        services.AddScoped<
            IQueryHandler<Modules.Notifications.Application.ListInAppNotificationsQuery, Modules.Notifications.Application.InAppNotificationPageDto>,
            Modules.Notifications.Application.ListInAppNotificationsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Notifications.Application.GetUnreadCountQuery, Modules.Notifications.Application.UnreadCountDto>,
            Modules.Notifications.Application.GetUnreadCountHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Notifications.Application.MarkNotificationReadCommand, Modules.Notifications.Application.InAppNotificationReadDto>,
            Modules.Notifications.Application.MarkNotificationReadHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Notifications.Application.MarkAllNotificationsReadCommand, Modules.Notifications.Application.MarkAllReadResultDto>,
            Modules.Notifications.Application.MarkAllNotificationsReadHandler>();
        // Task-notices spec D-2: abrir un ticket lee sus avisos de asignación. El validador lo registra el escaneo de abajo.
        services.AddScoped<
            ICommandHandler<Modules.Notifications.Application.MarkResourceNotificationsReadCommand, Modules.Notifications.Application.MarkResourceReadResultDto>,
            Modules.Notifications.Application.MarkResourceNotificationsReadHandler>();
        services.AddValidatorsFromAssemblyContaining<Modules.Notifications.Application.ListInAppNotificationsValidator>();
        // Spec 2026-10-21 D-I3: el nombre de los chatbots que firman mensajes, leído de Automations.
        services.AddScoped<IAutomationsChatbotNames, AutomationsChatbotNames>();
        // Soporte fase 2: el número de los tickets que nombran los eventos del hilo, leído de Soporte.
        services.AddScoped<IMessagingTicketNames, MessagingTicketNames>();
        // Spec 2026-10-31: iniciar el asistente desde el hilo; la decisión es de Automations (ManualRunStarter).
        services.AddScoped<IAutomationManualStart, AutomationManualStart>();
        // Spec 2026-10-21 §5.5: «Datos capturados» del asesor, leído de la corrida más reciente de Automations.
        services.AddScoped<IConversationAutomationDetails, AutomationConversationDetails>();
        // Spec 2026-10-21 §5.3: lo que Automations necesita de Mensajería e Integrations, por adaptadores.
        services.AddScoped<IAutomationConnections, AutomationConnections>();
        services.AddScoped<IAutomationExternalApis, AutomationExternalApis>();
        // Spec 2026-10-25 D-5: los miembros que pueden responder, los mismos que ofrece «Transferir».
        services.AddScoped<IAutomationMembers, AutomationMembers>();
        // Spec 2026-11-17 §5.2: los agentes de IA del tenant para el bloque «Agente IA», leídos de Ai.
        services.AddScoped<IAutomationsAgentDirectory, AutomationsAgentDirectory>();
        // Spec 2026-11-19 (chatbot CRM action): the tags and open stages for the «Acción de CRM» block, read from Customers and Pipeline.
        services.AddScoped<IAutomationsCrmDirectory, AutomationsCrmDirectory>();
        // Spec 2026-10-26 D-5, D-7 y D-10: la autorización de datos del cliente de la conversación, en Customers.
        services.AddScoped<IAutomationDataConsents, AutomationDataConsents>();
        // Spec 2026-11-19 (chatbot CRM action): the conversation's customer, shared with «Política de datos», and the «Acción de CRM»
        // block over Customers and Pipeline as the system.
        services.AddScoped<ConversationCustomerResolver>();
        services.AddScoped<IAutomationCrm, AutomationCrm>();
        services.AddScoped<IAutomationBusinessHours, AutomationBusinessHours>();
        // Spec 2026-10-21 D-H2, D-H5 y fallo P2-R31: el cliente del bloque HTTP va acá y no en AddAutomationsInfrastructure porque su handler
        // primario (anti-SSRF: sin redirecciones, sin proxy, ConnectCallback que rechaza rangos internos) es de Integrations.Infrastructure.
        // Sin loggers: el de HttpClient registraría la URL con la query del cliente. CompositionRootTests lo amarra.
        services.AddSingleton<IExternalApiUrlPolicy, AutomationExternalApiUrlPolicy>();
        services.AddHttpClient(HttpBlockExecutor.HttpClientName, HttpBlockExecutor.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                // Spec 2026-11-03 D-8: el pool va acá y no en CreateHandler, que también usa el probador de la conexión, una llamada suelta.
                var handler = ExternalApiUrlPolicy.CreateHandler();
                handler.PooledConnectionIdleTimeout = HttpBlockExecutor.PooledConnectionIdleTimeout;
                handler.PooledConnectionLifetime = HttpBlockExecutor.PooledConnectionLifetime;
                return handler;
            })
            .RemoveAllLoggers();
        // Spec 2026-10-21 §5.3, D-M5 y D-M14: los puertos del motor sobre la cola, la conversación, la salida del bot y los medios de
        // Mensajería, y el nombre de la empresa por Tenancy.
        services.AddScoped<IAutomationInboundQueue, AutomationInboundQueue>();
        services.AddScoped<IAutomationConversations, AutomationConversations>();
        services.AddScoped<IAutomationMessenger, AutomationMessenger>();
        services.AddScoped<IAutomationMedia, AutomationMedia>();
        services.AddScoped<IAutomationTenantNames, AutomationTenantNames>();
        // Spec 2026-10-21 §5.2: una unidad del motor por fila del inbox, en el scope que abre AutomationInboxWorker.
        services.AddScoped<RunEffects>();
        // Spec 2026-10-25 D-E5: el parámetro es opcional en RunProcessor; sin este registro el contenedor lo arma sin registro de unidades
        // y nada se pone rojo. CompositionRootTests lo amarra.
        services.AddScoped<RunUnitWriter>();
        services.AddScoped<RunProcessor>();
        services.AddScoped<ManualRunStarter>();
        // Spec 2026-10-21 §8: el simulador usa el mismo ejecutor HTTP y el mismo puerto de horario que el motor.
        services.AddScoped<SimulationRunner>();
        // Spec 2026-10-12 §3.3 (D-Q5): el nombre del tenant para las variables de las respuestas rápidas, por Tenancy.
        services.AddScoped<IMessagingTenantNames, MessagingTenantNames>();
        // Spec 2026-10-11 §4.2: el autor de una nota de cliente (usuario, no membresía) con su nombre visible.
        services.AddScoped<ICustomerAuthorNames, CustomersAuthorNames>();
        // Spec 2026-10-26 D-5 (medium): la conversación por la que se revoca la autorización de datos, por Mensajería.
        services.AddScoped<ICustomerConversations, CustomersConversations>();
        // Spec 2026-10-10 §6.1.3: quién puede responder, por Tenancy, Identity y Authorization.
        services.AddScoped<IMessagingAssignees, MessagingAssignees>();
        // Messaging (spec 2026-10-09 §6.4 y §8.6): la copia de los medios entrantes, en el bucket privado de Storage.
        services.AddScoped<IMessagingMediaStore, MessagingMediaStore>();
        // Messaging-media-reply: el staging de los adjuntos del asesor (R1, R13) en el mismo bucket privado y el compresor de
        // imágenes (R12); el compresor no guarda estado y es singleton.
        services.AddScoped<IMessagingUploadStaging, MessagingUploadStaging>();
        services.AddSingleton<IMessagingImageCompressor, ImageSharpMessagingImageCompressor>();
        // Pipeline (spec 2026-10-13 D9): conversaciones por el DbContext de Messaging, clientes por Customers y nombres de
        // miembros con el mismo adaptador que Messaging (una sola copia de «DisplayName ?? correo»). El único punto donde Pipeline
        // toca esos módulos.
        services.AddScoped<IPipelineConversationDirectory, PipelineConversationDirectory>();
        services.AddScoped<IPipelineCustomerDirectory, PipelineCustomerDirectory>();
        services.AddScoped<IPipelineMemberNames, MessagingMemberNames>();
        // Quién puede ser dueño de un negocio: sólo membresías Active (los nombres de arriba son sólo para mostrar).
        services.AddScoped<IPipelineActiveMembers, PipelineActiveMembers>();
        // Observabilidad fase 2: profundidad y edad de las seis colas, para el muestreador (SQL crudo por nombre).
        services.AddScoped<Observability.IQueueMetricsSource, Observability.QueueMetricsSource>();
        // El muestreador y sus gauges son singleton; IClock está registrado scoped, así que los gauges llevan su propio reloj (SystemClock
        // no guarda estado) y el IMeterFactory es el del host, el mismo que lee el exportador.
        services.AddSingleton(provider => new Observability.QueueGauges(
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), new BuildingBlocks.Infrastructure.SystemClock()));
        // Spec 2026-11-16: la hora del último webhook de Meta, en la misma vuelta del muestreador.
        services.AddScoped<Observability.IWebhookArrivalSource, Observability.WebhookArrivalSource>();
        services.AddSingleton(provider => new Observability.WebhookArrivalGauge(
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), new BuildingBlocks.Infrastructure.SystemClock()));
        services.AddHostedService<Observability.QueueSamplerWorker>();

        // Registro de llamadas a la API externa: el middleware encola y el escritor inserta, para que la base no demore una respuesta.
        services.AddSingleton<ExternalApiCallLogChannel>();
        services.AddHostedService<ExternalApiCallLogWriter>();

        // Mismo patrón (CAT-05) entre `customers` y `geography`: ninguno de los dos referencia al
        // otro — CustomersLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules lo
        // impide a propósito — y el composition root cablea el puerto que declara `customers`
        // contra los repositorios que ya registró AddGeographyInfrastructure.
        services.AddScoped<ICustomerGeographyLookup, CustomerGeographyLookup>();

        // Y el mismo patrón entre `customers` y `storage`, para dejar el Excel exportado en el
        // bucket y firmar su enlace de descarga.
        services.AddScoped<ICustomerExportStorage, CustomerExportStorage>();

        // Y entre `suppliers` y `storage`, con el mismo adaptador para su exportación.
        services.AddScoped<ISupplierExportStorage, SupplierExportStorage>();
        services.AddScoped<Modules.Wms.Application.IWmsExportStorage, WmsExportStorage>();

        // Y entre `quotations` y `storage`, para el Excel de las exportaciones asíncronas.
        services.AddScoped<IExportFileStorage, ExportFileStorage>();
        services.AddScoped<IQuotationPdfStorage, QuotationPdfStorage>();
        services.AddScoped<IQuotationPdfProvider, QuotationPdfProvider>();
        services.AddScoped<IOrderPdfProvider, OrderPdfProvider>();

        // Decisión 9 del spec 2026-09-19: el PDF lee el logo del tenant por Tenancy (el
        // LogoFileId vigente) y Storage (los bytes del original privado).
        services.AddScoped<IQuotationLogoLookup, QuotationTenantLogoLookup>();

        // Decisión 4 del spec 2026-09-19: publica/despublica el logo del tenant sobre Storage sin
        // pasar por sus handlers ni sus permisos.
        services.AddScoped<ITenantLogoStorage, TenantLogoStorage>();

        // Spec 2026-11-17: la imagen del encabezado de plantilla sobre Storage, con el mismo criterio que el logo: sin pasar por los
        // handlers de Storage ni por sus permisos (la autoriza messaging.template.manage).
        services.AddScoped<IMessagingTemplateImageStore, MessagingTemplateImageStore>();
        // Spec 2026-11-18: el archivo de un bloque «Mensaje» de chatbot. La política es la columna WhatsApp de Mensajería, el archivo vive en
        // Storage (dueño ChatbotMedia, sin pasar por sus handlers ni sus permisos: lo autoriza automations.chatbot.manage) y Storage lo
        // revisa y comprime con el sniffer y el compresor de Mensajería.
        services.AddScoped<IChatbotMediaPolicy, ChatbotMediaPolicy>();
        services.AddScoped<IChatbotMediaStore, ChatbotMediaStore>();
        services.AddScoped<IChatbotMediaProcessing, ChatbotMediaProcessing>();
        // Y al revés, para el barrido diario de Storage: qué imágenes de encabezado usa todavía una plantilla.
        services.AddScoped<ITemplateImageReferences, TemplateImageReferences>();
        // Y los archivos de chatbot (spec 2026-11-18): en uso mientras los nombre un chatbot, una versión, una plantilla guardada o un
        // mensaje de la bandeja.
        services.AddScoped<IChatbotMediaReferences, ChatbotMediaReferences>();
        // Spec 2026-11-20: el PDF de un contrato o de un registro de RRHH sobre Storage (dueño HrDocument, sin pasar por sus handlers ni
        // sus permisos: lo autoriza hr.contract.manage). Hr no referencia Storage (HrLayerTests).
        services.AddScoped<Modules.Hr.Application.IHrDocumentStore, HrDocumentStore>();

        // Mismo patrón (CAT-05) entre `companies` y `geography`: ninguno de los dos referencia al
        // otro, y el composition root cablea el puerto que declara `companies` contra los
        // repositorios que ya registró AddGeographyInfrastructure.
        services.AddScoped<ICompanyGeographyLookup, CompanyGeographyLookup>();

        // Hr: ciudades y países de Geography por su puerto (HrLayerTests.NoHrLayerReferencesGeography).
        services.AddScoped<Modules.Hr.Application.IHrGeographyLookup, HrGeographyLookup>();
        // Hr: las membresías (la persona de un empleado y el actor del historial) por su propio puerto.
        services.AddScoped<Modules.Hr.Application.IHrMemberDirectory, HrMemberDirectory>();
        // Hr, birthday notices: the WhatsApp connections of the tenant and whether a manager has phone and email, by their own ports.
        services.AddScoped<Modules.Hr.Application.BirthdayNotices.IHrWhatsAppConnections, HrWhatsAppConnections>();
        services.AddScoped<Modules.Hr.Application.BirthdayNotices.IHrMemberContacts, HrMemberContacts>();
        services.AddScoped<Modules.Hr.Application.MedicalExams.IHrBusinessCalendar, HrBusinessCalendarSource>();
        // Scheduling: las membresías (el vínculo de un recurso y el alcance de quien no ve todas las agendas) y el nombre del tenant, por sus puertos.
        services.AddScoped<ISchedulingMemberDirectory, SchedulingMemberDirectory>();
        services.AddScoped<ISchedulingTenantDirectory, SchedulingTenantDirectory>();
        // La Agenda busca y da de alta al cliente de la cita por su propio puerto (plan 2026-11-21, corrección 5), calco de IPosCustomerLookup.
        services.AddScoped<ISchedulingCustomerLookup, SchedulingCustomerLookup>();

        // Mismo patrón para `suppliers`: el puerto es suyo y el composition root lo cablea contra
        // los repositorios de Geography.
        services.AddScoped<ISupplierGeographyLookup, SupplierGeographyLookup>();

        // Mismo patrón para `billing` (spec cobros-recurrentes §3.3): guarda clientes y encargados como ids sueltos y el
        // composition root cablea sus puertos contra Customers, Tenancy, Identity y Authorization. Con ruta completa porque
        // importar el namespace choca con Messaging (AssigneesDto, entre otros).
        services.AddScoped<Modules.ServiceContracts.Application.IServiceContractsCustomerLookup, ServiceContractsCustomerLookup>();
        services.AddScoped<Modules.ServiceContracts.Application.IServiceContractsMemberDirectory, ServiceContractsMemberDirectory>();
        services.AddSingleton<Modules.ServiceContracts.Application.IServiceContractsConsentGate, ServiceContractsConsentGate>();
        services.AddSingleton<Modules.ServiceContracts.Application.IServiceContractsPhoneNormalizer, ServiceContractsPhoneNormalizer>();
        services.AddSingleton<Modules.ServiceContracts.Application.IServiceContractsIdentificationTypes, ServiceContractsIdentificationTypes>();
        services.AddScoped<Modules.ServiceContracts.Application.IServiceContractsCustomerProvisioning, ServiceContractsCustomerProvisioning>();
        services.AddScoped<ServiceContractsEmailSender>();
        services.AddScoped<Modules.ServiceContracts.Application.IServiceContractsCustomerEmail>(provider => provider.GetRequiredService<ServiceContractsEmailSender>());
        services.AddScoped<Modules.ServiceContracts.Application.IServiceContractsStaffEmail>(provider => provider.GetRequiredService<ServiceContractsEmailSender>());
        services.AddScoped<Modules.ServiceContracts.Application.IServiceContractsWhatsAppOutbound, ServiceContractsMetaWhatsAppSender>();
        services.AddScoped<IUserReferenceProbe, ServiceContractsUserReferenceProbe>();
        services.AddScoped<IUserReferenceProbe, LoansUserReferenceProbe>();
        services.AddScoped<IUserReferenceProbe, HrUserReferenceProbe>();
        services.AddScoped<IUserReferenceProbe, PurchasesUserReferenceProbe>();
        services.AddScoped<IUserReferenceProbe, SupportUserReferenceProbe>();

        // Mismo patrón para `loans` (spec de préstamos 2026-10-31): clientes y encargados son ids sueltos y el composition root cablea sus
        // puertos contra Customers, Tenancy y Authorization. Con ruta completa por el mismo choque de nombres.
        services.AddScoped<Modules.Loans.Application.ILoansCustomerLookup, LoansCustomerLookup>();
        services.AddScoped<Modules.Loans.Application.ILoansMemberDirectory, LoansMemberDirectory>();

        // `purchases` (spec 2026-11-17): proveedor, variante y membresías son ids sueltos de otros módulos; Compras sólo declara
        // puertos y el composition root los cablea contra Suppliers, Catalog y Tenancy.
        services.AddScoped<Modules.Purchases.Application.IPurchasesSupplierLookup, PurchasesSupplierLookup>();
        services.AddScoped<Modules.Purchases.Application.IPurchaseOrderEmail, PurchaseOrderEmailSender>();
        services.AddScoped<Modules.Purchases.Application.IPurchasesCatalogLookup, PurchasesCatalogLookup>();
        services.AddScoped<Modules.Purchases.Application.IPurchasesMemberDirectory, PurchasesMemberDirectory>();
        // Mayorista fase 3: el catálogo del mayorista, su motor de pedidos (por un scope propio por llamada) y el catálogo propio
        // para la confirmación, enrolado en la transacción del inbox de Compras.
        services.AddScoped<Modules.Purchases.Application.IWholesaleCatalog, PurchasesWholesaleCatalog>();
        services.AddScoped<Modules.Purchases.Application.IWholesaleOrderIntake, PurchasesWholesaleOrderIntake>();
        services.AddScoped<IWholesaleIntakeDirectory, WholesaleIntakeDirectory>();
        services.AddScoped<Modules.Purchases.Application.IPurchasesCatalogWriter, PurchasesCatalogWriter>();
        services.AddScoped<Modules.Purchases.Application.IPurchasesProductImages, PurchasesProductImages>();
        // Y la adenda del contrato: el estado de la relación con el mayorista en la lista y el detalle de proveedores.
        services.AddScoped<ISupplierRelationshipLookup, SuppliersWholesaleRelationshipLookup>();

        // Wholesale, spec 2026-11-17: Catalog, Companies, Tenancy, Suppliers y Customers entran por puertos del módulo.
        services.AddScoped<Modules.Wholesale.Application.IWholesaleCatalogReader, WholesaleCatalogReader>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleCompanyLookup, WholesaleCompanyLookup>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleCityLookup, WholesaleCityLookup>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleMemberDirectory, WholesaleMemberDirectory>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleTenantDirectory, WholesaleTenantDirectory>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleSupplierLinker, WholesaleSupplierLinker>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleCustomerProvisioning, WholesaleCustomerProvisioning>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleCompanyProvisioning, WholesaleCompanyProvisioning>();
        // Spec 2026-11-18: el aviso por WhatsApp de la invitación; Customers (teléfono), Integrations y Mensajería por puertos.
        services.AddScoped<Modules.Wholesale.Application.IWholesalePhoneNormalizer, WholesalePhoneNormalizer>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleWhatsAppConnections, WholesaleWhatsAppConnections>();
        services.AddScoped<Modules.Wholesale.Application.IWholesaleInvitationNoticeSender, WholesaleInvitationNoticeSender>();
        // Spec 2026-11-19: el link para «Copiar», el mismo del correo (Notifications:AppBaseUrl, ruling W1).
        services.AddScoped<Modules.Wholesale.Application.IWholesaleInvitationLinks, WholesaleInvitationLinks>();
        // Ruling W17: el paso de Identity de session-with-password, con la contraseña validada antes de crear al usuario.
        services.AddScoped<WholesaleInvitationPasswordSession>();
        // El paso de Identity de session con Google: aprovisiona sólo si la identidad no tiene usuario, y usa ese usuario.
        services.AddScoped<WholesaleInvitationGoogleSession>();

        // `support` (spec 2026-11-14): clientes, responsables y el calendario son de otros módulos y Soporte sólo declara puertos. El
        // calendario es lunes a viernes, con los festivos de Colombia (que viven en Mensajería) si el tenant es de allá [owner 2026-11-15].
        services.AddScoped<Modules.Support.Application.ISupportCustomerLookup, SupportCustomerLookup>();
        services.AddScoped<Modules.Support.Application.ISupportMemberDirectory, SupportMemberDirectory>();
        // Task-notices spec 2026-11-22 §5.4: the WhatsApp connections (Integrations) and the state of the TaskNotice template (Messaging).
        services.AddScoped<Modules.Support.Application.ISupportTaskNoticeChannels, SupportTaskNoticeChannels>();
        // Spec 2026-11-15 §9.3 (plan L30): the company that answers on the public portal is a Companies company, like IPosCompanyLookup.
        services.AddScoped<Modules.Support.Application.ISupportOrganizationDirectory, SupportOrganizationDirectory>();
        // Fase 4a (plan G8): el «quién soy» de IsMe; null fuera de una sesión.
        services.AddScoped<Modules.Support.Application.ISupportViewer, SupportViewer>();
        // App hub (spec 2026-11-15, plan N4 and N24): Support's topics are authorized against the member's current roles.
        services.AddScoped<Modules.Support.Application.ISupportMemberPermissions, SupportMemberPermissions>();
        Modules.Support.Api.SupportRealtimeExtensions.AddSupportRealtimeAuthorizer(services);
        // Spec 2026-11-22 §9.2: the same for Messaging's topic; its authorizer is registered by AddMessagingRealtime (Bootstrapper does not
        // reference Messaging.Api).
        services.AddScoped<Modules.Messaging.Application.IMessagingMemberPermissions, MessagingMemberPermissions>();
        services.AddAppRealtime();
        services.AddScoped<Modules.Support.Application.ISupportBusinessCalendarSource, SupportBusinessCalendarSource>();
        // Spec 2026-11-21 (Agenda), D-10: los festivos de las franjas, por puerto y con el adaptador de Bootstrapper (calco del de Soporte).
        services.AddScoped<ISchedulingHolidayCalendar, SchedulingHolidayCalendar>();
        // Plan 2026-11-21, Tarea 12: el recordatorio de cita sale por Mensajería (puerto IAppointmentReminderSender) y el cliente, su teléfono y su
        // consentimiento llegan por ISchedulingReminderRecipients; la Agenda no referencia ni Mensajería ni Clientes.
        services.AddScoped<IAppointmentReminderSender, SchedulingReminderSender>();
        services.AddScoped<ISchedulingReminderRecipients, SchedulingReminderRecipients>();
        // Spec 2026-11-23 §2: el estado de la plantilla de recordatorio de la conexión guardada, por puerto y con adaptador hacia Mensajería.
        services.AddScoped<ISchedulingReminderTemplateStatus, SchedulingReminderTemplateStatus>();
        // Soporte fase 2: la conversación, sus mensajes, el feed del seguimiento, los eventos del hilo y las respuestas son de Mensajería y
        // Notifications; Soporte sólo declara los puertos.
        services.AddScoped<Modules.Support.Application.ISupportConversationDirectory, SupportConversationDirectory>();
        services.AddScoped<Modules.Support.Application.ISupportThreadFeed, SupportThreadFeed>();
        services.AddScoped<Modules.Support.Application.ISupportSurveyFeed, SupportSurveyFeed>();
        services.AddScoped<Modules.Support.Application.ISupportAutoCloseFeed, SupportAutoCloseFeed>();
        services.AddScoped<Modules.Support.Application.ISupportConversationEvents, SupportConversationEvents>();
        services.AddScoped<Modules.Support.Application.ISupportReplyChannels, SupportReplyChannels>();
        services.AddScoped<Modules.Support.Application.ISupportReplyEmail, SupportReplyEmailSender>();
        // Soporte fase 4b (H5 y H8): el membrete de la respuesta formal (Companies, Tenancy y Storage) y el PDF en el bucket privado,
        // bajo support/ y sin regla de ciclo de vida.
        services.AddScoped<Modules.Support.Application.ISupportLetterhead, SupportLetterhead>();
        services.AddScoped<Modules.Support.Application.ISupportDocumentStorage, SupportDocumentStorage>();
        // Tarea 8: arma el PDF de la respuesta formal (firma, membrete, «siguiente paso», generación y bucket) antes de crear la fila.
        services.AddScoped<Modules.Support.Application.SupportFormalReplies>();
        // Tarea 11 (H16): el correo de la encuesta con el enlace de calificación (Notifications).
        services.AddScoped<Modules.Support.Application.ISupportSurveyEmail, SupportSurveyEmailSender>();
        // Soporte fase 3: el host del formulario público (plan F28 y F29).
        // Los orígenes del grupo público se validan al arrancar, como Cors:AllowedOrigins (plan F4).
        services.AddOptions<SupportPublicFormOptions>()
            .Bind(configuration.GetSection(SupportPublicFormOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupportPublicFormOptions>, SupportPublicFormOptionsValidator>();
        services.AddSingleton<Modules.Support.Application.ISupportPublicOptions, SupportPublicOptions>();
        // El grupo anónimo: Turnstile de la tienda (F28), países y celular (F19, F20) y la geografía de Colombia (F19).
        services.AddScoped<Modules.Support.Application.ISupportCaptcha, SupportCaptcha>();
        services.AddScoped<SupportPhoneNormalizer>();
        services.AddScoped<Modules.Support.Application.ISupportCountries>(provider => provider.GetRequiredService<SupportPhoneNormalizer>());
        services.AddScoped<Modules.Support.Application.ISupportPhoneNormalizer>(
            provider => provider.GetRequiredService<SupportPhoneNormalizer>());
        services.AddScoped<Modules.Support.Application.ISupportGeography, SupportGeography>();
        services.AddScoped<Modules.Support.Application.SupportPublicResolver>();
        services.AddScoped<Modules.Support.Application.ISupportTenantBranding, SupportTenantBranding>();
        services.AddScoped<Modules.Support.Application.ISupportOpeningEmail, SupportOpeningEmailSender>();
        services.AddScoped<Modules.Support.Application.SupportOpeningSender>();
        services.AddScoped<Modules.Support.Application.SupportOpeningDelivery>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.FilePublicTicketCommand, Modules.Support.Application.SupportPublicFiledDto>,
            Modules.Support.Application.FilePublicTicketHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.LookupPublicTicketCommand, Modules.Support.Application.SupportPublicTicketDto>,
            Modules.Support.Application.LookupPublicTicketHandler>();
        // Fase 4b (H19, H20): la verificación de la consulta, extraída para calificar por la consulta, y la encuesta por el enlace.
        services.AddScoped<Modules.Support.Application.SupportPublicAccess>();
        services.AddScoped<Modules.Support.Application.SupportPublicTicketView>();
        services.AddScoped<Modules.Support.Application.SupportPublicSurveys>();
        services.AddScoped<Modules.Support.Application.SupportSurveyAnswering>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.RatePublicTicketCommand, Modules.Support.Application.SupportPublicTicketDto>,
            Modules.Support.Application.RatePublicTicketHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.LookupPublicSurveyQuery, Modules.Support.Application.SupportPublicSurveyDto>,
            Modules.Support.Application.LookupPublicSurveyHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.AnswerPublicSurveyCommand, Modules.Support.Application.SupportPublicSurveyDto>,
            Modules.Support.Application.AnswerPublicSurveyHandler>();
        // Los adjuntos del formulario (plan F30 a F37): el archivo va por la subida sin miembro de Storage.
        services.AddScoped<Modules.Support.Application.ISupportAttachmentStorage, SupportAttachmentStorage>();
        services.AddScoped<
            ICommandHandler<
                Modules.Support.Application.OpenPublicAttachmentCommand, Modules.Support.Application.SupportPublicAttachmentSessionDto>,
            Modules.Support.Application.OpenPublicAttachmentHandler>();
        services.AddScoped<
            ICommandHandler<
                Modules.Support.Application.CompletePublicAttachmentCommand, Modules.Support.Application.SupportPublicAttachmentStatusDto>,
            Modules.Support.Application.CompletePublicAttachmentHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Support.Application.ResendPublicPinCommand, bool>,
            Modules.Support.Application.ResendPublicPinHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetPublicFormQuery, Modules.Support.Application.SupportPublicFormDto>,
            Modules.Support.Application.GetPublicFormHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.GetPublicConsultQuery, Modules.Support.Application.SupportPublicConsultDto>,
            Modules.Support.Application.GetPublicConsultHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListPublicSupportDepartmentsQuery, Modules.Support.Application.SupportPublicGeoListDto>,
            Modules.Support.Application.ListPublicSupportDepartmentsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Support.Application.ListPublicSupportCitiesQuery, Modules.Support.Application.SupportPublicGeoListDto>,
            Modules.Support.Application.ListPublicSupportCitiesHandler>();

        // `store` (spec de la tienda en línea, 2026-11-03): clientes, catálogo, archivos, empresas y cotizaciones son de otros módulos y
        // Store sólo declara puertos; el composition root los cablea. Los manejadores, a mano y con ruta completa, como Loans: uno
        // olvidado compila, mapea su endpoint y responde 500.
        services.AddScoped<Modules.Store.Application.IStoreTenantLookup, StoreTenantLookup>();
        services.AddScoped<Modules.Store.Application.IStoreFileLookup, StoreFileLookup>();
        services.AddScoped<Modules.Store.Application.IStoreCatalogReader, StoreCatalogReader>();
        services.AddScoped<Modules.Store.Application.IStoreBillingAccounts, StoreBillingAccounts>();
        services.AddScoped<Modules.Store.Application.IStoreCountries, StorePhoneNormalizer>();
        services.AddScoped<Modules.Store.Application.IStorePhoneNormalizer, StorePhoneNormalizer>();
        services.AddScoped<Modules.Store.Application.StoreSettingsComposer>();
        services.AddScoped<Modules.Store.Application.IStoreOrders, StoreOrdersAdapter>();
        services.AddScoped<Modules.Store.Application.IStoreCustomerDirectory, StoreCustomerDirectory>();
        services.AddScoped<Modules.Store.Application.IStoreMemberDirectory, StoreMemberDirectory>();
        services.AddScoped<Modules.Store.Application.IStoreGeography, StoreGeography>();
        services.AddScoped<Modules.Store.Application.StorefrontResolver>();
        services.AddScoped<Modules.Store.Application.PublicCartPricing>();
        services.AddScoped<
            ICommandHandler<Modules.Store.Application.SubmitOrderRequestCommand, (Modules.Store.Application.PublicOrderRequestReceiptDto Receipt, bool Created)>,
            Modules.Store.Application.SubmitOrderRequestHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.GetPublicStoreQuery, Modules.Store.Application.PublicStoreDto>,
            Modules.Store.Application.GetPublicStoreHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.ListPublicCategoriesQuery, Modules.Store.Application.PublicCategoriesDto>,
            Modules.Store.Application.ListPublicCategoriesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.ListPublicProductsQuery, Modules.Store.Application.PublicProductsPageDto>,
            Modules.Store.Application.ListPublicProductsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.GetPublicProductQuery, Modules.Store.Application.PublicProductDto>,
            Modules.Store.Application.GetPublicProductHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.QuotePublicCartQuery, Modules.Store.Application.PublicCartQuoteDto>,
            Modules.Store.Application.QuotePublicCartHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.ListPublicDepartmentsQuery, Modules.Store.Application.PublicGeoListDto>,
            Modules.Store.Application.ListPublicDepartmentsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.ListPublicCitiesQuery, Modules.Store.Application.PublicGeoListDto>,
            Modules.Store.Application.ListPublicCitiesHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.GetStoreSettingsQuery, Modules.Store.Application.StoreSettingsDto>,
            Modules.Store.Application.GetStoreSettingsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Store.Application.UpdateStoreSettingsCommand, Modules.Store.Application.StoreSettingsDto>,
            Modules.Store.Application.UpdateStoreSettingsHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Store.Application.PublishStoreCommand, Modules.Store.Application.StoreSettingsDto>,
            Modules.Store.Application.PublishStoreHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.PreviewStoreColorsQuery, Modules.Store.Application.StoreColorPreviewDto>,
            Modules.Store.Application.PreviewStoreColorsHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Store.Application.ListStoreBillingAccountsQuery, Modules.Store.Application.StoreBillingAccountListDto>,
            Modules.Store.Application.ListStoreBillingAccountsHandler>();

        // `buyers` (spec 2026-11-07): cuenta de comprador de plataforma. Buyers no referencia Store: el host de la ruta y Turnstile
        // entran por puertos que el composition root cablea. Los manejadores, a mano y con ruta completa: uno olvidado compila,
        // mapea su endpoint y responde 500. El validador entra con AddValidatorsFromAssemblyContaining.
        services.AddScoped<Modules.Buyers.Application.IBuyerStorefronts, BuyerStorefronts>();
        services.AddScoped<Modules.Buyers.Application.IBuyerOrders, BuyerOrdersAdapter>();
        services.AddScoped<Modules.Buyers.Application.IBuyerOrderLinker, BuyerOrderLinker>();
        services.AddScoped<Modules.Buyers.Application.IBuyerCaptcha, BuyerCaptcha>();
        services.AddScoped<Modules.Buyers.Application.BuyerProfileWriter>();
        services.AddScoped<Modules.Buyers.Application.BuyerEmailTokenIssuer>();
        services.AddScoped<Modules.Buyers.Application.BuyerSignup>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.RegisterBuyerCommand, bool>,
            Modules.Buyers.Application.RegisterBuyerHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.VerifyBuyerEmailCommand, bool>,
            Modules.Buyers.Application.VerifyBuyerEmailHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.ResendBuyerVerificationCommand, bool>,
            Modules.Buyers.Application.ResendBuyerVerificationHandler>();
        services.AddScoped<Modules.Buyers.Application.BuyerSessions>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.LoginBuyerCommand, Modules.Buyers.Application.BuyerSessionDto>,
            Modules.Buyers.Application.LoginBuyerHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Buyers.Application.GetBuyerSessionQuery, Modules.Buyers.Application.BuyerDto>,
            Modules.Buyers.Application.GetBuyerSessionHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Buyers.Application.ListBuyerOrdersQuery, Modules.Buyers.Application.BuyerOrderPageDto>,
            Modules.Buyers.Application.ListBuyerOrdersHandler>();
        services.AddScoped<
            IQueryHandler<Modules.Buyers.Application.GetBuyerOrderQuery, Modules.Buyers.Application.BuyerOrderDetailDto>,
            Modules.Buyers.Application.GetBuyerOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.CancelBuyerOrderCommand, Modules.Buyers.Application.BuyerOrderDetailDto>,
            Modules.Buyers.Application.CancelBuyerOrderHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.LogoutBuyerCommand, bool>,
            Modules.Buyers.Application.LogoutBuyerHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.UpdateBuyerProfileCommand, Modules.Buyers.Application.BuyerDto>,
            Modules.Buyers.Application.UpdateBuyerProfileHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.ChangeBuyerPasswordCommand, bool>,
            Modules.Buyers.Application.ChangeBuyerPasswordHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.DeleteBuyerAccountCommand, bool>,
            Modules.Buyers.Application.DeleteBuyerAccountHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.ForgotBuyerPasswordCommand, bool>,
            Modules.Buyers.Application.ForgotBuyerPasswordHandler>();
        services.AddScoped<
            ICommandHandler<Modules.Buyers.Application.ResetBuyerPasswordCommand, bool>,
            Modules.Buyers.Application.ResetBuyerPasswordHandler>();
        services.AddValidatorsFromAssemblyContaining<Modules.Buyers.Application.RegisterBuyerBodyValidator>();
        // Store → Buyers (D7): el pedido hashea la contraseña del alta y actualiza el perfil guardado sin ver Buyers.
        services.AddScoped<Modules.Buyers.Application.ReplaceBuyerProfileFromOrder>();
        services.AddScoped<Modules.Store.Application.IStoreBuyerAccounts, StoreBuyerAccounts>();

        // Wms (spec WMS 2026-10-31 §1.3): variantes y ciudades entran por puertos propios; Wms no referencia Catalog ni Geography.
        services.AddScoped<Modules.Wms.Application.IWmsCatalogReader, WmsCatalogReader>();
        services.AddScoped<Modules.Wms.Application.IWmsGeographyReader, WmsGeographyReader>();
        // Tareas de bodega (spec 2026-11-26): nombres y permisos de las membresías.
        services.AddScoped<Modules.Wms.Application.IWmsMemberDirectory, WmsMemberDirectory>();

        // Etiquetas de ubicación (RF-004, P-9): el servicio y la clave son los de Quotations:Pdf, que Wms no ve. Singleton con timeout propio,
        // como AddPdfRenderer: el default de HttpClient son 100 s y qcode-pdf corta la compilación a los 15. La fábrica va tipada con
        // el concreto para que MultiReplicaInventoryTests lo vea y lo escanee (spec 2026-10-29, D-6).
        services.AddSingleton<Modules.Wms.Application.IWmsPdfGateway, WmsPdfGateway>(sp =>
            new WmsPdfGateway(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                sp.GetRequiredService<IOptions<QuotationsOptions>>()));

        // Mismo patron (CAT-05) entre `quotations` y `customers`/`catalog`: ninguno referencia al
        // otro directo — QuotationsLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules
        // lo impide a proposito — y el composition root cablea los puertos que declara
        // `quotations` contra los repositorios que ya registraron AddCustomersInfrastructure y
        // AddCatalogInfrastructure.
        services.AddScoped<IQuotationCustomerLookup, QuotationCustomerLookup>();
        services.AddScoped<IQuotationAdvisorLookup, QuotationAdvisorLookup>();
        services.AddScoped<IQuotationProductLookup, QuotationProductLookup>();
        services.AddScoped<IQuotationCompanyLookup, QuotationCompanyLookup>();
        // Spec 2026-11-17 §5.3: las tasas de IVA por defecto de Colombia que pide Companies, sobre Catalog y Quotations.
        services.AddScoped<ICompanyDefaultTaxRates, CompanyDefaultTaxRates>();
        // Mismo patron (CAT-05) entre `quotations` y `geography`: resuelve la ciudad de una parte
        // con datos propios para el Excel de pedidos.
        services.AddScoped<IQuotationGeographyLookup, QuotationGeographyLookup>();
        // Spec 2026-11-15 §4: el correo de la cuenta de comprador del pedido, sobre el repositorio de Buyers.
        services.AddScoped<IQuotationBuyerAccountLookup, QuotationBuyerAccountLookup>();
        // Plan 2026-10-21: la cotización y el pedido salen por la conexión de Zenvia del tenant, que vive en Integrations.
        services.AddScoped<IWhatsAppChannelDirectory, QuotationsWhatsAppChannelDirectory>();
        // Spec 2026-10-21 D16/D17: la cotización por la conexión de Meta cruza a Mensajería; scoped por su DbContext.
        services.AddScoped<IMetaWhatsAppSender, QuotationsMetaWhatsAppSender>();
        // Arma la respuesta de una cotizacion con todo lo que su pantalla muestra, para que el
        // navegador no tenga que pedir cliente, miembros y catalogo por separado.
        services.AddScoped<IQuotationResponseComposer, QuotationResponseComposer>();
        services.AddScoped<IOrderResponseComposer, OrderResponseComposer>();
        services.AddScoped<IQuotationProductPricingLookup, QuotationProductPricingLookup>();
        // La tasa del IVA del envío (spec 2026-10-17 §4.2), sobre el repositorio de tasas de Catalog.
        services.AddScoped<IQuotationTaxRateLookup, QuotationTaxRateLookup>();
        // Impuestos por tipo (spec 2026-11-10 §7.5): las retenciones que congela un documento, con las tasas y la UVT de
        // Catalog y las tasas asignadas al cliente de Customers.
        services.AddScoped<IQuotationWithholdingLookup, QuotationWithholdingLookup>();
        // Impuestos por tipo (spec 2026-11-10 §7.2): Catalog verifica el país de una tasa contra las empresas del tenant.
        services.AddScoped<ITaxRateCountrySource, TaxRateCountrySource>();
        // Impuestos por tipo (spec 2026-11-10 §7.4): Customers valida y pinta las retenciones de un cliente contra el
        // catálogo, y Catalog sabe si una tasa está asignada a algún cliente. Dos puertos, un adaptador por lado.
        services.AddScoped<ICustomerTaxRateLookup, CustomerTaxRateLookup>();
        services.AddScoped<ITaxRateCustomerUsage, TaxRateCustomerUsage>();
        // ReteICA por empresa (spec 2026-11-12 §7.2): Catalog valida y pinta el municipio de una tasa municipal.
        services.AddScoped<ITaxRateGeographyLookup, TaxRateGeographyLookup>();
        // ReteICA por empresa (spec 2026-11-12 §7.3): Companies valida y pinta las tasas municipales de una empresa contra el
        // catálogo, y Catalog sabe si una tasa está asignada a alguna empresa. Dos puertos, un adaptador por lado.
        services.AddScoped<ICompanyTaxRateLookup, CompanyTaxRateLookup>();
        services.AddScoped<ITaxRateCompanyUsage, TaxRateCompanyUsage>();
        // Lo que el dominio no puede saber del envío (transportadora activa, recaudo, tasa del tenant):
        // lo usan crear, guardar y previsualizar la cotización y el pedido.
        services.AddScoped<ShippingResolver>();
        services.AddScoped<IQuotationFileLookup, QuotationFileLookup>();
        // Los comprobantes de pago públicos (spec 2026-09-15, P3): el publicador que copia al bucket
        // público o el que no hace nada, según Quotations:PaymentProofs:PublicLinks. Se decide al
        // resolver, con las opciones ya validadas por ValidateOnStart, mismo criterio que
        // IFileScanner en AddStorageInfrastructure. Scoped porque IFileResourceRepository lo es.
        services.AddScoped<PublicPaymentProofPublisher>();
        services.AddScoped<IPaymentProofPublisher>(provider =>
            provider.GetRequiredService<IOptions<QuotationsOptions>>().Value.PaymentProofs.PublicLinks
                ? provider.GetRequiredService<PublicPaymentProofPublisher>()
                : new DisabledPaymentProofPublisher());
        // P2 (spec 2026-09-15): con Quotations:PaymentProofs:PublicLinks encendida, el bucket
        // público es obligatorio. Vive acá porque es el único lugar que ve QuotationsOptions y
        // StorageOptions a la vez. Se suma al QuotationsOptionsValidator de
        // AddQuotationsInfrastructure, que ya declara ValidateOnStart: AddSingleton y no TryAdd,
        // porque con TryAdd este segundo validador no se registraría.
        services.AddSingleton<IValidateOptions<QuotationsOptions>, PaymentProofsOptionsValidator>();

        // El tick del worker de exportaciones. Scoped: ExportJobWorker abre un scope por job para
        // que cada uno tenga su DbContext limpio. Los procesadores por kind se registran con él
        // cuando existen (cotizaciones y pedidos).
        services.AddScoped<ExportJobRunner>();
        services.AddScoped<IExportJobProcessor, QuotationsExportProcessor>();
        // El Excel de pedidos sin indicativo (spec 2026-11-25): el partidor es de Customers.
        services.AddScoped<IQuotationPhoneNumbers, QuotationPhoneNumbers>();
        services.AddScoped<IExportJobProcessor, OrdersExportProcessor>();

        // Reporting es el caso extremo del mismo patron (CAT-05): el modulo no tiene tablas
        // propias, asi que **todos** sus origenes de datos cruzan una frontera de modulo. Van
        // despues de AddQuotationsInfrastructure, AddCustomersInfrastructure,
        // AddCatalogInfrastructure, AddIdentityInfrastructure y AddTenancyInfrastructure, porque
        // los adaptadores dependen de los DbContext que esos registran — y despues del
        // ICustomerGeographyLookup de arriba, que el de clientes reusa.
        services.AddScoped<ReportingPeopleLookup>();
        services.AddScoped<ReportingClientLookup>();
        services.AddScoped<IOrdersReportSource, OrdersReportSource>();
        services.AddScoped<IQuotationsReportSource, QuotationsReportSource>();
        services.AddScoped<IPriceChangeReportSource, PriceChangeReportSource>();
        services.AddScoped<ICustomerReportSource, CustomerReportSource>();
        services.AddScoped<IMarginReportSource, MarginReportSource>();

        // La semilla de arranque del ambiente desplegado (ver SeedOptions): apagada por
        // defecto, y ValidateOnStart la revisa antes de aceptar tráfico, mismo criterio que el
        // resto de las opciones que fallan rápido al arrancar.
        services.AddOptions<SeedOptions>()
            .Bind(configuration.GetSection(SeedOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SeedOptions>, SeedOptionsValidator>();

        // La carga sintética de la exportación (spec 2026-09-13): no hace nada con
        // Seed:ExportLoad:Quotations en 0, que es el default.
        services.AddHostedService<ExportLoadSeedWorker>();

        AddAuthorizationCapability(services, configuration);
        // La identidad del build (spec 2026-10-14): llega por configuración, no compilada, y no es
        // obligatoria. Se construye acá y no por el patrón de options porque OpenTelemetry necesita
        // el valor al registrarse, no al resolverse — y porque con Get<T> una sección ausente es
        // simplemente un objeto vacío, que es exactamente lo que corre en local.
        var deployment = new DeploymentInfo(
            configuration.GetSection(DeploymentOptions.SectionName).Get<DeploymentOptions>()
                ?? new DeploymentOptions(),
            environment);
        services.AddSingleton(deployment);
        services.AddQepObservability(configuration, environment, deployment.BuildNumber);
        AddAuthentication(services, configuration, environment);
        AddAuthorization(services);
        // Último a propósito (spec 2026-11-13, D-7): agrega el interceptor de timbres a cada DbContext ya registrado.
        Valkey.QueueDoorbellRegistration.AddQueueDoorbellInterceptor(services);
        return services;
    }

    // Registra la capacidad Authorization, el catálogo de sistema (sólo admin) y las plantillas de los roles que se siembran en
    // cada tenant. Las referencias de rol de la membresía (ADR 0016) mapean a la definición de admin o a una fila de
    // authorization.roles del tenant.
    private static void AddAuthorizationCapability(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // El catalogo del codigo sigue siendo singleton: son constantes del build.
        services.AddSingleton<IRoleCatalog, RoleCatalog>();
        // Spec 2026-10-07: puro y sin estado por tenant, así que singleton como el catálogo del
        // código. Indexa las PermissionDefinition registradas abajo.
        services.AddSingleton<ModuleEntitlementMask>();
        // La vista por tenant NO puede serlo: fusiona los roles que el tenant definio, y esos
        // cambian con un PUT y no con un deploy. Scoped, ademas, es lo que hace que memoizar
        // por request sea correcto — el scope dura lo que el request.
        services.AddScoped<ITenantRoleCatalog, TenantRoleCatalog>();
        services.AddAuthorizationInfrastructure(configuration);
        // Spec 2026-11-25 (roles por tenant): se llaman en el alta del tenant y al arrancar, nunca al resolver permisos. La lista
        // de tenants sale de Tenancy por este adaptador: Authorization no lee sus tablas.
        services.AddScoped<ITenantRoleSeeder, TenantRoleSeeder>();
        services.AddScoped<IRoleSeedTenantSource, TenancyRoleSeedTenantSource>();
        // IAuthorizationService lo registra AddQepValkey (spec 2026-11-13, D-5): AuthorizationService tal cual con
        // Valkey:Areas:Permissions apagado, o decorado por la caché de permisos con el área prendida.
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
                // Spec 2026-10-24 D-F1: fusionar duplicados es trabajo de quien atiende al cliente y del admin.
                CustomersPermissions.CustomerMerge,
                // Spec 2026-11-10 D-8: borrar es definitivo; sólo admin (el owner del tenant siempre lo es, `Membership.cs:529`).
                CustomersPermissions.CustomerDelete,
                // Spec 2026-10-26 D-9: quien atiende al titular registra su revocación; el admin también.
                CustomersPermissions.DataConsentRevoke,
                CustomersPermissions.ClassificationRead,
                CustomersPermissions.ClassificationManage,
                // Spec 2026-10-11 §4.3: renombrar o borrar etiquetas y borrar notas ajenas. Sólo admin: el
                // asesor crea etiquetas y notas con CustomerManage, pero no cambia lo que ven todos.
                CustomersPermissions.TagManage,
                CustomersPermissions.NoteModerate,
                QuotationsPermissions.QuotationRead,
                QuotationsPermissions.QuotationManage,
                OrdersPermissions.OrderRead,
                OrdersPermissions.OrderManage,
                // Aprobar es de quien revisa, no de quien registra: admin y facturación, nunca el
                // asesor. El rol vive en código, así que no hay migración de datos.
                OrdersPermissions.OrderApprove,
                // Sólo admin (spec 2026-09-16, decisión 5): anular deshace también un pedido ya
                // aprobado. El rol vive en código, así que no hay migración de datos.
                OrdersPermissions.OrderCancel,
                // Facturar y revertir la facturación (spec 2026-10-05, decisión 5): admin y
                // facturación. El rol vive en código, así que no hay migración de datos.
                OrdersPermissions.OrderInvoice,
                // Los cuatro reportes. Admin es el unico rol que ve los de cambios de precio y
                // padron de clientes: el primero expone el historial comercial completo del
                // catalogo, y el segundo el padron entero con datos de identificacion.
                ReportingPermissions.OrdersRead,
                ReportingPermissions.QuotationRead,
                ReportingPermissions.PriceChangeRead,
                ReportingPermissions.CustomerRead,
                // Sólo admin (decisión 2026-09-24): ver en los reportes de pedidos y cotizaciones
                // a todos los asesores. Sin él, el handler acota el reporte al asesor que llama.
                ReportingPermissions.AllAdvisorsRead,
                // Solo admin: el log expone trazas y mensajes crudos de todos los modulos.
                PlatformPermissions.RequestLogRead,
                PlatformPermissions.RequestLogPurge,
                // Punto de venta (spec 2026-10-07): admin tiene los siete, incluidos descontar y
                // anular, que el cajero no tiene.
                PosPermissions.SaleRead,
                PosPermissions.SaleCreate,
                PosPermissions.SaleCustomer,
                PosPermissions.SaleVoid,
                PosPermissions.SaleDiscount,
                PosPermissions.RegisterOperate,
                PosPermissions.RegisterRead,
                // Spec 2026-10-08 §2: el admin de fábrica es global, así que estos tres le llegan a
                // todo admin; el filtro de operador los deja vivos sólo en el tenant operador. Quien
                // asigna roles en QCode puede volver a alguien operador: aceptado (radio de explosión).
                OperatorPermissions.TenantsRead,
                OperatorPermissions.ModulesManage,
                OperatorPermissions.TenantsManage,
                // Spec 2026-10-26 §6: la consola de ciudades del operador, con el mismo criterio que las tres de arriba.
                GeographyOperatorPermissions.CitiesRead,
                GeographyOperatorPermissions.CitiesManage,
                // Spec 2026-11-10 (Impuestos por tipo): la consola de la unidad tributaria, con el mismo criterio.
                CatalogOperatorPermissions.TaxUnitsRead,
                CatalogOperatorPermissions.TaxUnitsManage,
                // Spec 2026-11-22: la consola de los parámetros legales (nómina), con el mismo criterio.
                HrOperatorPermissions.LegalParametersRead,
                HrOperatorPermissions.LegalParametersManage,
                // Reporte de uso de la plataforma: misma categoría y mismo criterio que los de arriba.
                OperatorPermissions.UsageRead,
                // Spec 2026-10-08 (Integraciones), decisión 3: de fábrica en admin. El rol vive en
                // código, así que no hay migración de datos.
                IntegrationsPermissions.ConnectionRead,
                IntegrationsPermissions.ConnectionManage,
                // Spec 2026-10-09 (Mensajería), decisión 5: la bandeja de WhatsApp. De núcleo; el
                // módulo messaging lo revisa cada handler. El rol vive en código: sin migración.
                MessagingPermissions.ConversationRead,
                MessagingPermissions.ConversationManage,
                // Spec 2026-10-11 §6.4 y decisión 16: las campañas llegan a miles de personas en nombre de la empresa
                // y el costo es por mensaje; de fábrica sólo en admin (no en advisor: P2 del spec).
                MessagingPermissions.CampaignManage,
                // Spec 2026-10-12 D-Q6: lo que el asesor manda en nombre de la empresa lo decide quien administra.
                // El rol vive en código: sin migración.
                MessagingPermissions.QuickReplyManage,
                // Spec 2026-10-15 D-R8: el catálogo de motivos de resolución. El rol vive en código: sin migración.
                MessagingPermissions.ResolutionReasonManage,
                // Spec 2026-10-21 D-H8: el horario decide cuándo atiende el asistente en nombre de la empresa; de fábrica sólo en admin.
                MessagingPermissions.BusinessHoursManage,
                // Spec 2026-10-14 D-T5: las plantillas de WhatsApp se gestionan desde QEP; de fábrica sólo en admin.
                MessagingPermissions.TemplateManage,
                // Spec 2026-10-13 D32: el embudo de ventas completo. De núcleo; la capacidad pipeline la revisa cada handler.
                PipelinePermissions.DealRead,
                PipelinePermissions.DealManage,
                PipelinePermissions.SettingsManage,
                // Spec 2026-10-21 D-C10: el chatbot atiende en nombre de la empresa; de fábrica sólo en admin. De núcleo.
                AutomationsPermissions.ChatbotRead,
                AutomationsPermissions.ChatbotManage,
                // Spec 2026-10-29 §1: un agente IA habla con los clientes en nombre de la empresa; de fábrica sólo en admin. De núcleo.
                AiPermissions.AgentRead,
                AiPermissions.AgentManage,
                // Spec 2026-11-17 (copiloto), D-C14 y OC-1: cada pregunta gasta saldo del tenant; de fábrica sólo en admin. De núcleo.
                AiPermissions.CopilotUse,
                AiPermissions.CopilotManage,
                // Spec 2026-10-23 D-14: el directorio de proveedores lo administra quien administra la empresa.
                SuppliersPermissions.Read,
                SuppliersPermissions.Manage,
                // Spec 2026-11-17 (compras): las órdenes de compra las administra quien administra la empresa (P-3, propuesta).
                PurchasesPermissions.OrderRead,
                PurchasesPermissions.OrderManage,
                // Spec 2026-11-17 (mayorista), P-3 aprobada: distribuidores los administra quien administra la empresa. Las listas de
                // precios son catalog.price_list.manage desde el spec 2026-11-22 (más abajo).
                WholesalePermissions.DistributorRead,
                WholesalePermissions.DistributorManage,
                // Spec 2026-11-18 (integración Siigo) §5.7: configurar las conexiones de Siigo y emitir la factura.
                SiigoPermissions.SettingsManage,
                SiigoPermissions.InvoiceIssue,
                // Spec 2026-11-21: ver las llamadas a Siigo que fallaron; el cuerpo lleva datos del cliente.
                SiigoPermissions.CallsRead,
                // Spec cobros-recurrentes (owner, 2026-10-29): los servicios que el tenant cobra de forma recurrente los administra quien administra la empresa.
                ServiceContractsPermissions.Read,
                ServiceContractsPermissions.Manage,
                // Spec de préstamos (2026-10-31): los préstamos que la empresa otorga los administra quien administra la empresa.
                LoansPermissions.Read,
                LoansPermissions.Manage,
                // Spec 2026-11-20 (RRHH fase 1): empleados y contratos los administra quien administra la empresa; ningún otro rol de
                // sistema los lleva (el salario y los datos bancarios son sensibles).
                HrPermissions.EmployeeRead,
                HrPermissions.EmployeeManage,
                HrPermissions.ContractRead,
                HrPermissions.ContractManage,
                HrPermissions.SettingsManage,
                // Spec 2026-11-23 (exámenes médicos): datos de salud; sólo admin los lleva de fábrica.
                HrPermissions.MedicalRead,
                HrPermissions.MedicalManage,
                // Spec 2026-11-21 (nómina): los cuatro permisos ven plata; sólo admin los lleva de fábrica.
                PayrollPermissions.PeriodRead,
                PayrollPermissions.PeriodManage,
                PayrollPermissions.PeriodApprove,
                PayrollPermissions.SettingsManage,
                // Spec 2026-11-21 (Agenda): admin lleva los cinco; las dos plantillas de la Agenda llevan los operativos y ninguna
                // la configuración (servicios, reglas y estructura son del administrador).
                SchedulingPermissions.AppointmentRead,
                SchedulingPermissions.AppointmentManage,
                SchedulingPermissions.AvailabilityManage,
                SchedulingPermissions.AgendaReadAll,
                SchedulingPermissions.SettingsManage,
                // Spec 2026-11-14 (soporte, C1): admin atiende tickets y además configura los tipos y el umbral de «por vencer».
                SupportPermissions.Read,
                SupportPermissions.Manage,
                SupportPermissions.SettingsManage,
                // Spec 2026-11-22 (tareas internas) D-4: los cuatro roles de fábrica pueden usar tareas.
                SupportPermissions.TaskUse,
                // Spec de la tienda en línea (owner, 2026-11-03): publicar la tienda y fijar sus cuentas de cobro es de admin y de nadie más.
                // La bandeja de solicitudes ya la tiene: es quotations.order.manage, el permiso de Pedidos.
                StorePermissions.SettingsManage,
                // Spec 2026-10-30: una clave de API actúa en nombre de la empresa sin que nadie esté conectado; de fábrica sólo en admin.
                ApiAccessPermissions.ApiKeyManage,
                // Spec 2026-11-10 §5: el costo y el margen son dato comercial sensible; de fábrica sólo en admin.
                CatalogPermissions.ProductCostRead,
                CatalogPermissions.ProductCostManage,
                // Spec 2026-11-22 (D-10): listas de precios por canal; las administra quien administra el catálogo.
                CatalogPermissions.PriceListManage,
                ReportingPermissions.MarginRead,
                // Spec WMS 2026-10-31 §11.5: de fábrica sólo en admin; el resto de roles los recibe como rol custom del tenant.
                WmsPermissions.WarehouseManage,
                WmsPermissions.OwnerManage,
                WmsPermissions.ProfileManage,
                WmsPermissions.DocumentRead,
                WmsPermissions.StockRead,
                WmsPermissions.StockExport,
                WmsPermissions.AdjustmentPropose,
                WmsPermissions.AdjustmentApprove,
                WmsPermissions.PurchaseOrderManage,
                WmsPermissions.TransferManage,
                WmsPermissions.TransferDispatch,
                WmsPermissions.ReceiptPerform,
                WmsPermissions.PutawayPerform,
                WmsPermissions.DiscrepancyResolve,
                WmsPermissions.CountPerform,
                WmsPermissions.CountApprove,
                WmsPermissions.TaskManage
            ]));
        // Spec 2026-11-25 (roles por tenant): admin es el único rol de sistema. Los cinco de abajo son plantillas: no conceden
        // nada ni salen en ningún catálogo; TenantRoleSeeder crea con ellas las filas de authorization.roles de cada tenant, que
        // el tenant edita o borra. Un permiso nuevo en una plantilla sólo llega a los tenants nuevos.
        services.AddSingleton(new RoleTemplate(
            RoleTemplateKeys.Advisor,
            "Asesor",
            "Gestiona clientes y consulta los datos maestros con los que cotiza.",
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
                // Spec 2026-10-24 D-F1: el asesor es quien ve los duplicados en la bandeja y en el listado.
                CustomersPermissions.CustomerMerge,
                // Spec 2026-10-26 D-9: el asesor es quien recibe el pedido de revocación del titular.
                CustomersPermissions.DataConsentRevoke,
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
                // US-12/US-14: enviar una cotizacion sube su PDF a Storage, y convertir en pedido
                // sube los comprobantes de pago -- las dos acciones de la asesora que tocan
                // archivos.
                StoragePermissions.FileUpload,
                StoragePermissions.FileRead,
                // Convertir una cotizacion aprobada en pedido (US-13 a US-16) es la continuacion
                // natural de cotizar, no una operacion separada que administre otro rol. Aprobar
                // (OrderApprove) queda afuera a proposito: quien registra el pedido no lo revisa.
                OrdersPermissions.OrderRead,
                OrdersPermissions.OrderManage,
                // Solo los dos reportes de su trabajo diario. Cambios de precio y padron de
                // clientes quedan en admin: son la vista agregada del negocio, no la operacion.
                ReportingPermissions.OrdersRead,
                ReportingPermissions.QuotationRead,
                // Spec 2026-10-09 (Mensajería), decisión 5: la asesora lee y responde la bandeja.
                MessagingPermissions.ConversationRead,
                MessagingPermissions.ConversationManage,
                // Spec 2026-10-15 D-R8: la asesora también corrige y archiva los motivos con que resuelve (decisión del owner).
                MessagingPermissions.ResolutionReasonManage,
                // Spec 2026-10-13 D32: la asesora trabaja sus negocios; los ajustes del embudo los decide quien administra.
                PipelinePermissions.DealRead,
                PipelinePermissions.DealManage,
                // Spec 2026-11-14 (soporte, C1): el asesor atiende los tickets; configurar los tipos y el umbral es de admin.
                SupportPermissions.Read,
                SupportPermissions.Manage,
                SupportPermissions.TaskUse,
                // Spec 2026-11-17 (mayorista), P-3 aprobada: el asesor ve los distribuidores (es el asesor asignado); invitar,
                // editar y bloquear, y las listas de precios, son de admin.
                WholesalePermissions.DistributorRead
            ]));
        services.AddSingleton(new RoleTemplate(
            RoleTemplateKeys.Billing,
            "Facturación",
            "Consulta la información de clientes necesaria para facturar.",
            [
                TenancyPermissions.SettingsRead,
                CustomersPermissions.CustomerRead,
                // Facturación ve clientes y pedidos, y aprueba y factura pedidos (abajo). No ve cotizaciones:
                // QuotationRead queda afuera a propósito (decisión del owner, 2026-10-01); lo que
                // necesita para facturar lo trae el detalle del pedido. Sólo lectura en pedidos,
                // salvo aprobar y facturar: facturar necesita ver el estado del pago y los comprobantes, no
                // convertir ni editar -- eso sigue siendo trabajo de la asesora.
                OrdersPermissions.OrderRead,
                // Dar el visto bueno al pedido: facturación revisa el pago y los comprobantes antes
                // de facturar, y es quien aprueba lo que la asesora registró.
                OrdersPermissions.OrderApprove,
                // Marcar como facturado lo que ya facturó afuera, y revertir la marca si se
                // equivocó (spec 2026-10-05, decisión 5). Es sólo el estado del pedido: la factura
                // no la emite QEP.
                OrdersPermissions.OrderInvoice,
                // Ver los comprobantes desde el detalle del pedido: el pedido sólo guarda el fileId,
                // y el enlace lo emite POST /files/{id}/download-url, que exige este permiso. Sin él,
                // OrderRead muestra la lista de comprobantes pero ninguno se abre (403).
                StoragePermissions.FileRead,
                // Spec 2026-10-23 D-14: facturación paga a los proveedores, así que ve y gestiona el directorio.
                SuppliersPermissions.Read,
                SuppliersPermissions.Manage,
                // Facturación cobra los servicios recurrentes: ve «Por cobrar» y marca los pagos (owner, 2026-10-29).
                ServiceContractsPermissions.Read,
                ServiceContractsPermissions.Manage,
                // Facturación cobra las cuotas de los préstamos: las ve y las marca pagadas (spec de préstamos, 2026-10-31).
                LoansPermissions.Read,
                LoansPermissions.Manage,
                // Spec 2026-11-18 (integración Siigo) §5.7: facturación emite la factura en Siigo, como marca el pedido facturado
                // (quotations.order.invoice).
                SiigoPermissions.InvoiceIssue,
                // Decisión del owner (2026-11-24): facturación ve las empresas y configura la conexión de Siigo. De Integraciones
                // sólo lee: integrations.connection.manage daría también WhatsApp, Instagram y Shopify, así que crear, editar la
                // credencial y desconectar siguen siendo de admin.
                CompaniesPermissions.CompanyRead,
                IntegrationsPermissions.ConnectionRead,
                SiigoPermissions.SettingsManage,
                // Decisión del owner (2026-11-25): facturación ve las fallas de las llamadas a Siigo para saber por qué no salió
                // su factura, aunque la petición y la respuesta lleven datos del cliente.
                SiigoPermissions.CallsRead,
                // Spec 2026-11-22 (tareas internas) D-4: cualquier miembro crea y trabaja tareas; no ve tickets de clientes.
                SupportPermissions.TaskUse
            ]));
        // Spec 2026-10-07, decisión 4: vende, abre y cierra su caja y lee sus ventas. Sin
        // descuentos ni anulación, y sin catálogo ni clientes: /pos/products le da lo que la caja
        // dibuja y elige o crea el cliente de la venta por /pos/customers, sin el padrón de Clientes
        // (spec 2026-10-14, decisión 4). Un tenant que quiera cajeros con descuento edita este
        // rol o hace uno propio.
        services.AddSingleton(new RoleTemplate(
            RoleTemplateKeys.Cashier,
            "Cajero",
            "Vende en el punto de venta y abre y cierra su propia caja.",
            [
                PosPermissions.SaleRead,
                PosPermissions.SaleCreate,
                PosPermissions.SaleCustomer,
                PosPermissions.RegisterOperate,
                // Spec 2026-11-22 (tareas internas) D-4: cualquier miembro crea y trabaja tareas; no ve tickets de clientes.
                SupportPermissions.TaskUse
            ]));
        // Spec 2026-11-21 (Agenda): su D-13 (roles de sistema en código) la reemplaza el spec 2026-11-25; se siembran como los
        // demás, en todo tenant y no al activar el módulo, y con el módulo apagado no son asignables (RoleAssignability). Llevan
        // sólo permisos de la Agenda, a propósito: uno de núcleo (Clientes) los haría asignables en un tenant sin Agenda, así que
        // la búsqueda y el alta de clientes de la cita irán por la propia Agenda, como en el POS. Ninguno lleva
        // `settings.manage`: servicios, reglas y estructura son del administrador.
        services.AddSingleton(new RoleTemplate(
            RoleTemplateKeys.SchedulingManager,
            "Gestor de agenda",
            "Ve y administra las agendas de todos los médicos o recursos: citas, franjas y bloqueos.",
            [
                SchedulingPermissions.AppointmentRead,
                SchedulingPermissions.AppointmentManage,
                SchedulingPermissions.AvailabilityManage,
                SchedulingPermissions.AgendaReadAll
            ]));
        services.AddSingleton(new RoleTemplate(
            RoleTemplateKeys.SchedulingProvider,
            "Profesional de agenda",
            "Ve y administra su propia agenda: la del recurso vinculado a su usuario.",
            [
                SchedulingPermissions.AppointmentRead,
                SchedulingPermissions.AppointmentManage,
                SchedulingPermissions.AvailabilityManage
            ]));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.SettingsRead,
            "Leer configuración",
            "Permite consultar la configuración del tenant.",
            "Tenancy",
            "low",
            RequiredModules: [])
        {
            Shape = new("tenancy.settings", "Configuración", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.SettingsUpdate,
            "Actualizar configuración",
            "Permite modificar la configuración del tenant.",
            "Tenancy",
            "high",
            RequiredModules: [])
        {
            Shape = new("tenancy.settings", "Configuración", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.AdvisorshipInvite,
            "Invitar miembros",
            "Permite invitar usuarios al tenant.",
            "Tenancy",
            "medium",
            RequiredModules: [])
        {
            Shape = new("tenancy.members", "Miembros", "invite"),
        });
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.AdvisorshipRead,
            "Leer miembros",
            "Permite consultar membresías y catálogo de roles/permisos.",
            "Tenancy",
            "low",
            RequiredModules: [])
        {
            Shape = new("tenancy.members", "Miembros", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.AdvisorshipManage,
            "Gestionar miembros",
            "Permite suspender, remover y cambiar los roles que tiene un miembro.",
            "Tenancy",
            "high",
            RequiredModules: [])
        {
            Shape = new("tenancy.members", "Miembros", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.AdvisorshipRolesManage,
            "Definir roles",
            "Permite crear roles propios y elegir que permisos concede cada uno.",
            "Tenancy",
            "high",
            RequiredModules: [])
        {
            Shape = new("tenancy.roles", "Roles", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            StoragePermissions.FileUpload,
            "Subir archivos",
            "Permite crear sesiones de carga y completar uploads.",
            "Storage",
            "medium",
            RequiredModules: [])
        {
            Shape = new("storage.file", "Archivos", "upload"),
        });
        services.AddSingleton(new PermissionDefinition(
            StoragePermissions.FileRead,
            "Leer archivos",
            "Permite solicitar URLs de descarga.",
            "Storage",
            "medium",
            RequiredModules: [])
        {
            Shape = new("storage.file", "Archivos", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            StoragePermissions.FileDelete,
            "Eliminar archivos",
            "Permite marcar archivos como eliminados.",
            "Storage",
            "high",
            RequiredModules: [])
        {
            Shape = new("storage.file", "Archivos", "delete"),
        });
        services.AddSingleton(new PermissionDefinition(
            StoragePermissions.FilePublish,
            "Publicar imágenes",
            "Permite publicar y despublicar imágenes y sus variantes.",
            "Storage",
            "high",
            RequiredModules: [])
        {
            Shape = new("storage.file", "Archivos", "publish"),
        });
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.ProductRead,
            "Leer productos",
            "Permite consultar el catálogo de productos del tenant.",
            "Catalog",
            "low",
            RequiredModules: [ModuleKeys.Catalog])
        {
            Shape = new("catalog.product", "Productos", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.ProductManage,
            "Gestionar productos",
            "Permite crear, editar e inactivar productos.",
            "Catalog",
            "medium",
            RequiredModules: [ModuleKeys.Catalog])
        {
            Shape = new("catalog.product", "Productos", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.TaxRateRead,
            "Leer tasas de impuesto",
            "Permite consultar las tasas de impuesto del tenant.",
            "Catalog",
            "low",
            RequiredModules: [ModuleKeys.Catalog])
        {
            Shape = new("catalog.tax_rate", "Tasas de impuesto", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.TaxRateManage,
            "Gestionar tasas de impuesto",
            "Permite crear, editar e inactivar tasas de impuesto.",
            "Catalog",
            "high",
            RequiredModules: [ModuleKeys.Catalog])
        {
            Shape = new("catalog.tax_rate", "Tasas de impuesto", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.ProductCostRead,
            "Ver costos de productos",
            "Permite ver el costo unitario de las variantes.",
            "Catalog",
            "medium",
            RequiredModules: [ModuleKeys.Catalog])
        {
            Shape = new("catalog.product_cost", "Costos", "read"),
        });
        // Spec 2026-11-22 (D-10): sin [wholesale]: el POS, la tienda y las cotizaciones usan listas sin el módulo mayorista.
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.PriceListManage,
            "Gestionar listas de precios",
            "Permite crear, editar y borrar listas de precios, sus precios fijos y asignarlas a los canales.",
            "Catalog",
            "medium",
            RequiredModules: [ModuleKeys.Catalog])
        {
            Shape = new("catalog.price_list", "Listas de precios", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.ProductCostManage,
            "Gestionar costos de productos",
            "Permite escribir y borrar el costo unitario de las variantes.",
            "Catalog",
            "medium",
            RequiredModules: [ModuleKeys.Catalog])
        {
            Shape = new("catalog.product_cost", "Costos", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            CompaniesPermissions.CompanyRead,
            "Leer empresas",
            "Permite consultar las empresas del tenant.",
            "Companies",
            "low",
            RequiredModules: [ModuleKeys.Companies])
        {
            Shape = new("companies.company", "Empresas", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            CompaniesPermissions.CompanyManage,
            "Gestionar empresas",
            "Permite crear, editar, inactivar y reactivar empresas.",
            "Companies",
            "medium",
            RequiredModules: [ModuleKeys.Companies])
        {
            Shape = new("companies.company", "Empresas", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.CustomerRead,
            "Leer clientes",
            "Permite consultar el listado y el detalle de los clientes del tenant.",
            "Customers",
            "low",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.customer", "Clientes", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.CustomerManage,
            "Gestionar clientes",
            "Permite crear, editar, inactivar y reactivar clientes.",
            "Customers",
            "medium",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.customer", "Clientes", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.CustomerImport,
            "Importar clientes",
            "Permite cargar clientes masivamente desde un archivo Excel.",
            "Customers",
            // Alto y no medio: una carga masiva escribe cientos de registros de datos personales
            // de una sola vez, y el gate CLI-00 todavia tiene abierta la politica de retencion de
            // PII. Separado de manage justamente para poder darlo a menos gente.
            "high",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.customer", "Clientes", "import"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.CustomerMerge,
            "Fusionar clientes",
            "Permite fusionar clientes duplicados en uno. No se puede deshacer.",
            "Customers",
            "high",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.customer", "Clientes", "merge"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.CustomerDelete,
            "Borrar clientes",
            "Permite borrar clientes de forma definitiva. No se puede deshacer.",
            "Customers",
            "high",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.customer", "Clientes", "delete"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.DataConsentRevoke,
            "Revocar autorización de datos",
            "Permite registrar que un cliente revocó su autorización de tratamiento de datos personales.",
            "Customers",
            "high",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.data_consent", "Autorización de datos", "revoke"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.ClassificationRead,
            "Leer clasificaciones de clientes",
            "Permite consultar el catalogo de clasificaciones de clientes del tenant.",
            "Customers",
            "low",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.classification", "Clasificaciones", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.ClassificationManage,
            "Gestionar clasificaciones de clientes",
            "Permite crear, editar, inactivar, reactivar y eliminar clasificaciones de clientes.",
            "Customers",
            "medium",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.classification", "Clasificaciones", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.TagManage,
            "Gestionar etiquetas de clientes",
            "Permite renombrar, cambiar de color y eliminar etiquetas de clientes. Crearlas y asignarlas es de quien gestiona clientes.",
            "Customers",
            "medium",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.tag", "Etiquetas", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            CustomersPermissions.NoteModerate,
            "Moderar notas de clientes",
            "Permite eliminar las notas internas que escribió otra persona.",
            "Customers",
            "medium",
            RequiredModules: [ModuleKeys.Customers])
        {
            Shape = new("customers.note", "Notas", "moderate"),
        });
        services.AddSingleton(new PermissionDefinition(
            QuotationsPermissions.QuotationRead,
            "Leer cotizaciones",
            "Permite consultar el listado y el detalle de las cotizaciones del tenant.",
            "Quotations",
            "low",
            RequiredModules: [ModuleKeys.Quotations])
        {
            Shape = new("quotations.quotation", "Cotizaciones", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            QuotationsPermissions.QuotationManage,
            "Gestionar cotizaciones",
            "Permite crear y editar cotizaciones en borrador, incluidas sus lineas de producto.",
            "Quotations",
            "medium",
            RequiredModules: [ModuleKeys.Quotations])
        {
            Shape = new("quotations.quotation", "Cotizaciones", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderRead,
            "Leer pedidos",
            "Permite consultar el pedido convertido de una cotización.",
            "Quotations",
            "low",
            RequiredModules: [ModuleKeys.Orders])
        {
            Shape = new("quotations.order", "Pedidos", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderManage,
            "Gestionar pedidos",
            "Permite convertir una cotización enviada en pedido, con sus comprobantes de pago.",
            "Quotations",
            "medium",
            RequiredModules: [ModuleKeys.Orders])
        {
            Shape = new("quotations.order", "Pedidos", "manage"),
        });
        // Aparte de OrderManage: quien carga los comprobantes (el asesor) no es quien verifica la plata.
        // Spec 2026-11-10 §7: la constante y el valor siguen; cambia lo que significa y su nombre visible.
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderApprove,
            "Marcar pedidos como pagados",
            "Permite marcar un pedido como pagado, deshacerlo y quitar comprobantes de un pedido pagado.",
            "Quotations",
            "medium",
            RequiredModules: [ModuleKeys.Orders])
        {
            Shape = new("quotations.order", "Pedidos", "mark_paid"),
        });
        // High y sólo en admin, mismo criterio que TaxRateManage: revierte un pedido que otra
        // persona ya registró (spec 2026-11-10: abierto, sin preparar ni facturar).
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderCancel,
            "Anular pedidos",
            "Permite anular un pedido abierto que no esté preparado ni facturado, con un motivo obligatorio.",
            "Quotations",
            "high",
            RequiredModules: [ModuleKeys.Orders])
        {
            Shape = new("quotations.order", "Pedidos", "void"),
        });
        // Medium, igual que marcar pagado: sólo cambia la marca del pedido, no emite ni anula nada fuera
        // de QEP (spec 2026-10-05). Exigir el pago antes es el ajuste RequireFullPaymentToInvoice (spec 2026-11-10).
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderInvoice,
            "Facturar pedidos",
            "Permite marcar un pedido como facturado y revertir esa marca. Si antes debe estar pagado lo decide el ajuste del pedido de cada tenant.",
            "Quotations",
            "medium",
            RequiredModules: [ModuleKeys.Orders])
        {
            Shape = new("quotations.order", "Pedidos", "invoice"),
        });
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.OrdersRead,
            "Reporte de pedidos",
            "Permite consultar y exportar el reporte de pedidos convertidos del tenant.",
            "Reporting",
            "low",
            RequiredModules: [ModuleKeys.Reporting, ModuleKeys.Orders])
        {
            Shape = new("reporting.order", "Pedidos", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.QuotationRead,
            "Reporte de cotizaciones",
            "Permite consultar y exportar el reporte de cotizaciones del tenant.",
            "Reporting",
            "low",
            RequiredModules: [ModuleKeys.Reporting, ModuleKeys.Quotations])
        {
            Shape = new("reporting.quotation", "Cotizaciones", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.PriceChangeRead,
            "Reporte de cambios de precio",
            "Permite consultar y exportar el historico de cambios de precio del catalogo.",
            "Reporting",
            // Medio y no bajo: el historico completo de precios de un catalogo es el margen del
            // negocio visto de costado, y por eso queda solo en admin.
            "medium",
            RequiredModules: [ModuleKeys.Reporting, ModuleKeys.Catalog])
        {
            Shape = new("reporting.price_change", "Cambios de precio", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.CustomerRead,
            "Reporte de clientes",
            "Permite consultar y exportar el padron de clientes (Clientes CUC) del tenant.",
            "Reporting",
            "low",
            RequiredModules: [ModuleKeys.Reporting, ModuleKeys.Customers])
        {
            Shape = new("reporting.customer", "Clientes", "read"),
        });
        // Medio y no bajo, mismo criterio que PriceChangeRead: es la venta de cada asesor vista
        // por otro. No abre ningún reporte por sí solo; amplía el alcance de los de pedidos y
        // cotizaciones, que sin él muestran sólo lo de quien consulta. Spec 2026-10-07: exige sólo
        // reporting, porque esos reportes ya quedan enmascarados por su fuente.
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.AllAdvisorsRead,
            "Ver reportes de todos los asesores",
            "Permite ver en los reportes de pedidos y cotizaciones los datos de todos los asesores, no sólo los propios.",
            "Reporting",
            "medium",
            RequiredModules: [ModuleKeys.Reporting])
        {
            Shape = new("reporting.all_advisors", "Datos de todos los asesores", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.MarginRead,
            "Reporte de margen",
            "Permite consultar ventas netas, costo y margen bruto por producto.",
            "Reporting",
            "medium",
            RequiredModules: [ModuleKeys.Reporting, ModuleKeys.Catalog])
        {
            Shape = new("reporting.margin", "Margen", "read"),
        });
        // "high" las dos: el log arrastra la traza y el mensaje crudo de cualquier modulo --lo
        // que un 500 lleve adentro-- y el purgado borra en lote y no se deshace.
        services.AddSingleton(new PermissionDefinition(
            PlatformPermissions.RequestLogRead,
            "Leer el log de la aplicacion",
            "Permite consultar los POST, PUT, PATCH y DELETE que fallaron, con su error completo.",
            "Platform",
            "high",
            RequiredModules: [])
        {
            Shape = new("platform.request_log", "Log de la aplicación", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            PlatformPermissions.RequestLogPurge,
            "Purgar el log de la aplicacion",
            "Permite borrar del log lo anterior a la ultima semana.",
            "Platform",
            "high",
            RequiredModules: [])
        {
            Shape = new("platform.request_log", "Log de la aplicación", "purge"),
        });
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleRead,
            "Ver ventas y cierres de caja",
            "Permite consultar las ventas del punto de venta y los cierres de caja propios.",
            "Pos",
            "low",
            RequiredModules: [ModuleKeys.Pos])
        {
            Shape = new("pos.sale", "Ventas", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleCreate,
            "Vender en caja",
            "Permite registrar ventas en el punto de venta con la caja propia abierta.",
            "Pos",
            "medium",
            RequiredModules: [ModuleKeys.Pos])
        {
            Shape = new("pos.sale", "Ventas", "create"),
        });
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleCustomer,
            "Elegir o crear el cliente de una venta",
            "Permite buscar el cliente de una venta del punto de venta o darlo de alta rápida.",
            "Pos",
            "medium",
            RequiredModules: [ModuleKeys.Pos, ModuleKeys.Customers])
        {
            Shape = new("pos.sale", "Ventas", "customer"),
        });
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleDiscount,
            "Dar descuentos en caja",
            "Permite dar descuentos por línea y cobrar ventas en $0 en el punto de venta.",
            "Pos",
            "high",
            RequiredModules: [ModuleKeys.Pos])
        {
            Shape = new("pos.sale", "Ventas", "discount"),
        });
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleVoid,
            "Anular ventas de caja",
            "Permite anular, con un motivo, una venta cuya caja sigue abierta.",
            "Pos",
            "high",
            RequiredModules: [ModuleKeys.Pos])
        {
            Shape = new("pos.sale", "Ventas", "void"),
        });
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.RegisterOperate,
            "Abrir y cerrar su caja",
            "Permite abrir y cerrar la caja propia del punto de venta.",
            "Pos",
            "medium",
            RequiredModules: [ModuleKeys.Pos])
        {
            Shape = new("pos.register", "Cajas", "operate"),
        });
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.RegisterRead,
            "Ver todas las cajas y ventas",
            "Permite consultar las cajas y las ventas de todos los cajeros del tenant.",
            "Pos",
            "medium",
            RequiredModules: [ModuleKeys.Pos])
        {
            Shape = new("pos.register", "Cajas", "read"),
        });
        // Spec 2026-10-08 §2: categoría "Operator" (no "Platform", que es la del log de fallas) y
        // núcleo, para que el enmascarado por módulos no las toque.
        services.AddSingleton(new PermissionDefinition(
            OperatorPermissions.TenantsRead,
            "Ver tenants de la plataforma",
            "Permite listar los tenants, ver sus módulos y su historial.",
            "Operator",
            "medium",
            RequiredModules: [])
        {
            Shape = new("operator.tenants", "Tenants", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            OperatorPermissions.ModulesManage,
            "Gestionar módulos de los tenants",
            "Permite activar o desactivar los módulos de cualquier tenant.",
            "Operator",
            "high",
            RequiredModules: [])
        {
            Shape = new("operator.modules", "Módulos de los tenants", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            OperatorPermissions.TenantsManage,
            "Activar o inactivar tenants",
            "Permite inactivar un tenant, lo que corta el acceso de toda la empresa, o reactivarlo.",
            "Operator",
            "high",
            RequiredModules: [])
        {
            Shape = new("operator.tenants", "Tenants", "manage"),
        });
        // Spec 2026-10-26 §6: misma categoría y núcleo. Gestionar es "high": un sinónimo corrige pedidos y clientes de todos los
        // tenants.
        services.AddSingleton(new PermissionDefinition(
            GeographyOperatorPermissions.CitiesRead,
            "Ver ciudades sin resolver",
            "Permite ver la bandeja de ciudades sin resolver, los sinónimos y lo que cambiaría al confirmarlos.",
            "Operator",
            "medium",
            RequiredModules: [])
        {
            Shape = new("operator.cities", "Ciudades", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            GeographyOperatorPermissions.CitiesManage,
            "Gestionar sinónimos de ciudades",
            "Permite confirmar, descartar, corregir y borrar sinónimos de ciudades, que corrigen pedidos y clientes de todos los tenants.",
            "Operator",
            "high",
            RequiredModules: [])
        {
            Shape = new("operator.cities", "Ciudades", "manage"),
        });
        // Spec 2026-11-10: la unidad tributaria. Cargar un valor mueve el umbral de las retenciones de todos los tenants: "high".
        services.AddSingleton(new PermissionDefinition(
            CatalogOperatorPermissions.TaxUnitsRead,
            "Ver la unidad tributaria",
            "Permite ver el valor de la unidad tributaria (UVT) cargado por país y año.",
            "Operator",
            "medium",
            RequiredModules: [])
        {
            Shape = new("operator.tax_units", "Unidad tributaria", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            CatalogOperatorPermissions.TaxUnitsManage,
            "Gestionar la unidad tributaria",
            "Permite cargar y corregir el valor de la unidad tributaria de un año, que fija desde qué base se retiene en todos los tenants.",
            "Operator",
            "high",
            RequiredModules: [])
        {
            Shape = new("operator.tax_units", "Unidad tributaria", "manage"),
        });
        // Spec 2026-11-22: los parámetros legales de nómina. Cambiar un valor mueve los cálculos de todos los tenants: "high".
        services.AddSingleton(new PermissionDefinition(
            HrOperatorPermissions.LegalParametersRead,
            "Ver los parámetros legales",
            "Permite ver los valores legales de nómina (salario mínimo, aportes, provisiones, fondo de solidaridad) y su historial.",
            "Operator",
            "medium",
            RequiredModules: [])
        {
            Shape = new("operator.legal_parameters", "Parámetros legales", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            HrOperatorPermissions.LegalParametersManage,
            "Gestionar los parámetros legales",
            "Permite cargar, corregir y borrar los valores legales de nómina, a mano o por Excel: los usan los cálculos de todos los tenants.",
            "Operator",
            "high",
            RequiredModules: [])
        {
            Shape = new("operator.legal_parameters", "Parámetros legales", "manage"),
        });
        // Reporte de uso: sólo lectura, por eso "medium". Núcleo, como los demás operator.*.
        services.AddSingleton(new PermissionDefinition(
            OperatorPermissions.UsageRead,
            "Ver el uso de la plataforma",
            "Permite ver cuántas conversaciones tiene cada tenant, los registros nuevos y quién está conectado.",
            "Operator",
            "medium",
            RequiredModules: [])
        {
            Shape = new("operator.usage", "Uso de la plataforma", "read"),
        });

        // Spec 2026-10-08 (Integraciones), «Permisos»: núcleo, para que el enmascarado por módulos no
        // las toque. Gestionar es "high": cambia las credenciales con las que la empresa le habla a sus
        // clientes.
        services.AddSingleton(new PermissionDefinition(
            IntegrationsPermissions.ConnectionRead,
            "Ver integraciones",
            "Permite ver las conexiones del tenant con plataformas externas, sin sus credenciales.",
            "Integrations",
            "medium",
            RequiredModules: [])
        {
            Shape = new("integrations.connection", "Conexiones", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            IntegrationsPermissions.ConnectionManage,
            "Gestionar integraciones",
            "Permite conectar, probar, pausar, reanudar y eliminar conexiones con plataformas externas y cambiar sus credenciales.",
            "Integrations",
            "high",
            RequiredModules: [])
        {
            Shape = new("integrations.connection", "Conexiones", "manage"),
        });
        // Spec 2026-10-09 §6.3: categoría "Messaging" y núcleo (RequiredModules vacío), para que el
        // enmascarado por módulos no las toque y el 403 con módulo apagado salga con su código.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.ConversationRead,
            "Ver la bandeja de WhatsApp",
            "Permite ver las conversaciones de WhatsApp del tenant, su hilo y sus archivos.",
            "Messaging",
            "medium",
            RequiredModules: [])
        {
            Shape = new("messaging.conversation", "Conversaciones", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.ConversationManage,
            "Responder en la bandeja de WhatsApp",
            "Permite responder mensajes, marcarlos como leídos y resolver o reabrir conversaciones.",
            "Messaging",
            "medium",
            RequiredModules: [])
        {
            Shape = new("messaging.conversation", "Conversaciones", "manage"),
        });
        // Spec 2026-10-11 §6.4: núcleo como las otras dos de Messaging; el módulo lo revisa cada handler.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.CampaignManage,
            "Gestionar campañas de WhatsApp",
            "Permite crear, programar, lanzar y cancelar campañas de plantillas de WhatsApp a etiquetas o clientes; cada mensaje tiene costo.",
            "Messaging",
            "high",
            RequiredModules: [])
        {
            Shape = new("messaging.campaign", "Campañas", "manage"),
        });
        // Spec 2026-10-12 §3.5: núcleo como las demás de Messaging; el módulo lo revisa cada handler.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.QuickReplyManage,
            "Gestionar respuestas rápidas de WhatsApp",
            "Permite crear, editar y borrar los textos guardados que los asesores insertan en la bandeja de WhatsApp.",
            "Messaging",
            "medium",
            RequiredModules: [])
        {
            Shape = new("messaging.quick_reply", "Respuestas rápidas", "manage"),
        });
        // Spec 2026-10-15 §3.7: núcleo como las demás de Messaging; el módulo lo revisa cada handler.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.ResolutionReasonManage,
            "Gestionar motivos de resolución",
            "Permite renombrar y archivar los motivos con los que se resuelven las conversaciones de la bandeja.",
            "Messaging",
            "low",
            RequiredModules: [])
        {
            Shape = new("messaging.resolution_reason", "Motivos de resolución", "manage"),
        });
        // Spec 2026-10-21 D-H8: núcleo como las demás de Messaging; el módulo lo revisa cada handler.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.BusinessHoursManage,
            "Administrar horarios de atención",
            "Permite crear, editar, duplicar y asignar los horarios de atención de los números de la bandeja.",
            "Messaging",
            "low",
            RequiredModules: [])
        {
            Shape = new("messaging.business_hours", "Horarios de atención", "manage"),
        });
        // Spec 2026-10-14 §4.6: núcleo como las demás de Messaging; el módulo lo revisa cada handler.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.TemplateManage,
            "Gestionar plantillas de WhatsApp",
            "Permite crear, sincronizar y borrar las plantillas de WhatsApp de una conexión y aprovisionar la plantilla de cotización; cada plantilla pasa por la revisión de Meta.",
            "Messaging",
            "high",
            RequiredModules: [])
        {
            Shape = new("messaging.template", "Plantillas", "manage"),
        });
        // Spec 2026-10-13 D23 y P1 del plan: categoría "Pipeline" y núcleo (RequiredModules vacío), para que el enmascarado por
        // módulos no las toque y el 403 con la capacidad apagada salga con su código.
        services.AddSingleton(new PermissionDefinition(
            PipelinePermissions.DealRead,
            "Ver el embudo de ventas",
            "Permite ver el tablero, los negocios del tenant, el triage de conversaciones y los ajustes del embudo.",
            "Pipeline",
            "low",
            RequiredModules: [])
        {
            Shape = new("pipeline.deal", "Negocios", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            PipelinePermissions.DealManage,
            "Gestionar negocios",
            "Permite crear, editar, mover, ganar, perder y reabrir negocios, enlazar cotizaciones y decidir el triage.",
            "Pipeline",
            "medium",
            RequiredModules: [])
        {
            Shape = new("pipeline.deal", "Negocios", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            PipelinePermissions.SettingsManage,
            "Configurar el embudo",
            "Permite cambiar las etapas del embudo y los motivos de pérdida y de descarte.",
            "Pipeline",
            "medium",
            RequiredModules: [])
        {
            Shape = new("pipeline.settings", "Configuración del embudo", "manage"),
        });
        // Spec 2026-10-21 D-C10: categoría "Automations" y núcleo (RequiredModules vacío), como Pipeline: con [automations] el 403
        // con la capacidad apagada saldría authorization.denied en vez de tenancy.module_not_enabled.
        services.AddSingleton(new PermissionDefinition(
            AutomationsPermissions.ChatbotRead,
            "Ver chatbots",
            "Permite ver los chatbots, sus plantillas y las conexiones que atienden.",
            "Automations",
            "low",
            RequiredModules: [])
        {
            Shape = new("automations.chatbot", "Chatbots", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            AutomationsPermissions.ChatbotManage,
            "Gestionar chatbots",
            "Permite crear, editar, publicar, pausar y borrar chatbots y sus plantillas.",
            "Automations",
            "medium",
            RequiredModules: [])
        {
            Shape = new("automations.chatbot", "Chatbots", "manage"),
        });
        // Spec 2026-10-29 §1: categoría "Ai" y núcleo (RequiredModules vacío), como Automations: con [ai] el 403 con la capacidad
        // apagada saldría authorization.denied en vez de tenancy.module_not_enabled.
        services.AddSingleton(new PermissionDefinition(
            AiPermissions.AgentRead,
            "Ver agentes IA",
            "Permite ver los agentes IA, los modelos disponibles, su consumo y el estado de las claves de los proveedores.",
            "Ai",
            "low",
            RequiredModules: [])
        {
            Shape = new("ai.agent", "Agentes IA", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            AiPermissions.AgentManage,
            "Gestionar agentes IA",
            "Permite crear, editar, probar, publicar, pausar y borrar agentes IA.",
            "Ai",
            "high",
            RequiredModules: [])
        {
            Shape = new("ai.agent", "Agentes IA", "manage"),
        });
        // Spec 2026-11-17 (copiloto), D-C14: de núcleo como los de agentes; cada handler revisa la capacidad copilot.
        services.AddSingleton(new PermissionDefinition(
            AiPermissions.CopilotUse,
            "Usar el copiloto",
            "Permite preguntarle al copiloto por los datos de la empresa. Sólo ve lo que el miembro ya puede ver.",
            "Ai",
            "medium",
            RequiredModules: [])
        {
            Shape = new("ai.copilot", "Copiloto", "use"),
        });
        services.AddSingleton(new PermissionDefinition(
            AiPermissions.CopilotManage,
            "Configurar el copiloto",
            "Permite elegir el proveedor y el modelo del copiloto, su contexto del negocio, el tope mensual de tokens y la retención.",
            "Ai",
            "high",
            RequiredModules: [])
        {
            Shape = new("ai.copilot", "Copiloto", "manage"),
        });
        // Spec 2026-10-23 D-14: proveedores exige el módulo suppliers.
        services.AddSingleton(new PermissionDefinition(
            SuppliersPermissions.Read,
            "Ver proveedores",
            "Permite ver el directorio de proveedores y exportarlo.",
            "Suppliers",
            "low",
            RequiredModules: [ModuleKeys.Suppliers])
        {
            Shape = new("suppliers.supplier", "Proveedores", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            SuppliersPermissions.Manage,
            "Gestionar proveedores",
            "Permite crear, editar, activar y desactivar proveedores.",
            "Suppliers",
            "medium",
            RequiredModules: [ModuleKeys.Suppliers])
        {
            Shape = new("suppliers.supplier", "Proveedores", "manage"),
        });
        // Spec 2026-11-17 (compras): con [purchases], ModuleEntitlementMask quita el permiso al apagar el módulo y el editor de
        // roles no lo ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled.
        services.AddSingleton(new PermissionDefinition(
            PurchasesPermissions.OrderRead,
            "Ver órdenes de compra",
            "Permite ver las órdenes de compra y descargar su PDF.",
            "Compras",
            "low",
            RequiredModules: [ModuleKeys.Purchases])
        {
            Shape = new("purchases.order", "Órdenes de compra", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            PurchasesPermissions.OrderManage,
            "Gestionar órdenes de compra",
            "Permite crear, editar, enviar, confirmar, recibir y cancelar órdenes de compra.",
            "Compras",
            "medium",
            RequiredModules: [ModuleKeys.Purchases])
        {
            Shape = new("purchases.order", "Órdenes de compra", "manage"),
        });
        // Spec 2026-11-17 (mayorista): con [wholesale], ModuleEntitlementMask quita el permiso al apagar el módulo y el editor de
        // roles no lo ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled.
        services.AddSingleton(new PermissionDefinition(
            WholesalePermissions.DistributorRead,
            "Ver distribuidores",
            "Permite ver los distribuidores, su lista de precios y su asesor.",
            "Mayorista",
            "low",
            RequiredModules: [ModuleKeys.Wholesale])
        {
            Shape = new("wholesale.distributor", "Distribuidores", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            WholesalePermissions.DistributorManage,
            "Gestionar distribuidores",
            "Permite invitar, editar, bloquear y desbloquear distribuidores.",
            "Mayorista",
            "medium",
            RequiredModules: [ModuleKeys.Wholesale])
        {
            Shape = new("wholesale.distributor", "Distribuidores", "manage"),
        });
        // Spec 2026-11-18 (integración Siigo) §5.7: con [siigo], ModuleEntitlementMask quita el permiso al apagar el módulo y el
        // editor de roles no lo ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled.
        services.AddSingleton(new PermissionDefinition(
            SiigoPermissions.SettingsManage,
            "Configurar Siigo",
            "Permite configurar las conexiones de Siigo: empresa, comprobante, impuestos, formas de pago y productos.",
            "Siigo",
            "high",
            RequiredModules: [ModuleKeys.Siigo])
        {
            Shape = new("siigo.settings", "Configuración de Siigo", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            SiigoPermissions.InvoiceIssue,
            "Facturar en Siigo",
            "Permite generar y reintentar la factura electrónica de un pedido en Siigo.",
            "Siigo",
            "high",
            RequiredModules: [ModuleKeys.Siigo])
        {
            Shape = new("siigo.invoice", "Facturas en Siigo", "invoice"),
        });
        // Spec 2026-11-21: con [siigo], ModuleEntitlementMask la quita al apagar el módulo y el editor de roles no la ofrece.
        services.AddSingleton(new PermissionDefinition(
            SiigoPermissions.CallsRead,
            "Ver las fallas de Siigo",
            "Permite ver las llamadas a Siigo que fallaron, con la petición y la respuesta: incluyen datos del cliente.",
            "Siigo",
            "high",
            RequiredModules: [ModuleKeys.Siigo])
        {
            Shape = new("siigo.calls", "Fallas de las llamadas a Siigo", "read"),
        });
        // Spec 2026-10-30: las claves de API exigen el módulo developer; sin él el permiso ni se ofrece en el editor de roles.
        services.AddSingleton(new PermissionDefinition(
            ApiAccessPermissions.ApiKeyManage,
            "Administrar claves de API",
            "Permite crear, ver y revocar las claves con las que tus sistemas se conectan a QEP.",
            "Developer",
            "high",
            RequiredModules: [ModuleKeys.Developer])
        {
            Shape = new("developer.api_key", "Claves de API", "manage"),
        });
        // Spec cobros-recurrentes (owner, 2026-10-29): nunca se muestran opciones ni permisos que el tenant no tiene. Con
        // [billing], ModuleEntitlementMask quita el permiso al apagar el módulo y el editor de roles no lo ofrece; la API
        // responde un 403 pelado, sin tenancy.module_not_enabled (la revisión de ServiceContractsAuthorization queda de respaldo).
        services.AddSingleton(new PermissionDefinition(
            ServiceContractsPermissions.Read,
            "Ver contratos de servicio",
            "Permite ver los contratos de servicio, los cobros por cobrar y los encargados.",
            "Contratos de servicio",
            "low",
            RequiredModules: [ModuleKeys.ServiceContracts])
        {
            Shape = new("billing.service", "Contratos de servicio", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            ServiceContractsPermissions.Manage,
            "Gestionar contratos de servicio",
            "Permite crear y editar contratos, marcar pagos, definir el perfil de cobro, importar y configurar los avisos.",
            "Contratos de servicio",
            "medium",
            RequiredModules: [ModuleKeys.ServiceContracts])
        {
            Shape = new("billing.service", "Contratos de servicio", "manage"),
        });
        // Spec de préstamos (2026-10-31): nunca se muestran permisos que el tenant no tiene. Con [loans], ModuleEntitlementMask quita el
        // permiso al apagar el módulo y el editor de roles no lo ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled.
        services.AddSingleton(new PermissionDefinition(
            LoansPermissions.Read,
            "Ver préstamos",
            "Permite ver los préstamos, las cuotas por cobrar y los abonos.",
            "Préstamos",
            "low",
            RequiredModules: [ModuleKeys.Loans])
        {
            Shape = new("loans.loan", "Préstamos", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            LoansPermissions.Manage,
            "Gestionar préstamos",
            "Permite crear y editar préstamos, marcar cuotas como pagadas y registrar abonos a capital.",
            "Préstamos",
            "medium",
            RequiredModules: [ModuleKeys.Loans])
        {
            Shape = new("loans.loan", "Préstamos", "manage"),
        });
        // Spec 2026-11-20 (RRHH fase 1): con [hr], ModuleEntitlementMask quita el permiso al apagar el módulo y el editor de roles no lo
        // ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled. Ver el empleado no incluye su contrato ni su dinero.
        services.AddSingleton(new PermissionDefinition(
            HrPermissions.EmployeeRead,
            "Ver empleados",
            "Permite ver la lista, la ficha, el historial de acciones y la configuración de RRHH, sin salario, cargo ni datos bancarios.",
            "Recursos humanos",
            "low",
            RequiredModules: [ModuleKeys.Hr])
        {
            Shape = new("hr.employee", "Empleados", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            HrPermissions.EmployeeManage,
            "Gestionar empleados",
            "Permite editar los datos personales y de seguridad social, desactivar y eliminar empleados.",
            "Recursos humanos",
            "medium",
            RequiredModules: [ModuleKeys.Hr])
        {
            Shape = new("hr.employee", "Empleados", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            HrPermissions.ContractRead,
            "Ver contratos y salarios",
            "Permite ver contratos, salarios, cargos y sedes con su historial, prórrogas y datos bancarios.",
            "Recursos humanos",
            "high",
            RequiredModules: [ModuleKeys.Hr])
        {
            Shape = new("hr.contract", "Contratos laborales", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            HrPermissions.ContractManage,
            "Gestionar contratos",
            "Permite crear empleados, registrar salarios y cambios de cargo, prorrogar, convertir, terminar, reincorporar, editar los datos bancarios e importar.",
            "Recursos humanos",
            "high",
            RequiredModules: [ModuleKeys.Hr])
        {
            Shape = new("hr.contract", "Contratos laborales", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            HrPermissions.SettingsManage,
            "Configurar RRHH",
            "Permite crear, editar, archivar y eliminar sedes, centros de trabajo, áreas y cargos.",
            "Recursos humanos",
            "medium",
            RequiredModules: [ModuleKeys.Hr])
        {
            Shape = new("hr.settings", "Configuración de RRHH", "manage"),
        });
        // Spec 2026-11-23 (exámenes médicos): restricciones y recomendaciones son datos de salud (Ley 1581), así que ver la ficha no basta.
        services.AddSingleton(new PermissionDefinition(
            HrPermissions.MedicalRead,
            "Ver exámenes médicos",
            "Permite ver los exámenes médicos ocupacionales de los empleados, con sus restricciones, recomendaciones y certificados.",
            "Recursos humanos",
            "high",
            RequiredModules: [ModuleKeys.Hr])
        {
            Shape = new("hr.medical", "Exámenes médicos", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            HrPermissions.MedicalManage,
            "Gestionar exámenes médicos",
            "Permite registrar, editar y eliminar exámenes médicos, adjuntar certificados, marcar egresos sin asistir e importar el historial.",
            "Recursos humanos",
            "high",
            RequiredModules: [ModuleKeys.Hr])
        {
            Shape = new("hr.medical", "Exámenes médicos", "manage"),
        });
        // Spec 2026-11-21 (nómina): con [payroll], ModuleEntitlementMask quita el permiso al apagar el módulo y el editor de roles no lo
        // ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled. Todos ven plata: riesgo alto.
        services.AddSingleton(new PermissionDefinition(
            PayrollPermissions.PeriodRead,
            "Ver nómina",
            "Permite ver los períodos de nómina, la liquidación de cada empleado con su rastro de cálculo y exportar el resumen a Excel.",
            "Nómina",
            "high",
            RequiredModules: [ModuleKeys.Payroll])
        {
            Shape = new("payroll.period", "Períodos de nómina", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            PayrollPermissions.PeriodManage,
            "Liquidar nómina",
            "Permite abrir quincenas, registrar horas extras, ingresos y deducciones, liquidar, cerrar y reabrir.",
            "Nómina",
            "high",
            RequiredModules: [ModuleKeys.Payroll])
        {
            Shape = new("payroll.period", "Períodos de nómina", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            PayrollPermissions.PeriodApprove,
            "Aprobar nómina",
            "Permite aprobar una quincena liquidada.",
            "Nómina",
            "high",
            RequiredModules: [ModuleKeys.Payroll])
        {
            Shape = new("payroll.period", "Períodos de nómina", "approve"),
        });
        services.AddSingleton(new PermissionDefinition(
            PayrollPermissions.SettingsManage,
            "Configurar nómina",
            "Permite cambiar la configuración de nómina de cada empresa, sus conceptos y los conceptos recurrentes de cada empleado.",
            "Nómina",
            "high",
            RequiredModules: [ModuleKeys.Payroll])
        {
            Shape = new("payroll.settings", "Configuración de nómina", "manage"),
        });
        // Spec 2026-11-21 (Agenda): con [scheduling], ModuleEntitlementMask quita el permiso al apagar el módulo y el editor de roles no
        // lo ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled. `agenda.read_all` no concede acciones: sólo amplía
        // sobre qué recursos valen las demás.
        services.AddSingleton(new PermissionDefinition(
            SchedulingPermissions.AppointmentRead,
            "Ver citas",
            "Permite ver el calendario, las citas y los espacios libres de los recursos a los que tiene alcance.",
            "Agenda",
            "low",
            RequiredModules: [ModuleKeys.Scheduling])
        {
            Shape = new("scheduling.appointment", "Citas", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            SchedulingPermissions.AppointmentManage,
            "Gestionar citas",
            "Permite crear, confirmar, reprogramar, cancelar y marcar como asistidas las citas de los recursos a los que tiene alcance.",
            "Agenda",
            "medium",
            RequiredModules: [ModuleKeys.Scheduling])
        {
            Shape = new("scheduling.appointment", "Citas", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            SchedulingPermissions.AvailabilityManage,
            "Gestionar franjas y bloqueos",
            "Permite abrir y cerrar franjas, publicar sesiones y bloquear horas de los recursos a los que tiene alcance.",
            "Agenda",
            "medium",
            RequiredModules: [ModuleKeys.Scheduling])
        {
            Shape = new("scheduling.availability", "Franjas y bloqueos", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            SchedulingPermissions.AgendaReadAll,
            "Ver todas las agendas",
            "Amplía el alcance de los demás permisos de la Agenda a todos los recursos del tenant; sin él, sólo los vinculados a su membresía.",
            "Agenda",
            "medium",
            RequiredModules: [ModuleKeys.Scheduling])
        {
            Shape = new("scheduling.agenda", "Todas las agendas", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            SchedulingPermissions.SettingsManage,
            "Configurar la agenda",
            "Permite crear sedes, recursos y servicios, y cambiar las reglas de reserva y el recordatorio.",
            "Agenda",
            "medium",
            RequiredModules: [ModuleKeys.Scheduling])
        {
            Shape = new("scheduling.settings", "Configuración de la agenda", "manage"),
        });
        // Spec 2026-11-14 (soporte, C1): con [support], ModuleEntitlementMask quita el permiso al apagar el módulo y el editor de
        // roles no lo ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled.
        services.AddSingleton(new PermissionDefinition(
            SupportPermissions.Read,
            "Ver tickets de soporte",
            "Permite ver todos los tickets de soporte del tenant, su detalle y su trazabilidad.",
            "Soporte",
            "low",
            RequiredModules: [ModuleKeys.Support])
        {
            Shape = new("support.ticket", "Tickets", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            SupportPermissions.Manage,
            "Gestionar tickets de soporte",
            "Permite crear tickets y cambiar su estado, su responsable, su tipo y su cliente.",
            "Soporte",
            "medium",
            RequiredModules: [ModuleKeys.Support])
        {
            Shape = new("support.ticket", "Tickets", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            SupportPermissions.SettingsManage,
            "Configurar soporte",
            "Permite crear, editar y ordenar los tipos de ticket y fijar cuántos días hábiles antes un ticket está por vencer.",
            "Soporte",
            "medium",
            RequiredModules: [ModuleKeys.Support])
        {
            Shape = new("support.settings", "Configuración de soporte", "manage"),
        });
        // Spec 2026-11-22 (tareas internas, §5.6): riesgo bajo, porque no abre ningún ticket de cliente; con [support],
        // ModuleEntitlementMask lo quita al apagar el módulo.
        services.AddSingleton(new PermissionDefinition(
            SupportPermissions.TaskUse,
            "Usar tareas internas",
            "Permite crear tareas internas y trabajar las propias, las que creaste o te asignaron. No da acceso a los tickets de clientes.",
            "Soporte",
            "low",
            RequiredModules: [ModuleKeys.Support])
        {
            Shape = new("support.task", "Tareas internas", "use"),
        });
        // Spec de la tienda en línea (2026-11-03): con [store], ModuleEntitlementMask quita el permiso al apagar el módulo y el editor de
        // roles no lo ofrece. La bandeja no declara permiso nuevo: usa quotations.order.manage.
        services.AddSingleton(new PermissionDefinition(
            StorePermissions.SettingsManage,
            "Configurar la tienda en línea",
            "Permite publicar la tienda, cambiar su marca, contacto, monedas con sus cuentas y modo de precios.",
            "Tienda",
            "medium",
            RequiredModules: [ModuleKeys.Store])
        {
            Shape = new("store.settings", "Configuración de la tienda", "manage"),
        });
        // Spec WMS 2026-10-31 §11.5: nunca se muestran permisos que el tenant no tiene. Con [wms], ModuleEntitlementMask quita el
        // permiso al apagar el módulo y el editor de roles no lo ofrece; la API responde un 403 pelado, sin tenancy.module_not_enabled.
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.WarehouseManage,
            "Gestionar bodegas",
            "Permite crear y editar bodegas y sus ubicaciones.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.warehouse", "Bodegas y ubicaciones", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.OwnerManage,
            "Gestionar dueños del inventario",
            "Permite crear y editar los dueños a los que pertenece el inventario.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.owner", "Dueños del inventario", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.ProfileManage,
            "Gestionar perfiles de variante",
            "Permite definir cómo se maneja cada variante en bodega: unidad de medida, seguimiento, conversiones e identificadores.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.profile", "Perfiles de variante", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.DocumentRead,
            "Ver bodegas y documentos de inventario",
            "Permite consultar bodegas, ubicaciones, documentos y tareas del inventario.",
            "Bodegas",
            "low",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.document", "Documentos de inventario", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.StockRead,
            "Ver existencias",
            "Permite consultar las existencias por variante, ubicación y dueño.",
            "Bodegas",
            "low",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.stock", "Existencias", "read"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.StockExport,
            "Exportar existencias",
            "Permite descargar las existencias del inventario a un archivo.",
            "Bodegas",
            "high",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.stock", "Existencias", "export"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.AdjustmentPropose,
            "Proponer ajustes de inventario",
            "Permite proponer un ajuste para corregir las existencias de una ubicación.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.adjustment", "Ajustes de inventario", "propose"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.AdjustmentApprove,
            "Aprobar ajustes de inventario",
            "Permite aprobar los ajustes propuestos, que modifican las existencias.",
            "Bodegas",
            "high",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.adjustment", "Ajustes de inventario", "approve"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.PurchaseOrderManage,
            "Gestionar órdenes de compra",
            "Permite crear y gestionar las órdenes de compra a proveedores.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.purchase_order", "Órdenes de compra", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.TransferManage,
            "Gestionar traslados",
            "Permite crear y gestionar traslados entre bodegas y ubicaciones.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.transfer", "Traslados", "manage"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.TransferDispatch,
            "Despachar traslados",
            "Permite despachar un traslado y sacar la mercancía de su ubicación de origen.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.transfer", "Traslados", "dispatch"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.ReceiptPerform,
            "Recibir mercancía",
            "Permite registrar la recepción de mercancía en la bodega.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.receipt", "Recepción de mercancía", "perform"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.PutawayPerform,
            "Ubicar mercancía recibida",
            "Permite llevar la mercancía recibida a su ubicación de almacenamiento.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.putaway", "Ubicación de mercancía", "perform"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.DiscrepancyResolve,
            "Resolver discrepancias de recepción",
            "Permite resolver las diferencias entre lo pedido y lo recibido.",
            "Bodegas",
            "high",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.discrepancy", "Discrepancias de recepción", "resolve"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.CountPerform,
            "Contar inventario",
            "Permite registrar los conteos físicos de una ubicación.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.count", "Conteos", "perform"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.CountApprove,
            "Aprobar conteos",
            "Permite aprobar los conteos, que ajustan las existencias a lo contado.",
            "Bodegas",
            "high",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.count", "Conteos", "approve"),
        });
        services.AddSingleton(new PermissionDefinition(
            WmsPermissions.TaskManage,
            "Supervisar tareas de bodega",
            "Permite crear, asignar, priorizar y cancelar las tareas de los operarios de bodega.",
            "Bodegas",
            "medium",
            RequiredModules: [ModuleKeys.Wms])
        {
            Shape = new("wms.task", "Tareas de bodega", "manage"),
        });
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
                    _ => { })
                .AddApiKeyScheme();
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
        // nunca debe servir para autenticar otro endpoint, o eso saltaría la revocación de
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
            })
            .AddApiKeyScheme();
    }

    // El esquema de la API externa (spec 2026-10-30 §4.3) se registra en las dos ramas de AddAuthentication, con stub y sin él: si
    // quedara solo en la real, la API externa no existiría en local. No es el esquema por defecto en ninguna: solo lo piden las
    // rutas de /api/v1/external, y por eso la cookie no entra allí ni X-Api-Key en las rutas internas.
    private static AuthenticationBuilder AddApiKeyScheme(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyDefaults.AuthenticationScheme,
            _ => { });

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
                CatalogPermissions.ProductCostRead,
                policy => AddPermissionRequirement(policy, CatalogPermissions.ProductCostRead))
            .AddPolicy(
                CatalogPermissions.ProductCostManage,
                policy => AddPermissionRequirement(policy, CatalogPermissions.ProductCostManage))
            // Spec 2026-11-22: sin esta política, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                CatalogPermissions.PriceListManage,
                policy => AddPermissionRequirement(policy, CatalogPermissions.PriceListManage))
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
            // La otra mitad del permiso de fusión. Sin esta política RequireAuthorization no resuelve y el síntoma es 500.
            .AddPolicy(
                CustomersPermissions.CustomerMerge,
                policy => AddPermissionRequirement(policy, CustomersPermissions.CustomerMerge))
            // La otra mitad del permiso de borrar clientes (spec 2026-11-10 D-8). Sin esta política la ruta no resuelve y el síntoma es 500.
            .AddPolicy(
                CustomersPermissions.CustomerDelete,
                policy => AddPermissionRequirement(policy, CustomersPermissions.CustomerDelete))
            // La otra mitad del permiso de revocar la autorización de datos (spec 2026-10-26 D-9). Sin esta política la ruta
            // POST …/data-consent/revocations no resuelve y el síntoma es 500.
            .AddPolicy(
                CustomersPermissions.DataConsentRevoke,
                policy => AddPermissionRequirement(policy, CustomersPermissions.DataConsentRevoke))
            // La otra mitad del permiso de clasificaciones. Sin esta politica RequireAuthorization
            // no resuelve y el sintoma es 500, no 403 — mismo gotcha que TaxRateRead/TaxRateManage.
            .AddPolicy(
                CustomersPermissions.ClassificationRead,
                policy => AddPermissionRequirement(policy, CustomersPermissions.ClassificationRead))
            .AddPolicy(
                CustomersPermissions.ClassificationManage,
                policy => AddPermissionRequirement(
                    policy, CustomersPermissions.ClassificationManage))
            // Spec 2026-10-11 §4.3: las dos mitades de los permisos de etiquetas y notas. Sin la política el síntoma
            // es un 500, no un 403.
            .AddPolicy(
                CustomersPermissions.TagManage,
                policy => AddPermissionRequirement(policy, CustomersPermissions.TagManage))
            .AddPolicy(
                CustomersPermissions.NoteModerate,
                policy => AddPermissionRequirement(policy, CustomersPermissions.NoteModerate))
            // Borrar una nota lo puede hacer el autor (manage) o un moderador: RequireClaim con varios valores deja
            // pasar a quien tenga cualquiera, y el handler decide según de quién es la nota.
            .AddPolicy(
                CustomersPolicies.NoteDelete,
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim(
                        QepClaimTypes.Permission, CustomersPermissions.CustomerManage, CustomersPermissions.NoteModerate);
                })
            // Leer los canales de WhatsApp: gestionar cotizaciones o pedidos (spec 2026-11-09 D8: quien manda pedidos pregunta por el
            // canal) o actualizar la configuración (decisión del owner 2026-10-25). RequireClaim con varios valores deja pasar a quien
            // tenga cualquiera.
            .AddPolicy(
                QuotationsPolicies.WhatsAppChannelsRead,
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim(
                        QepClaimTypes.Permission, QuotationsPermissions.QuotationManage, OrdersPermissions.OrderManage, TenancyPermissions.SettingsUpdate);
                })
            // Leer las opciones de envio: leer cotizaciones o leer pedidos. Un tenant que oculta cotizaciones pierde
            // QuotationRead por el modulo (ModuleEntitlementMask), asi que sin OrderRead aqui su formulario de pedido
            // no podria dibujar la entrega.
            .AddPolicy(
                QuotationsPolicies.ShippingOptionsRead,
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim(
                        QepClaimTypes.Permission, QuotationsPermissions.QuotationRead, OrdersPermissions.OrderRead);
                })
            // La otra mitad del permiso, para los dos de Quotations. Sin esta politica
            // RequireAuthorization no resuelve y el sintoma es 500, no 403 — mismo gotcha que
            // TaxRateRead/TaxRateManage y ClassificationRead/ClassificationManage.
            .AddPolicy(
                QuotationsPermissions.QuotationRead,
                policy => AddPermissionRequirement(policy, QuotationsPermissions.QuotationRead))
            .AddPolicy(
                QuotationsPermissions.QuotationManage,
                policy => AddPermissionRequirement(policy, QuotationsPermissions.QuotationManage))
            // La otra mitad del permiso, para los de Orders -- mismo gotcha.
            .AddPolicy(
                OrdersPermissions.OrderRead,
                policy => AddPermissionRequirement(policy, OrdersPermissions.OrderRead))
            .AddPolicy(
                OrdersPermissions.OrderManage,
                policy => AddPermissionRequirement(policy, OrdersPermissions.OrderManage))
            .AddPolicy(
                OrdersPermissions.OrderApprove,
                policy => AddPermissionRequirement(policy, OrdersPermissions.OrderApprove))
            .AddPolicy(
                OrdersPermissions.OrderCancel,
                policy => AddPermissionRequirement(policy, OrdersPermissions.OrderCancel))
            .AddPolicy(
                OrdersPermissions.OrderInvoice,
                policy => AddPermissionRequirement(policy, OrdersPermissions.OrderInvoice))
            // La otra mitad del permiso, para los cuatro de Reporting. Sin esta politica
            // RequireAuthorization no resuelve y el sintoma es 500, no 403 -- mismo gotcha que
            // TaxRateRead/TaxRateManage, ClassificationRead/ClassificationManage y los de
            // Quotations/Orders.
            .AddPolicy(
                ReportingPermissions.OrdersRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.OrdersRead))
            .AddPolicy(
                ReportingPermissions.QuotationRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.QuotationRead))
            .AddPolicy(
                ReportingPermissions.PriceChangeRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.PriceChangeRead))
            .AddPolicy(
                ReportingPermissions.CustomerRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.CustomerRead))
            // Hoy ningún endpoint la pide con RequireAuthorization —el permiso lo miran los
            // handlers para acotar el alcance—, pero todo permiso del catálogo tiene su política:
            // el día que un endpoint la use, no aparece un 500 que no se parece a su causa.
            .AddPolicy(
                ReportingPermissions.AllAdvisorsRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.AllAdvisorsRead))
            .AddPolicy(
                ReportingPermissions.MarginRead,
                policy => AddPermissionRequirement(policy, ReportingPermissions.MarginRead))
            .AddPolicy(
                PlatformPermissions.RequestLogRead,
                policy => AddPermissionRequirement(policy, PlatformPermissions.RequestLogRead))
            .AddPolicy(
                PlatformPermissions.RequestLogPurge,
                policy => AddPermissionRequirement(policy, PlatformPermissions.RequestLogPurge))
            .AddPolicy(
                PosPermissions.SaleRead,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleRead))
            .AddPolicy(
                PosPermissions.SaleCreate,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleCreate))
            .AddPolicy(
                PosPermissions.SaleCustomer,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleCustomer))
            .AddPolicy(
                PosPermissions.SaleVoid,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleVoid))
            .AddPolicy(
                PosPermissions.SaleDiscount,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleDiscount))
            .AddPolicy(
                PosPermissions.RegisterOperate,
                policy => AddPermissionRequirement(policy, PosPermissions.RegisterOperate))
            .AddPolicy(
                PosPermissions.RegisterRead,
                policy => AddPermissionRequirement(policy, PosPermissions.RegisterRead))
            .AddPolicy(
                OperatorPermissions.TenantsRead,
                policy => AddPermissionRequirement(policy, OperatorPermissions.TenantsRead))
            .AddPolicy(
                OperatorPermissions.ModulesManage,
                policy => AddPermissionRequirement(policy, OperatorPermissions.ModulesManage))
            .AddPolicy(
                OperatorPermissions.TenantsManage,
                policy => AddPermissionRequirement(policy, OperatorPermissions.TenantsManage))
            // Sin estas dos, RequireAuthorization de la consola de ciudades no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                GeographyOperatorPermissions.CitiesRead,
                policy => AddPermissionRequirement(policy, GeographyOperatorPermissions.CitiesRead))
            .AddPolicy(
                GeographyOperatorPermissions.CitiesManage,
                policy => AddPermissionRequirement(policy, GeographyOperatorPermissions.CitiesManage))
            // Sin estas dos, RequireAuthorization de la consola de la unidad tributaria no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                CatalogOperatorPermissions.TaxUnitsRead,
                policy => AddPermissionRequirement(policy, CatalogOperatorPermissions.TaxUnitsRead))
            .AddPolicy(
                CatalogOperatorPermissions.TaxUnitsManage,
                policy => AddPermissionRequirement(policy, CatalogOperatorPermissions.TaxUnitsManage))
            // Sin estas dos, RequireAuthorization de la consola de parámetros legales no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                HrOperatorPermissions.LegalParametersRead,
                policy => AddPermissionRequirement(policy, HrOperatorPermissions.LegalParametersRead))
            .AddPolicy(
                HrOperatorPermissions.LegalParametersManage,
                policy => AddPermissionRequirement(policy, HrOperatorPermissions.LegalParametersManage))
            // Sin esta, RequireAuthorization del reporte de uso no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                OperatorPermissions.UsageRead,
                policy => AddPermissionRequirement(policy, OperatorPermissions.UsageRead))
            .AddPolicy(
                IntegrationsPermissions.ConnectionRead,
                policy => AddPermissionRequirement(policy, IntegrationsPermissions.ConnectionRead))
            .AddPolicy(
                IntegrationsPermissions.ConnectionManage,
                policy => AddPermissionRequirement(policy, IntegrationsPermissions.ConnectionManage))
            .AddPolicy(
                MessagingPermissions.ConversationRead,
                policy => AddPermissionRequirement(policy, MessagingPermissions.ConversationRead))
            .AddPolicy(
                MessagingPermissions.ConversationManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.ConversationManage))
            .AddPolicy(
                MessagingPermissions.CampaignManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.CampaignManage))
            // Sin esta política, RequireAuthorization(QuickReplyManage) no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                MessagingPermissions.QuickReplyManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.QuickReplyManage))
            // Spec 2026-10-15: sin esta política, RequireAuthorization(ResolutionReasonManage) no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                MessagingPermissions.ResolutionReasonManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.ResolutionReasonManage))
            // Spec 2026-10-21: sin esta política, RequireAuthorization(BusinessHoursManage) no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                MessagingPermissions.BusinessHoursManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.BusinessHoursManage))
            // Spec 2026-10-14: sin esta política, RequireAuthorization(TemplateManage) no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                MessagingPermissions.TemplateManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.TemplateManage))
            // Spec 2026-10-13: sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403 (CLAUDE.md).
            .AddPolicy(
                PipelinePermissions.DealRead,
                policy => AddPermissionRequirement(policy, PipelinePermissions.DealRead))
            .AddPolicy(
                PipelinePermissions.DealManage,
                policy => AddPermissionRequirement(policy, PipelinePermissions.DealManage))
            .AddPolicy(
                PipelinePermissions.SettingsManage,
                policy => AddPermissionRequirement(policy, PipelinePermissions.SettingsManage))
            // Spec 2026-10-21 D-C10: sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                AutomationsPermissions.ChatbotRead,
                policy => AddPermissionRequirement(policy, AutomationsPermissions.ChatbotRead))
            .AddPolicy(
                AutomationsPermissions.ChatbotManage,
                policy => AddPermissionRequirement(policy, AutomationsPermissions.ChatbotManage))
            // Spec 2026-10-29: la otra mitad de los permisos de agentes IA; sin ella el síntoma es 500, no 403.
            .AddPolicy(
                AiPermissions.AgentRead,
                policy => AddPermissionRequirement(policy, AiPermissions.AgentRead))
            .AddPolicy(
                AiPermissions.AgentManage,
                policy => AddPermissionRequirement(policy, AiPermissions.AgentManage))
            // Spec 2026-11-17 (copiloto): la otra mitad de sus permisos; sin ella el síntoma es 500, no 403.
            .AddPolicy(
                AiPermissions.CopilotUse,
                policy => AddPermissionRequirement(policy, AiPermissions.CopilotUse))
            .AddPolicy(
                AiPermissions.CopilotManage,
                policy => AddPermissionRequirement(policy, AiPermissions.CopilotManage))
            // Spec 2026-10-23 D-14: la otra mitad de los permisos de proveedores; sin ella el síntoma es 500.
            .AddPolicy(
                SuppliersPermissions.Read,
                policy => AddPermissionRequirement(policy, SuppliersPermissions.Read))
            .AddPolicy(
                SuppliersPermissions.Manage,
                policy => AddPermissionRequirement(policy, SuppliersPermissions.Manage))
            // Spec 2026-11-17 (compras): sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                PurchasesPermissions.OrderRead,
                policy => AddPermissionRequirement(policy, PurchasesPermissions.OrderRead))
            .AddPolicy(
                PurchasesPermissions.OrderManage,
                policy => AddPermissionRequirement(policy, PurchasesPermissions.OrderManage))
            // Spec 2026-11-17 (mayorista): sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                WholesalePermissions.DistributorRead,
                policy => AddPermissionRequirement(policy, WholesalePermissions.DistributorRead))
            .AddPolicy(
                WholesalePermissions.DistributorManage,
                policy => AddPermissionRequirement(policy, WholesalePermissions.DistributorManage))
            // Spec 2026-11-18 (integración Siigo): sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                SiigoPermissions.SettingsManage,
                policy => AddPermissionRequirement(policy, SiigoPermissions.SettingsManage))
            .AddPolicy(
                SiigoPermissions.InvoiceIssue,
                policy => AddPermissionRequirement(policy, SiigoPermissions.InvoiceIssue))
            .AddPolicy(
                SiigoPermissions.CallsRead,
                policy => AddPermissionRequirement(policy, SiigoPermissions.CallsRead))
            // Spec cobros-recurrentes: sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                ServiceContractsPermissions.Read,
                policy => AddPermissionRequirement(policy, ServiceContractsPermissions.Read))
            .AddPolicy(
                ServiceContractsPermissions.Manage,
                policy => AddPermissionRequirement(policy, ServiceContractsPermissions.Manage))
            // Spec de préstamos: sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                LoansPermissions.Read,
                policy => AddPermissionRequirement(policy, LoansPermissions.Read))
            .AddPolicy(
                LoansPermissions.Manage,
                policy => AddPermissionRequirement(policy, LoansPermissions.Manage))
            // Spec 2026-11-20 (RRHH): sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                HrPermissions.EmployeeRead,
                policy => AddPermissionRequirement(policy, HrPermissions.EmployeeRead))
            .AddPolicy(
                HrPermissions.EmployeeManage,
                policy => AddPermissionRequirement(policy, HrPermissions.EmployeeManage))
            .AddPolicy(
                HrPermissions.ContractRead,
                policy => AddPermissionRequirement(policy, HrPermissions.ContractRead))
            .AddPolicy(
                HrPermissions.ContractManage,
                policy => AddPermissionRequirement(policy, HrPermissions.ContractManage))
            .AddPolicy(
                HrPermissions.SettingsManage,
                policy => AddPermissionRequirement(policy, HrPermissions.SettingsManage))
            // Spec 2026-11-23 (exámenes médicos): sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                HrPermissions.MedicalRead,
                policy => AddPermissionRequirement(policy, HrPermissions.MedicalRead))
            .AddPolicy(
                HrPermissions.MedicalManage,
                policy => AddPermissionRequirement(policy, HrPermissions.MedicalManage))
            // Spec 2026-11-21 (nómina): sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                PayrollPermissions.PeriodRead,
                policy => AddPermissionRequirement(policy, PayrollPermissions.PeriodRead))
            .AddPolicy(
                PayrollPermissions.PeriodManage,
                policy => AddPermissionRequirement(policy, PayrollPermissions.PeriodManage))
            .AddPolicy(
                PayrollPermissions.PeriodApprove,
                policy => AddPermissionRequirement(policy, PayrollPermissions.PeriodApprove))
            .AddPolicy(
                PayrollPermissions.SettingsManage,
                policy => AddPermissionRequirement(policy, PayrollPermissions.SettingsManage))
            // Spec 2026-11-21 (nómina): leer empresas y configuración basta con ver períodos o con configurar; no es un permiso.
            .AddPolicy(
                PayrollPolicies.SettingsView,
                policy => AddAnyPermissionRequirement(policy, PayrollPermissions.PeriodRead, PayrollPermissions.SettingsManage))
            // Spec 2026-11-21 (Agenda): sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                SchedulingPermissions.AppointmentRead,
                policy => AddPermissionRequirement(policy, SchedulingPermissions.AppointmentRead))
            .AddPolicy(
                SchedulingPermissions.AppointmentManage,
                policy => AddPermissionRequirement(policy, SchedulingPermissions.AppointmentManage))
            .AddPolicy(
                SchedulingPermissions.AvailabilityManage,
                policy => AddPermissionRequirement(policy, SchedulingPermissions.AvailabilityManage))
            .AddPolicy(
                SchedulingPermissions.AgendaReadAll,
                policy => AddPermissionRequirement(policy, SchedulingPermissions.AgendaReadAll))
            .AddPolicy(
                SchedulingPermissions.SettingsManage,
                policy => AddPermissionRequirement(policy, SchedulingPermissions.SettingsManage))
            // Spec 2026-11-14 (soporte): sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                SupportPermissions.Read,
                policy => AddPermissionRequirement(policy, SupportPermissions.Read))
            .AddPolicy(
                SupportPermissions.Manage,
                policy => AddPermissionRequirement(policy, SupportPermissions.Manage))
            .AddPolicy(
                SupportPermissions.SettingsManage,
                policy => AddPermissionRequirement(policy, SupportPermissions.SettingsManage))
            // Plan D5: ver tickets acepta leer o gestionar, y D15: leer los ajustes acepta además configurar. Un solo claim con
            // varios valores permitidos; no son permisos, así que no se asignan a roles.
            .AddPolicy(
                SupportPolicies.TicketView,
                policy => AddAnyPermissionRequirement(policy, SupportPermissions.Read, SupportPermissions.Manage))
            .AddPolicy(
                SupportPolicies.SettingsView,
                policy => AddAnyPermissionRequirement(
                    policy, SupportPermissions.Read, SupportPermissions.Manage, SupportPermissions.SettingsManage))
            // Spec 2026-11-22 (tareas internas) §5.6 y plan D10: actuar sobre un ticket y verlo por la puerta; la decisión fina por
            // ticket es de SupportTicketScope. No son permisos, así que no se asignan a roles.
            .AddPolicy(
                SupportPolicies.TicketAct,
                policy => AddAnyPermissionRequirement(policy, SupportPermissions.Manage, SupportPermissions.TaskUse))
            .AddPolicy(
                SupportPolicies.TicketScopedView,
                policy => AddAnyPermissionRequirement(
                    policy, SupportPermissions.Read, SupportPermissions.Manage, SupportPermissions.TaskUse))
            // Spec de la tienda en línea: sin esta política, RequireAuthorization no resuelve y el síntoma es 500, no 403.
            .AddPolicy(
                StorePermissions.SettingsManage,
                policy => AddPermissionRequirement(policy, StorePermissions.SettingsManage))
            // Spec 2026-10-30: la otra mitad del permiso de claves de API; sin ella el síntoma es 500, no 403.
            .AddPolicy(
                ApiAccessPermissions.ApiKeyManage,
                policy => AddPermissionRequirement(policy, ApiAccessPermissions.ApiKeyManage))
            // Spec WMS 2026-10-31 §11.5: la otra mitad de los dieciséis permisos wms.*; sin ellas el síntoma es 500, no 403.
            .AddPolicy(
                WmsPermissions.WarehouseManage,
                policy => AddPermissionRequirement(policy, WmsPermissions.WarehouseManage))
            .AddPolicy(
                WmsPermissions.OwnerManage,
                policy => AddPermissionRequirement(policy, WmsPermissions.OwnerManage))
            .AddPolicy(
                WmsPermissions.ProfileManage,
                policy => AddPermissionRequirement(policy, WmsPermissions.ProfileManage))
            .AddPolicy(
                WmsPermissions.DocumentRead,
                policy => AddPermissionRequirement(policy, WmsPermissions.DocumentRead))
            .AddPolicy(
                WmsPermissions.StockRead,
                policy => AddPermissionRequirement(policy, WmsPermissions.StockRead))
            .AddPolicy(
                WmsPermissions.StockExport,
                policy => AddPermissionRequirement(policy, WmsPermissions.StockExport))
            .AddPolicy(
                WmsPermissions.AdjustmentPropose,
                policy => AddPermissionRequirement(policy, WmsPermissions.AdjustmentPropose))
            .AddPolicy(
                WmsPermissions.AdjustmentApprove,
                policy => AddPermissionRequirement(policy, WmsPermissions.AdjustmentApprove))
            .AddPolicy(
                WmsPermissions.PurchaseOrderManage,
                policy => AddPermissionRequirement(policy, WmsPermissions.PurchaseOrderManage))
            .AddPolicy(
                WmsPermissions.TransferManage,
                policy => AddPermissionRequirement(policy, WmsPermissions.TransferManage))
            .AddPolicy(
                WmsPermissions.TransferDispatch,
                policy => AddPermissionRequirement(policy, WmsPermissions.TransferDispatch))
            .AddPolicy(
                WmsPermissions.ReceiptPerform,
                policy => AddPermissionRequirement(policy, WmsPermissions.ReceiptPerform))
            .AddPolicy(
                WmsPermissions.PutawayPerform,
                policy => AddPermissionRequirement(policy, WmsPermissions.PutawayPerform))
            .AddPolicy(
                WmsPermissions.DiscrepancyResolve,
                policy => AddPermissionRequirement(policy, WmsPermissions.DiscrepancyResolve))
            .AddPolicy(
                WmsPermissions.CountPerform,
                policy => AddPermissionRequirement(policy, WmsPermissions.CountPerform))
            .AddPolicy(
                WmsPermissions.CountApprove,
                policy => AddPermissionRequirement(policy, WmsPermissions.CountApprove))
            .AddPolicy(
                WmsPermissions.TaskManage,
                policy => AddPermissionRequirement(policy, WmsPermissions.TaskManage));
    }

    private static void AddPermissionRequirement(
        AuthorizationPolicyBuilder policy,
        string permission)
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(QepClaimTypes.Permission, permission);
    }

    private static void AddAnyPermissionRequirement(
        AuthorizationPolicyBuilder policy,
        params string[] permissions)
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(QepClaimTypes.Permission, permissions);
    }
}
