namespace Modules.Support.Application;

/// <summary>
/// La configuración del host del formulario público (<c>Support:PublicForm</c>, plan F28 y F29), con adaptador en Bootstrapper.
/// <see cref="BaseUrl"/> arma el enlace del formulario (<c>{BaseUrl}/{slug}</c>) y el de consulta; <see cref="Hostname"/> es el que
/// exige Turnstile en producción.
/// </summary>
public interface ISupportPublicOptions
{
    /// <summary>Sin barra final; por defecto <c>https://soporte.lulocrm.com</c>.</summary>
    string BaseUrl { get; }

    /// <summary>Por defecto <c>soporte.lulocrm.com</c>.</summary>
    string Hostname { get; }
}
