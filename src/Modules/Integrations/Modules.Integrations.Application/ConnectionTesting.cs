using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

public enum ConnectionTestOutcome
{
    Ok,
    CredentialsRejected,
    Unreachable,
    Invalid,
}

/// <summary>Spec 2026-10-08, «Probar la credencial»: <c>Ok</c>, <c>CredentialsRejected</c>,
/// <c>Unreachable(reason)</c> o <c>Invalid(fieldKey, reason)</c>. <c>Reason</c> es técnico y nunca
/// lleva un valor: no sale por HTTP.</summary>
public sealed record ConnectionTestResult(ConnectionTestOutcome Outcome, string? FieldKey = null, string? Reason = null)
{
    public static ConnectionTestResult Ok { get; } = new(ConnectionTestOutcome.Ok);

    public static ConnectionTestResult CredentialsRejected { get; } = new(ConnectionTestOutcome.CredentialsRejected);

    public static ConnectionTestResult Unreachable(string reason) => new(ConnectionTestOutcome.Unreachable, Reason: reason);

    public static ConnectionTestResult Invalid(string fieldKey, string reason) =>
        new(ConnectionTestOutcome.Invalid, fieldKey, reason);
}

/// <summary>
/// Prueba una credencial contra el proveedor. Un adaptador por proveedor en Infrastructure, elegido por
/// <c>provider.Key</c> (spec, «Probar la credencial»). Nunca registra secretos, headers ni cuerpos.
/// </summary>
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken);
}
