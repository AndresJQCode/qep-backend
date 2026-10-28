using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-15 §7 <c>GET /stores</c> (<c>integrations.connection.read</c>).</summary>
public sealed record ListShopifyStoresQuery(Guid TenantId) : IQuery<IReadOnlyList<ShopifyStoreSummaryDto>>;

/// <summary>Spec 2026-10-15 §7 <c>GET /stores/{connectionId}</c> (<c>integrations.connection.read</c>).</summary>
public sealed record GetShopifyStoreQuery(Guid TenantId, Guid ConnectionId) : IQuery<ShopifyStoreDto>;

/// <summary>Spec 2026-10-15 §6.1 y §7 <c>PUT /stores/{connectionId}</c> (<c>integrations.connection.manage</c>).
/// <paramref name="ExpectedVersion"/> llega por <c>If-Match</c>.</summary>
public sealed record UpdateShopifyStoreCommand(
    Guid TenantId,
    Guid ConnectionId,
    Guid AdvisorMembershipId,
    Guid BillingCompanyId,
    string? BillingBankName,
    string? BillingAccountNumber,
    string? BillingCurrency,
    Guid? DefaultClassificationId,
    string? DocumentAttributeKey,
    string? DocumentTypeAttributeKey,
    string? DefaultIdentificationType,
    long ExpectedVersion) : ICommand<ShopifyStoreDto>;

/// <summary>
/// La forma de todo lo que <see cref="ShopifyStore.Configure"/> rechaza con <see cref="ArgumentException"/>: sin esto, un valor
/// fuera de forma daría 500. Cada error lleva el nombre del campo del cuerpo (camelCase) para que el formulario marque el input
/// (422 <c>validation.failed</c> con <c>errors</c>). Lo que depende de otros módulos (asesor, cuenta, clasificación) lo valida el
/// handler y responde con su código de §7.1.
/// </summary>
public sealed class UpdateShopifyStoreValidator : AbstractValidator<UpdateShopifyStoreCommand>
{
    public UpdateShopifyStoreValidator()
    {
        RuleFor(command => command.AdvisorMembershipId).NotEmpty().OverridePropertyName("advisorMembershipId");
        RuleFor(command => command.BillingCompanyId).NotEmpty().OverridePropertyName("billingCompanyId");
        RequiredText(command => command.BillingBankName, ShopifyBillingAccount.BankNameMaxLength, "billingBankName");
        RequiredText(command => command.BillingAccountNumber, ShopifyBillingAccount.AccountNumberMaxLength, "billingAccountNumber");
        RuleFor(command => command.BillingCurrency)
            .Must(currency => currency?.Trim() is { Length: ShopifyBillingAccount.CurrencyLength } code && code.All(char.IsAsciiLetter))
            .WithMessage("The billing currency must be a three-letter ISO 4217 code.")
            .OverridePropertyName("billingCurrency");
        RuleFor(command => command.DefaultClassificationId)
            .NotEqual(Guid.Empty)
            .When(command => command.DefaultClassificationId is not null)
            .OverridePropertyName("defaultClassificationId");
        RequiredText(command => command.DocumentAttributeKey, ShopifyStore.AttributeKeyMaxLength, "documentAttributeKey");
        RequiredText(command => command.DocumentTypeAttributeKey, ShopifyStore.AttributeKeyMaxLength, "documentTypeAttributeKey");
        // §6.2.1: el tipo por defecto es uno de los cuatro de Customers; uno distinto haría que todo comprador cayera al alta
        // incompleta sin que nadie supiera por qué.
        RuleFor(command => command.DefaultIdentificationType)
            .Must(type => ShopifyDocumentTypes.Normalize(type) is not null)
            .WithMessage($"The default identification type must be one of {string.Join(", ", ShopifyDocumentTypes.Supported)}.")
            .OverridePropertyName("defaultIdentificationType");
        RuleFor(command => command.ExpectedVersion).GreaterThan(0).OverridePropertyName("expectedVersion");
    }

    private void RequiredText(System.Linq.Expressions.Expression<Func<UpdateShopifyStoreCommand, string?>> field, int maxLength, string name) =>
        RuleFor(field)
            .Must(value => value?.Trim() is { Length: > 0 } trimmed && trimmed.Length <= maxLength)
            .WithMessage($"The value is required and cannot exceed {maxLength} characters.")
            .OverridePropertyName(name);
}

/// <summary>
/// §7 <c>GET /stores</c>: las conexiones de Shopify del tenant con lo que el módulo sabe de cada una. Sólo lee: una tienda que
/// todavía no existe en el módulo sale sin configurar y sin conciliación; la crea la primera lectura de su configuración.
/// </summary>
public sealed class ListShopifyStoresHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyTenantConnections connections,
    IShopifyStoreRepository stores,
    IShopifyOrderMirrorRepository mirrors) : IQueryHandler<ListShopifyStoresQuery, IReadOnlyList<ShopifyStoreSummaryDto>>
{
    public async Task<IReadOnlyList<ShopifyStoreSummaryDto>> HandleAsync(ListShopifyStoresQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.ConnectionRead, cancellationToken);

        var linked = await connections.ListAsync(query.TenantId, cancellationToken);
        var byConnection = (await stores.ListAsync(query.TenantId, cancellationToken)).ToDictionary(store => store.ConnectionId);
        var waiting = await mirrors.CountWaitingByConnectionAsync(query.TenantId, cancellationToken);
        return
        [
            .. linked.Select(connection =>
            {
                var store = byConnection.GetValueOrDefault(connection.ConnectionId);
                return new ShopifyStoreSummaryDto(
                    connection.ConnectionId,
                    connection.Name,
                    connection.ShopDomain,
                    connection.Status,
                    store?.IsConfigured ?? false,
                    waiting.GetValueOrDefault(connection.ConnectionId),
                    store?.LastReconciledAt);
            }),
        ];
    }
}

/// <summary>
/// §7 <c>GET /stores/{connectionId}</c>. La conexión tiene que ser de Shopify y del tenant, y su ruta tiene que apuntar a ella
/// (<see cref="IShopifyConnectionDirectory.FindAsync"/>); si no, 403 <c>authorization.denied</c>, nunca 404. §6.1: si el módulo
/// todavía no tiene la tienda, esta lectura la crea vacía.
/// </summary>
public sealed class GetShopifyStoreHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyStoreRepository stores,
    IShopifyMemberDirectory members,
    IShopifyStoreReferences references,
    IShopifyUnitOfWork unitOfWork,
    IClock clock) : IQueryHandler<GetShopifyStoreQuery, ShopifyStoreDto>
{
    public async Task<ShopifyStoreDto> HandleAsync(GetShopifyStoreQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.ConnectionRead, cancellationToken);
        var connection = await ShopifyStoreSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);

        var store = await stores.FindAsync(query.TenantId, query.ConnectionId, cancellationToken);
        if (store is null)
        {
            store = await CreateAsync(connection, cancellationToken);
        }

        return await ShopifyStoreSupport.ToDtoAsync(store, members, references, cancellationToken);
    }

    // Dos primeras lecturas a la vez chocan con PK_stores (concurrency.conflict): la perdedora lee la que creó la otra.
    private async Task<ShopifyStore> CreateAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        try
        {
            var created = ShopifyStoreSupport.NewStore(connection, clock.UtcNow);
            await ShopifyStoreSupport.AddNewAsync(stores, created, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return created;
        }
        catch (RequestConcurrencyException exception) when (ShopifyStoreSupport.IsConflict(exception))
        {
            unitOfWork.DiscardPendingChanges();
            if (await stores.FindAsync(connection.TenantId, connection.ConnectionId, cancellationToken) is { } winner)
            {
                return winner;
            }

            throw;
        }
    }
}

/// <summary>
/// §6.1 y §7 <c>PUT /stores/{connectionId}</c>. En orden: tenant, permiso y módulo (403); la forma (422 <c>validation.failed</c>);
/// la conexión del tenant (403); la versión (412 <c>concurrency.conflict</c>, como el resto del repo); el asesor activo
/// (<c>shopify.store.advisor_not_member</c>), la cuenta de cobro en la empresa con esa moneda
/// (<c>shopify.store.billing_account_not_found</c>) y la clasificación del tenant (<c>shopify.store.classification_not_found</c>).
/// Si la tienda todavía no existe, este <c>PUT</c> es el primer contacto y la crea (§6.1) con el dominio de la conexión y la
/// ventana de la conciliación en el enlace; una tienda nueva vale versión 1, la misma que daría el <c>GET</c>. Guardar
/// sube la versión (<see cref="ShopifyStore.Configure"/>), que es además el token de concurrencia de la fila, y audita
/// <c>shopify.store.updated</c> en la misma transacción.
/// </summary>
public sealed class UpdateShopifyStoreHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyStoreRepository stores,
    IShopifyMemberDirectory members,
    IShopifyBillingAccounts billingAccounts,
    IShopifyStoreReferences references,
    IMembershipDirectory memberships,
    IShopifyUnitOfWork unitOfWork,
    IShopifyAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateShopifyStoreCommand> validator) : ICommandHandler<UpdateShopifyStoreCommand, ShopifyStoreDto>
{
    public async Task<ShopifyStoreDto> HandleAsync(UpdateShopifyStoreCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.ConnectionManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var connection = await ShopifyStoreSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);

        var store = await stores.FindAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var isNew = store is null;
        store ??= ShopifyStoreSupport.NewStore(connection, clock.UtcNow);
        if (store.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The Shopify store changed after it was loaded.");
        }

        var billing = await ValidatedReferencesAsync(command, cancellationToken);
        var updatedBy = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw ShopifyAuthorization.Denied();

        var before = ComparedFields(store);
        store.Configure(
            command.AdvisorMembershipId,
            billing,
            command.DefaultClassificationId,
            command.DocumentAttributeKey!,
            command.DocumentTypeAttributeKey!,
            ShopifyDocumentTypes.Normalize(command.DefaultIdentificationType)!,
            updatedBy,
            clock.UtcNow);
        if (isNew)
        {
            await ShopifyStoreSupport.AddNewAsync(stores, store, cancellationToken);
        }

        // En la misma unidad de trabajo que la tienda; si el guardado pierde con 412, no queda auditoría.
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            ShopifyAuditActions.StoreUpdated,
            ShopifyAuditActions.ConnectionResourceType,
            store.ConnectionId.ToString(),
            ChangedFieldNamesJson(before, ComparedFields(store)),
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ShopifyStoreSupport.ToDtoAsync(store, members, references, cancellationToken);
    }

    // Lo que el formulario edita, con los nombres de los campos del cuerpo. Los valores sólo sirven para comparar el antes y el
    // después: a la auditoría llegan los nombres de los que cambiaron, nunca los valores (el número de cuenta no se audita).
    private static Dictionary<string, string?> ComparedFields(ShopifyStore store) => new(StringComparer.Ordinal)
    {
        ["advisorMembershipId"] = store.AdvisorMembershipId?.ToString(),
        ["billingCompanyId"] = store.Billing?.CompanyId.ToString(),
        ["billingBankName"] = store.Billing?.BankName,
        ["billingAccountNumber"] = store.Billing?.AccountNumber,
        ["billingCurrency"] = store.Billing?.Currency,
        ["defaultClassificationId"] = store.DefaultClassificationId?.ToString(),
        ["documentAttributeKey"] = store.DocumentAttributeKey,
        ["documentTypeAttributeKey"] = store.DocumentTypeAttributeKey,
        ["defaultIdentificationType"] = store.DefaultIdentificationType,
    };

    /// <summary>El detalle de <c>shopify.store.updated</c>, con la misma forma que <c>tenancy.settings.updated</c>: un arreglo JSON
    /// con los <b>nombres</b> de los campos que cambiaron, en el orden del formulario, sin ningún valor. Un <c>PUT</c> que no
    /// cambia nada audita <c>[]</c>.</summary>
    private static string ChangedFieldNamesJson(Dictionary<string, string?> before, Dictionary<string, string?> after) =>
        JsonSerializer.Serialize(
            after
                .Where(pair => !string.Equals(before.GetValueOrDefault(pair.Key), pair.Value, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray());

    // La cuenta se guarda como la tiene Companies (banco y número recortados como allá): es la misma comparación con la que el
    // importador la vuelve a buscar en cada pedido (billing_account_missing). La moneda tiene que ser la de la cuenta: una tienda
    // con otra moneda dejaría todo pedido en currency_mismatch contra una cuenta que no la tiene.
    private async Task<ShopifyBillingAccount> ValidatedReferencesAsync(UpdateShopifyStoreCommand command, CancellationToken cancellationToken)
    {
        if (!await members.IsActiveMemberAsync(command.TenantId, command.AdvisorMembershipId, cancellationToken))
        {
            throw new ShopifyDomainException(
                ShopifyErrorCodes.StoreAdvisorNotMember, "The store advisor is not an active membership of the tenant.");
        }

        var account = await billingAccounts.FindAsync(
            command.TenantId, command.BillingCompanyId, command.BillingBankName!, command.BillingAccountNumber!, cancellationToken);
        if (account is null
            || !string.Equals(account.Currency.Trim(), command.BillingCurrency!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ShopifyDomainException(
                ShopifyErrorCodes.StoreBillingAccountNotFound, "The billing account does not exist in that company with that currency.");
        }

        if (command.DefaultClassificationId is { } classificationId
            && await references.ClassificationNameAsync(command.TenantId, classificationId, cancellationToken) is null)
        {
            throw new ShopifyDomainException(
                ShopifyErrorCodes.StoreClassificationNotFound, "The default classification does not belong to the tenant.");
        }

        return new ShopifyBillingAccount(account.CompanyId, account.BankName, account.AccountNumber, account.Currency);
    }
}

internal static class ShopifyStoreSupport
{
    private const string ConcurrencyConflict = "concurrency.conflict";

    public static bool IsConflict(RequestConcurrencyException exception) =>
        string.Equals(exception.Code, ConcurrencyConflict, StringComparison.Ordinal);

    /// <summary>La conexión de Shopify del tenant; una de otro tenant, inexistente o cuya ruta ya apunta a otra conexión da 403
    /// <c>authorization.denied</c>, como «Sincronizar ahora».</summary>
    public static async Task<ShopifyConnectionRef> ConnectionAsync(
        IShopifyConnectionDirectory connections, Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await connections.FindAsync(tenantId, connectionId, cancellationToken);
        return connection is not null && connection.TenantId == tenantId && connection.ConnectionId == connectionId
            ? connection
            : throw ShopifyAuthorization.Denied();
    }

    /// <summary>§6.1: la tienda vacía del primer contacto, con el dominio de la conexión y la ventana de la conciliación en el
    /// enlace (D-S4), no en el primer contacto. <paramref name="now"/> es el respaldo si la conexión no trae la fecha del enlace.
    /// No la agrega: eso es <see cref="AddNewAsync"/>.</summary>
    public static ShopifyStore NewStore(ShopifyConnectionRef connection, DateTimeOffset now)
    {
        var store = ShopifyStore.Create(connection.ConnectionId, connection.TenantId, connection.LinkedAt ?? now);
        if (!string.IsNullOrWhiteSpace(connection.ShopDomain))
        {
            store.RecordShopDomain(connection.ShopDomain);
        }

        return store;
    }

    /// <summary>
    /// El único lugar donde nace una tienda (el <c>GET</c> y el <c>PUT</c> de la tienda, la ingesta y la conciliación): la agrega
    /// y le pasa las filas <c>Waiting</c> que dejó otra conexión del mismo tenant y dominio
    /// (<see cref="IShopifyStoreRepository.AdoptWaitingOrdersAsync"/>). Sin eso, borrar la conexión y volver a enlazar la tienda
    /// (D-S23) dejaba esas filas para siempre: el reintento resuelve la conexión nueva y daba <c>not_operational</c>, y la
    /// conciliación sólo reintenta las de su conexión. La reasignación escribe en el acto; si el guardado de la tienda pierde, la
    /// repite quien la cree y no cambia nada.
    /// </summary>
    public static async Task AddNewAsync(IShopifyStoreRepository stores, ShopifyStore store, CancellationToken cancellationToken)
    {
        stores.Add(store);
        if (store.ShopDomain is { } shopDomain)
        {
            await stores.AdoptWaitingOrdersAsync(store.TenantId, store.ConnectionId, shopDomain, cancellationToken);
        }
    }

    public static async Task<ShopifyStoreDto> ToDtoAsync(
        ShopifyStore store, IShopifyMemberDirectory members, IShopifyStoreReferences references, CancellationToken cancellationToken)
    {
        var advisorName = store.AdvisorMembershipId is { } advisorId
            ? await members.NameAsync(store.TenantId, advisorId, cancellationToken)
            : null;
        var companyName = store.Billing is { } billing
            ? await references.CompanyNameAsync(store.TenantId, billing.CompanyId, cancellationToken)
            : null;
        var classificationName = store.DefaultClassificationId is { } classificationId
            ? await references.ClassificationNameAsync(store.TenantId, classificationId, cancellationToken)
            : null;
        return new ShopifyStoreDto(
            store.ConnectionId,
            store.ShopDomain,
            store.IsConfigured,
            store.AdvisorMembershipId,
            advisorName,
            store.Billing?.CompanyId,
            companyName,
            store.Billing?.BankName,
            store.Billing?.AccountNumber,
            store.Billing?.Currency,
            store.DefaultClassificationId,
            classificationName,
            store.DocumentAttributeKey,
            store.DocumentTypeAttributeKey,
            store.DefaultIdentificationType,
            store.LastReconciledAt,
            store.UpdatedAt,
            store.Version);
    }
}
