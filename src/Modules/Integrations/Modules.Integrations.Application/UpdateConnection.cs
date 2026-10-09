using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por If-Match.</param>
public sealed record UpdateConnectionCommand(
    Guid TenantId,
    Guid ConnectionId,
    long ExpectedVersion,
    string? Name,
    IReadOnlyDictionary<string, string?>? Fields,
    IReadOnlyDictionary<string, string?>? Secrets) : ICommand<ConnectionResponse>
{
    public override string ToString() =>
        $"UpdateConnectionCommand {{ TenantId = {TenantId}, ConnectionId = {ConnectionId}, "
        + $"ExpectedVersion = {ExpectedVersion}, Name = {Name}, Fields = [{CommandText.Keys(Fields)}], "
        + $"Secrets = [{CommandText.Keys(Secrets)}] }}";
}

/// <summary>
/// Lo que no depende del proveedor. Campos y secretos se validan en el handler, con el proveedor de la
/// conexión ya cargada, por las mismas <see cref="ConnectionInputRules"/>.
/// </summary>
public sealed class UpdateConnectionValidator : AbstractValidator<UpdateConnectionCommand>
{
    public UpdateConnectionValidator()
    {
        // Inalcanzable desde la pantalla: el endpoint ya exige un If-Match mayor que cero.
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
        RuleFor(command => command.Name)
            .Custom((name, context) => ConnectionInputRules.AddTo(context, ConnectionInputRules.CheckName(name)));
    }
}

/// <summary>
/// Spec 2026-10-08, <c>PUT /connections/{id}</c>: secreto ausente conserva (D5); prueba sólo si cambió
/// un campo público o llegó un secreto (P9), con los secretos guardados descifrados para lo que no
/// llegó; una prueba fallida no guarda nada; sin cambios, no-op. Un diccionario <c>fields</c> o
/// <c>secrets</c> null (no un valor null) significa "sin cambios" para ese grupo.
/// </summary>
public sealed class UpdateConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    ISecretProtector protector,
    IConnectionTester tester,
    ITenantModules tenantModules,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateConnectionCommand> validator)
    : ICommandHandler<UpdateConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(UpdateConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        SecretProtectionGuard.EnsureAvailable(protector);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);
        ConcurrencyGuard.EnsureVersion(connection, command.ExpectedVersion);

        // Un fields null conserva los campos guardados; un secrets null no trae secretos nuevos.
        // Con Embedded Signup (spec 2026-10-09 §6.1) los campos los llena el backend: el request sólo puede
        // cambiar el nombre, y lo guardado se conserva tal cual (P6 del plan).
        var backendOwned = provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup;
        IReadOnlyDictionary<string, string?> requestedFields = command.Fields is { } sent && !backendOwned
            ? sent
            : connection.Fields.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        // CheckValues recibe lo que llegó, para que un campo mandado a un proveedor de Meta salga 422.
        ConnectionInputRules.ThrowIfAny(
            ConnectionInputRules.CheckValues(provider, backendOwned ? command.Fields : requestedFields, command.Secrets, requireSecrets: false));

        var fields = ConnectionInputRules.Normalize(provider, requestedFields, secret: false);
        var replacedSecrets = ConnectionInputRules.Normalize(provider, command.Secrets, secret: true);
        var mustTest = replacedSecrets.Count > 0 || !SameValues(connection.Fields, fields);
        if (mustTest)
        {
            var merged = ConnectionSecrets.ReadForTest(protector, connection, replacedSecrets);
            await ConnectionVerification.EnsurePassesAsync(tester, provider, fields, merged, cancellationToken);
        }

        var now = clock.UtcNow;
        var changed = connection.Update(
            provider, command.Name!, fields, replacedSecrets, protector.Protect, now, verified: mustTest);
        if (changed.Count == 0)
        {
            return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
        }

        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Updated, changed, now);
        if (mustTest)
        {
            ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Verified, [], now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }

    private static bool SameValues(IReadOnlyDictionary<string, string> current, Dictionary<string, string> next) =>
        current.Count == next.Count
        && current.All(pair => next.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));
}
