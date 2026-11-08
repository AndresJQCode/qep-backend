namespace Modules.Buyers.Domain;

/// <summary>Códigos de error de la cuenta de comprador del contrato HTTP.</summary>
public static class BuyerErrorCodes
{
    public const string CredentialsInvalid = "store.buyer.credentials_invalid";
    public const string EmailNotVerified = "store.buyer.email_not_verified";
    public const string SessionInvalid = "store.buyer.session_invalid";
    public const string TokenInvalid = "store.buyer.token_invalid";
    public const string PasswordIncorrect = "store.buyer.password_incorrect";
    public const string ConsentRequired = "store.buyer.consent_required";
    public const string CaptchaInvalid = "store.buyer.captcha_invalid";

    /// <summary>Interno: nunca llega al cliente, para no revelar qué correos existen.</summary>
    public const string EmailTaken = "store.buyer.email_taken";

    public const string StoreNotFound = "store.public.not_found";
    public const string VisitorIpRequired = "store.public.visitor_ip_required";
}
