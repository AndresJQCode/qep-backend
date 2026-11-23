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
        new(LegalParameterKey.WeeklyHours, new DateOnly(2024, 7, 15), 46m),
        new(LegalParameterKey.WeeklyHours, new DateOnly(2025, 7, 15), 44m),
        new(LegalParameterKey.WeeklyHours, new DateOnly(2026, 7, 15), 42m),
    ];

    public static IReadOnlyList<LegalParameterBracket> BracketTable { get; } =
    [
        .. SolidarityYear(new DateOnly(2025, 1, 1)),
        .. SolidarityYear(new DateOnly(2026, 1, 1)),
    ];

    private static IEnumerable<LegalParameterBracket> SolidarityYear(DateOnly from)
    {
        yield return new(LegalBracketKey.SolidarityFund, from, 4m, 16m, 0.01m);
        yield return new(LegalBracketKey.SolidarityFund, from, 16m, 17m, 0.012m);
        yield return new(LegalBracketKey.SolidarityFund, from, 17m, 18m, 0.014m);
        yield return new(LegalBracketKey.SolidarityFund, from, 18m, 19m, 0.016m);
        yield return new(LegalBracketKey.SolidarityFund, from, 19m, 20m, 0.018m);
        yield return new(LegalBracketKey.SolidarityFund, from, 20m, 25m, 0.02m);
    }

    public static SalaryFloor Floor2026 { get; } = new(new DateOnly(2026, 11, 20), 1_750_905m, 22_761_765m);

    public static SalaryFloor Floor2025 { get; } = new(new DateOnly(2025, 6, 30), 1_423_500m, 18_505_500m);

    /// <summary>Hoy en el tenant de las pruebas.</summary>
    public static DateOnly Today { get; } = new(2026, 11, 20);

    public static DateTimeOffset Now { get; } = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    public static Guid TenantId { get; } = Guid.Parse("01900000-0000-7000-8000-000000000001");

    public static Guid CompanyId { get; } = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    public static Guid OtherCompanyId { get; } = Guid.Parse("01900000-0000-7000-8000-0000000000c2");

    // Cada prueba que archiva o edita arma su propia sede: estas son de sólo lectura.
    public static Site MainSite { get; } = Site.Create(
        new SiteId(Guid.Parse("01900000-0000-7000-8000-0000000000a1")), TenantId, CompanyId, "Principal", null, null, null, Now);

    public static Site OtherSite { get; } = Site.Create(
        new SiteId(Guid.Parse("01900000-0000-7000-8000-0000000000a2")), TenantId, CompanyId, "Norte", null, null, null, Now);

    public static Workplace Warehouse { get; } = Workplace.Create(
        new WorkplaceId(Guid.Parse("01900000-0000-7000-8000-0000000000b1")), TenantId, MainSite, "Bodega", null, ArlRiskClass.III, false, Now);

    public static Workplace OtherWarehouse { get; } = Workplace.Create(
        new WorkplaceId(Guid.Parse("01900000-0000-7000-8000-0000000000b2")), TenantId, OtherSite, "Bodega norte", null, ArlRiskClass.III, false, Now);

    public static PositionId AnalystId { get; } = new(Guid.Parse("01900000-0000-7000-8000-0000000000d1"));

    public static PositionId ManagerId { get; } = new(Guid.Parse("01900000-0000-7000-8000-0000000000d2"));

    public static AreaId LogisticsId { get; } = new(Guid.Parse("01900000-0000-7000-8000-0000000000e1"));

    public static EmployeeProfile Profile(string documentNumber = "1020304050") => new(
        "Ana",
        "Pérez",
        HrDocumentType.CC,
        documentNumber,
        "CO",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        ContributorSubtype.Standard,
        false,
        [DayOfWeek.Sunday]);

    public static ContractTerms Terms(
        ContractType type = ContractType.Employee,
        ContractDuration duration = ContractDuration.FixedTerm,
        SalaryType salaryType = SalaryType.Monthly,
        DateOnly? start = null,
        DateOnly? end = null,
        Guid? companyId = null) => new(
            companyId ?? CompanyId,
            type,
            duration,
            salaryType,
            start ?? new DateOnly(2026, 1, 15),
            duration == ContractDuration.Indefinite ? null : end ?? new DateOnly(2026, 7, 14));

    public static NewContract Contract(
        ContractTerms? terms = null,
        decimal amount = 2_500_000m,
        bool acknowledge = false,
        Workplace? workplace = null,
        PositionId? position = null) => new(
            EmploymentContractId.New(),
            terms ?? Terms(),
            null,
            SalaryRecordId.New(),
            amount,
            false,
            acknowledge,
            AssignmentRecordId.New(),
            position ?? AnalystId,
            null,
            (workplace ?? Warehouse).SiteId,
            (workplace ?? Warehouse).Id);

    public static Employee Hire(NewContract? contract = null, string documentNumber = "1020304050") => Employee.Hire(
        EmployeeId.New(), TenantId, Profile(documentNumber), null, contract ?? Contract(), Floor2026, Now);

    public static HrDomainException Rejects(Action action) => Assert.Throws<HrDomainException>(action);
}
