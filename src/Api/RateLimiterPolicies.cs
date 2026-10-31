namespace Api;

public static class RateLimiterPolicies
{
    // Compartido por las superficies públicas/sin autenticar: ventana fija por IP, generosa
    // para tráfico real pero acotada contra el abuso.
    public const string Public = "public";

    /// <summary>Spec 2026-10-09 §6.7: concurrencia global con cola para el webhook de Meta, no una
    /// ventana por IP (§8.2).</summary>
    public const string Webhook = "webhook";

    /// <summary>Endpoints de credenciales (login, registro, reset, aceptar invitación): 10 por minuto
    /// por IP y por endpoint, cada uno con su contador.</summary>
    public const string Credentials = "credentials";

    /// <summary>Lotes de tiempos del navegador: 600 por minuto por IP. Ver su registro en <c>Program.cs</c>.</summary>
    public const string Telemetry = "telemetry";
}
