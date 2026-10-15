using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>El cuerpo de <c>POST /integrations/instagram/connect</c> (spec 2026-10-14 §5.1). <see cref="ToString"/>
/// no imprime el <c>code</c>.</summary>
public sealed record ConnectInstagramCommand(Guid TenantId, string? Name, string? Code, string? RedirectUri)
    : ICommand<ConnectionResponse>
{
    public override string ToString() =>
        $"ConnectInstagramCommand {{ TenantId = {TenantId}, Name = {Name}, RedirectUri = {RedirectUri} }}";
}

/// <summary>Cada regla nombra su clave en camelCase, que es la que lee el formulario en <c>errors</c>. El
/// <c>redirect_uri</c> tiene que ser el mismo con el que se pidió la autorización: Meta rechaza el canje si no
/// coincide, así que se exige absoluto, http(s) y sin fragmento.</summary>
public sealed class ConnectInstagramValidator : AbstractValidator<ConnectInstagramCommand>
{
    public const int RedirectUriMaxLength = 512;

    public const string RedirectUriMessage = "La dirección de retorno no tiene una forma válida; vuelve a abrir el flujo.";

    public const string RedirectUriNotAllowedMessage = "La dirección de retorno no está permitida para esta aplicación.";

    public ConnectInstagramValidator()
    {
        RuleFor(command => command.Name)
            .Custom((name, context) => ConnectionInputRules.AddTo(context, ConnectionInputRules.CheckName(name)));
        RuleFor(command => command.Code)
            .Must(code => !string.IsNullOrWhiteSpace(code) && code.Length <= 1024 && !code.Any(char.IsControl))
            .OverridePropertyName("code")
            .WithMessage("Meta no devolvió un código válido; vuelve a abrir el flujo.");
        RuleFor(command => command.RedirectUri)
            .Must(value => TryParse(value, out _))
            .OverridePropertyName("redirectUri")
            .WithMessage(RedirectUriMessage);
    }

    public static bool TryParse(string? value, out Uri uri)
    {
        uri = null!;
        return value is { Length: > 0 and <= RedirectUriMaxLength }
            && !value.Any(char.IsControl)
            && Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(parsed.Fragment)
            && (uri = parsed) is not null;
    }
}

/// <summary>
/// Spec 2026-10-14 §8.1, en este orden: autoriza → llave de cifrado → proveedor visible → validador → membresía →
/// tope y nombre → app configurada y origen del <c>redirect_uri</c> permitido → canje del <c>code</c> (token corto,
/// id y permisos) → los dos permisos → token largo → <c>/me</c> → cuenta profesional → cuenta libre →
/// suscripción al webhook → conexión, ruta y auditoría en una transacción. El <c>code</c> vale una sola vez:
/// después del canje nada se deshace en Meta, y volver a intentar exige otro <c>code</c>. Una carrera con otro
/// tenant por la misma cuenta la resuelve <c>IX_connection_routes_provider_external</c>.
/// </summary>
public sealed class ConnectInstagramHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IConnectionRouteRepository routes,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    ISecretProtector protector,
    IInstagramLoginGateway gateway,
    IInstagramAppSettings instagramApp,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ConnectInstagramCommand> validator)
    : ICommandHandler<ConnectInstagramCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(ConnectInstagramCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        SecretProtectionGuard.EnsureAvailable(protector);
        var provider = catalog.Find(IntegrationProviders.Instagram.Key)
            ?? throw new InvalidOperationException("The instagram provider is not in the catalog.");
        await ProviderVisibility.EnsureVisibleAsync(tenantModules, command.TenantId, provider, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var author = await IntegrationsMember.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        if (await repository.CountAsync(command.TenantId, provider.Key, cancellationToken) >= provider.MaxConnections)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.LimitReached, $"A tenant can have at most {provider.MaxConnections} connections of this provider.");
        }

        if ((await repository.ListAsync(command.TenantId, cancellationToken)).Any(connection =>
                string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal)
                && string.Equals(connection.Name, command.Name!.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.NameTaken, "Another connection of this provider already uses that name.");
        }

        if (!instagramApp.IsConfigured)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.InstagramCodeExchangeFailed, "Meta:Instagram is not configured; the code cannot be exchanged.");
        }

        // §11: el origen del retorno es lista blanca; uno ajeno es un error del campo, no del canje. El validador ya
        // comprobó la forma, así que el TryParse acá no puede fallar.
        if (!ConnectInstagramValidator.TryParse(command.RedirectUri, out var redirectUri) || !instagramApp.IsAllowedRedirect(redirectUri))
        {
            throw new ValidationException([new ValidationFailure("redirectUri", ConnectInstagramValidator.RedirectUriNotAllowedMessage)]);
        }

        var exchange = await gateway.ExchangeCodeAsync(command.Code!, command.RedirectUri!, cancellationToken);
        if (!exchange.Succeeded || exchange.Value is not { } shortLived || string.IsNullOrWhiteSpace(shortLived.AccessToken))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.InstagramCodeExchangeFailed, "Meta did not exchange the code.");
        }

        if (!InstagramLogin.HasRequiredScopes(shortLived.Permissions))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.InstagramPermissionsMissing,
                "The person did not grant instagram_business_basic and instagram_business_manage_messages.");
        }

        var longLived = await gateway.ExchangeLongLivedAsync(shortLived.AccessToken, cancellationToken);
        if (!longLived.Succeeded || longLived.Value is not { } token || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.InstagramCodeExchangeFailed, "Meta did not exchange the short-lived token.");
        }

        var me = await gateway.GetMeAsync(token.AccessToken, cancellationToken);
        if (!me.Succeeded || me.Value is not { } account || string.IsNullOrWhiteSpace(account.Id))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.InstagramAccountReadFailed, "Meta did not describe the Instagram account.");
        }

        if (!InstagramLogin.IsProfessional(account.AccountType))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.InstagramAccountNotProfessional, "Only an Instagram professional account (business or creator) can be connected.");
        }

        // Lectura indexada antes de suscribir; la garantía final la da el índice único al guardar.
        if (await routes.ExistsAsync(provider.Key, account.Id, cancellationToken))
        {
            throw AccountAlreadyConnected();
        }

        var subscribed = await gateway.SubscribeAsync(account.Id, token.AccessToken, cancellationToken);
        if (!subscribed.Succeeded)
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.InstagramSubscriptionFailed, "Meta rejected the webhook subscription of the account.");
        }

        var now = clock.UtcNow;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [InstagramFieldKeys.IgUserId] = account.Id,
            [InstagramFieldKeys.TokenExpiresAt] = TokenExpiry(now, token.ExpiresIn),
        };
        Put(fields, InstagramFieldKeys.Username, account.Username);
        Put(fields, InstagramFieldKeys.AccountName, account.Name);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [InstagramFieldKeys.AccessToken] = token.AccessToken };
        var connection = IntegrationConnection.Create(
            provider, command.TenantId, command.Name!, fields, secrets, protector.Protect, author, now);
        repository.Add(connection);
        routes.Add(IntegrationConnectionRoute.Create(provider.Key, account.Id, accountId: null, command.TenantId, connection.Id));
        ConnectionAudit.ByMember(
            auditRecorder, executionContext, connection, ConnectionAuditActions.Created,
            [.. ConnectionAudit.KeysOf(connection), InstagramLogin.OnboardingAuditField], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }

    /// <summary>ISO 8601 en UTC (<c>O</c>): lo que el worker de refresco vuelve a leer. Un <c>expires_in</c> que no
    /// vino cuenta como los 60 días que Meta documenta.</summary>
    public static string TokenExpiry(DateTimeOffset now, TimeSpan expiresIn) =>
        now.Add(expiresIn > TimeSpan.Zero ? expiresIn : TimeSpan.FromDays(60)).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static IntegrationsDomainException AccountAlreadyConnected() =>
        new(IntegrationsErrorCodes.InstagramAccountAlreadyConnected, "That Instagram account is already connected.");

    private static void Put(Dictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = value.Trim();
        }
    }
}
