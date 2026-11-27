using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary>Los pasos del asistente por su nombre en la auditoría (<c>changedFields</c>). Nombres de pasos, nunca valores de Siigo.</summary>
internal static class SiigoSettingsSteps
{
    public const string Company = "company";
    public const string Document = "document";
    public const string Taxes = "taxes";
    public const string Payments = "payments";
    public const string Sellers = "sellers";
    public const string AccountReset = "account_reset";
}

/// <summary>Lo que comparten los casos de uso de la configuración (spec 2026-11-18 §5.3): la doble capa de autorización, la conexión del
/// tenant, la configuración con su versión, la auditoría y el armado del DTO.</summary>
internal static class SiigoSettingsSupport
{
    private const string ConcurrencyConflict = "concurrency.conflict";

    /// <summary>Si ninguna clave se repite entre los elementos de una lista del cuerpo (los textos, carácter por carácter). Un elemento
    /// nulo no cuenta: falla aparte, en su propio campo.</summary>
    public static bool HasNoRepeated<TItem, TKey>(IEnumerable<TItem?> items, Func<TItem, TKey> key)
        where TItem : class
    {
        var keys = items.OfType<TItem>().Select(key).ToList();
        return keys.Distinct().Count() == keys.Count;
    }

    /// <summary>Tenant de la ruta, permiso <c>siigo.settings.manage</c> y módulo <c>siigo</c> (403, nunca 404).</summary>
    public static Task AuthorizeAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken) =>
        SiigoAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, SiigoPermissions.SettingsManage, cancellationToken);

    /// <summary>La conexión de Siigo del tenant; una de otro tenant, inexistente o de otro proveedor da 403 <c>authorization.denied</c>.</summary>
    public static async Task<SiigoConnectionInfo> ConnectionAsync(
        ISiigoConnectionDirectory connections, Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await connections.FindAsync(tenantId, connectionId, cancellationToken) ?? throw SiigoAuthorization.Denied();

    /// <summary>La configuración, creada vacía al primer acceso. Con otro tenant en la fila, 403.</summary>
    public static async Task<SiigoSettings> SettingsAsync(
        ISiigoSettingsRepository settings, Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await settings.FindOrCreateAsync(tenantId, connectionId, withVariantLinks: false, cancellationToken)
        ?? throw SiigoAuthorization.Denied();

    /// <summary>412 <c>concurrency.conflict</c> si el <c>If-Match</c> no es la versión que se tiene.</summary>
    public static void EnsureVersion(SiigoSettings settings, long expectedVersion)
    {
        if (settings.Version != expectedVersion)
        {
            throw new RequestConcurrencyException(ConcurrencyConflict, "The Siigo settings changed after they were loaded.");
        }
    }

    /// <summary>En la misma unidad de trabajo que el cambio: si el guardado pierde, no queda auditoría. Lleva el nombre del paso y nada
    /// más: ni ids de Siigo ni valores.</summary>
    public static void Audit(
        ISiigoAuditRecorder recorder,
        IExecutionContext executionContext,
        SiigoSettings settings,
        string step,
        DateTimeOffset now,
        string action = SiigoAuditActions.SettingsUpdated) =>
        recorder.Record(
            settings.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            action,
            SiigoAuditActions.ConnectionResourceType,
            settings.ConnectionId.ToString(),
            JsonSerializer.Serialize(new[] { step }),
            now);

    public static async Task<SiigoSettingsDto> ToDtoAsync(
        SiigoSettings settings,
        SiigoConnectionInfo connection,
        ISiigoCompanyDirectory companies,
        ISiigoTaxRateDirectory taxRates,
        ISiigoMemberDirectory members,
        CancellationToken cancellationToken)
    {
        SiigoCompanyRef? company = null;
        IReadOnlyList<SiigoBillingAccountRef> accounts = [];
        IReadOnlyList<SiigoWithholdingRef> withholdings = [];
        if (settings.CompanyId is { } companyId)
        {
            company = await companies.FindCompanyAsync(settings.TenantId, companyId, cancellationToken);
            accounts = await companies.ListBillingAccountsAsync(settings.TenantId, companyId, cancellationToken);
            withholdings = await taxRates.ListWithholdingRatesAsync(settings.TenantId, companyId, cancellationToken);
        }

        var vatInUse = await taxRates.ListVatPercentagesInUseAsync(settings.TenantId, cancellationToken);

        // El IVA: lo que está en uso, más lo que ya se vinculó aunque hoy no se use (una variante que se desactivó no borra el vínculo).
        var vat = vatInUse
            .Concat(settings.TaxLinks.Select(link => link.TaxPercentage))
            .Distinct()
            .Order()
            .Select(percentage => new SiigoVatLinkDto(
                percentage, settings.TaxLinks.FirstOrDefault(link => link.TaxPercentage == percentage)?.SiigoTaxId))
            .ToArray();

        var withholdingLinks = withholdings
            .OrderBy(rate => rate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rate => rate.Percentage)
            .Select(rate => new SiigoWithholdingLinkDto(
                rate.TaxRateId,
                rate.Name,
                rate.Type,
                rate.Percentage,
                settings.WithholdingLinks.FirstOrDefault(link => link.TaxRateId == rate.TaxRateId)?.SiigoTaxId))
            .ToArray();

        // Las cuentas de la empresa y, aparte, los vínculos de una cuenta que ya no está (el pedido congela el texto viejo).
        var accountLinks = accounts
            .Select(account => new SiigoAccountLinkDto(
                account.Key,
                account.BankName,
                account.AccountNumber,
                settings.PaymentLinks.FirstOrDefault(link => link.BankAccountKey == account.Key)?.SiigoPaymentTypeId,
                InCompany: true))
            .Concat(settings.PaymentLinks
                .Where(link => accounts.All(account => account.Key != link.BankAccountKey))
                .Select(link => new SiigoAccountLinkDto(link.BankAccountKey, null, null, link.SiigoPaymentTypeId, InCompany: false)))
            .ToArray();

        // Los miembros activos y, aparte, los vínculos de quien ya no está (para verlo y poder quitarlo), por nombre o correo.
        var roster = await members.ListAsync(
            settings.TenantId, [.. settings.SellerLinks.Select(link => link.MembershipId)], cancellationToken);
        var sellers = roster
            .Select(member =>
            {
                var link = settings.SellerLinks.FirstOrDefault(value => value.MembershipId == member.MembershipId);
                return new SiigoSellerDto(
                    member.MembershipId, member.DisplayName, member.Email, member.Active, link?.SiigoUserId, link?.SiigoUserName);
            })
            // Una membresía purgada ya no sale del directorio, pero su vínculo sigue en la base: se muestra como fila inactiva para
            // poder verlo y quitarlo, en vez de dejarlo huérfano e invisible.
            .Concat(settings.SellerLinks
                .Where(link => roster.All(member => member.MembershipId != link.MembershipId))
                .Select(link => new SiigoSellerDto(link.MembershipId, null, null, Active: false, link.SiigoUserId, link.SiigoUserName)))
            .OrderBy(seller => seller.Name ?? seller.Email ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SiigoSettingsDto(
            settings.ConnectionId,
            new SiigoConnectionDto(connection.Name, connection.Status),
            settings.CompanyId,
            company?.Name,
            new SiigoDocumentDto(
                settings.DocumentTypeId,
                settings.CostCenterId,
                settings.WarehouseId,
                settings.SendToDian,
                settings.SendMail,
                settings.FreightSeparateInvoice,
                settings.FreightProductCode,
                settings.PriceMode.ToString()),
            new SiigoTaxesDto(vat, withholdingLinks),
            new SiigoPaymentsDto(
                accountLinks,
                settings.CreditPaymentTypeId,
                settings.CreditDays,
                settings.CashOnDeliveryPaymentTypeId,
                settings.DefaultPaymentTypeId),
            sellers,
            settings.SellersAutoLinkedAt,
            settings.ProductsAutoLinkedAt,
            settings.Version,
            ReadinessOf(settings, connection));
    }

    // Las credenciales las decide la conexión de Integrations, no la fila: una conexión con la clave rechazada deja el paso incompleto,
    // con el estado como código. Una pausada está configurada: pausar es dejar de facturar, no le quita nada a la credencial, y
    // connection.status lo sigue diciendo.
    private static IReadOnlyList<SiigoStepReadinessDto> ReadinessOf(SiigoSettings settings, SiigoConnectionInfo connection) =>
        [
            .. SiigoSettingsReadiness.Evaluate(settings).Select(step => step.Step == SiigoSetupStep.Credentials
                ? CredentialsStep(connection)
                : new SiigoStepReadinessDto(step.Step.ToString(), step.Complete, step.MissingCodes)),
        ];

    private static SiigoStepReadinessDto CredentialsStep(SiigoConnectionInfo connection) =>
        connection.Status is "Active" or "Paused"
            ? new SiigoStepReadinessDto(SiigoSetupStep.Credentials.ToString(), true, [])
            : new SiigoStepReadinessDto(SiigoSetupStep.Credentials.ToString(), false, [$"connection_{ToSnakeCase(connection.Status)}"]);

    private static string ToSnakeCase(string value) =>
        string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? $"_{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));
}
