namespace Modules.Buyers.Domain;

public static class BuyerDataPolicy
{
    public const string Version = "2026-11-07";

    public static string UrlFor(string storeHost) => $"https://{storeHost}/cuenta/politica";
}
