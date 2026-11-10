namespace Modules.Store.Domain;

/// <summary>
/// La tienda pública de un tenant (una por tenant). Guarda los ajustes, las monedas con su cuenta,
/// los países de venta y si está publicada. Una tienda publicada nunca queda con un bloqueo de
/// publicación: <see cref="Configure"/> lo revisa antes de tocar nada.
/// </summary>
public sealed class OnlineStore
{
    public const int MaxCurrencies = 10;

    private readonly List<StoreCurrency> _currencies = [];
    private readonly List<string> _salesCountries = [];
    private readonly List<Guid> _featuredCategoryIds = [];

    // EF Core materializa por acá; CreateEmpty es el único punto de entrada de código.
    private OnlineStore()
    {
        Contact = new StoreContact(null, null, null, null, null, null);
    }

    public Guid TenantId { get; private set; }

    public string? Slug { get; private set; }

    public bool IsPublished { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Sustituye al nombre del tenant en la tienda; null = usar el del tenant.</summary>
    public string? Name { get; private set; }

    public string? PrimaryColor { get; private set; }

    /// <summary>El override tal como se guardó; null = automático.</summary>
    public string? OnPrimaryColor { get; private set; }

    public string? AccentTextColor { get; private set; }

    public Guid? BannerFileId { get; private set; }

    public string? BannerText { get; private set; }

    public StoreContact Contact { get; private set; }

    public string? DataPolicyUrl { get; private set; }

    /// <summary>Cambia sólo cuando cambia la URL: es la fecha que queda en el consentimiento.</summary>
    public DateTimeOffset? DataPolicySetAt { get; private set; }

    public StorePricingMode PricingMode { get; private set; }

    /// <summary>Asesor de los pedidos de la tienda (spec 2026-11-09, D2); null = el owner del tenant, resuelto al crear el pedido.</summary>
    public Guid? AdvisorMembershipId { get; private set; }

    public string? PrimaryCurrency { get; private set; }

    public IReadOnlyList<StoreCurrency> Currencies => _currencies;

    public IReadOnlyList<string> SalesCountries => _salesCountries;

    public IReadOnlyList<Guid> FeaturedCategoryIds => _featuredCategoryIds;

    /// <summary>0 mientras nunca se guardó; es el ETag del backoffice.</summary>
    public long Version { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public static OnlineStore CreateEmpty(Guid tenantId, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            PricingMode = StorePricingMode.RetailOnly,
            Version = 0,
        };

    /// <summary>
    /// Devuelve false (y no sube la versión) cuando nada cambió y la tienda ya se había guardado: un PUT idéntico no escribe ni audita.
    /// La primera vez siempre es true, aunque todo quede en sus valores por defecto.
    /// </summary>
    public bool Configure(
        StoreSettingsInput input,
        Func<string, bool> isKnownCountry,
        Guid updatedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(isKnownCountry);

        // Todo se valida y se arma antes de asignar: un rechazo no deja la tienda a medias.
        string? slug = null;
        if (StoreText.Clean(input.Slug) is not null)
        {
            if (!StoreSlug.TryNormalize(input.Slug, out var normalized, out var slugError))
            {
                throw new StoreDomainException(slugError!, "El slug de la tienda no es válido.");
            }

            slug = normalized;
        }

        var primaryColor = StoreColorScheme.ParseRequired(input.PrimaryColor);
        var onPrimaryColor = StoreColorScheme.ParseOptional(input.OnPrimaryColor);
        var accentTextColor = StoreColorScheme.ParseOptional(input.AccentTextColor);

        ValidateCurrencies(input.Currencies, input.PrimaryCurrency);
        ValidateSalesCountries(input.SalesCountries, isKnownCountry);

        var policyUrl = StoreText.Clean(input.DataPolicyUrl);
        var currencies = input.Currencies.Select(NormalizeCurrency).ToList();
        var countries = input.SalesCountries.Select(StoreCountry.Normalize).ToList();

        if (IsPublished)
        {
            var blockers = Blockers(slug, currencies.Count, countries.Count);
            if (blockers.Count > 0)
            {
                throw new StoreDomainException(StoreErrorCodes.For(blockers[0]), "La tienda publicada no puede quedar incompleta.");
            }
        }

        if (!string.Equals(DataPolicyUrl, policyUrl, StringComparison.Ordinal))
        {
            DataPolicySetAt = policyUrl is null ? null : now;
        }

        var before = StateKey();

        Slug = slug;
        Name = StoreText.Clean(input.Name);
        PrimaryColor = primaryColor.ToHex();
        OnPrimaryColor = onPrimaryColor?.ToHex();
        AccentTextColor = accentTextColor?.ToHex();
        BannerFileId = input.BannerFileId;
        BannerText = StoreText.Clean(input.BannerText);
        Contact = new StoreContact(
            StoreText.Clean(input.Contact.WhatsApp),
            StoreText.Clean(input.Contact.Address),
            StoreText.Clean(input.Contact.Schedule),
            StoreText.Clean(input.Contact.InstagramUrl),
            StoreText.Clean(input.Contact.FacebookUrl),
            StoreText.Clean(input.Contact.TiktokUrl));
        DataPolicyUrl = policyUrl;
        PricingMode = input.PricingMode;
        AdvisorMembershipId = input.AdvisorMembershipId;
        PrimaryCurrency = currencies.Count == 0 ? null : input.PrimaryCurrency!.Trim().ToUpperInvariant();

        _currencies.Clear();
        _currencies.AddRange(currencies);
        _salesCountries.Clear();
        _salesCountries.AddRange(countries);
        _featuredCategoryIds.Clear();
        _featuredCategoryIds.AddRange(input.FeaturedCategoryIds);

        var changed = Version == 0 || !string.Equals(before, StateKey(), StringComparison.Ordinal);
        if (changed)
        {
            Touch(updatedBy, now);
        }

        return changed;
    }

    // Los records componen su ToString con todos sus campos, así que la clave cambia si cambia cualquiera.
    private string StateKey() => string.Join(
        '',
        Slug, Name, PrimaryColor, OnPrimaryColor, AccentTextColor, BannerFileId, BannerText, Contact, DataPolicyUrl, PricingMode, AdvisorMembershipId,
        PrimaryCurrency, string.Join('|', _currencies), string.Join('|', _salesCountries), string.Join('|', _featuredCategoryIds));

    /// <summary>Lo que falta para publicar, en el orden del contrato; vacío = publicable.</summary>
    public IReadOnlyList<StorePublishBlocker> PublishBlockers() =>
        Blockers(Slug, _currencies.Count, _salesCountries.Count);

    public void Publish(Guid by, DateTimeOffset now)
    {
        var blockers = PublishBlockers();
        if (blockers.Count > 0)
        {
            throw new StoreDomainException(StoreErrorCodes.For(blockers[0]), "La tienda no se puede publicar todavía.");
        }

        if (IsPublished)
        {
            return;
        }

        IsPublished = true;
        PublishedAt = now;
        Touch(by, now);
    }

    public void Unpublish(Guid by, DateTimeOffset now)
    {
        if (!IsPublished)
        {
            return;
        }

        IsPublished = false;
        PublishedAt = null;
        Touch(by, now);
    }

    /// <summary>Lo que pinta la tienda; null mientras nunca se guardó un color primario.</summary>
    public StoreResolvedColors? ResolveColors()
    {
        if (!StoreColor.TryParse(PrimaryColor, out var primary))
        {
            return null;
        }

        // Los overrides se guardaron ya validados: null = automático.
        StoreColor? onPrimary = StoreColor.TryParse(OnPrimaryColor, out var o) ? o : null;
        StoreColor? accentText = StoreColor.TryParse(AccentTextColor, out var a) ? a : null;

        return StoreColorScheme.Resolve(primary, onPrimary, accentText);
    }

    public bool Sells(string currency) =>
        _currencies.Any(c => string.Equals(c.Currency, currency, StringComparison.OrdinalIgnoreCase));

    /// <summary>La cuenta de esa moneda; null cuando la tienda no la vende.</summary>
    public StoreBillingAccount? BillingAccountFor(string currency) =>
        _currencies
            .FirstOrDefault(c => string.Equals(c.Currency, currency, StringComparison.OrdinalIgnoreCase))
            ?.BillingAccount;

    /// <summary>
    /// Los códigos en el orden guardado. La principal no se mueve: sólo se informa en
    /// <see cref="PrimaryCurrency"/> y la app de la tienda decide cuál muestra primero.
    /// </summary>
    public IReadOnlyList<string> CurrenciesForVisitors() => _currencies.Select(c => c.Currency).ToList();

    public bool SellsTo(string country) =>
        _salesCountries.Any(c => string.Equals(c, country, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puro, y sin I/O para que Application lo corra antes de cualquier consulta. Cada chequeo
    /// recorre toda la lista antes de pasar al siguiente: formato, repetida, cuenta de otra moneda
    /// y principal. Gana la primera falla.
    /// </summary>
    public static void ValidateCurrencies(IReadOnlyList<StoreCurrency> entries, string? primary)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count > MaxCurrencies)
        {
            throw new StoreDomainException(
                StoreErrorCodes.ValidationFailed,
                $"La tienda admite hasta {MaxCurrencies} monedas.");
        }

        // Las entradas se recortan igual que la principal: " cop " y "COP" son la misma moneda.
        if (entries.Any(e => !IsCurrencyShape(e.Currency?.Trim())))
        {
            throw new StoreDomainException(StoreErrorCodes.CurrencyInvalid, "La moneda debe ser de 3 letras.");
        }

        var codes = entries.Select(e => e.Currency.Trim().ToUpperInvariant()).ToList();
        if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
        {
            throw new StoreDomainException(StoreErrorCodes.CurrencyDuplicate, "Una moneda no puede repetirse.");
        }

        if (entries.Any(e => !string.Equals(e.BillingAccount.Currency?.Trim(), e.Currency.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new StoreDomainException(
                StoreErrorCodes.CurrencyMismatch,
                "La cuenta de cobro debe estar en la misma moneda de su entrada.");
        }

        // Sin monedas no hay principal, y con monedas tiene que ser una de ellas.
        var primaryOk = entries.Count == 0
            ? primary is null
            : primary is not null && codes.Contains(primary.Trim().ToUpperInvariant(), StringComparer.Ordinal);
        if (!primaryOk)
        {
            throw new StoreDomainException(
                StoreErrorCodes.PrimaryCurrencyInvalid,
                "La moneda principal debe ser una de las monedas de la tienda.");
        }
    }

    /// <summary>
    /// Puro: el predicado dice si el código es un país real (en el backend es
    /// <c>IsKnownRegion</c>). Primero se revisan todos los países por forma o desconocidos y luego
    /// los repetidos.
    /// </summary>
    public static void ValidateSalesCountries(IReadOnlyList<string> countries, Func<string, bool> isKnownCountry)
    {
        ArgumentNullException.ThrowIfNull(countries);
        ArgumentNullException.ThrowIfNull(isKnownCountry);

        if (countries.Any(c => !StoreCountry.IsShapeValid(c) || !isKnownCountry(StoreCountry.Normalize(c))))
        {
            throw new StoreDomainException(StoreErrorCodes.SalesCountryInvalid, "Un país de venta no es válido.");
        }

        var codes = countries.Select(StoreCountry.Normalize).ToList();
        if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
        {
            throw new StoreDomainException(StoreErrorCodes.SalesCountryDuplicate, "Un país de venta no puede repetirse.");
        }
    }

    /// <summary>La forma de un código de moneda (3 letras); que el catálogo la use lo decide Application.</summary>
    public static bool IsCurrencyShape(string? code) =>
        code is { Length: 3 } && code.All(char.IsAsciiLetter);

    // La política de datos no bloquea: es opcional para publicar (decisión del owner, 2026-11-05).
    private static List<StorePublishBlocker> Blockers(string? slug, int currencies, int countries)
    {
        var blockers = new List<StorePublishBlocker>();
        if (slug is null)
        {
            blockers.Add(StorePublishBlocker.SlugRequired);
        }

        if (currencies == 0)
        {
            blockers.Add(StorePublishBlocker.CurrencyRequired);
        }

        if (countries == 0)
        {
            blockers.Add(StorePublishBlocker.SalesCountryRequired);
        }

        return blockers;
    }

    private static StoreCurrency NormalizeCurrency(StoreCurrency entry) =>
        new(
            entry.Currency.Trim().ToUpperInvariant(),
            entry.BillingAccount with
            {
                BankName = entry.BillingAccount.BankName.Trim(),
                AccountNumber = entry.BillingAccount.AccountNumber.Trim(),
                Currency = entry.BillingAccount.Currency.Trim().ToUpperInvariant(),
            });

    private void Touch(Guid by, DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
        UpdatedBy = by;
    }
}
