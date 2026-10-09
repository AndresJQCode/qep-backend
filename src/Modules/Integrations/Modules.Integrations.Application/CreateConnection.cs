using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// El cuerpo de <c>POST /connections</c>: <c>{ providerKey, name, fields{}, secrets{} }</c>.
/// <see cref="ToString"/> sólo muestra claves: el de un record imprimiría los secretos.
/// </summary>
public sealed record CreateConnectionCommand(
    Guid TenantId,
    string? ProviderKey,
    string? Name,
    IReadOnlyDictionary<string, string?>? Fields,
    IReadOnlyDictionary<string, string?>? Secrets) : ICommand<ConnectionResponse>
{
    public override string ToString() =>
        $"CreateConnectionCommand {{ TenantId = {TenantId}, ProviderKey = {ProviderKey}, Name = {Name}, "
        + $"Fields = [{CommandText.Keys(Fields)}], Secrets = [{CommandText.Keys(Secrets)}] }}";
}

/// <summary>
/// Requeridos, largos, patrones, claves desconocidas y <c>providerKey</c> fuera del catálogo (spec,
/// «Códigos de error»). Cada falla la arma <see cref="ConnectionInputRules"/> con su nombre exacto
/// (P26).
/// </summary>
public sealed class CreateConnectionValidator : AbstractValidator<CreateConnectionCommand>
{
    public CreateConnectionValidator(IIntegrationProviderCatalog catalog)
    {
        RuleFor(command => command.Name)
            .Custom((name, context) => ConnectionInputRules.AddTo(context, ConnectionInputRules.CheckName(name)));
        RuleFor(command => command.ProviderKey).Custom((providerKey, context) =>
        {
            if (catalog.Find(providerKey) is not { } provider)
            {
                context.AddFailure(new FluentValidation.Results.ValidationFailure(
                    "providerKey", ConnectionInputRules.ProviderUnknownMessage));
                return;
            }

            // Spec 2026-10-09 §6.1: el formulario genérico no crea una conexión de Meta (quedaría sin token).
            if (provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup)
            {
                context.AddFailure(new FluentValidation.Results.ValidationFailure(
                    "providerKey", ConnectionInputRules.ProviderUsesMetaSignupMessage));
                return;
            }

            var command = context.InstanceToValidate;
            ConnectionInputRules.AddTo(
                context, ConnectionInputRules.CheckValues(provider, command.Fields, command.Secrets, requireSecrets: true));
        });
    }
}

/// <summary>
/// Spec 2026-10-08, <c>POST /connections</c>, en este orden (P18): autoriza (403 antes de leer el
/// cuerpo), 503 sin llave activa, valida, visibilidad (403 <c>tenancy.module_not_enabled</c>), membresía,
/// tope (D1), prueba contra el proveedor (decisión 5) y recién ahí crea, audita y guarda.
/// </summary>
public sealed class CreateConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    ISecretProtector protector,
    IConnectionTester tester,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateConnectionCommand> validator)
    : ICommandHandler<CreateConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        SecretProtectionGuard.EnsureAvailable(protector);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // El validador ya garantizó que el proveedor existe.
        var provider = catalog.Find(command.ProviderKey)!;
        await ProviderVisibility.EnsureVisibleAsync(tenantModules, command.TenantId, provider, cancellationToken);
        var author = await IntegrationsMember.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        if (await repository.CountAsync(command.TenantId, provider.Key, cancellationToken) >= provider.MaxConnections)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.LimitReached,
                $"A tenant can have at most {provider.MaxConnections} connections of this provider.");
        }

        var fields = ConnectionInputRules.Normalize(provider, command.Fields, secret: false);
        var secrets = ConnectionInputRules.Normalize(provider, command.Secrets, secret: true);
        await ConnectionVerification.EnsurePassesAsync(tester, provider, fields, secrets, cancellationToken);

        var now = clock.UtcNow;
        var connection = IntegrationConnection.Create(
            provider, command.TenantId, command.Name!, fields, secrets, protector.Protect, author, now);
        repository.Add(connection);
        ConnectionAudit.ByMember(
            auditRecorder, executionContext, connection, ConnectionAuditActions.Created, ConnectionAudit.KeysOf(connection), now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }
}
