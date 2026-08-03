namespace Api;

public static class RateLimiterPolicies
{
    // Shared by public/unauthenticated surfaces: per-IP fixed window, generous enough
    // for real traffic but bounded against abuse.
    public const string Public = "public";
}
