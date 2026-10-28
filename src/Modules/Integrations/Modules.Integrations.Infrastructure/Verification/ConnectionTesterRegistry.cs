using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Verification;

/// <summary>Elige el probador por <c>provider.Key</c> (spec, «Probar la credencial»). Un proveedor del
/// catálogo sin probador es un error de programación, no una credencial mala.</summary>
internal sealed class ConnectionTesterRegistry(IEnumerable<IProviderConnectionTester> testers) : IConnectionTester
{
    private readonly Dictionary<string, IProviderConnectionTester> _byKey =
        testers.ToDictionary(tester => tester.ProviderKey, StringComparer.Ordinal);

    public Task<ConnectionTestResult> TestAsync(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken) =>
        _byKey.TryGetValue(provider.Key, out var tester)
            ? tester.TestAsync(fields, secrets, cancellationToken)
            : throw new InvalidOperationException($"No connection tester is registered for provider '{provider.Key}'.");
}
