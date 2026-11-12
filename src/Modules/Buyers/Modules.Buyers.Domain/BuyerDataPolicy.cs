namespace Modules.Buyers.Domain;

public static class BuyerDataPolicy
{
    public const string Version = "1.0.0";

    public static string UrlFor(string storeHost) => $"https://{storeHost}/cuenta/politica";
}
