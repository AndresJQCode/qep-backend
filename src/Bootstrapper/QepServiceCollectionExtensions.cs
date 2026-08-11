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
        services.AddValidatorsFromAssemblyContaining<UpdateTenantSettingsValidator>();
        services.AddAuditInfrastructure(configuration);
        services.AddTenancyInfrastructure(configuration);
        services.AddIdentityInfrastructure(configuration);
        services.AddNotificationsInfrastructure(configuration);
        services.AddStorageInfrastructure(configuration);
        services.AddCatalogInfrastructure(configuration);
        AddAuthorizationCapability(services);
        services.AddQepObservability(configuration, environment);
        AddAuthentication(services, configuration, environment);
        AddAuthorization(services);
        return services;
    }

    // Registers the Authorization capability and the tenancy system-role catalog.
    // Role definitions are code-versioned; membership role references (ADR 0016) map
    // to these permission sets. Custom/DB roles and global platform roles are later.
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
            "Permite crear, editar e inactivar tasas de impuesto. Cambiarlas mueve los totales de toda cotización.",
            "Catalog",
            "high"));
    }

    private static void AddAuthentication(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var useDevelopmentStub = QepAuthenticationMode.UseDevelopmentStub(configuration, environment);

        // Hard fail-closed (ADR 0001, required item #2): the stub trusts caller-supplied
        // headers as identity and self-asserted permissions, so it must be impossible to
        // enable outside Development, even via an explicit config override. Refusing to
        // start beats silently falling back to the real provider, which would mask the
        // misconfiguration instead of surfacing it.
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

        // Resource-server validation of the external provider's JWT (ADR 0014/0015,
        // implementation-baseline). Google is the first provider; its client id is
        // the audience. Explicit non-empty Authority/Audience config overrides the
        // Google defaults.
        var authority = Coalesce(
            configuration["Authentication:Authority"],
            "https://accounts.google.com");
        var audience = Coalesce(
            configuration["Authentication:Audience"],
            configuration["Authentication:Google:ClientId"])
            ?? throw new InvalidOperationException(
                "Authentication:Audience or Authentication:Google:ClientId is required outside Development.");

        // Default scheme is the session cookie — it is what every endpoint except
        // POST /auth/session authenticates against. "GoogleBearer" is registered but
        // deliberately NOT the default and NOT reachable via any header-sniffing
        // fallback: it is only ever requested explicitly by /auth/session's own
        // authorization policy (see AuthSessionEndpoints). A still-valid Google id
        // token must never be usable to authenticate any other endpoint, or it would
        // bypass session revocation (suspending a tenant / removing a member revokes
        // the session row, not the underlying ~1h-lived Google token).
        services
            .AddAuthentication(SessionCookieAuthenticationHandler.AuthenticationSchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionCookieAuthenticationHandler>(
                SessionCookieAuthenticationHandler.AuthenticationSchemeName,
                _ => { })
            .AddJwtBearer("GoogleBearer", options =>
            {
                options.Audience = audience;
                // Keep provider claim names (sub, email, email_verified) intact so the
                // /auth/session endpoint can read them directly.
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

                // Test-only escape hatch (never set outside integration tests): a
                // symmetric signing key lets tests self-issue a Google-shaped JWT
                // without hitting Google's real OIDC discovery endpoint. Real
                // deployments never set Authentication:TestSigningKey, so Authority
                // stays wired to Google's discovery document as before.
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

    // Returns the first non-empty value, treating whitespace-only config (e.g. the
    // placeholder "" in appsettings.json) as absent.
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
