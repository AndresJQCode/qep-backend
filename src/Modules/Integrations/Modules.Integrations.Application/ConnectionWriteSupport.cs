using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Audit.Domain;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// Auditoría atómica de Integrations (ADR 0019): la entrada se acumula en <c>IntegrationsDbContext</c> y
/// commitea con el cambio. Deliberadamente no es el <c>IAuditRecorder</c> compartido, que ya está
/// ligado a <c>TenancyDbContext</c>: una segunda ligadura le robaría la auditoría a Tenancy (P1;
/// precedente <c>IIdentityAuditRecorder</c>). <paramref name="changedFields"/> va por clave, nunca por
/// valor (spec, «Auditoría»).
/// </summary>
public interface IIntegrationsAuditRecorder
{
    void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        Guid connectionId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

/// <summary>Spec 2026-10-08, «Auditoría».</summary>
public static class ConnectionAuditActions
{
    public const string ResourceType = "integration_connection";
    public const string Created = "integrations.connection.created";
    public const string Updated = "integrations.connection.updated";
    public const string Paused = "integrations.connection.paused";
    public const string Resumed = "integrations.connection.resumed";
    public const string Deleted = "integrations.connection.deleted";
    public const string Verified = "integrations.connection.verified";
    public const string NeedsAttention = "integrations.connection.needs_attention";
    public const string Success = "success";
    public const string Failure = "failure";
}

internal static class ConnectionAudit
{
    public static void ByMember(
        IIntegrationsAuditRecorder recorder,
        IExecutionContext executionContext,
        IntegrationConnection connection,
        string action,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt,
        string outcome = ConnectionAuditActions.Success) =>
        recorder.Record(
            connection.TenantId, executionContext.SubjectId, AuditActorType.Human, action, connection.Id,
            outcome, changedFields, occurredAt);

    /// <summary>Lo que se audita al crear: el nombre y cada clave, ordenadas.</summary>
    public static string[] KeysOf(IntegrationConnection connection)
    {
        string[] keys = ["name", .. connection.Fields.Keys, .. connection.Secrets.Select(secret => secret.FieldKey)];
        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
    }
}

internal static class SecretProtectionGuard
{
    /// <summary>Spec, «Secreto en reposo»: fuera de producción, sin llave activa, crear o editar
    /// responde 503 (la lectura sigue funcionando).</summary>
    public static void EnsureAvailable(ISecretProtector protector)
    {
        if (protector.ActiveKeyId is null)
        {
            throw new ServiceUnavailableException(
                IntegrationsErrorCodes.SecretProtectionUnavailable,
                "Integrations:SecretProtection:ActiveKeyId is not configured: connections cannot be created or edited.");
        }
    }
}

internal static class ConcurrencyGuard
{
    public static void EnsureVersion(IntegrationConnection connection, long expectedVersion)
    {
        if (connection.Version != expectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The connection changed after it was loaded.");
        }
    }
}

/// <summary>Subject autenticado → su membresía activa (copia de <c>PosCashierResolver</c>):
/// <c>created_by</c> es un MemberId.</summary>
internal static class IntegrationsMember
{
    public static async Task<Guid> ResolveAsync(
        IMembershipDirectory membershipDirectory,
        IExecutionContext executionContext,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
        ?? throw new RequestForbiddenException(
            "authorization.denied", "The subject does not have an active membership in this tenant.");
}

/// <summary>Los secretos guardados, en claro, para probarlos. <see cref="ToString"/> sólo muestra
/// claves: el de un record imprimiría todo.</summary>
internal sealed record StoredSecrets(IReadOnlyDictionary<string, string> Plain, IReadOnlyList<string> Unreadable)
{
    public override string ToString() =>
        $"StoredSecrets {{ Keys = [{string.Join(", ", Plain.Keys)}], Unreadable = [{string.Join(", ", Unreadable)}] }}";
}

internal static class ConnectionSecrets
{
    public static StoredSecrets Read(ISecretProtector protector, IntegrationConnection connection)
    {
        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var secret in connection.Secrets)
        {
            if (protector.TryUnprotect(connection.Id, secret.FieldKey, secret.Protected, out var value))
            {
                plain[secret.FieldKey] = value;
            }
            else
            {
                unreadable.Add(secret.FieldKey);
            }
        }

        return new StoredSecrets(plain, unreadable);
    }

    /// <summary>
    /// Los secretos con que se prueba: los guardados en claro, pisados por los que llegaron. Un guardado
    /// que ya no descifra y no viene reemplazado es 422 en su campo (P14): sin él no hay con qué probar.
    /// Lo comparten crear/editar, <c>test</c> y <c>resume</c>.
    /// </summary>
    public static Dictionary<string, string> ReadForTest(
        ISecretProtector protector,
        IntegrationConnection connection,
        IReadOnlyDictionary<string, string>? replaced = null)
    {
        var stored = Read(protector, connection);
        var missing = stored.Unreadable.Where(key => replaced is null || !replaced.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
        {
            throw Unreadable(missing);
        }

        var merged = new Dictionary<string, string>(stored.Plain, StringComparer.Ordinal);
        if (replaced is not null)
        {
            foreach (var (key, value) in replaced)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>P14: llave retirada o bytes dañados; la pantalla pide pegar la clave otra vez.</summary>
    public static ValidationException Unreadable(IEnumerable<string> fieldKeys) =>
        new(fieldKeys
            .Select(key => new ValidationFailure($"secrets.{key}", ConnectionInputRules.UnreadableSecretMessage))
            .ToList());
}

internal static class ConnectionVerification
{
    /// <summary>Spec, «Probar la credencial»: después de validar y antes de guardar; cualquier cosa
    /// que no sea <c>Ok</c> bloquea (decisión 5 y D3).</summary>
    public static async Task EnsurePassesAsync(
        IConnectionTester tester,
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        var result = await tester.TestAsync(provider, fields, secrets, cancellationToken);
        if (result.Outcome != ConnectionTestOutcome.Ok)
        {
            throw Failure(provider, result);
        }
    }

    /// <summary>
    /// <c>credentials_rejected</c> marca todos los secretos del proveedor en <c>errors</c> (spec,
    /// «Códigos de error»); <c>provider_unreachable</c> no marca campo (el formulario lo muestra como
    /// aviso); <c>Invalid</c> marca el campo que el proveedor nombró (P12).
    /// </summary>
    public static Exception Failure(IntegrationProvider provider, ConnectionTestResult result) =>
        result.Outcome switch
        {
            ConnectionTestOutcome.CredentialsRejected => new IntegrationsDomainException(
                IntegrationsErrorCodes.CredentialsRejected,
                "The provider rejected the credentials.",
                provider.SecretFields.ToDictionary(
                    field => $"secrets.{field.Key}",
                    _ => new[] { ConnectionInputRules.CredentialsRejectedMessage },
                    StringComparer.Ordinal)),
            ConnectionTestOutcome.Invalid => new ValidationException(
            [
                new ValidationFailure(
                    ConnectionInputRules.PropertyFor(provider, result.FieldKey),
                    ConnectionInputRules.ProviderRejectedFieldMessage),
            ]),
            ConnectionTestOutcome.Unreachable => new IntegrationsDomainException(
                IntegrationsErrorCodes.ProviderUnreachable,
                "The provider could not be reached to verify the credentials; nothing was saved."),
            _ => new InvalidOperationException("A successful test is not a failure."),
        };

    public static string FailureCode(ConnectionTestResult result) =>
        result.Outcome switch
        {
            ConnectionTestOutcome.CredentialsRejected => ConnectionFailureCodes.CredentialsRejected,
            ConnectionTestOutcome.Invalid => ConnectionFailureCodes.FieldInvalid,
            ConnectionTestOutcome.Unreachable => ConnectionFailureCodes.ProviderUnreachable,
            _ => throw new InvalidOperationException("A successful test has no failure code."),
        };
}

internal static class CommandText
{
    /// <summary>Sólo las claves: los comandos se pueden terminar imprimiendo en un log.</summary>
    public static string Keys(IReadOnlyDictionary<string, string?>? values) =>
        values is null ? string.Empty : string.Join(", ", values.Keys);
}
