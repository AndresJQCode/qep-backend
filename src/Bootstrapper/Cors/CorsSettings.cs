namespace Bootstrapper.Cors;

/// <summary>
/// Los orígenes exactos desde los que el navegador puede llamar a la API con la cookie de sesión:
/// la SPA en <c>https://qep.qcode.co</c> llama directo a <c>https://qep-api.qcode.co</c>. Los dos
/// hosts son el mismo sitio —la cookie <c>SameSite=Lax</c> viaja— pero orígenes distintos, así que
/// el navegador exige CORS con credenciales.
///
/// La defensa CSRF (<c>RequireCsrfHeaderMiddleware</c>) se apoya en esta lista: el navegador sólo
/// manda <c>X-Qep-Client</c> desde un origen que pasó el preflight. Por eso la lista es exacta y
/// sólo https; un comodín —ni siquiera de subdominio: bajo <c>*.qcode.co</c> corren otras apps— o
/// un origen reflejado desactivarían esa defensa en silencio. Ver <c>CorsSettingsValidator</c>.
///
/// Sin valor por defecto a propósito: vacía no registra CORS, que es como corren las pruebas
/// (TestServer). En local, <c>appsettings.Development.json</c> trae <c>http://localhost:3002</c>, la
/// única excepción a "sólo https", y sólo en Development.
/// </summary>
public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    /// <summary>Orígenes <c>https</c> exactos, p. ej. <c>https://qep.qcode.co</c>; en Development, también <c>http://localhost[:puerto]</c>.</summary>
    public List<string> AllowedOrigins { get; set; } = [];
}
