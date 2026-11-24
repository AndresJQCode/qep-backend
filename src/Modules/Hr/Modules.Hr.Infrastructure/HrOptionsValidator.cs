using Microsoft.Extensions.Options;
using Modules.Hr.Application.MedicalExams;

namespace Modules.Hr.Infrastructure;

// Fails fast at start (ValidateOnStart), same criterion as ServiceContractsOptionsValidator. The rules live in HrOptions.Validate so
// they can be tested without a host.
internal sealed class HrOptionsValidator : IValidateOptions<HrOptions>
{
    public ValidateOptionsResult Validate(string? name, HrOptions options)
    {
        var failures = options.Validate();
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
