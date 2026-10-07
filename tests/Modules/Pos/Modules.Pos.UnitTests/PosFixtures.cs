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
}
