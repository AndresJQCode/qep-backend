using Microsoft.Extensions.Options;

namespace Modules.Audit.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart) para que una sección Audit mal configurada se
// detecte en el boot: las ventanas de retención tienen que ser positivas.
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
