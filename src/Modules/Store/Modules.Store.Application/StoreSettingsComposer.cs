using Modules.Store.Domain;

namespace Modules.Store.Application;

/// <summary>
/// Arma el <see cref="StoreSettingsDto"/> de una tienda: leerla, guardarla y publicarla comparten la misma forma. Todo lo que no
/// está en el agregado (nombre y logo del tenant, URL del banner, nombres de las categorías) entra por sus puertos.
/// </summary>
public sealed class StoreSettingsComposer(
    IStoreTenantLookup tenants,
    IStoreFileLookup files,
    IStoreCatalogReader catalog,
    IStoreMemberDirectory members,
    StoreOptions options)
{
    public async Task<StoreSettingsDto> ComposeAsync(OnlineStore store, CancellationToken cancellationToken)
    {
        var tenant = await tenants.GetAsync(store.TenantId, cancellationToken);
        var bannerUrl = store.BannerFileId is { } bannerId
            ? await files.FindPublicImageAsync(store.TenantId, bannerId, cancellationToken)
            : null;

        // Las categorías borradas se descartan sin avisar (contrato); el orden guardado manda sobre el del catálogo.
        var names = store.FeaturedCategoryIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await catalog.CategoryNamesAsync(store.TenantId, store.FeaturedCategoryIds, cancellationToken))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        var featured = store.FeaturedCategoryIds
            .Where(names.ContainsKey)
            .Select(id => new StoreFeaturedCategoryDto(id, names[id]))
            .ToList();

        // Un asesor que ya no aparece en el directorio viaja sin nombre: la pantalla muestra el id sin romperse.
        string? advisorName = null;
        if (store.AdvisorMembershipId is { } advisorId)
        {
            var advisorNames = await members.NamesAsync(store.TenantId, [advisorId], cancellationToken);
            advisorName = advisorNames.GetValueOrDefault(advisorId);
        }

        var resolved = store.ResolveColors();

        return new StoreSettingsDto(
            store.Slug,
            store.Slug is null ? null : $"https://{store.Slug}.{options.RootDomain}",
            store.IsPublished,
            store.PublishedAt,
            store.Name,
            store.Name ?? tenant?.DisplayName ?? string.Empty,
            tenant?.LogoUrl,
            store.PrimaryColor,
            store.OnPrimaryColor,
            store.AccentTextColor,
            resolved is null ? null : StoreColorMapping.ToDto(resolved),
            resolved is null ? null : StoreColorMapping.ToDto(resolved.Checks),
            new StoreBannerDto(store.BannerFileId, bannerUrl, store.BannerText),
            new StoreContactDto(
                store.Contact.WhatsApp,
                store.Contact.Address,
                store.Contact.Schedule,
                store.Contact.InstagramUrl,
                store.Contact.FacebookUrl,
                store.Contact.TiktokUrl),
            store.DataPolicyUrl,
            store.DataPolicySetAt,
            store.Currencies
                .Select(currency => new StoreCurrencyDto(
                    currency.Currency,
                    new StoreBillingAccountDto(
                        currency.BillingAccount.CompanyId,
                        currency.BillingAccount.BankName,
                        currency.BillingAccount.AccountNumber,
                        currency.BillingAccount.Currency)))
                .ToList(),
            store.PrimaryCurrency,
            store.PricingMode.ToString(),
            store.SalesCountries.ToList(),
            featured,
            store.AdvisorMembershipId,
            advisorName,
            store.PublishBlockers().Select(blocker => blocker.ToString()).ToList(),
            store.Version,
            store.UpdatedAt);
    }
}
