using Microsoft.Extensions.Options;
using Modules.ServiceContracts.Application;

namespace Modules.ServiceContracts.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart), mismo criterio que QuotationsOptionsValidator. Las reglas viven en
// ServiceContractsOptions.Validate para probarlas sin host.
internal sealed class ServiceContractsOptionsValidator : IValidateOptions<ServiceContractsOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceContractsOptions options)
    {
        var failures = options.Validate();
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
