using Modules.Pos.Domain;

namespace Modules.Pos.UnitTests;

/// <summary>Datos del ejemplo trabajado del spec, compartidos por las pruebas del módulo.</summary>
internal static class PosFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 15, 20, 3, TimeSpan.Zero);

    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    public static readonly MemberId Cashier = new(Guid.Parse("01920000-0000-7000-8000-000000000010"));

    public static readonly PosCompanySnapshot Company = new(
        Guid.Parse("01910000-0000-7000-8000-000000000001"),
        "Origen Botánico SAS",
        "900123456-1",
        "Cra 50 # 10-20, Rionegro",
        "6045551234");

    public static CashSession OpenSession(decimal openingFloat = 100_000m, MemberId? cashier = null) =>
        CashSession.Open(
            CashSessionId.New(),
            TenantId,
            cashier ?? Cashier,
            "Laura Gómez",
            Company,
            openingFloat,
            Now);

    public static readonly Guid Shampoo = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    public static readonly Guid Avena = Guid.Parse("01900000-0000-7000-8000-0000000000a2");
    public static readonly Guid Jabon = Guid.Parse("01900000-0000-7000-8000-0000000000a3");

    public static readonly string Fingerprint = new('a', 64);

    public static PosSaleLineInput[] WorkedExampleLines() =>
    [
        new(Shampoo, "SH-400", "Shampoo 400 ml", 2m, 11_900m, 10m, 19),
        new(Avena, "AV-01", "Avena granel (kg)", 1.5m, 5_000m, 0m, 0),
        new(Jabon, "JB-03", "Jabón", 3m, 2_990m, 0m, 5),
    ];

    public static PosPaymentInput Cash(decimal tendered) => new(PosPaymentMethod.Cash, null, tendered, null);

    public static PosPaymentInput Card(decimal amount, string? reference = null) =>
        new(PosPaymentMethod.Card, amount, null, reference);

    public static PosPaymentInput Transfer(decimal amount) => new(PosPaymentMethod.Transfer, amount, null, null);

    /// <summary>La venta del ejemplo: tarjeta 20 000 (ref 1234) + efectivo, recibido 20 000.</summary>
    public static PosSale Sale(
        CashSession session,
        PosSaleLineInput[]? lines = null,
        PosPaymentInput[]? payments = null,
        PosSaleId? id = null) =>
        PosSale.Create(
            id ?? PosSaleId.New(),
            Fingerprint,
            session,
            lines ?? WorkedExampleLines(),
            payments ?? [Card(20_000m, "1234"), Cash(20_000m)],
            Now);
}
