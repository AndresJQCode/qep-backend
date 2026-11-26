using System.Globalization;
using System.Text.RegularExpressions;

namespace ArchitectureTests;

/// <summary>
/// El apagado del pod tiene que caber en el plazo que le da Kubernetes. Con <c>replicas: 1</c> y <c>RollingUpdate</c>, el ingress
/// le sigue mandando requests al pod viejo unos segundos después del <c>SIGTERM</c>: el <c>preStop</c> los cubre. Después, el
/// host de .NET tiene su <c>ShutdownTimeout</c> para que los workers terminen lo que tienen en curso (un envío a Meta, un POST a
/// Siigo). Si <c>preStop + ShutdownTimeout</c> no cabe en <c>terminationGracePeriodSeconds</c>, el <c>SIGKILL</c> cae en medio de
/// ese trabajo: un mensaje que Meta aceptó y QEP no registró se vuelve a enviar.
/// </summary>
public sealed class ShutdownBudgetTests
{
    /// <summary>El default de <c>HostOptions.ShutdownTimeout</c> en .NET: el repo no lo cambia (lo verifica la prueba de abajo).</summary>
    private const int DotNetShutdownTimeoutSeconds = 30;

    [Fact]
    public void TheDeploymentDrainsBeforeTheSigtermAndLeavesTheHostItsFullShutdownTimeout()
    {
        var deployment = File.ReadAllText(RepositoryFile("k8s/prod-deployment.yaml"));

        var preStop = Regex.Match(deployment, @"preStop:\s*\n\s*exec:\s*\n\s*command:\s*\[\s*""sleep""\s*,\s*""(\d+)""\s*\]");
        Assert.True(preStop.Success, "El Deployment no tiene un preStop con sleep: el ingress le manda requests al pod que se apaga.");
        var grace = Regex.Match(deployment, @"(?m)^\s*terminationGracePeriodSeconds:\s*(\d+)\s*$");
        Assert.True(grace.Success, "El Deployment no fija terminationGracePeriodSeconds: el default de 30 s no deja lugar al preStop.");

        var sleep = int.Parse(preStop.Groups[1].Value, CultureInfo.InvariantCulture);
        var budget = int.Parse(grace.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(sleep > 0, "El preStop tiene que esperar algo.");
        Assert.True(
            budget - sleep >= DotNetShutdownTimeoutSeconds,
            $"terminationGracePeriodSeconds ({budget}) menos el preStop ({sleep}) deja {budget - sleep} s al host, " +
            $"menos que su ShutdownTimeout ({DotNetShutdownTimeoutSeconds} s).");
    }

    [Fact]
    public void TheHostKeepsTheDefaultShutdownTimeout()
    {
        // Si alguien lo cambia, el presupuesto de arriba deja de valer: esta prueba obliga a revisarlo.
        var source = Directory.EnumerateFiles(Path.GetDirectoryName(RepositoryFile("src/Api/Program.cs"))!, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Bootstrapper"), "*.cs", SearchOption.AllDirectories))
            .Where(path => File.ReadAllText(path).Contains("ShutdownTimeout", StringComparison.Ordinal));

        Assert.Empty(source);
    }

    private static string RepositoryRoot() =>
        Path.GetDirectoryName(Path.GetDirectoryName(RepositoryFile("k8s/prod-deployment.yaml"))!)!;

    private static string RepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"No se encontró {relativePath} subiendo desde {AppContext.BaseDirectory}.");
    }
}
