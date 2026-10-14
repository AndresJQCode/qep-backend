using Microsoft.Extensions.Hosting;

namespace Bootstrapper.Deployment;

/// <summary>
/// Lo que el proceso sabe de sí mismo: el build que lo produjo (<see cref="DeploymentOptions"/>,
/// normalizado) y cuándo arrancó. Singleton construido una vez en <c>AddQepPlatform</c>, así que
/// <see cref="StartedAt"/> es el arranque del proceso y no la primera llamada.
/// </summary>
/// <remarks>
/// No valida ni tumba el arranque: la versión es información, no una precondición. Un pod que no
/// arranca por no saber su versión es peor que uno que la desconoce y lo dice con <c>null</c>.
/// </remarks>
public sealed class DeploymentInfo
{
    // Así empieza un token de la plantilla del pipeline (#{Build.BuildId}#). Si el paso de
    // reemplazo no lo resolvió, llega literal a la variable de entorno; publicarlo como si fuera
    // un dato sería peor que no saberlo.
    private const string UnresolvedTokenPrefix = "#{";

    public DeploymentInfo(DeploymentOptions options, IHostEnvironment environment)
    {
        BuildId = Normalize(options.BuildId);
        BuildNumber = Normalize(options.BuildNumber);
        Commit = Normalize(options.Commit);
        Environment = environment.EnvironmentName;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary><c>Build.BuildId</c>: el tag de la imagen desplegada. <c>null</c> si no se sabe.</summary>
    public string? BuildId { get; }

    /// <summary><c>Build.BuildNumber</c>, <c>yyyyMMdd.r</c> por defecto. <c>null</c> si no se sabe.</summary>
    public string? BuildNumber { get; }

    /// <summary>SHA completo del commit. <c>null</c> si no se sabe.</summary>
    public string? Commit { get; }

    /// <summary>Nombre del <see cref="IHostEnvironment"/>: <c>Production</c>, <c>Development</c>.</summary>
    public string Environment { get; }

    /// <summary>Instante UTC en que se construyó, o sea el arranque del proceso.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// <c>null</c> para un valor nulo, vacío, sólo espacios o que sea un token sin resolver; en
    /// cualquier otro caso el valor sin espacios alrededor.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        var value = raw?.Trim();
        return string.IsNullOrEmpty(value)
            || value.StartsWith(UnresolvedTokenPrefix, StringComparison.Ordinal)
            ? null
            : value;
    }
}
