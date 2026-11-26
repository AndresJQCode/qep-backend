using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Audit.Domain;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Los topes de la parte de envío de Quotations (<c>QuotationPartyDetails.*MaxLength</c>), copiados porque Shopify no ve
/// Quotations (ShopifyLayerTests). Los amarra <c>ShopifyPartyLimitsParityTests</c> (Bootstrapper.UnitTests): un ancho del dominio
/// que cambie solo saldría como 422 de dominio sin el mapa <c>errors</c>.</summary>
public static class ShopifyPartyLimits
{
    public const int NameMaxLength = 255;

    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 255;
}

/// <summary>Spec 2026-11-14 §5: los topes del resto de la corrección (contrato con el formulario del detalle del pedido).</summary>
public static class ShopifyOrderCorrectionLimits
{
    public const int BuyerNotesMaxLength = 2000;

    public const int MaxAttributes = 50;

    public const int AttributeKeyMaxLength = 255;

    public const int AttributeValueMaxLength = 1000;
}

/// <summary>Un atributo de Shopify como llega del formulario; <c>null</c> en el valor es <c>""</c>.</summary>
public sealed record ShopifyAttributeInput(string? Key, string? Value);

/// <summary>La parte de envío como llega del formulario. Sin país: sólo se corrige en Colombia (plan D21).</summary>
public sealed record ShopifyPartyInput(string? Name, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? CityId);

/// <summary>
/// Spec 2026-11-14 §5 <c>PUT …/shopify/orders/{id}/corrections</c> (<c>quotations.order.manage</c>, plan D11). El estado completo del
/// formulario: <see cref="BuyerNotes"/> y <see cref="Attributes"/> van siempre; <see cref="ShippingParty"/> <c>null</c> no toca la
/// parte (obligatorio con recogida). <see cref="ExpectedOrderVersion"/> es el <c>If-Match</c> del pedido de QEP.
/// </summary>
public sealed record CorrectShopifyOrderCommand(
    Guid TenantId,
    Guid MirrorOrderId,
    long ExpectedOrderVersion,
    ShopifyPartyInput? ShippingParty,
    string? BuyerNotes,
    IReadOnlyList<ShopifyAttributeInput>? Attributes) : ICommand<ShopifyOrderSyncDto>;

/// <summary>Spec 2026-11-14 §6 <c>GET …/shopify/qep-orders/{qepOrderId}</c> (<c>quotations.order.read</c>, plan D11): el panel de
/// Shopify del detalle del pedido de QEP, que no conoce la fila del espejo.</summary>
public sealed record GetShopifyOrderSyncQuery(Guid TenantId, Guid QepOrderId) : IQuery<ShopifyOrderSyncDto>;

/// <summary>
/// Las claves de <c>errors</c> son contrato con el formulario (camelCase, índice base 0): <c>shippingParty.name</c>,
/// <c>shippingParty.phone</c>, <c>shippingParty.email</c>, <c>shippingParty.address</c>, <c>shippingParty.departmentId</c>,
/// <c>shippingParty.cityId</c>, <c>buyerNotes</c>, <c>attributes</c> (ausente, más de 50 o con una clave repetida tras recortar),
/// <c>attributes[i].key</c> y <c>attributes[i].value</c>. Los textos se miden recortados. Con la forma válida, el handler agrega
/// con el mismo 422 <c>validation.failed</c> (owner 2026-11-15): <c>shippingParty.departmentId</c> si el departamento no existe y
/// <c>shippingParty.cityId</c> si la ciudad no existe o es de otro departamento.
/// </summary>
public sealed class CorrectShopifyOrderValidator : AbstractValidator<CorrectShopifyOrderCommand>
{
    public CorrectShopifyOrderValidator()
    {
        When(command => command.ShippingParty is not null, () =>
        {
            RuleFor(command => command.ShippingParty!.Name)
                .Must(value => Within(value, ShopifyPartyLimits.NameMaxLength))
                .WithMessage($"The name is required and cannot exceed {ShopifyPartyLimits.NameMaxLength} characters.")
                .OverridePropertyName("shippingParty.name");
            RuleFor(command => command.ShippingParty!.Phone)
                .Must(value => Within(value, ShopifyPartyLimits.PhoneMaxLength))
                .WithMessage($"The phone is required and cannot exceed {ShopifyPartyLimits.PhoneMaxLength} characters.")
                .OverridePropertyName("shippingParty.phone");
            RuleFor(command => command.ShippingParty!.Email)
                .Must(value => Within(value, ShopifyPartyLimits.EmailMaxLength) && IsEmail(value!.Trim()))
                .WithMessage($"The email is required, must be an email address and cannot exceed {ShopifyPartyLimits.EmailMaxLength} characters.")
                .OverridePropertyName("shippingParty.email");
            RuleFor(command => command.ShippingParty!.Address)
                .Must(value => Within(value, ShopifyPartyLimits.AddressMaxLength))
                .WithMessage($"The address is required and cannot exceed {ShopifyPartyLimits.AddressMaxLength} characters.")
                .OverridePropertyName("shippingParty.address");
            RuleFor(command => command.ShippingParty!.DepartmentId)
                .Must(value => value is { } id && id != Guid.Empty)
                .WithMessage("The department is required.")
                .OverridePropertyName("shippingParty.departmentId");
            RuleFor(command => command.ShippingParty!.CityId)
                .Must(value => value is { } id && id != Guid.Empty)
                .WithMessage("The city is required.")
                .OverridePropertyName("shippingParty.cityId");
        });

        RuleFor(command => command.BuyerNotes)
            .Must(value => (value?.Trim().Length ?? 0) <= ShopifyOrderCorrectionLimits.BuyerNotesMaxLength)
            .WithMessage($"The buyer notes cannot exceed {ShopifyOrderCorrectionLimits.BuyerNotesMaxLength} characters.")
            .OverridePropertyName("buyerNotes");

        RuleFor(command => command.Attributes)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .WithMessage("The attributes are required (send an empty list to clear them).")
            .Must(attributes => attributes!.Count <= ShopifyOrderCorrectionLimits.MaxAttributes)
            .WithMessage($"There cannot be more than {ShopifyOrderCorrectionLimits.MaxAttributes} attributes.")
            .Must(attributes => attributes!
                .Select(attribute => attribute?.Key?.Trim())
                .Where(key => !string.IsNullOrEmpty(key))
                .GroupBy(key => key, StringComparer.Ordinal)
                .All(group => group.Count() == 1))
            .WithMessage("Two attributes cannot have the same key.")
            .OverridePropertyName("attributes");
        // ChildRules salta los elementos null: sin esta regla, un [null] llega al handler y es un 500 (review Minor 4).
        RuleForEach(command => command.Attributes)
            .NotNull()
            .WithMessage("An attribute cannot be null.")
            .When(command => command.Attributes is not null)
            .OverridePropertyName("attributes");
        RuleForEach(command => command.Attributes)
            .ChildRules(attribute =>
            {
                attribute.RuleFor(item => item.Key)
                    .Must(key => Within(key, ShopifyOrderCorrectionLimits.AttributeKeyMaxLength))
                    .WithMessage($"The key is required and cannot exceed {ShopifyOrderCorrectionLimits.AttributeKeyMaxLength} characters.")
                    .OverridePropertyName("key");
                attribute.RuleFor(item => item.Value)
                    .Must(value => (value ?? string.Empty).Length <= ShopifyOrderCorrectionLimits.AttributeValueMaxLength)
                    .WithMessage($"The value cannot exceed {ShopifyOrderCorrectionLimits.AttributeValueMaxLength} characters.")
                    .OverridePropertyName("value");
            })
            .When(command => command.Attributes is not null)
            .OverridePropertyName("attributes");
    }

    private static bool Within(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= maxLength;
    }

    // La misma regla que el EmailAddress() de FluentValidation (modo AspNetCoreCompatible): una sola arroba, ni al principio ni
    // al final.
    private static bool IsEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at != value.Length - 1 && at == value.LastIndexOf('@');
    }
}

/// <summary>Los atributos de Shopify de una fila: los de QEP si el asesor los corrigió (<c>qep_attributes</c>); si no, los de la
/// proyección (lo aplicado) o, en una fila importada antes de la sincronización (plan D6), los del último snapshot aplicado.</summary>
public static class ShopifyOrderAttributes
{
    // Las tildes tal cual, como la proyección y los grupos de un cambio.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static IReadOnlyDictionary<string, string> CurrentOf(ShopifyOrderMirror row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.QepAttributes is { } edited)
        {
            return ShopifyEditPlanner.ReadAttributes(edited);
        }

        return row.AppliedProjection is { } projection
            ? ShopifyOrderProjection.Read(projection).Attributes
            : ShopifyOrderIngestion.ReadPayload(row.AppliedPayload ?? row.Payload).Attributes;
    }

    /// <summary>Las claves recortadas y el valor <c>null</c> como <c>""</c>; el orden es el del formulario.</summary>
    public static IReadOnlyDictionary<string, string> From(IReadOnlyList<ShopifyAttributeInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            attributes[input.Key!.Trim()] = input.Value ?? string.Empty;
        }

        return attributes;
    }

    public static string Write(IReadOnlyDictionary<string, string> attributes) => JsonSerializer.Serialize(attributes, Json);
}

/// <summary>
/// Arma <see cref="ShopifyOrderSyncDto"/> de la fila del espejo y del pedido de QEP: el motivo por el que no se edita (plan D20), si
/// la dirección se puede corregir (D21), los atributos vigentes, el último envío y el cambio abierto.
/// </summary>
public sealed class ShopifyOrderSyncReader(
    IShopifyOrderPushRepository pushes, IShopifyOrderEditRepository edits, ShopifyOrderChangeMapper changes)
{
    private const string Colombia = "CO";

    public async Task<ShopifyOrderSyncDto> ReadAsync(ShopifyOrderMirror row, ShopifyOrderState order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(order);
        var blocked = BlockedReason(row, order);
        var push = await pushes.FindLatestAsync(row.TenantId, row.Id, cancellationToken);
        var open = await edits.FindOpenAsync(row.TenantId, row.Id, cancellationToken);
        return new ShopifyOrderSyncDto(
            row.Id,
            row.ConnectionId,
            row.OrderName,
            order.Version,
            blocked is null,
            blocked,
            !order.IsStorePickup && IsColombian(row, order),
            [.. ShopifyOrderAttributes.CurrentOf(row).Select(pair => new ShopifyAttributeDto(pair.Key, pair.Value))],
            row.QepAttributes is not null,
            push is null
                ? null
                : new ShopifyOrderPushDto(
                    push.Status.ToString(),
                    [.. push.Groups.Select(group => group.ToString())],
                    push.FailureCode,
                    push.Attempts,
                    push.Attempts > 0 ? push.UpdatedAt : null,
                    push.SentAt),
            open is null ? null : await changes.MapAsync(open, cancellationToken));
    }

    /// <summary>Plan D21: la dirección del pedido es de Colombia. Con parte, su país (<c>null</c> es Colombia); sin ella, el
    /// <c>countryCode</c> del último snapshot (vacío cuenta como Colombia).</summary>
    public static bool IsColombian(ShopifyOrderMirror row, ShopifyOrderState order)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(order);
        var country = order.ShippingParty is not null
            ? order.ShippingCountry
            : ShopifyOrderIngestion.ReadPayload(row.Payload).ShippingAddress?.CountryCode;
        return string.IsNullOrWhiteSpace(country) || string.Equals(country.Trim(), Colombia, StringComparison.OrdinalIgnoreCase);
    }

    // Plan D20, en orden de precedencia. Un pedido no editable que no cae en ninguno (no debería pasar: son los estados de
    // Order.IsEditable) se muestra como no importado para no dejar editar.
    private static string? BlockedReason(ShopifyOrderMirror row, ShopifyOrderState order)
    {
        if (row.ImportState != ShopifyImportState.Imported || row.QepOrderId is null)
        {
            return "NotImported";
        }

        if (order.IsEditable)
        {
            return null;
        }

        return order.Status switch
        {
            "Cancelled" => "Cancelled",
            "Archived" => "Archived",
            _ when order.IsInvoiced => "Invoiced",
            _ when order.IsPrepared => "Prepared",
            _ => "NotImported",
        };
    }
}

/// <summary>
/// Spec 2026-11-14 §5 y plan D3, D11, D21, D22: el asesor corrige la parte de envío, la nota del cliente y los atributos de un pedido
/// de Shopify, y lo que cambió viaja a Shopify en segundo plano.
/// <list type="number">
/// <item>Permiso de gestión de pedidos y membresía activa (firma el historial; sin ella, 403).</item>
/// <item>La fila tiene que estar <c>Imported</c> con pedido (422 <c>shopify.order.not_imported</c>); el pedido, en la versión del
/// <c>If-Match</c> (412) y editable (422 <c>order.order.not_editable</c>). Con parte: sin recogida (422 <c>order.order.store_pickup</c>)
/// y en Colombia (422 <c>shopify.order.address_not_supported</c>), con el departamento existente y la ciudad existente y de ese
/// departamento (422 <c>validation.failed</c> en <c>shippingParty.departmentId</c> o <c>shippingParty.cityId</c>, owner 2026-11-15).
/// Todo antes de abrir la transacción.</item>
/// <item>Se decide qué grupos cambiaron: la parte contra la del pedido (<see cref="ShopifyOrderProjection.SameParty"/> y el correo),
/// la nota recortada (vacía es <c>null</c>) contra la del pedido, los atributos contra los vigentes
/// (<see cref="ShopifyOrderAttributes.CurrentOf"/>). Sin cambios no se escribe nada.</item>
/// <item>En una transacción de Shopify: el pedido (Quotations enrolado, <see cref="IShopifyOrderCorrectionWriter"/>), los atributos
/// de la fila, el envío encolado y la auditoría <c>shopify.order.corrected</c>. Lo que falle deshace todo.</item>
/// </list>
/// La proyección <b>no</b> se mueve (plan D3): el pedido queda distinto de ella, y una edición posterior de Shopify sobre ese grupo es
/// conflicto, también después del eco (Review Focus 5).
/// </summary>
public sealed class CorrectShopifyOrderHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory memberships,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyOrderWriter orders,
    IShopifyOrderCorrectionWriter corrections,
    ShopifyOrderPushes pushes,
    IShopifyAuditRecorder auditRecorder,
    IShopifyUnitOfWork unitOfWork,
    IClock clock,
    ShopifyOrderSyncReader reader,
    IShopifyConnectionDirectory connections,
    IShopifyGeographyLookup geography,
    IValidator<CorrectShopifyOrderCommand> validator) : ICommandHandler<CorrectShopifyOrderCommand, ShopifyOrderSyncDto>
{
    /// <summary>Owner 2026-11-14: los valores de <see cref="ShopifyOrderSyncDto.ShopifySync"/>.</summary>
    public const string SyncQueued = "Queued";

    public const string SyncHeld = "Held";

    public const string SyncNothingToSend = "NothingToSend";

    public const string OrderNotEditableCode = "order.order.not_editable";

    public const string StorePickupCode = "order.order.store_pickup";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ShopifyOrderSyncDto> HandleAsync(CorrectShopifyOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var memberId = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw ShopifyAuthorization.Denied();
        var row = await ShopifyOrderSupport.FindAsync(mirrors, command.TenantId, command.MirrorOrderId, cancellationToken);
        if (row.ImportState != ShopifyImportState.Imported || row.QepOrderId is not { } orderId)
        {
            throw new ShopifyDomainException(ShopifyErrorCodes.OrderNotImported, "Only an imported Shopify order can be corrected.");
        }

        var order = await orders.ReadStateAsync(command.TenantId, orderId, cancellationToken)
            ?? throw new ResourceNotFoundException(ShopifyErrorCodes.OrderNotFound, "The order of this Shopify order no longer exists.");
        if (order.Version != command.ExpectedOrderVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The order changed since it was loaded.");
        }

        if (!order.IsEditable)
        {
            throw new ShopifyDomainException(OrderNotEditableCode, "The order can no longer be edited.");
        }

        if (command.ShippingParty is not null)
        {
            if (order.IsStorePickup)
            {
                throw new ShopifyDomainException(StorePickupCode, "An order picked up at the store has no shipping party.");
            }

            if (!ShopifyOrderSyncReader.IsColombian(row, order))
            {
                throw new ShopifyDomainException(
                    ShopifyErrorCodes.OrderAddressNotSupported, "Only a shipping address in Colombia can be corrected.");
            }

            await EnsureCityInDepartmentAsync(command.ShippingParty, cancellationToken);
        }

        var party = command.ShippingParty is { } input && PartyChanged(input, order.ShippingParty)
            ? new ShopifyPartyDraft(
                input.Name!.Trim(), input.Phone!.Trim(), input.Email!.Trim(), input.Address!.Trim(), input.DepartmentId, input.CityId)
            : null;
        var notes = Blank(command.BuyerNotes);
        var notesChanged = !string.Equals(notes, order.BuyerNotes, StringComparison.Ordinal);
        var attributes = ShopifyOrderAttributes.From(command.Attributes!);
        var attributesChanged = !ShopifyEditPlanner.SameAttributes(attributes, ShopifyOrderAttributes.CurrentOf(row));
        var groups = new List<ShopifyPushGroup>();
        if (party is not null)
        {
            groups.Add(ShopifyPushGroup.ShippingAddress);
        }

        if (notesChanged)
        {
            groups.Add(ShopifyPushGroup.Note);
        }

        if (attributesChanged)
        {
            groups.Add(ShopifyPushGroup.Attributes);
        }

        if (groups.Count == 0)
        {
            return await reader.ReadAsync(row, order, cancellationToken) with { ShopifySync = SyncNothingToSend };
        }

        // Owner 2026-11-14: con la conexión no operativa (la misma regla que el worker, connection_missing) la corrección se guarda
        // igual y el envío queda retenido: la marca de que este pedido hay que sincronizarlo cuando se reactive la conexión.
        var holdReason = await connections.FindAsync(command.TenantId, row.ConnectionId, cancellationToken) is { IsOperational: true }
            ? null
            : ShopifyPushFailureCodes.ConnectionInactive;
        var now = clock.UtcNow;
        ShopifyOrderPush push;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            if (party is not null || notesChanged)
            {
                order = await corrections.CorrectAsync(
                    new ShopifyOrderCorrection(
                        command.TenantId, orderId, order.Version, party, notesChanged, notes, memberId, executionContext.SubjectId),
                    cancellationToken);
            }

            if (attributesChanged)
            {
                row.SetQepAttributes(ShopifyOrderAttributes.Write(attributes));
            }

            push = await pushes.EnqueueAsync(row, groups, now, cancellationToken, holdReason);
            auditRecorder.Record(
                command.TenantId,
                executionContext.SubjectId,
                AuditActorType.Human,
                ShopifyAuditActions.OrderCorrected,
                ShopifyAuditActions.ShopifyOrderResourceType,
                row.Id.ToString(),
                JsonSerializer.Serialize(new { groups = groups.Select(group => group.ToString()) }, Json),
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await reader.ReadAsync(row, order, cancellationToken) with
        {
            ShopifySync = push.Status == ShopifyOrderPushStatus.Held ? SyncHeld : SyncQueued,
        };
    }

    // Owner 2026-11-15: una ciudad inexistente dejaba el envío reintentando ~8 h, y una de otro departamento mandaba a Shopify una
    // dirección incoherente. Es el mismo 422 validation.failed del validador, con la clave del campo que hay que corregir.
    private async Task EnsureCityInDepartmentAsync(ShopifyPartyInput party, CancellationToken cancellationToken)
    {
        var failure = await geography.CheckCityAsync(party.DepartmentId!.Value, party.CityId!.Value, cancellationToken) switch
        {
            ShopifyCityCheck.DepartmentNotFound => new ValidationFailure("shippingParty.departmentId", "The department does not exist."),
            ShopifyCityCheck.CityNotInDepartment => new ValidationFailure(
                "shippingParty.cityId", "The city does not exist or does not belong to the department."),
            _ => null,
        };
        if (failure is not null)
        {
            throw new ValidationException([failure]);
        }
    }

    // El correo no cuenta en SameParty (Shopify no lo trae en la dirección), pero el asesor sí lo corrige en la parte de QEP. El
    // formulario no tiene código postal: Quotations conserva el del pedido mientras no cambie el departamento (y si cambia, la
    // parte ya cuenta como cambiada), así que no es un cambio por sí solo.
    private static bool PartyChanged(ShopifyPartyInput input, ShopifyShippingFingerprint? current)
    {
        var candidate = new ShopifyShippingFingerprint(
            input.Name?.Trim(), input.Phone?.Trim(), input.Email?.Trim(), input.Address?.Trim(), input.DepartmentId!.Value, input.CityId!.Value,
            current?.PostalCode);
        return !ShopifyOrderProjection.SameParty(candidate, current)
            || !string.Equals(candidate.Email, current?.Email?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Spec 2026-11-14 §6: el panel de Shopify del pedido de QEP. Un pedido que no vino de Shopify (o de otro tenant) es 404
/// <c>shopify.order.not_found</c>.</summary>
public sealed class GetShopifyOrderSyncHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyOrderWriter orders,
    ShopifyOrderSyncReader reader) : IQueryHandler<GetShopifyOrderSyncQuery, ShopifyOrderSyncDto>
{
    public async Task<ShopifyOrderSyncDto> HandleAsync(GetShopifyOrderSyncQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.OrderRead, cancellationToken);
        var row = await mirrors.FindByQepOrderIdAsync(query.TenantId, query.QepOrderId, cancellationToken)
            ?? throw NotFound();
        var order = await orders.ReadStateAsync(query.TenantId, query.QepOrderId, cancellationToken) ?? throw NotFound();
        return await reader.ReadAsync(row, order, cancellationToken);
    }

    private static ResourceNotFoundException NotFound() =>
        new(ShopifyErrorCodes.OrderNotFound, "The order did not come from Shopify in this tenant.");
}
