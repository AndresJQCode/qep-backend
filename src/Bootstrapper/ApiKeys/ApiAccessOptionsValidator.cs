using Microsoft.Extensions.Options;
using Modules.ApiAccess.Application;

namespace Bootstrapper.ApiKeys;

// Falla rápido al arrancar (ValidateOnStart), igual que SeedOptionsValidator: un límite en 0 o negativo hace que
// FixedWindowRateLimiterOptions lance en el primer uso y toda llamada externa responda 500.
internal sealed class ApiAccessOptionsValidator : IValidateOptions<ApiAccessOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        if (options.RateLimit.PerKeyPerMinute <= 0)
        {
            failures.Add("ApiAccess:RateLimit:PerKeyPerMinute must be greater than 0.");
        }

        if (options.RateLimit.PerIpPerMinute <= 0)
        {
            failures.Add("ApiAccess:RateLimit:PerIpPerMinute must be greater than 0.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
