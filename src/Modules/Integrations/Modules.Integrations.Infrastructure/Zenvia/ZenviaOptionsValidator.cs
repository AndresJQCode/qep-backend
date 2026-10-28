using Microsoft.Extensions.Options;

namespace Modules.Integrations.Infrastructure.Zenvia;

/// <summary>P17: el API token viaja en un header; por http saldría en claro. Mismo criterio que
/// <c>QuotationsOptionsValidator</c> con el PDF.</summary>
internal sealed class ZenviaOptionsValidator : IValidateOptions<ZenviaOptions>
{
    public ValidateOptionsResult Validate(string? name, ZenviaOptions options) =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ZenviaOptions.SectionName}:BaseUrl must be an absolute https URL: the API token travels in a header.");
}
