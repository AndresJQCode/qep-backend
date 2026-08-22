using Microsoft.Extensions.Options;

namespace Modules.Identity.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart) para que una política de sesión mal configurada
// se detecte en el boot y no en el primer login.
internal sealed class SessionOptionsValidator : IValidateOptions<QepSessionOptions>
{
    public ValidateOptionsResult Validate(string? name, QepSessionOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.CookieName))
        {
            failures.Add("Authentication:Session:CookieName must not be empty.");
        }

        if (options.AbsoluteLifetimeDays <= 0)
        {
            failures.Add("Authentication:Session:AbsoluteLifetimeDays must be positive.");
        }

        if (options.IdleTimeoutDays <= 0)
        {
            failures.Add("Authentication:Session:IdleTimeoutDays must be positive.");
        }

        if (options.IdleTimeoutDays > options.AbsoluteLifetimeDays)
        {
            failures.Add(
                "Authentication:Session:IdleTimeoutDays must not exceed AbsoluteLifetimeDays.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
