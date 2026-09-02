using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// El validador de una fila del Excel de importacion (Fase 5): texto crudo entrando, sin acceso a
/// la base — solo campos obligatorios, longitudes y formato. Resolver Departamento+Ciudad y
/// Clasificacion contra la base es responsabilidad de <c>ImportCustomersHandler</c>, no de este
/// validador, y no se cubre aca.
/// </summary>
public sealed class ExcelCustomerRowRulesTests
{
    private readonly ExcelCustomerRowRules validator = new();

    private static ExcelCustomerRow ValidRow(
        string? cuc = null,
        string? name = "Verde Esencial S.A.S.",
        string? identificationType = "NIT",
        string? identificationNumber = "900.123.456-1",
        string? phone = "3001234567",
        string? email = "compras@verde.co",
        string? address = "Calle 10 # 20-30",
        string? department = "Antioquia",
        string? city = "Medellin",
        string? classification = "Mayorista",
        string? withRetention = "No") =>
        new(2, cuc, name, identificationType, identificationNumber, phone, email, address,
            department, city, classification, withRetention);

    [Fact]
    public void AValidRowPassesEveryRule()
    {
        var result = validator.Validate(ValidRow());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ARowWithAValidCucPassesEveryRule()
    {
        var result = validator.Validate(ValidRow(cuc: "CLI08000037"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ACucShorterThanTheStableSuffixFails()
    {
        var result = validator.Validate(ValidRow(cuc: "CLI0800"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.cuc_invalid" &&
            error.PropertyName == nameof(ExcelCustomerRow.Cuc));
    }

    [Fact]
    public void AnEmptyNameFailsWithTheRequiredCode()
    {
        var result = validator.Validate(ValidRow(name: null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.name_required" &&
            error.PropertyName == nameof(ExcelCustomerRow.Name));
    }

    [Fact]
    public void ANameLongerThanTheLimitFails()
    {
        var result = validator.Validate(ValidRow(name: new string('A', Customer.NameMaxLength + 1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.name_too_long");
    }

    [Fact]
    public void AnEmptyIdentificationNumberFailsWithTheRequiredCode()
    {
        var result = validator.Validate(ValidRow(identificationNumber: null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.identification_number_required" &&
            error.PropertyName == nameof(ExcelCustomerRow.IdentificationNumber));
    }

    [Theory]
    [InlineData("NIT")]
    [InlineData("cc")]
    [InlineData("Ce")]
    [InlineData("PASAPORTE")]
    public void EveryWireValueOfIdentificationTypeIsAccepted(string wireValue)
    {
        var result = validator.Validate(ValidRow(identificationType: wireValue));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AnUnsupportedIdentificationTypeFails()
    {
        var result = validator.Validate(ValidRow(identificationType: "RUT"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.identification_type_invalid");
    }

    [Fact]
    public void AMissingIdentificationTypeFailsWithTheRequiredCodeAndNotTheFormatCode()
    {
        var result = validator.Validate(ValidRow(identificationType: null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.identification_type_required");
        Assert.DoesNotContain(result.Errors, error =>
            error.ErrorCode == "customers.import.row.identification_type_invalid");
    }

    [Fact]
    public void AMalformedEmailFails()
    {
        var result = validator.Validate(ValidRow(email: "not-an-email"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.email_invalid");
    }

    // Vacio es ausente para un campo opcional: sin este caso, una fila que legitimamente no trae
    // correo se rechazaria por EmailAddress().
    [Fact]
    public void AnEmptyEmailIsAccepted()
    {
        var result = validator.Validate(ValidRow(email: null));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void APhoneLongerThanTheLimitFails()
    {
        var result = validator.Validate(
            ValidRow(phone: new string('1', CustomerContactInfo.PhoneMaxLength + 1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.phone_too_long");
    }

    [Fact]
    public void AnAddressLongerThanTheLimitFails()
    {
        var result = validator.Validate(
            ValidRow(address: new string('A', CustomerContactInfo.AddressMaxLength + 1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.address_too_long");
    }

    [Fact]
    public void AnEmptyDepartmentFails()
    {
        var result = validator.Validate(ValidRow(department: null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.department_required");
    }

    [Fact]
    public void AnEmptyCityFails()
    {
        var result = validator.Validate(ValidRow(city: null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.city_required");
    }

    [Fact]
    public void AnEmptyClassificationFails()
    {
        var result = validator.Validate(ValidRow(classification: null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.classification_required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Si")]
    [InlineData("si")]
    [InlineData("SI")]
    [InlineData("No")]
    [InlineData("no")]
    public void WithRetentionAcceptsSiNoOrEmpty(string? value)
    {
        var result = validator.Validate(ValidRow(withRetention: value));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void WithRetentionRejectsAnyOtherValue()
    {
        var result = validator.Validate(ValidRow(withRetention: "Tal vez"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.with_retention_invalid");
    }

    // Una fila puede fallar por mas de un motivo a la vez, y el reporte tiene que decir los dos —
    // no solo el primero que encuentre FluentValidation.
    [Fact]
    public void ARowWithSeveralProblemsReportsAllOfThem()
    {
        var result = validator.Validate(ValidRow(name: null, department: null, city: null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorCode == "customers.import.row.name_required");
        Assert.Contains(result.Errors, error =>
            error.ErrorCode == "customers.import.row.department_required");
        Assert.Contains(result.Errors, error => error.ErrorCode == "customers.import.row.city_required");
    }
}
