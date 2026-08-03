using Microsoft.Extensions.Options;

namespace Modules.Identity.Infrastructure;

// Fails fast at startup (ValidateOnStart) so a misconfigured session policy is caught
// on boot rather than on the first login.
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
