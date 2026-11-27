using FluentValidation;
using Modules.Hr.Application;

namespace Modules.Hr.UnitTests;

/// <summary>
/// La forma de los cuerpos de empleados y contratos. Las claves de <c>errors</c> son las del contrato HTTP (camelCase y punto para lo
/// anidado): las pruebas leen la excepción de <c>ValidateAndThrow</c>, que es lo que ve el endpoint.
/// </summary>
public sealed class HrRequestValidatorsTests
{
    private static readonly Guid CompanyId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid PositionId = Guid.Parse("01900000-0000-7000-8000-0000000000d1");
    private static readonly Guid WorkplaceId = Guid.Parse("01900000-0000-7000-8000-0000000000b1");

    private static ContractTermsRequest Terms() => new(
        CompanyId, "Employee", "FixedTerm", "Monthly", new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 14), null);

    private static NewContractRequest Contract(ContractTermsRequest? terms = null, InitialSalaryRequest? salary = null) => new(
        terms ?? Terms(), salary ?? new InitialSalaryRequest(2_500_000m, null, null), new InitialAssignmentRequest(PositionId, null, WorkplaceId));

    private static CreateEmployeeRequest Hire(
        NewContractRequest? contract = null, PaymentRequest? payment = null) => new()
        {
            FirstName = "Ana",
            LastName = "Pérez",
            DocumentType = "CC",
            DocumentNumber = "1020304050",
            Contract = contract ?? Contract(),
            Payment = payment,
        };

    private static string[] Failed<T>(HrValidator<T> validator, T request)
    {
        try
        {
            validator.ValidateAndThrow(request);
            return [];
        }
        catch (ValidationException exception)
        {
            return exception.Errors.Select(failure => failure.PropertyName).Distinct().Order().ToArray();
        }
    }

    private static string[] FailedHire(CreateEmployeeRequest request) => Failed(new CreateEmployeeRequestValidator(), request);

    [Theory]
    [InlineData("CC", "PEREZ")]
    [InlineData("CE", "12A45")]
    [InlineData("PEP", "12 345")]
    [InlineData("PPT", "abc")]
    public void ALetteredOrSpacedNumberForANumericTypeFailsOnTheDocumentNumber(string type, string number)
    {
        var request = Hire() with { DocumentType = type, DocumentNumber = number };

        Assert.Contains("documentNumber", FailedHire(request));
    }

    [Theory]
    [InlineData("PEP", "12.345-6")]
    [InlineData("PA", "AB123")]
    [InlineData("CD", "X-12")]
    [InlineData("SC", "S12")]
    public void ANumberTheTypeAllowsPasses(string type, string number)
    {
        var request = Hire() with { DocumentType = type, DocumentNumber = number };

        Assert.Empty(FailedHire(request));
    }

    [Fact]
    public void TheNumericMessageNamesTheHrTypes()
    {
        var validator = new EmployeeRequestValidator();

        var error = Assert.Throws<ValidationException>(() =>
            validator.ValidateAndThrow(Hire() with { DocumentType = "CC", DocumentNumber = "PEREZ" }));

        Assert.Contains(error.Errors, e => e.PropertyName == "documentNumber"
            && e.ErrorMessage == HrDocumentNumberRules.NumericTypesMessage);
        Assert.Equal("El número de documento solo puede tener números para CC, CE, PEP y PPT.", HrDocumentNumberRules.NumericTypesMessage);
    }

    [Fact]
    public void AValidHirePasses() => Assert.Empty(FailedHire(Hire()));

    [Fact]
    public void AFixedTermWithoutEndDateFailsOnTheNestedKey() =>
        Assert.Equal(["contract.contract.endDate"], FailedHire(Hire(Contract(Terms() with { EndDate = null }))));

    [Fact]
    public void AnIndefiniteContractWithEndDateFails() =>
        Assert.Equal(["contract.contract.endDate"], FailedHire(Hire(Contract(Terms() with { Duration = "Indefinite" }))));

    [Fact]
    public void AWorkOrLaborNeedsItsEstimatedEnd() =>
        Assert.Equal(["contract.contract.endDate"], FailedHire(Hire(Contract(Terms() with { Duration = "WorkOrLabor", EndDate = null }))));

    [Fact]
    public void AnEndDateBeforeTheStartFails() =>
        Assert.Equal(["contract.contract.endDate"], FailedHire(Hire(Contract(Terms() with { EndDate = new DateOnly(2026, 1, 14) }))));

    [Fact]
    public void IntegralIsOnlyForEmployees() =>
        Assert.Equal(
            ["contract.contract.salaryType"],
            FailedHire(Hire(Contract(Terms() with { ContractType = "Contractor", SalaryType = "Integral" }))));

    [Fact]
    public void APhoneThatIsNotE164Fails() =>
        Assert.Equal(["phone"], FailedHire(Hire() with { Phone = "3001234567" }));

    [Theory]
    [InlineData("COL")]
    [InlineData("1A")]
    public void AMalformedCountryFails(string country) =>
        Assert.Equal(["country"], FailedHire(Hire() with { Country = country }));

    // El catálogo lo revisa el caso de uso, porque necesita el puerto a Geografía.
    [Fact]
    public void AnUnknownButWellFormedCountryPassesTheValidator() =>
        Assert.Empty(FailedHire(Hire() with { Country = "XX" }));

    [Fact]
    public void ALowercaseCountryIsAccepted() => Assert.Empty(FailedHire(Hire() with { Country = " ve " }));

    [Fact]
    public void ANullCountryIsAccepted() => Assert.Empty(FailedHire(Hire() with { Country = null }));

    [Theory]
    [MemberData(nameof(InvalidRestDays))]
    public void RestDaysNeedOneToSixDistinctDays(string[] days) =>
        Assert.Equal(["restDays"], FailedHire(Hire() with { RestDays = days }));

    public static TheoryData<string[]> InvalidRestDays => new(
        [],
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"],
        ["Sunday", "Sunday"]);

    [Fact]
    public void ABankTransferNeedsTheThreeFields() =>
        Assert.Equal(
            ["payment.bankAccountNumber"],
            FailedHire(Hire(payment: new PaymentRequest("BankTransfer", "Bancolombia", "Savings", null))));

    [Fact]
    public void OtherPaymentRejectsBankFields() =>
        Assert.Equal(
            ["payment.bankName"],
            FailedHire(Hire(payment: new PaymentRequest("Other", "Bancolombia", null, null))));

    [Fact]
    public void AnAccountNumberOfEighteenDigitsFails() =>
        Assert.Equal(
            ["payment.bankAccountNumber"],
            FailedHire(Hire(payment: new PaymentRequest("BankTransfer", "Bancolombia", "Savings", "123456789012345678"))));

    [Fact]
    public void AllNullPaymentClearsAndPasses() =>
        Assert.Empty(Failed(new PaymentRequestValidator(), new PaymentRequest(null, null, null, null)));

    [Fact]
    public void AnEnumNumberIsNotAName() =>
        Assert.Equal(["contract.contract.contractType"], FailedHire(Hire(Contract(Terms() with { ContractType = "1" }))));

    [Fact]
    public void ASalaryWithThreeDecimalsFails() =>
        Assert.Equal(
            ["contract.salary.amount"],
            FailedHire(Hire(Contract(salary: new InitialSalaryRequest(2_500_000.123m, null, null)))));

    [Fact]
    public void AContractWithoutCompanyFails() =>
        Assert.Equal(["contract.contract.companyId"], FailedHire(Hire(Contract(Terms() with { CompanyId = null }))));

    [Fact]
    public void AHireWithoutContractFails() =>
        Assert.Equal(["contract"], FailedHire(Hire() with { Contract = null }));

    [Fact]
    public void ASalaryWithoutEffectiveDateFails() =>
        Assert.Equal(["effectiveDate"], Failed(new SalaryRequestValidator(), new SalaryRequest(null, 2_800_000m, null, null, null)));

    [Fact]
    public void AnAssignmentWithoutWorkplaceFails() =>
        Assert.Equal(
            ["workplaceId"],
            Failed(new AssignmentRequestValidator(), new AssignmentRequest(new DateOnly(2026, 12, 1), PositionId, null, null, null)));

    // La reincorporación y la conversión reciben el `NewContractRequest` suelto: las claves no llevan el prefijo del alta.
    [Fact]
    public void AReincorporationWithoutCompanyFails() =>
        Assert.Equal(["contract.companyId"], Failed(new NewContractRequestValidator(), Contract(Terms() with { CompanyId = null })));

    [Fact]
    public void ATerminationWithoutReasonFails() =>
        Assert.Equal(
            ["reason"],
            Failed(new TerminateRequestValidator(), new TerminateRequest(null, new DateOnly(2026, 11, 30), null)));

    [Fact]
    public void AnUnknownReasonFails() =>
        Assert.Equal(
            ["reason"],
            Failed(new TerminateRequestValidator(), new TerminateRequest("Fired", new DateOnly(2026, 11, 30), null)));

    [Fact]
    public void ARewriteWithoutConfirmationPassesValidation() =>
        Assert.Empty(Failed(new SalaryRewriteRequestValidator(), new SalaryRewriteRequest(2_900_000m, null, null, null)));

    [Fact]
    public void ARenewalWithoutNewEndDateFails() =>
        Assert.Equal(["newEndDate"], Failed(new RenewalRequestValidator(), new RenewalRequest(null, null)));
}
