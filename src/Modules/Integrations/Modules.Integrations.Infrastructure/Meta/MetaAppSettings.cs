using Microsoft.Extensions.Options;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Singleton: lee las opciones una vez, al arrancar, que es cuando se validaron.</summary>
internal sealed class MetaAppSettings(IOptions<MetaAppOptions> options) : IMetaAppSettings
{
    private readonly MetaAppOptions settings = options.Value;

    public bool IsConfigured => settings.IsConfigured();

    public string? AppId => string.IsNullOrWhiteSpace(settings.AppId) ? null : settings.AppId.Trim();

    public string? ConfigId => string.IsNullOrWhiteSpace(settings.ConfigId) ? null : settings.ConfigId.Trim();

    public string GraphApiVersion =>
        string.IsNullOrWhiteSpace(settings.GraphApiVersion)
            ? MetaAppOptions.DefaultGraphApiVersion
            : settings.GraphApiVersion.Trim();
}
