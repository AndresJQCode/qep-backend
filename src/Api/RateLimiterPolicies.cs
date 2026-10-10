namespace Api;

public static class RateLimiterPolicies
{
    // Compartido por las superficies públicas/sin autenticar: ventana fija por IP, generosa
    // para tráfico real pero acotada contra el abuso.
    public const string Public = "public";

    /// <summary>Spec 2026-10-09 §6.7: concurrencia global con cola para el webhook de Meta, no una
    /// ventana por IP (§8.2).</summary>
    public const string Webhook = "webhook";
}
