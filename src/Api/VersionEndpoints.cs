using Bootstrapper.Deployment;

namespace Api;

/// <summary>
/// <c>GET /api/v1/version</c>: qué build corre en este proceso (spec 2026-10-14). Existe para
/// verificar un despliegue en un segundo —<c>curl.exe</c>, o la línea de versiones de la SPA— sin
/// abrir Azure DevOps ni <c>kubectl</c>. Bajo <c>/api/v1</c> y no bajo <c>/health</c> a propósito:
/// el proxy de Vite y el rewrite de Vercel sólo cubren <c>/api/*</c>, así que fuera de ese prefijo
/// la SPA no llegaría en local ni en los previews.
/// </summary>
/// <remarks>
/// Anónimo porque el caso de uso principal es mirar qué hay desplegado cuando el login puede estar
/// roto, y limitado por IP como el documento OpenAPI: es la otra superficie pública de lectura.
/// Nada de lo que responde es secreto —un SHA de un repo privado y dos números de build— y la
/// superficie entera de la API ya se publica en <c>/openapi/v1.json</c>.
/// </remarks>
public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/version", GetVersion)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Public)
            .WithTags("Platform")
            .Produces<VersionResponse>();

        return endpoints;
    }

    private static IResult GetVersion(DeploymentInfo deployment) =>
        Results.Ok(new VersionResponse(
            deployment.BuildId,
            deployment.BuildNumber,
            deployment.Commit,
            deployment.Environment,
            deployment.StartedAt));
}

/// <summary>
/// Lo que la línea de versiones de la SPA dibuja, y nada más: <c>web a1b2c3d · api 20261014.3
/// (e4f5a6b)</c>. Los tres primeros van en <c>null</c> cuando el pod no recibió la sección
/// <c>Deployment</c> —local, o tokens del pipeline sin resolver— y la SPA lo dibuja como
/// <c>api local</c>. <c>StartedAt</c> distingue "desplegué hace un minuto" de "este pod lleva tres
/// días" sin abrir <c>kubectl</c>. Sin versión de .NET, hostname ni nombre de servicio: no hay
/// pantalla que los dibuje, y acá se devuelve lo que la pantalla necesita (BFF).
/// </summary>
public sealed record VersionResponse(
    string? BuildId,
    string? BuildNumber,
    string? Commit,
    string Environment,
    DateTimeOffset StartedAt);
