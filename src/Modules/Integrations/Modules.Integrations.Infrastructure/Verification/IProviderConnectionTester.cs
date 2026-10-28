using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Verification;

/// <summary>El probador de un proveedor. Un proveedor nuevo trae el suyo (criterio 5 del spec).</summary>
internal interface IProviderConnectionTester
{
    string ProviderKey { get; }

    Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken);
}
