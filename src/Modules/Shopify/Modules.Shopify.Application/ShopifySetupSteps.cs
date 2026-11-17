using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-11-17 (onboarding) §4.1: las claves de los cinco pasos del asistente, en el orden en que se muestran.
/// Son contrato con la pantalla.</summary>
public static class ShopifySetupStepKeys
{
    public const string Company = "company";
    public const string Advisor = "advisor";
    public const string Buyers = "buyers";
    public const string Products = "products";
    public const string History = "history";

    public static IReadOnlyList<string> All { get; } = [Company, Advisor, Buyers, Products, History];
}

/// <summary>Spec 2026-11-17 (onboarding) §4.1: los códigos de lo que le falta a un paso. Contrato con la pantalla, que tiene
/// un texto para cada uno; no se traducen.</summary>
public static class ShopifySetupMissing
{
    public const string CompanyMissing = "company_missing";
    public const string TaxIdMissing = "tax_id_missing";
    public const string AdvisorMissing = "advisor_missing";
    public const string AdvisorInactive = "advisor_inactive";
    public const string ClassificationMissing = "classification_missing";
    public const string DocumentSourceMissing = "document_source_missing";
    public const string ImportNotDone = "import_not_done";
    public const string ImportFailed = "import_failed";
    public const string ScopeMissing = "scope_missing";
    public const string HistoryNotRun = "history_not_run";
}

/// <summary>El estado de un paso; viaja con su nombre.</summary>
public enum ShopifySetupStepStatus
{
    Done,
    Pending,
    Blocked,
}

/// <param name="BlockedBy">Las claves de los pasos de los que espera; <c>[]</c> salvo en <see cref="ShopifySetupStepStatus.Blocked"/>.</param>
/// <param name="Missing">Los códigos de <see cref="ShopifySetupMissing"/> de lo que falta; <c>[]</c> en un paso hecho.</param>
public sealed record ShopifySetupStep(
    string Key, ShopifySetupStepStatus Status, IReadOnlyList<string> BlockedBy, IReadOnlyList<string> Missing);

/// <summary>Lo que el BFF averigua de la tienda para calcular los pasos (spec §4.1, plan D3–D4).</summary>
/// <param name="HasCompany">La tienda tiene empresa (<c>store.Billing</c> no nulo): el <c>PUT</c> y la importación la exigen.</param>
/// <param name="CompanyActive">La empresa de la tienda existe en el tenant y está activa.</param>
/// <param name="CompanyHasTaxId">El NIT de la empresa no está vacío.</param>
/// <param name="IsConfigured">La configuración pasó por el <c>PUT</c> (plan D4): hasta entonces las claves del documento son
/// sugerencia.</param>
/// <param name="ScopeGranted">La tienda concedió <c>read_products</c>.</param>
/// <param name="LastImportStatus">El estado de la última importación de productos; <c>null</c> si nunca hubo.</param>
/// <param name="CoverageAccepted">Alguien eligió «Seguir así» con la cobertura que había.</param>
/// <param name="CoverageComplete">La última medición encontró todo el catálogo de la tienda vinculado.</param>
/// <param name="HistoryRan">Se corrió una sincronización desde el asistente.</param>
public sealed record ShopifySetupFacts(
    bool HasCompany,
    bool CompanyActive,
    bool CompanyHasTaxId,
    bool HasAdvisor,
    bool AdvisorActive,
    bool HasClassification,
    bool IsConfigured,
    bool ScopeGranted,
    ShopifyProductImportStatus? LastImportStatus,
    bool CoverageAccepted,
    bool CoverageComplete,
    bool HistoryRan);

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.1 y §7 H5: los pasos del asistente como función pura de <see cref="ShopifySetupFacts"/>.
/// Un paso está <c>Done</c> si cumple; si no, <c>Blocked</c> cuando espera de otro paso; si no, <c>Pending</c>. <c>Missing</c>
/// se calcula igual en <c>Blocked</c> (la pantalla explica qué faltará), y un paso <c>Done</c> no lleva ni <c>Missing</c> ni
/// <c>BlockedBy</c>.
/// </summary>
public static class ShopifySetupSteps
{
    public static IReadOnlyList<ShopifySetupStep> Evaluate(ShopifySetupFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var waitsForCompany = facts.HasCompany ? Array.Empty<string>() : [ShopifySetupStepKeys.Company];

        var companyDone = facts.HasCompany && facts.CompanyActive && facts.CompanyHasTaxId;
        var company = Build(
            ShopifySetupStepKeys.Company,
            companyDone,
            [],
            !facts.HasCompany || !facts.CompanyActive ? [ShopifySetupMissing.CompanyMissing] : [ShopifySetupMissing.TaxIdMissing]);

        var advisorDone = facts.HasAdvisor && facts.AdvisorActive;
        var advisor = Build(
            ShopifySetupStepKeys.Advisor,
            advisorDone,
            waitsForCompany,
            [facts.HasAdvisor ? ShopifySetupMissing.AdvisorInactive : ShopifySetupMissing.AdvisorMissing]);

        // El PUT de la tienda exige un asesor activo: los compradores esperan también por él.
        var buyersMissing = new List<string>();
        if (!facts.HasClassification)
        {
            buyersMissing.Add(ShopifySetupMissing.ClassificationMissing);
        }

        if (!facts.IsConfigured)
        {
            buyersMissing.Add(ShopifySetupMissing.DocumentSourceMissing);
        }

        var buyers = Build(
            ShopifySetupStepKeys.Buyers,
            facts.HasClassification && facts.IsConfigured,
            [.. waitsForCompany, .. advisorDone ? Array.Empty<string>() : [ShopifySetupStepKeys.Advisor]],
            buyersMissing);

        var products = Build(
            ShopifySetupStepKeys.Products,
            facts.LastImportStatus == ShopifyProductImportStatus.Done || facts.CoverageAccepted || facts.CoverageComplete,
            waitsForCompany,
            [ProductsMissing(facts)]);

        var importLive = facts.LastImportStatus is ShopifyProductImportStatus.Pending or ShopifyProductImportStatus.Running;
        var history = Build(
            ShopifySetupStepKeys.History,
            facts.HistoryRan,
            importLive ? [ShopifySetupStepKeys.Products] : [],
            [ShopifySetupMissing.HistoryNotRun]);

        return [company, advisor, buyers, products, history];
    }

    /// <summary>El primer paso que no está <c>Done</c>, o <c>null</c> si todos lo están (spec §4.1: retomar donde quedó).</summary>
    public static string? OpenStep(IReadOnlyList<ShopifySetupStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.FirstOrDefault(step => step.Status != ShopifySetupStepStatus.Done)?.Key;
    }

    // Uno solo, por precedencia: sin el scope no hay importación posible; con él, una fallida dice más que «falta».
    private static string ProductsMissing(ShopifySetupFacts facts) =>
        !facts.ScopeGranted ? ShopifySetupMissing.ScopeMissing
        : facts.LastImportStatus == ShopifyProductImportStatus.Failed ? ShopifySetupMissing.ImportFailed
        : ShopifySetupMissing.ImportNotDone;

    private static ShopifySetupStep Build(string key, bool done, string[] blockedBy, IReadOnlyList<string> missing)
    {
        if (done)
        {
            return new ShopifySetupStep(key, ShopifySetupStepStatus.Done, [], []);
        }

        return new ShopifySetupStep(
            key, blockedBy.Length > 0 ? ShopifySetupStepStatus.Blocked : ShopifySetupStepStatus.Pending, blockedBy, [.. missing]);
    }
}
