namespace Api;

public static class RateLimiterPolicies
{
    // Compartido por las superficies públicas/sin autenticar: ventana fija por IP, generosa
    // para tráfico real pero acotada contra el abuso.
    public const string Public = "public";
}
