namespace Modules.Store.Domain;

/// <summary>
/// Los campos del <c>PUT /store/settings</c>, ya con la forma validada por FluentValidation en
/// Application. Los colores viajan como texto: que sean <c>#RRGGBB</c> lo decide el dominio.
/// </summary>
public sealed record StoreSettingsInput(
    string? Slug,
    string? Name,
    string? PrimaryColor,
    string? OnPrimaryColor,
    string? AccentTextColor,
    Guid? BannerFileId,
    string? BannerText,
    StoreContact Contact,
    string? DataPolicyUrl,
    StorePricingMode PricingMode,
    IReadOnlyList<StoreCurrency> Currencies,
    string? PrimaryCurrency,
    IReadOnlyList<string> SalesCountries,
    IReadOnlyList<Guid> FeaturedCategoryIds);
