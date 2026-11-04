using Modules.Store.Domain;

namespace Modules.Store.Application;

// Las formas de HTTP contract A. Todo lo que llega es anulable en el binding para que un campo ausente sea un 422 de
// validation.failed con su campo y no un 400 del deserializador; las enumeraciones viajan por nombre, como texto.

/// <summary>El cuerpo plano de <c>PUT /store/settings</c>.</summary>
public sealed record StoreSettingsRequest(
    string? Slug,
    string? Name,
    string? PrimaryColor,
    string? OnPrimaryColor,
    string? AccentTextColor,
    Guid? BannerFileId,
    string? BannerText,
    string? WhatsApp,
    string? Address,
    string? Schedule,
    string? InstagramUrl,
    string? FacebookUrl,
    string? TiktokUrl,
    string? DataPolicyUrl,
    string? PricingMode,
    IReadOnlyList<StoreCurrencyRequest?>? Currencies,
    string? PrimaryCurrency,
    IReadOnlyList<string?>? SalesCountries,
    IReadOnlyList<Guid>? FeaturedCategoryIds);

public sealed record StoreCurrencyRequest(string? Currency, StoreBillingAccountRequest? BillingAccount);

public sealed record StoreBillingAccountRequest(Guid? CompanyId, string? BankName, string? AccountNumber, string? Currency);

/// <summary>El cuerpo de <c>POST /store/settings/colors/preview</c>.</summary>
public sealed record StoreColorPreviewRequest(string? PrimaryColor, string? OnPrimaryColor, string? AccentTextColor);

public sealed record StoreSettingsDto(
    string? Slug,
    string? StoreUrl,
    bool IsPublished,
    DateTimeOffset? PublishedAt,
    string? Name,
    string EffectiveName,
    string? LogoUrl,
    string? PrimaryColor,
    string? OnPrimaryColor,
    string? AccentTextColor,
    StoreColorsDto? ResolvedColors,
    StoreColorChecksDto? ColorChecks,
    StoreBannerDto Banner,
    StoreContactDto Contact,
    string? DataPolicyUrl,
    DateTimeOffset? DataPolicySetAt,
    IReadOnlyList<StoreCurrencyDto> Currencies,
    string? PrimaryCurrency,
    string PricingMode,
    IReadOnlyList<string> SalesCountries,
    IReadOnlyList<StoreFeaturedCategoryDto> FeaturedCategories,
    IReadOnlyList<string> PublishBlockers,
    long Version,
    DateTimeOffset? UpdatedAt);

public sealed record StoreColorsDto(string Primary, string OnPrimary, string AccentText);

public sealed record StoreColorChecksDto(
    double OnPrimaryContrast,
    double AccentTextContrast,
    bool OnPrimaryLowContrast,
    bool AccentTextLowContrast);

public sealed record StoreColorPreviewDto(StoreColorsDto ResolvedColors, StoreColorChecksDto ColorChecks);

public sealed record StoreBannerDto(Guid? ImageFileId, string? ImageUrl, string? Text);

public sealed record StoreContactDto(
    string? WhatsApp,
    string? Address,
    string? Schedule,
    string? InstagramUrl,
    string? FacebookUrl,
    string? TiktokUrl);

public sealed record StoreCurrencyDto(string Currency, StoreBillingAccountDto BillingAccount);

public sealed record StoreBillingAccountDto(Guid CompanyId, string BankName, string AccountNumber, string Currency);

public sealed record StoreFeaturedCategoryDto(Guid Id, string Name);

public sealed record StoreBillingAccountListDto(IReadOnlyList<StoreBillingAccountOption> Items);

/// <summary>
/// Una sola traducción de los colores resueltos a su forma HTTP, para que lo que el backoffice previsualiza y lo que guarda sean
/// idénticos: los dos salen de <see cref="StoreColorScheme.Resolve"/> y de acá.
/// </summary>
internal static class StoreColorMapping
{
    public static StoreColorsDto ToDto(StoreResolvedColors colors) =>
        new(colors.Primary.ToHex(), colors.OnPrimary.ToHex(), colors.AccentText.ToHex());

    public static StoreColorChecksDto ToDto(StoreColorChecks checks) =>
        new(checks.OnPrimaryContrast, checks.AccentTextContrast, checks.OnPrimaryLowContrast, checks.AccentTextLowContrast);

    public static StoreColorPreviewDto ToPreview(StoreResolvedColors colors) =>
        new(ToDto(colors), ToDto(colors.Checks));
}
