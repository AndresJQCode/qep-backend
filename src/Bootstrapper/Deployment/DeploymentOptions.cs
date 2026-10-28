namespace Bootstrapper.Deployment;

/// <summary>
/// Identidad del build que corre en el pod, para <c>GET /api/v1/version</c> y el
/// <c>service.version</c> de OpenTelemetry (spec 2026-10-14). Las tres llegan por el ConfigMap de
/// producción (<c>k8s/prod-configMap.yaml</c>) con los tokens que el pipeline reemplaza:
/// <c>Build.BuildId</c> es el tag de la imagen desplegada, <c>Build.BuildNumber</c> trae la fecha
/// (<c>yyyyMMdd.r</c>) y <c>Build.SourceVersion</c> el SHA del commit. Ninguna es obligatoria —en
/// local no hay sección— y un token que la plantilla no resolvió se trata como ausente: ver
/// <see cref="DeploymentInfo"/>.
/// </summary>
public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    public string? BuildId { get; set; }

    public string? BuildNumber { get; set; }

    public string? Commit { get; set; }
}
