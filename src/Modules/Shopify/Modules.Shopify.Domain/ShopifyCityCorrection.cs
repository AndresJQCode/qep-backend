namespace Modules.Shopify.Domain;

/// <summary>
/// La parte de envío de un pedido de QEP tal como la guardó Quotations (spec 2026-10-26 D-C4): es la huella con que se decide si
/// alguien la editó después de que un sinónimo la escribiera. Vive en Domain (y no en los puertos) porque la guarda
/// <see cref="ShopifyCityCorrection"/>, y Domain no ve Application (ShopifyLayerTests).
/// </summary>
public sealed record ShopifyShippingFingerprint(
    string? Name, string? Phone, string? Email, string? Address, Guid DepartmentId, Guid CityId);

/// <summary>De dónde salió la huella (O8): la corrección en segundo plano de un sinónimo confirmado, o un pedido o cliente que casó
/// por el sinónimo al importarse.</summary>
public enum ShopifyCityCorrectionOrigin
{
    Correction,
    Import,
}

/// <summary>Qué arregló el sinónimo (desvío S5): pedido y cliente viven en módulos y unidades de trabajo distintos.</summary>
public enum ShopifyCityCorrectionTarget
{
    Order,
    Customer,
}

/// <summary>
/// Spec 2026-10-26 §5 (<c>shopify.city_corrections</c>): un pedido o un cliente que el sinónimo <see cref="SynonymId"/> arregló o
/// que casó por él al importarse, con lo que se escribió. Para un pedido, la parte de envío (<see cref="Party"/>); para un
/// cliente, su <see cref="CustomerVersion"/> después de completarlo (desvío S7). Corregir el sinónimo compara contra esto: si no
/// coincide, alguien lo editó y se respeta. Una fila por <c>(sinónimo, destino, id)</c>: el índice único lo cierra.
/// </summary>
public sealed class ShopifyCityCorrection
{
    // Los anchos de QuotationPartyDetails (la huella es la parte como la guardó Quotations); los amarra
    // ShopifyCityCorrectionParityTests, en Bootstrapper.UnitTests, que ve los dos módulos.
    public const int PartyNameMaxLength = 255;
    public const int PartyPhoneMaxLength = 32;
    public const int PartyEmailMaxLength = 254;
    public const int PartyAddressMaxLength = 255;

    private ShopifyCityCorrection()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Referencia blanda a <c>geography.city_synonyms</c>: un sinónimo borrado deja sus huellas para la auditoría (§4.7).</summary>
    public Guid SynonymId { get; private set; }

    public ShopifyCityCorrectionOrigin Origin { get; private set; }

    public ShopifyCityCorrectionTarget Target { get; private set; }

    /// <summary>El pedido de Quotations o el cliente de Customers, según <see cref="Target"/>.</summary>
    public Guid TargetId { get; private set; }

    /// <summary>La fila de <c>shopify.orders</c> de la que salió. La de un pedido siempre la trae; la de un cliente puede no
    /// traerla (<see cref="ForCustomer"/>).</summary>
    public Guid? MirrorOrderId { get; private set; }

    public Guid DepartmentId { get; private set; }

    /// <summary>La ciudad que se escribió.</summary>
    public Guid CityId { get; private set; }

    /// <summary>La ciudad que había antes de recorregir el sinónimo (§4.6); <c>null</c> mientras nadie lo recorrigió.</summary>
    public Guid? PreviousCityId { get; private set; }

    public string? PartyName { get; private set; }

    public string? PartyPhone { get; private set; }

    public string? PartyEmail { get; private set; }

    public string? PartyAddress { get; private set; }

    public long? CustomerVersion { get; private set; }

    public DateTimeOffset AppliedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>La huella de la parte de envío; <c>null</c> en la de un cliente.</summary>
    public ShopifyShippingFingerprint? Party =>
        Target == ShopifyCityCorrectionTarget.Order
            ? new ShopifyShippingFingerprint(PartyName, PartyPhone, PartyEmail, PartyAddress, DepartmentId, CityId)
            : null;

    public static ShopifyCityCorrection ForOrder(
        Guid tenantId,
        Guid synonymId,
        ShopifyCityCorrectionOrigin origin,
        Guid orderId,
        Guid mirrorOrderId,
        ShopifyShippingFingerprint party,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(mirrorOrderId, Guid.Empty);
        var correction = New(tenantId, synonymId, origin, ShopifyCityCorrectionTarget.Order, orderId, mirrorOrderId, now);
        correction.WriteParty(party);
        return correction;
    }

    public static ShopifyCityCorrection ForCustomer(
        Guid tenantId,
        Guid synonymId,
        ShopifyCityCorrectionOrigin origin,
        Guid customerId,
        Guid? mirrorOrderId,
        Guid departmentId,
        Guid cityId,
        long customerVersion,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(departmentId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(cityId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(customerVersion, 1L);
        var correction = New(tenantId, synonymId, origin, ShopifyCityCorrectionTarget.Customer, customerId, mirrorOrderId, now);
        correction.DepartmentId = departmentId;
        correction.CityId = cityId;
        correction.CustomerVersion = customerVersion;
        return correction;
    }

    /// <summary>§4.6: el sinónimo cambió de ciudad y la parte de envío pasó a <paramref name="party"/>. Conserva
    /// <see cref="Origin"/>: lo que casó al importarse sigue siendo de la importación.</summary>
    public void Recorrected(ShopifyShippingFingerprint party, DateTimeOffset now)
    {
        EnsureTarget(ShopifyCityCorrectionTarget.Order);
        PreviousCityId = CityId;
        WriteParty(party);
        UpdatedAt = now;
    }

    /// <summary>§4.6: el cliente pasó a <paramref name="cityId"/> y quedó en <paramref name="customerVersion"/>.</summary>
    public void Recorrected(Guid cityId, long customerVersion, DateTimeOffset now)
    {
        EnsureTarget(ShopifyCityCorrectionTarget.Customer);
        ArgumentOutOfRangeException.ThrowIfEqual(cityId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(customerVersion, 1L);
        PreviousCityId = CityId;
        CityId = cityId;
        CustomerVersion = customerVersion;
        UpdatedAt = now;
    }

    private static ShopifyCityCorrection New(
        Guid tenantId,
        Guid synonymId,
        ShopifyCityCorrectionOrigin origin,
        ShopifyCityCorrectionTarget target,
        Guid targetId,
        Guid? mirrorOrderId,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(synonymId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(targetId, Guid.Empty);
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown correction origin.");
        }

        return new ShopifyCityCorrection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SynonymId = synonymId,
            Origin = origin,
            Target = target,
            TargetId = targetId,
            MirrorOrderId = mirrorOrderId,
            AppliedAt = now,
            UpdatedAt = now,
        };
    }

    private void WriteParty(ShopifyShippingFingerprint party)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentOutOfRangeException.ThrowIfEqual(party.DepartmentId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(party.CityId, Guid.Empty);
        PartyName = party.Name;
        PartyPhone = party.Phone;
        PartyEmail = party.Email;
        PartyAddress = party.Address;
        DepartmentId = party.DepartmentId;
        CityId = party.CityId;
    }

    private void EnsureTarget(ShopifyCityCorrectionTarget expected)
    {
        if (Target != expected)
        {
            throw new InvalidOperationException($"This correction is for a {Target}, not a {expected}.");
        }
    }
}
