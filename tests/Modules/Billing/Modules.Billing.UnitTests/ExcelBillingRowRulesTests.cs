using Modules.Billing.Application;
using static Modules.Billing.UnitTests.BillingImportTestDoubles;

namespace Modules.Billing.UnitTests;

/// <summary>Las reglas de una fila del Excel de cobros (spec §3.2b): un código por regla, sobre el texto crudo de la celda.</summary>
public sealed class ExcelBillingRowRulesTests
{
    private static readonly ExcelBillingRowRules Rules = new(new FakePhones(), new FakeIdentificationTypes(), new FixedClock());

    private static ExcelBillingRow With(string field, string? value)
    {
        var row = ValidRow();
        return field switch
        {
            nameof(ExcelBillingRow.CustomerName) => row with { CustomerName = value },
            nameof(ExcelBillingRow.IdentificationType) => row with { IdentificationType = value },
            nameof(ExcelBillingRow.IdentificationNumber) => row with { IdentificationNumber = value },
            nameof(ExcelBillingRow.Phone) => row with { Phone = value },
            nameof(ExcelBillingRow.Email) => row with { Email = value },
            nameof(ExcelBillingRow.Country) => row with { Country = value },
            nameof(ExcelBillingRow.ServiceName) => row with { ServiceName = value },
            nameof(ExcelBillingRow.Price) => row with { Price = value },
            nameof(ExcelBillingRow.Currency) => row with { Currency = value },
            nameof(ExcelBillingRow.Cycle) => row with { Cycle = value },
            nameof(ExcelBillingRow.DueDate) => row with { DueDate = value },
            nameof(ExcelBillingRow.Channel) => row with { Channel = value },
            nameof(ExcelBillingRow.AssigneeEmail) => row with { AssigneeEmail = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
    }

    private static string[] CodesOf(ExcelBillingRow row) =>
        Rules.Validate(row).Errors.Select(error => error.ErrorCode).ToArray();

    [Fact]
    public void AValidRowHasNoErrors() => Assert.Empty(CodesOf(ValidRow()));

    [Theory]
    [InlineData("billing.import.row.customer_name_required", nameof(ExcelBillingRow.CustomerName), null)]
    [InlineData("billing.import.row.customer_name_required", nameof(ExcelBillingRow.CustomerName), "151")]
    [InlineData("billing.import.row.identification_incomplete", nameof(ExcelBillingRow.IdentificationNumber), null)]
    [InlineData("billing.import.row.identification_incomplete", nameof(ExcelBillingRow.IdentificationType), null)]
    [InlineData("billing.import.row.identification_type_invalid", nameof(ExcelBillingRow.IdentificationType), "RUT")]
    [InlineData("billing.import.row.phone_invalid", nameof(ExcelBillingRow.Phone), "12")]
    [InlineData("billing.import.row.phone_required", nameof(ExcelBillingRow.Phone), null)]
    [InlineData("billing.import.row.email_invalid", nameof(ExcelBillingRow.Email), "not-an-email")]
    [InlineData("billing.import.row.country_invalid", nameof(ExcelBillingRow.Country), "ZZ")]
    [InlineData("billing.import.row.country_invalid", nameof(ExcelBillingRow.Country), "Colombia")]
    [InlineData("billing.import.row.service_name_required", nameof(ExcelBillingRow.ServiceName), null)]
    [InlineData("billing.import.row.service_name_required", nameof(ExcelBillingRow.ServiceName), "151")]
    [InlineData("billing.import.row.price_invalid", nameof(ExcelBillingRow.Price), "0")]
    [InlineData("billing.import.row.price_invalid", nameof(ExcelBillingRow.Price), "abc")]
    [InlineData("billing.import.row.price_invalid", nameof(ExcelBillingRow.Price), "150.000")]
    [InlineData("billing.import.row.price_invalid", nameof(ExcelBillingRow.Price), null)]
    [InlineData("billing.import.row.currency_unknown", nameof(ExcelBillingRow.Currency), "XYZ")]
    [InlineData("billing.import.row.cycle_invalid", nameof(ExcelBillingRow.Cycle), "Trimestral")]
    [InlineData("billing.import.row.cycle_invalid", nameof(ExcelBillingRow.Cycle), null)]
    [InlineData("billing.import.row.due_date_invalid", nameof(ExcelBillingRow.DueDate), "mañana")]
    [InlineData("billing.import.row.due_date_invalid", nameof(ExcelBillingRow.DueDate), "1999-12-31")]
    [InlineData("billing.import.row.due_date_invalid", nameof(ExcelBillingRow.DueDate), null)]
    [InlineData("billing.import.row.channel_invalid", nameof(ExcelBillingRow.Channel), "SMS")]
    [InlineData("billing.import.row.assignee_required", nameof(ExcelBillingRow.AssigneeEmail), null)]
    public void EachRuleFailsWithItsCode(string code, string field, string? value)
    {
        // "151" es la marca de «una cadena de 151 caracteres»: InlineData no admite expresiones.
        var cell = value == "151" ? new string('x', 151) : value;

        Assert.Contains(code, CodesOf(With(field, cell)));
    }

    [Fact]
    public void TheEmailChannelRequiresAnEmail()
    {
        var row = ValidRow() with { Channel = "Email", Email = null };

        Assert.Equal(["billing.import.row.email_required"], CodesOf(row));
    }

    [Fact]
    public void WithoutAChannelAMissingPhoneIsNotARowRule()
    {
        // Sin canal, el teléfono sólo es obligatorio si la fila crea el cliente, y eso lo sabe el emparejamiento, no la fila.
        var row = ValidRow() with { Channel = null, Phone = null };

        Assert.Empty(CodesOf(row));
    }

    [Fact]
    public void ThePhoneIsNormalizedWithTheCountryOfTheRow()
    {
        // Diez dígitos son un número colombiano; con país US el doble no lo normaliza: el país de la fila manda.
        Assert.Contains("billing.import.row.phone_invalid", CodesOf(ValidRow() with { Country = "US" }));
        Assert.Empty(CodesOf(ValidRow() with { Country = null }));
    }

    [Fact]
    public void CycleAcceptsAccentsAndCaseInsensitively()
    {
        Assert.Empty(CodesOf(ValidRow() with { Cycle = "mensual" }));
        Assert.Empty(CodesOf(ValidRow() with { Cycle = "SEMESTRAL" }));
        Assert.Empty(CodesOf(ValidRow() with { Cycle = "Anual" }));
        Assert.Empty(CodesOf(ValidRow() with { Cycle = "Ánual" }));
    }

    [Fact]
    public void OptionalCellsMayBeEmpty()
    {
        var row = ValidRow() with
        {
            IdentificationType = null,
            IdentificationNumber = null,
            Email = null,
            Country = null,
            Currency = null,
            Channel = null,
        };

        Assert.Empty(CodesOf(row));
    }

    [Fact]
    public void ADayFirstDateAndAChannelInAnyCaseAreAccepted()
    {
        Assert.Empty(CodesOf(ValidRow() with { DueDate = "10/04/2026", Channel = "whatsapp" }));
        Assert.Empty(CodesOf(ValidRow() with { Channel = "EMAIL" }));
    }

    [Fact]
    public void TheParsedRowCarriesTheNormalizedValues()
    {
        var row = BillingImportRow.From(
            ValidRow() with { Cycle = "semestral", Channel = "email", IdentificationType = "cc", Email = "Pan@Example.com", Currency = "usd" },
            new FakePhones());

        Assert.Equal("CC", row.IdentificationType);
        Assert.Equal("+573001234567", row.PhoneE164);
        Assert.Equal("pan@example.com", row.Email);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(Modules.Billing.Domain.BillingCycle.Semiannual, row.Cycle);
        Assert.Equal(Modules.Billing.Domain.BillingChannel.Email, row.Channel);
        Assert.Equal(new DateOnly(2026, 4, 10), row.FirstDueDate);
        Assert.Equal(150000m, row.Price);
        Assert.Equal("CO", row.Country);
    }
}
