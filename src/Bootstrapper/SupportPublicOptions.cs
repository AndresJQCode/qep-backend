using Microsoft.Extensions.Options;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// La sección <c>Support:PublicForm</c> (plan F28 y F29), con los valores de producción por defecto en código: en local y en las
/// pruebas el enlace sale igual que en producción sin configurar nada. La Task 4 suma <c>AllowedOrigins</c> y su validador.
/// </summary>
public sealed class SupportPublicFormOptions
{
    public const string SectionName = "Support:PublicForm";

    public string BaseUrl { get; set; } = "https://soporte.lulocrm.com";

    public string Hostname { get; set; } = "soporte.lulocrm.com";
}

/// <summary>El puerto de Soporte sobre la sección. Quita la barra final de <c>BaseUrl</c>, para que el enlace no salga con dos.</summary>
internal sealed class SupportPublicOptions(IOptions<SupportPublicFormOptions> options) : ISupportPublicOptions
{
    public string BaseUrl => options.Value.BaseUrl.TrimEnd('/');

    public string Hostname => options.Value.Hostname;
}
