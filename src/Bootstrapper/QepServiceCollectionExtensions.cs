using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Bootstrapper.Authentication;
using Bootstrapper.Messaging;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Observability;
using Modules.Audit.Infrastructure;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure;
using Modules.Authorization.Application;
using Modules.Identity.Infrastructure;
using Modules.Notifications.Infrastructure;
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
            IQueryHandler<ListMembershipsQuery, IReadOnlyList<MembershipListItemDto>>,
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
            IQueryHandler<ListFilesQuery, PagedFilesDto>,
            ListFilesHandler>();
        services.AddScoped<
            IQueryHandler<ListProductsQuery, IReadOnlyList<ProductDto>>,
            ListProductsHandler>();
        services.AddScoped<
            IQueryHandler<GetProductQuery, ProductDto>,
            GetProductHandler>();
        services.AddScoped<
            ICommandHandler<CreateProductCommand, ProductDto>,
            CreateProductHandler>();
        services.AddScoped<
            ICommandHandler<UpdateProductCommand, ProductDto>,
            UpdateProductHandler>();
        services.AddScoped<
            ICommandHandler<DeactivateProductCommand, ProductDto>,
            DeactivateProductHandler>();
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
        services.AddValidatorsFromAssemblyContaining<UpdateTenantSettingsValidator>();
        services.AddValidatorsFromAssemblyContaining<CreateProductValidator>();
        services.AddAuditInfrastructure(configuration);
        services.AddTenancyInfrastructure(configuration);
        services.AddIdentityInfrastructure(configuration);
        services.AddNotificationsInfrastructure(configuration);
        services.AddStorageInfrastructure(configuration);
        services.AddCatalogInfrastructure(configuration);

        // CAT-05 — el único punto donde `catalog` y `storage` se tocan, y es acá a propósito:
        // ningún módulo referencia al otro, el composition root los cablea. Va después de los
        // dos AddXInfrastructure porque el adaptador depende de servicios que ellos registran.
        services.AddScoped<IProductImageLookup, ProductImageLookup>();

        AddAuthorizationCapability(services);
        services.AddQepObservability(configuration, environment);
        AddAuthentication(services, configuration, environment);
        AddAuthorization(services);
        return services;
    }

    // Registra la capacidad Authorization y el catálogo de roles de sistema de tenancy.
    // Las definiciones de rol se versionan en código; las referencias de rol de la membresía
    // (ADR 0016) mapean a estos permisos. Los roles custom/de base y los globales van después.
    private static void AddAuthorizationCapability(IServiceCollection services)
    {
        services.AddSingleton<IRoleCatalog, RoleCatalog>();
        services.AddScoped<
            Modules.Authorization.Application.IAuthorizationService,
            AuthorizationService>();
        services.AddScoped<IRolePermissionChecker, RolePermissionChecker>();
        services.AddScoped<IRoleReferenceValidator, RoleReferenceValidator>();
        services.AddSingleton(new RoleDefinition(
            "tenancy.owner",
            "Propietario",
            "Administra la configuración, membresías y capacidades base del tenant.",
            "Tenancy",
            "high",
            [
                TenancyPermissions.SettingsRead,
                TenancyPermissions.SettingsUpdate,
                TenancyPermissions.MembershipInvite,
                TenancyPermissions.MembershipRead,
                TenancyPermissions.MembershipManage,
                StoragePermissions.FileUpload,
                StoragePermissions.FileRead,
                StoragePermissions.FileDelete,
                StoragePermissions.FilePublish,
                CatalogPermissions.ProductRead,
                CatalogPermissions.ProductManage,
                CatalogPermissions.TaxRateRead,
                CatalogPermissions.TaxRateManage
            ]));
        services.AddSingleton(new RoleDefinition(
            "tenancy.member",
            "Miembro",
            "Acceso operativo básico al tenant y lectura de información común.",
            "Tenancy",
            "medium",
            [
                TenancyPermissions.SettingsRead,
                TenancyPermissions.MembershipRead,
                CatalogPermissions.ProductRead,
                // Sólo lectura: cambiar una tasa mueve los totales de toda cotización, así que
                // TaxRateManage es high y queda en tenancy.owner. Ratificado en el gate CAT-00.
                CatalogPermissions.TaxRateRead
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
            TenancyPermissions.MembershipInvite,
            "Invitar miembros",
            "Permite invitar usuarios al tenant.",
            "Tenancy",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.MembershipRead,
            "Leer miembros",
            "Permite consultar membresías y catálogo de roles/permisos.",
            "Tenancy",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.MembershipManage,
            "Gestionar miembros y roles",
            "Permite suspender, remover y cambiar roles de miembros.",
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
                TenancyPermissions.MembershipInvite,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.MembershipInvite))
            .AddPolicy(
                TenancyPermissions.MembershipRead,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.MembershipRead))
            .AddPolicy(
                TenancyPermissions.MembershipManage,
                policy => AddPermissionRequirement(
                    policy,
                    TenancyPermissions.MembershipManage))
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
            // La otra mitad del permiso. Sin esta política RequireAuthorization no resuelve y el
            // síntoma es 500, no 403 — un error que no se parece en nada a su causa. Por eso
            // CA-CAT-03-10 verifica que la política resuelva, y no sólo que el permiso figure en
            // el catálogo.
            .AddPolicy(
                CatalogPermissions.TaxRateRead,
                policy => AddPermissionRequirement(policy, CatalogPermissions.TaxRateRead))
            .AddPolicy(
                CatalogPermissions.TaxRateManage,
                policy => AddPermissionRequirement(policy, CatalogPermissions.TaxRateManage));
    }

    private static void AddPermissionRequirement(
        AuthorizationPolicyBuilder policy,
        string permission)
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(QepClaimTypes.Permission, permission);
    }
}
