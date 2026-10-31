namespace Modules.ApiAccess.Application;

/// <summary>
/// Configuración de la API externa (sección <c>ApiAccess</c>). Todo trae un valor por defecto: no hay configuración nueva que
/// cargar en <c>Backend-prod</c>.
/// </summary>
public sealed class ApiAccessOptions
{
    public const string SectionName = "ApiAccess";

    public ApiAccessRateLimitOptions RateLimit { get; set; } = new();
}

/// <summary>Los dos límites de la API externa, en peticiones por minuto.</summary>
public sealed class ApiAccessRateLimitOptions
{
    /// <summary>Por clave, después de autenticar: una integración desbocada no le quita el cupo a las demás del tenant.</summary>
    public int PerKeyPerMinute { get; set; } = 300;

    /// <summary>Por IP, antes de autenticar: frena a quien prueba claves inventadas.</summary>
    public int PerIpPerMinute { get; set; } = 600;
}
