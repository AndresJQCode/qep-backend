using BuildingBlocks.Application;
using FluentValidation;
using Modules.Store.Domain;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c> y 0 es «nunca se guardó».</param>
public sealed record UpdateStoreSettingsCommand(Guid TenantId, long ExpectedVersion, StoreSettingsRequest Body)
    : ICommand<StoreSettingsDto>;

/// <summary>
/// La forma del cuerpo: tamaños de texto y de lista, y que nada falte. Los topes son los de HTTP contract A. Lo que depende del
/// dominio (slug, colores, monedas, países) lo decide el dominio con su propio código <c>store.settings.*</c>, no un
/// <c>validation.failed</c> genérico.
/// </summary>
public sealed class UpdateStoreSettingsValidator : AbstractValidator<UpdateStoreSettingsCommand>
{
    public const int NameMaxLength = 80;

    public const int BannerTextMaxLength = 200;

    public const int WhatsAppMaxLength = 32;

    public const int AddressMaxLength = 200;

    public const int ScheduleMaxLength = 200;

    public const int MaxFeaturedCategories = 12;

    public const int MaxSalesCountries = 50;

    // Los anchos de las columnas de companies.company_bank_accounts: si la cuenta no cabe ahí, no puede ser de la empresa.
    public const int BankNameMaxLength = 120;

    public const int AccountNumberMaxLength = 32;

    public UpdateStoreSettingsValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(command => command.Body).NotNull().OverridePropertyName("body");

        When(command => command.Body is not null, () =>
        {
            Text(command => command.Body.Name, NameMaxLength, "name");
            Text(command => command.Body.BannerText, BannerTextMaxLength, "bannerText");
            Text(command => command.Body.WhatsApp, WhatsAppMaxLength, "whatsApp");
            Text(command => command.Body.Address, AddressMaxLength, "address");
            Text(command => command.Body.Schedule, ScheduleMaxLength, "schedule");

            RuleFor(command => command.Body.PricingMode)
                .Must(value => StorePricingModeNames.TryParse(value, out _))
                .OverridePropertyName("pricingMode");

            RuleFor(command => command.Body.Currencies)
                .NotNull()
                .Must(list => list is null || list.Count <= OnlineStore.MaxCurrencies)
                .OverridePropertyName("currencies");
            RuleForEach(command => command.Body.Currencies)
                .Must(entry => entry is { BillingAccount: not null })
                .WithMessage("Cada moneda lleva su cuenta de cobro.")
                .OverridePropertyName("currencies");
            RuleForEach(command => command.Body.Currencies)
                .Where(entry => entry?.BillingAccount is not null)
                .Must(entry => entry!.BillingAccount!.CompanyId is { } id && id != Guid.Empty)
                .WithMessage("La cuenta de cobro necesita su empresa.")
                .Must(entry => HasText(entry!.BillingAccount!.BankName, BankNameMaxLength))
                .WithMessage($"El banco es obligatorio y admite hasta {BankNameMaxLength} caracteres.")
                .Must(entry => HasText(entry!.BillingAccount!.AccountNumber, AccountNumberMaxLength))
                .WithMessage($"El número de cuenta es obligatorio y admite hasta {AccountNumberMaxLength} caracteres.")
                .OverridePropertyName("currencies");

            RuleFor(command => command.Body.SalesCountries)
                .NotNull()
                .Must(list => list is null || list.Count <= MaxSalesCountries)
                .OverridePropertyName("salesCountries");

            RuleFor(command => command.Body.FeaturedCategoryIds)
                .NotNull()
                .Must(list => list is null || list.Count <= MaxFeaturedCategories)
                .Must(list => list is null || list.Distinct().Count() == list.Count)
                .OverridePropertyName("featuredCategoryIds");
        });
    }

    private static bool HasText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength;

    private void Text(System.Linq.Expressions.Expression<Func<UpdateStoreSettingsCommand, string?>> selector, int maxLength, string name) =>
        RuleFor(selector)
            .Must(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length <= maxLength)
            .WithMessage($"Admite hasta {maxLength} caracteres.")
            .OverridePropertyName(name);
}

/// <summary>
/// Orden de las comprobaciones: autorizar; forma del cuerpo; las puras (slug, URLs, monedas, países), que no tocan nada; la versión;
/// las que consultan (slug ajeno, monedas con precio, cada cuenta, banner, categorías); y sólo entonces el dominio arma la tienda.
/// El subdominio se reemplaza en la misma unidad de trabajo que la tienda: no hay restricción en la base que garantice una sola
/// fila <c>Subdomain</c> por tenant, así que dos guardados separados podrían dejar un hostname viejo apuntando a la tienda.
/// </summary>
public sealed class UpdateStoreSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IOnlineStoreRepository stores,
    IStoreDomainRepository domains,
    IStoreCatalogReader catalog,
    IStoreBillingAccounts billingAccounts,
    IStoreFileLookup files,
    IStoreMemberDirectory members,
    IStoreCountries countries,
    IStoreUnitOfWork unitOfWork,
    IStoreAuditPublisher auditPublisher,
    IClock clock,
    StoreOptions options,
    StoreSettingsComposer composer,
    IValidator<UpdateStoreSettingsCommand> validator)
    : ICommandHandler<UpdateStoreSettingsCommand, StoreSettingsDto>
{
    public async Task<StoreSettingsDto> HandleAsync(UpdateStoreSettingsCommand command, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, StorePermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var body = command.Body;
        var slug = NormalizeSlug(body.Slug);
        EnsureUrl(body.DataPolicyUrl);
        EnsureUrl(body.InstagramUrl);
        EnsureUrl(body.FacebookUrl);
        EnsureUrl(body.TiktokUrl);

        // El validador ya garantizó que las listas y las cuentas existen; lo que falte en un texto lo rechaza el dominio con su código.
        var entries = body.Currencies!
            .Select(entry => new StoreCurrency(
                entry!.Currency ?? string.Empty,
                new StoreBillingAccount(
                    entry.BillingAccount!.CompanyId!.Value,
                    entry.BillingAccount.BankName!,
                    entry.BillingAccount.AccountNumber!,
                    entry.BillingAccount.Currency ?? string.Empty)))
            .ToList();
        var salesCountries = body.SalesCountries!.Select(country => country ?? string.Empty).ToList();

        // R8: el predicado del dominio es la regla de «país real» del normalizador de teléfonos.
        OnlineStore.ValidateCurrencies(entries, body.PrimaryCurrency);
        OnlineStore.ValidateSalesCountries(salesCountries, countries.IsKnown);

        var existing = await stores.FindAsync(command.TenantId, cancellationToken);
        var store = existing ?? OnlineStore.CreateEmpty(command.TenantId, clock.UtcNow);
        if (store.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The store settings changed after they were loaded.");
        }

        if (slug is not null && await stores.SlugTakenByAnotherAsync(command.TenantId, slug, cancellationToken))
        {
            throw new ResourceConflictException(StoreErrorCodes.SlugTaken, "The store address is already in use.");
        }

        var resolvedEntries = await ResolveCurrenciesAsync(command.TenantId, entries, cancellationToken);
        await EnsureBannerAsync(command.TenantId, body.BannerFileId, cancellationToken);
        await EnsureCategoriesAsync(command.TenantId, body.FeaturedCategoryIds!, cancellationToken);
        if (body.AdvisorMembershipId is { } advisorId
            && !await members.IsActiveMemberAsync(command.TenantId, advisorId, cancellationToken))
        {
            throw new StoreDomainException(
                StoreErrorCodes.AdvisorNotFound, "El asesor no es un miembro activo de este tenant.");
        }

        var input = new StoreSettingsInput(
            slug,
            body.Name,
            body.PrimaryColor,
            body.OnPrimaryColor,
            body.AccentTextColor,
            body.BannerFileId,
            body.BannerText,
            new StoreContact(body.WhatsApp, body.Address, body.Schedule, body.InstagramUrl, body.FacebookUrl, body.TiktokUrl),
            body.DataPolicyUrl,
            StorePricingModeNames.TryParse(body.PricingMode, out var pricingMode) ? pricingMode : default,
            resolvedEntries,
            body.PrimaryCurrency,
            salesCountries,
            body.FeaturedCategoryIds!,
            body.AdvisorMembershipId);

        var previousSlug = store.Slug;
        var before = Snapshot(store);
        var now = clock.UtcNow;
        var changed = store.Configure(input, countries.IsKnown, executionContext.SubjectId, now);
        if (existing is not null && !changed)
        {
            // Nada cambió: ni versión nueva, ni auditoría, ni escritura. Se responde la tienda tal como está.
            return await composer.ComposeAsync(store, cancellationToken);
        }

        var changedFields = Changed(before, Snapshot(store));

        if (existing is null)
        {
            stores.Add(store);
        }

        if (existing is null || !string.Equals(previousSlug, store.Slug, StringComparison.Ordinal))
        {
            await domains.ReplaceSubdomainAsync(command.TenantId, store.Slug, options.RootDomain, cancellationToken);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            StoreAuditActions.SettingsUpdated,
            StoreAuditActions.Resources.Store,
            command.TenantId.ToString(),
            "success",
            changedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(store, cancellationToken);
    }

    private static string? NormalizeSlug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return StoreSlug.TryNormalize(raw, out var slug, out var errorCode)
            ? slug
            : throw new StoreDomainException(errorCode!, "El slug de la tienda no es válido.");
    }

    private static void EnsureUrl(string? raw)
    {
        if (!StoreUrls.IsValid(raw))
        {
            throw new StoreDomainException(StoreErrorCodes.UrlInvalid, "Una URL debe ser https:// y tener hasta 500 caracteres.");
        }
    }

    /// <summary>
    /// Cada moneda debe tener precios en el catálogo (una sola consulta) y cada cuenta se comprueba, en el orden de la lista, con la
    /// regla con que un pedido resuelve la suya. El resultado lleva la cuenta tal como la guarda la empresa.
    /// </summary>
    private async Task<List<StoreCurrency>> ResolveCurrenciesAsync(
        Guid tenantId, List<StoreCurrency> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return entries;
        }

        var inUse = (await catalog.CurrenciesInUseAsync(tenantId, cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entries.Any(entry => !inUse.Contains(entry.Currency.Trim())))
        {
            throw new StoreDomainException(
                StoreErrorCodes.CurrencyInvalid, "Una moneda de la tienda no tiene productos con precio.");
        }

        var resolved = new List<StoreCurrency>(entries.Count);
        foreach (var entry in entries)
        {
            var account = entry.BillingAccount;
            var validated = await billingAccounts.ValidateAsync(
                tenantId, account.CompanyId, account.BankName, account.AccountNumber, entry.Currency.Trim(), cancellationToken);
            resolved.Add(new StoreCurrency(entry.Currency, validated));
        }

        return resolved;
    }

    private async Task EnsureBannerAsync(Guid tenantId, Guid? fileId, CancellationToken cancellationToken)
    {
        if (fileId is { } id && await files.FindPublicImageAsync(tenantId, id, cancellationToken) is null)
        {
            throw new StoreDomainException(
                StoreErrorCodes.BannerInvalid, "El banner debe ser una imagen pública de la tienda.");
        }
    }

    private async Task EnsureCategoriesAsync(
        Guid tenantId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var names = await catalog.CategoryNamesAsync(tenantId, ids, cancellationToken);
        if (ids.Any(id => !names.ContainsKey(id)))
        {
            throw new StoreDomainException(
                StoreErrorCodes.CategoryNotFound, "Una categoría destacada no existe en este tenant.");
        }
    }

    // Sólo nombres de campo viajan a auditoría. Los valores (cuentas incluidas) se comparan acá y no salen de este método.
    private static Dictionary<string, string?> Snapshot(OnlineStore store) => new()
    {
        ["slug"] = store.Slug,
        ["name"] = store.Name,
        ["primaryColor"] = store.PrimaryColor,
        ["onPrimaryColor"] = store.OnPrimaryColor,
        ["accentTextColor"] = store.AccentTextColor,
        ["bannerFileId"] = store.BannerFileId?.ToString(),
        ["bannerText"] = store.BannerText,
        ["whatsApp"] = store.Contact.WhatsApp,
        ["address"] = store.Contact.Address,
        ["schedule"] = store.Contact.Schedule,
        ["instagramUrl"] = store.Contact.InstagramUrl,
        ["facebookUrl"] = store.Contact.FacebookUrl,
        ["tiktokUrl"] = store.Contact.TiktokUrl,
        ["dataPolicyUrl"] = store.DataPolicyUrl,
        ["pricingMode"] = store.PricingMode.ToString(),
        ["currencies"] = string.Join(
            '|',
            store.Currencies.Select(currency =>
                $"{currency.Currency}:{currency.BillingAccount.CompanyId}:{currency.BillingAccount.BankName}:{currency.BillingAccount.AccountNumber}")),
        ["primaryCurrency"] = store.PrimaryCurrency,
        ["salesCountries"] = string.Join('|', store.SalesCountries),
        ["featuredCategoryIds"] = string.Join('|', store.FeaturedCategoryIds),
        ["advisorMembershipId"] = store.AdvisorMembershipId?.ToString(),
    };

    private static List<string> Changed(Dictionary<string, string?> before, Dictionary<string, string?> after) =>
        after.Where(pair => !string.Equals(before[pair.Key], pair.Value, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();
}

internal static class StorePricingModeNames
{
    // Enum.TryParse acepta «1»: el contrato viaja por nombre, así que un número no es un modo.
    public static bool TryParse(string? value, out StorePricingMode mode)
    {
        mode = default;
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsDigit(value.Trim()[0])
            && Enum.TryParse(value.Trim(), ignoreCase: true, out mode)
            && Enum.IsDefined(mode);
    }
}

internal static class StoreUrls
{
    public const int MaxLength = 500;

    /// <summary>Vacío es válido (el campo es opcional); lo demás debe ser una URL absoluta https de hasta 500 caracteres.</summary>
    public static bool IsValid(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        return value.Length <= MaxLength
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host);
    }
}
