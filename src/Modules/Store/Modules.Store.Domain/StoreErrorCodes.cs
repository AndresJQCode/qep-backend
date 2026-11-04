namespace Modules.Store.Domain;

/// <summary>Todos los códigos <c>store.*</c> del contrato HTTP; el status lo decide el manejador central por tipo de excepción.</summary>
public static class StoreErrorCodes
{
    // Los 422 de forma viajan con el código genérico del resto de la API.
    public const string ValidationFailed = "validation.failed";

    public const string SlugTaken = "store.settings.slug_taken";
    public const string SlugInvalid = "store.settings.slug_invalid";
    public const string SlugReserved = "store.settings.slug_reserved";
    public const string ColorInvalid = "store.settings.color_invalid";
    public const string UrlInvalid = "store.settings.url_invalid";
    public const string CurrencyInvalid = "store.settings.currency_invalid";
    public const string CurrencyDuplicate = "store.settings.currency_duplicate";
    public const string CurrencyMismatch = "store.settings.currency_mismatch";
    public const string PrimaryCurrencyInvalid = "store.settings.primary_currency_invalid";
    public const string CategoryNotFound = "store.settings.category_not_found";
    public const string BannerInvalid = "store.settings.banner_invalid";
    public const string SalesCountryInvalid = "store.settings.sales_country_invalid";
    public const string SalesCountryDuplicate = "store.settings.sales_country_duplicate";
    public const string SlugRequired = "store.settings.slug_required";
    public const string DataPolicyRequired = "store.settings.data_policy_required";
    public const string CurrencyRequired = "store.settings.currency_required";
    public const string SalesCountryRequired = "store.settings.sales_country_required";

    public const string PublicClientUnauthorized = "store.public.client_unauthorized";
    public const string PublicVisitorIpRequired = "store.public.visitor_ip_required";
    public const string PublicNotFound = "store.public.not_found";
    public const string PublicProductNotFound = "store.public.product_not_found";
    public const string PublicCurrencyInvalid = "store.public.currency_invalid";
    public const string PublicDepartmentNotFound = "store.public.department_not_found";

    public const string OrderRequestNotFound = "store.order_request.not_found";
    public const string NotNew = "store.order_request.not_new";
    public const string BeingTaken = "store.order_request.being_taken";
    public const string OrderRequestConflict = "store.order_request.conflict";
    public const string DuplicateClientRequest = "store.order_request.duplicate_client_request";
    public const string CustomerNotFound = "store.order_request.customer_not_found";
    public const string CurrencyNotInStore = "store.order_request.currency_not_in_store";
    public const string CustomerDocumentTaken = "store.order_request.customer_document_taken";
    public const string ConsentRequired = "store.order_request.consent_required";
    public const string CaptchaInvalid = "store.order_request.captcha_invalid";
    public const string CountryNotSold = "store.order_request.country_not_sold";
    public const string CityInvalid = "store.order_request.city_invalid";
    public const string PhoneInvalid = "store.order_request.phone_invalid";
    public const string LineUnavailable = "store.order_request.line_unavailable";
    public const string DailyCap = "store.order_request.daily_cap";

    public const string CaptchaUnavailable = "store.captcha.unavailable";

    public static string For(StorePublishBlocker blocker) => blocker switch
    {
        StorePublishBlocker.SlugRequired => SlugRequired,
        StorePublishBlocker.DataPolicyRequired => DataPolicyRequired,
        StorePublishBlocker.CurrencyRequired => CurrencyRequired,
        StorePublishBlocker.SalesCountryRequired => SalesCountryRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(blocker)),
    };
}
