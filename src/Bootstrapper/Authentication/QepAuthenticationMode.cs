using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bootstrapper.Authentication;

public static class QepAuthenticationMode
{
    // El modo de auth está desacoplado del entorno de hosting: el proveedor real es el
    // default. El stub por headers es opt-in vía Authentication:UseDevelopmentStub, cuyo
    // default está encendido sólo en el entorno Development para que las pruebas de
    // integración conserven la auth por headers sin fricción. Ver docs/decisions/0001-development-auth-stub.md.
    public static bool UseDevelopmentStub(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue("Authentication:UseDevelopmentStub", environment.IsDevelopment());

    /// <summary>
    /// Fija un endpoint al bearer token de Google — lo usan sólo los pocos endpoints
    /// previos a la sesión (establecer una sesión, registrar un tenant nuevo) que
    /// leen <c>sub</c>/<c>email</c>/<c>email_verified</c> directo del token del proveedor
    /// antes de que exista una cookie de sesión QEP. El stub de desarrollo nunca registra un
    /// esquema "GoogleBearer" (su único esquema "Development" ya simula esos claims vía
    /// X-Email/X-Email-Verified), así que esto sólo aplica a la rama real; en modo stub
    /// cae al esquema por defecto (el del stub).
    /// </summary>
    public static void RequireGoogleBearerOrDevStub(
        this RouteHandlerBuilder endpoint,
        IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (UseDevelopmentStub(configuration, environment))
        {
            endpoint.RequireAuthorization();
        }
        else
        {
            endpoint.RequireAuthorization(policy => policy
                .AddAuthenticationSchemes("GoogleBearer")
                .RequireAuthenticatedUser());
        }
    }
}
