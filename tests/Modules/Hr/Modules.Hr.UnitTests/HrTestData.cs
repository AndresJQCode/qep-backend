using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

/// <summary>
/// Los valores legales viven <b>sólo</b> acá y en la migración <c>AddHrLegalParameters</c>: el código de producción los resuelve de
/// <c>hr.legal_parameters</c> a la fecha que evalúa, y estas pruebas arman su propia tabla con los valores sembrados.
/// </summary>
internal static class HrTestData
{
    public static IReadOnlyList<LegalParameterValue> LegalTable { get; } =
    [
        new(LegalParameterKey.Smmlv, new DateOnly(2024, 1, 1), 1_300_000m),
        new(LegalParameterKey.IntegralSalaryFloor, new DateOnly(2024, 1, 1), 16_900_000m),
        new(LegalParameterKey.Smmlv, new DateOnly(2025, 1, 1), 1_423_500m),
        new(LegalParameterKey.IntegralSalaryFloor, new DateOnly(2025, 1, 1), 18_505_500m),
        new(LegalParameterKey.Smmlv, new DateOnly(2026, 1, 1), 1_750_905m),
        new(LegalParameterKey.IntegralSalaryFloor, new DateOnly(2026, 1, 1), 22_761_765m),
    ];

    public static SalaryFloor Floor2026 { get; } = new(new DateOnly(2026, 11, 20), 1_750_905m, 22_761_765m);

    public static SalaryFloor Floor2025 { get; } = new(new DateOnly(2025, 6, 30), 1_423_500m, 18_505_500m);
}
