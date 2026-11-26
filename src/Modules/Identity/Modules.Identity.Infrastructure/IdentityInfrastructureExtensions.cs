using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure.Messaging;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure;

public static class IdentityInfrastructureExtensions
{
    public static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<IdentityDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "identity")));
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPasswordCredentialRepository, PasswordCredentialRepository>();
        services.AddScoped<IEmailTokenRepository, EmailTokenRepository>();
        // Spec 2026-11-19 (onboarding por link): el código que confirma un correo antes de que exista el usuario.
        services.AddScoped<IEmailChallengeRepository, EmailChallengeRepository>();
        services.AddScoped<IEmailChallenges, EmailChallengeService>();
        // Spec 2026-11-19: términos aceptados por usuario. Sin ValidateOnStart a propósito: Terms:CurrentVersion es
        // decisión pendiente del owner y una clave requerida rompería todos los hosts de prueba. Sin valor, el servicio
        // falla cerrado al usarse (identity.terms.unavailable).
        services.AddOptions<TermsOptions>().Bind(configuration.GetSection(TermsOptions.SectionName));
        services.AddScoped(provider => provider.GetRequiredService<IOptionsSnapshot<TermsOptions>>().Value);
        services.AddScoped<ITermsAcceptanceRepository, TermsAcceptanceRepository>();
        services.AddScoped<ITermsAcceptances, TermsAcceptanceService>();
        services.AddScoped<IEmailTokenIssuer, EmailTokenIssuer>();
        // Sin estado por request: el hash ficticio se calcula una vez por proceso.
        services.AddSingleton<IPasswordHasher, AspNetCorePasswordHasher>();
        services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();
        services.AddScoped<IIdentityOutbox, IdentityOutboxWriter>();
        services.AddScoped<IIdentityProvisioning, IdentityProvisioningService>();
        services.AddScoped<IVerifiedProviderLinker, VerifiedProviderLinker>();
        services.AddScoped<IProviderLinking, ProviderLinkingService>();
        services.AddScoped<IOwnerProvisioning, OwnerProvisioningService>();
        services.AddScoped<IPasswordRegistration, PasswordRegistrationService>();
        services.AddScoped<IEmailVerification, EmailVerificationService>();
        services.AddScoped<IPasswordReset, PasswordResetService>();
        services.AddScoped<PasswordCredentialVerifier>();
        services.AddScoped<IPasswordLogin, PasswordLoginService>();
        services.AddScoped<IPasswordCredentials, PasswordCredentialsService>();
        services.AddScoped<IProviderIdentityResolver, ProviderIdentityResolver>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IUserPreferenceService, UserPreferenceService>();

        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IIdentityAuditRecorder, IdentityAuditRecorder>();
        services.AddScoped<ISessionService, SessionService>();

        var section = configuration.GetSection(QepSessionOptions.SectionName);
        services.AddOptions<QepSessionOptions>()
            .Bind(section)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<QepSessionOptions>, SessionOptionsValidator>();
        services.AddHostedService<SessionRevocationWorker>();
        services.AddHostedService<OrphanUserCleanupWorker>();

        return services;
    }
}
