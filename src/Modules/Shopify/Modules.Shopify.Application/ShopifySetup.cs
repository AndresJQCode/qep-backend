using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary><c>GET …/stores/{connectionId}/setup?redirectUri=</c> (<c>integrations.connection.manage</c>): el asistente de la tienda.
/// <paramref name="RedirectUri"/> es a dónde vuelve la reautorización si falta <c>read_products</c>.</summary>
public sealed record GetShopifySetupQuery(Guid TenantId, Guid ConnectionId, string? RedirectUri) : IQuery<ShopifySetupDto>;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.1, plan Tarea 8 paso 4: la empresa que el asistente sugiere con el perfil de la tienda. Función
/// pura. Un perfil ausente o roto (sin <c>billingAddress</c>) da sólo el nombre de la tienda: nunca falla (Review Focus 5).
/// </summary>
public static class ShopifySetupSuggestion
{
    private const string Colombia = "CO";

    /// <summary>Lo que hay que preguntarle a Geography: el departamento (por el código de Shopify o, si no casa, el texto de la
    /// provincia) y la ciudad. <c>null</c> fuera de <c>CO</c> o sin ciudad: entonces la ciudad viaja como texto.</summary>
    public static (string Department, string City)? CityQuery(ShopifyShopProfile? profile)
    {
        var address = profile?.BillingAddress;
        if (address is null
            || !string.Equals(address.CountryCode?.Trim(), Colombia, StringComparison.OrdinalIgnoreCase)
            || Clean(address.City) is not { } city)
        {
            return null;
        }

        var department = (Clean(address.ProvinceCode) is { } code ? ColombiaProvinceCodes.DepartmentNameFor(code.ToUpperInvariant()) : null)
            ?? Clean(address.Province);
        return department is null ? null : (department, city);
    }

    /// <summary>La sugerencia. <paramref name="city"/> es lo que respondió Geography (<c>null</c> si no se preguntó): si la ciudad
    /// casó, viaja por id y sin texto; si no, el texto de Shopify y, si lo hubo, el departamento.</summary>
    public static ShopifySuggestedCompanyDto Build(ShopifyShopProfile? profile, string shopName, ShopifyCityLookup? city)
    {
        var address = profile?.BillingAddress;
        var lines = new[] { Clean(address?.Address1), Clean(address?.Address2) }.OfType<string>().ToArray();
        var matched = city?.City;
        return new ShopifySuggestedCompanyDto(
            Clean(address?.Company) ?? shopName,
            lines.Length == 0 ? null : string.Join(", ", lines),
            matched?.CityId,
            matched?.DepartmentId ?? city?.DepartmentId,
            matched is null ? Clean(address?.City) : null,
            Clean(address?.Phone),
            Clean(profile?.ContactEmail) ?? Clean(profile?.Email),
            Clean(address?.CountryCode)?.ToUpperInvariant());
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.1 y §7, plan Tarea 8: el BFF del asistente. En orden: tenant, permiso y módulo (403); la
/// conexión del tenant (403, nunca 404); la tienda (la crea si el módulo todavía no la tiene). Después compone cada sección por los
/// puertos y calcula los pasos con <see cref="ShopifySetupSteps"/>. No escribe nada más que la tienda nueva y la cobertura medida
/// en el acto (plan D8), que no sube la versión.
/// <para>Shopify caído no tumba la lectura (Review Focus 3): la sugerencia de IVA y la cobertura en el acto caen a <c>null</c> con un
/// log que lleva sólo el id de la conexión y el tipo de la excepción; nunca el correo ni el teléfono del perfil.</para>
/// </summary>
public sealed partial class GetShopifySetupHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyTenantConnections tenantConnections,
    IShopifyStoreRepository stores,
    IShopifyUnitOfWork unitOfWork,
    IShopifySetupCompanies companies,
    IShopifyMemberDirectory members,
    IShopifyStoreReferences references,
    IMembershipDirectory memberships,
    IShopifyGeographyLookup geography,
    IShopifyProductsSource productsSource,
    IShopifyReauthorization reauthorization,
    IShopifyProductImportRepository imports,
    ShopifyProductCoverageCalculator coverageCalculator,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyProductLinkRepository links,
    IClock clock,
    ILogger<GetShopifySetupHandler> logger) : IQueryHandler<GetShopifySetupQuery, ShopifySetupDto>
{
    /// <summary>Plan D8: el tope de la medición de la cobertura dentro de la lectura. Pasado, la lectura sigue sin ella.</summary>
    public static readonly TimeSpan CoverageTimeout = TimeSpan.FromSeconds(15);

    /// <summary>La ventana de <see cref="ShopifySetupStatsDto.ImportedLast7Days"/>.</summary>
    public static readonly TimeSpan StatsWindow = TimeSpan.FromDays(7);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The charges-VAT suggestion of Shopify store {ConnectionId} is unavailable: {ExceptionType}")]
    private static partial void LogSuggestionUnavailable(ILogger logger, Guid connectionId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The product coverage of Shopify store {ConnectionId} could not be measured for the setup: {ExceptionType}")]
    private static partial void LogCoverageUnavailable(ILogger logger, Guid connectionId, string exceptionType);

    public async Task<ShopifySetupDto> HandleAsync(GetShopifySetupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ShopifyPermissions.ConnectionManage, cancellationToken);
        var connection = await ShopifyStoreSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);
        var now = clock.UtcNow;
        var store = await ShopifyStoreSupport.FindOrCreateAsync(stores, unitOfWork, connection, now, cancellationToken);

        var summary = (await tenantConnections.ListAsync(query.TenantId, cancellationToken))
            .FirstOrDefault(candidate => candidate.ConnectionId == query.ConnectionId);
        var activeCompanies = await companies.ListActiveAsync(query.TenantId, cancellationToken);
        var selected = store.Billing is { } billing
            ? await companies.FindActiveAsync(query.TenantId, billing.CompanyId, cancellationToken)
            : null;
        var advisorActive = store.AdvisorMembershipId is { } advisorId
            && await members.IsActiveMemberAsync(query.TenantId, advisorId, cancellationToken);
        var lastImport = await imports.FindLatestAsync(query.TenantId, query.ConnectionId, cancellationToken);
        var scopeGranted = ShopifyScopes.CanReadProducts(connection.Scopes);
        var coverage = store.ProductsCoverage
            ?? (scopeGranted && lastImport is null && HasToken(connection)
                ? await MeasureCoverageAsync(connection, store, cancellationToken)
                : null);

        var steps = ShopifySetupSteps.Evaluate(new ShopifySetupFacts(
            HasCompany: store.Billing is not null,
            CompanyActive: selected is not null,
            CompanyHasTaxId: !string.IsNullOrWhiteSpace(selected?.TaxId),
            HasAdvisor: store.AdvisorMembershipId is not null,
            AdvisorActive: advisorActive,
            HasClassification: store.DefaultClassificationId is not null,
            IsConfigured: store.IsConfigured,
            ScopeGranted: scopeGranted,
            LastImportStatus: lastImport?.Status,
            CoverageAccepted: store.ProductsCoverageAcceptedAt is not null,
            CoverageComplete: coverage?.IsComplete ?? false,
            HistoryRan: store.SetupHistoryRanAt is not null));
        var companyDone = steps[0].Status == ShopifySetupStepStatus.Done;

        return new ShopifySetupDto(
            new ShopifySetupStoreDto(
                summary?.Name ?? connection.ShopDomain,
                store.ShopDomain ?? connection.ShopDomain,
                connection.Profile?.CurrencyCode ?? store.Billing?.Currency,
                summary?.Status ?? string.Empty,
                await ShopifyStoreSupport.ToDtoAsync(store, members, references, cancellationToken)),
            [.. steps.Select(step => new ShopifySetupStepDto(step.Key, step.Status.ToString(), step.BlockedBy, step.Missing))],
            ShopifySetupSteps.OpenStep(steps),
            new ShopifySetupCompanySectionDto(
                selected is null ? null : ToOption(selected, hasBankAccount: store.Billing?.BankName is not null),
                await SuggestAsync(connection, summary?.Name ?? connection.ShopDomain, cancellationToken),
                !companyDone && scopeGranted && HasToken(connection) ? await SuggestChargesVatAsync(connection, cancellationToken) : null),
            [.. activeCompanies.Select(company => ToOption(company, hasBankAccount: company.BankAccounts.Count > 0))],
            await AdvisorAsync(store, cancellationToken),
            await BuyersAsync(store, cancellationToken),
            new ShopifySetupProductsSectionDto(
                scopeGranted,
                !scopeGranted && !string.IsNullOrWhiteSpace(query.RedirectUri)
                    ? await reauthorization.CreateUrlAsync(connection.TenantId, connection.ShopDomain, query.RedirectUri.Trim(), cancellationToken)
                    : null,
                lastImport is null ? null : await GetShopifyProductImportHandler.ToDtoAsync(imports, lastImport, cancellationToken),
                coverage is null ? null : ToCoverage(coverage),
                store.ProductsCoverageAcceptedAt),
            new ShopifySetupHistorySectionDto(store.HistoryDays, store.SetupHistorySummary is { } run ? ToRun(run) : null),
            [.. (await mirrors.SummarizeWaitingAsync(query.TenantId, query.ConnectionId, null, cancellationToken))
                .Select(group => new ShopifyWaitingGroupDto(group.Reason, group.Count, group.Sample))],
            new ShopifySetupStatsDto(
                await mirrors.CountImportedSinceAsync(query.TenantId, query.ConnectionId, now - StatsWindow, cancellationToken),
                await links.CountLinkedProductsAsync(query.TenantId, query.ConnectionId, cancellationToken),
                store.LastReconciledAt));
    }

    private static bool HasToken(ShopifyConnectionRef connection) =>
        connection.IsOperational && !string.IsNullOrWhiteSpace(connection.AccessToken);

    private static ShopifySetupCompanyOptionDto ToOption(ShopifySetupCompanyRef company, bool hasBankAccount) =>
        new(
            company.Id,
            company.Name,
            string.IsNullOrWhiteSpace(company.TaxId) ? null : company.TaxId,
            company.DocumentType,
            company.Country,
            company.ChargesVat,
            hasBankAccount,
            [.. company.BankAccounts.Select(account => new ShopifySetupBankAccountDto(account.BankName, account.AccountNumber, account.Currency))]);

    private static ShopifySetupCoverageDto ToCoverage(ShopifyProductsCoverage coverage) =>
        new(
            coverage.ShopifyVariants,
            coverage.Matched,
            coverage.CatalogEmpty,
            [.. coverage.MissingSample.Select(sample => new ShopifyMissingVariantDto(sample.Sku, sample.Title))],
            coverage.MeasuredAt);

    private static ShopifySetupHistoryRunDto ToRun(ShopifySetupHistorySummary run) =>
        new(run.RanAt, run.Days, run.Fetched, run.Imported, [.. run.Waiting.Select(group => new ShopifyWaitingCountDto(group.Reason, group.Count))]);

    private async Task<ShopifySuggestedCompanyDto> SuggestAsync(
        ShopifyConnectionRef connection, string shopName, CancellationToken cancellationToken)
    {
        var city = ShopifySetupSuggestion.CityQuery(connection.Profile) is { } lookup
            ? await geography.LookupCityAsync(lookup.Department, lookup.City, cancellationToken)
            : null;
        return ShopifySetupSuggestion.Build(connection.Profile, shopName, city);
    }

    // Spec §4.3 y plan D9: sin productos no hay qué sugerir; cualquier falla de Shopify es «sin sugerencia».
    private async Task<bool?> SuggestChargesVatAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        try
        {
            var shop = await productsSource.ReadShopAsync(connection, cancellationToken);
            return shop.ProductsCount == 0 ? null : await productsSource.HasTaxableVariantAsync(connection, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogSuggestionUnavailable(logger, connection.ConnectionId, exception.GetType().Name);
            return null;
        }
    }

    // Plan D8: la primera vez que se abre el paso con el scope y sin importación, la cobertura se mide acá, con tope. Se guarda sin
    // subir la versión; si un PUT ganó entre la lectura y el guardado, la medición vuelve igual y la próxima lectura la guarda.
    private async Task<ShopifyProductsCoverage?> MeasureCoverageAsync(
        ShopifyConnectionRef connection, ShopifyStore store, CancellationToken cancellationToken)
    {
        ShopifyProductsCoverage coverage;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(CoverageTimeout);
            try
            {
                coverage = await coverageCalculator.MeasureAsync(connection, timeout.Token);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogCoverageUnavailable(logger, connection.ConnectionId, exception.GetType().Name);
                return null;
            }
        }

        try
        {
            store.RecordProductsCoverage(coverage);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (RequestConcurrencyException exception) when (ShopifyStoreSupport.IsConflict(exception))
        {
            unitOfWork.DiscardPendingChanges();
        }

        return coverage;
    }

    private async Task<ShopifySetupAdvisorSectionDto> AdvisorAsync(ShopifyStore store, CancellationToken cancellationToken)
    {
        var options = await members.ListActiveAsync(store.TenantId, cancellationToken);
        var me = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, store.TenantId, cancellationToken);
        return new ShopifySetupAdvisorSectionDto(
            store.AdvisorMembershipId,
            store.AdvisorMembershipId ?? me,
            [.. options.Select(option => new ShopifySetupMemberDto(option.MembershipId, option.Name, option.MembershipId == me))]);
    }

    private async Task<ShopifySetupBuyersSectionDto> BuyersAsync(ShopifyStore store, CancellationToken cancellationToken)
    {
        var keys = await mirrors.ListAttributeKeysAsync(
            store.TenantId, store.ConnectionId, GetShopifyDocumentSourcesHandler.RecentOrders, cancellationToken);
        var classifications = await references.ListClassificationsAsync(store.TenantId, cancellationToken);
        return new ShopifySetupBuyersSectionDto(
            ShopifyDocumentSources.Options(keys),
            store.DocumentAttributeKey,
            store.DocumentTypeAttributeKey,
            ShopifyDocumentTypes.Supported,
            store.DefaultIdentificationType,
            store.DefaultClassificationId,
            [.. classifications.Select(classification => new ShopifySetupClassificationDto(classification.Id, classification.Name))]);
    }
}
