using Microsoft.Extensions.Options;

namespace Modules.Audit.Infrastructure;

// Fails fast at startup (ValidateOnStart) so a misconfigured Audit section is caught on
// boot: retention windows must be positive.
internal sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        var failures = new List<string>();

        if (options.SecurityRetentionDays <= 0)
        {
            failures.Add("Audit:SecurityRetentionDays must be greater than zero.");
        }

        if (options.OperationalRetentionDays <= 0)
        {
            failures.Add("Audit:OperationalRetentionDays must be greater than zero.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
