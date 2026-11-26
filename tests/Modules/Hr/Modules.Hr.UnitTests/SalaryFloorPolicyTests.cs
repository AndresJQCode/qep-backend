using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class SalaryFloorPolicyTests
{
    private static readonly SalaryFloor Floor = HrTestData.Floor2026;

    private static HrDomainException Rejected(
        ContractType type, SalaryType salaryType, decimal amount, bool acknowledged = false) =>
        Assert.Throws<HrDomainException>(() => SalaryFloorPolicy.Check(type, salaryType, amount, Floor, acknowledged));

    [Fact]
    public void AnEmployeeBelowTheMinimumNeedsAcknowledgement() =>
        Assert.Equal(
            HrErrorCodes.BelowMinimumUnacknowledged,
            Rejected(ContractType.Employee, SalaryType.Monthly, 1_750_904m).Code);

    [Fact]
    public void AcknowledgedBelowTheMinimumIsKeptAndFlagged() =>
        Assert.True(SalaryFloorPolicy.Check(ContractType.Employee, SalaryType.Monthly, 1_750_904m, Floor, acknowledged: true));

    [Fact]
    public void TheMinimumItselfNeedsNothing() =>
        Assert.False(SalaryFloorPolicy.Check(ContractType.Employee, SalaryType.Monthly, 1_750_905m, Floor, acknowledged: false));

    [Theory]
    [InlineData(ContractType.Contractor)]
    [InlineData(ContractType.PartTime)]
    [InlineData(ContractType.SenaApprenticeTeaching)]
    [InlineData(ContractType.SenaApprenticeProductive)]
    [InlineData(ContractType.SenaApprenticeDual)]
    [InlineData(ContractType.StudentLaw789)]
    [InlineData(ContractType.StudentDecree055)]
    public void ExemptTypesGoBelowTheMinimumSilently(ContractType type) =>
        Assert.False(SalaryFloorPolicy.Check(type, SalaryType.Monthly, 1_000_000m, Floor, acknowledged: false));

    [Fact]
    public void AUniversityInternIsNotExempt() =>
        Assert.Equal(
            HrErrorCodes.BelowMinimumUnacknowledged,
            Rejected(ContractType.UniversityIntern, SalaryType.Monthly, 1_000_000m).Code);

    [Fact]
    public void AnIntegralSalaryBelowTheFloorIsRejected() =>
        Assert.Equal(
            HrErrorCodes.BelowIntegralFloor,
            Rejected(ContractType.Employee, SalaryType.Integral, 22_761_764m, acknowledged: true).Code);

    [Fact]
    public void TheIntegralFloorItselfIsAccepted() =>
        Assert.False(SalaryFloorPolicy.Check(ContractType.Employee, SalaryType.Integral, 22_761_765m, Floor, acknowledged: false));

    [Fact]
    public void TheFloorIsTheOneOfTheRecordDate() =>
        Assert.False(SalaryFloorPolicy.Check(
            ContractType.Employee, SalaryType.Monthly, 1_500_000m, HrTestData.Floor2025, acknowledged: false));
}
