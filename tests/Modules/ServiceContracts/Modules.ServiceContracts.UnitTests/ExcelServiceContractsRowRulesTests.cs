using Modules.ServiceContracts.Application;
using static Modules.ServiceContracts.UnitTests.ServiceContractsImportTestDoubles;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>Las reglas de una fila del Excel de cobros (spec §3.2b): un código por regla, sobre el texto crudo de la celda.</summary>
public sealed class ExcelServiceContractsRowRulesTests
{
    private static readonly ExcelServiceContractsRowRules Rules = new(new FakePhones(), new FakeIdentificationTypes(), new FixedClock());

    private static ExcelServiceContractsRow With(string field, string? value)
    {
        var row = ValidRow();
        return field switch
        {
            nameof(ExcelServiceContractsRow.CustomerName) => row with { CustomerName = value },
            nameof(ExcelServiceContractsRow.IdentificationType) => row with { IdentificationType = value },
            nameof(ExcelServiceContractsRow.IdentificationNumber) => row with { IdentificationNumber = value },
            nameof(ExcelServiceContractsRow.Phone) => row with { Phone = value },
            nameof(ExcelServiceContractsRow.Email) => row with { Email = value },
            nameof(ExcelServiceContractsRow.Country) => row with { Country = value },
            nameof(ExcelServiceContractsRow.ServiceName) => row with { ServiceName = value },
            nameof(ExcelServiceContractsRow.Price) => row with { Price = value },
            nameof(ExcelServiceContractsRow.Currency) => row with { Currency = value },
            nameof(ExcelServiceContractsRow.Cycle) => row with { Cycle = value },
            nameof(ExcelServiceContractsRow.DueDate) => row with { DueDate = value },
            nameof(ExcelServiceContractsRow.Channel) => row with { Channel = value },
            nameof(ExcelServiceContractsRow.AssigneeEmail) => row with { AssigneeEmail = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
    }

    private static string[] CodesOf(ExcelServiceContractsRow row) =>
        Rules.Validate(row).Errors.Select(error => error.ErrorCode).ToArray();

    [Fact]
    public void AValidRowHasNoErrors() => Assert.Empty(CodesOf(ValidRow()));

    [Theory]
    [InlineData("billing.import.row.customer_name_required", nameof(ExcelServiceContractsRow.CustomerName), null)]
    [InlineData("billing.import.row.customer_name_required", nameof(ExcelServiceContractsRow.CustomerName), "151")]
    [InlineData("billing.import.row.identification_incomplete", nameof(ExcelServiceContractsRow.IdentificationNumber), null)]
    [InlineData("billing.import.row.identification_incomplete", nameof(ExcelServiceContractsRow.IdentificationType), null)]
    [InlineData("billing.import.row.identification_type_invalid", nameof(ExcelServiceContractsRow.IdentificationType), "RUT")]
    [InlineData("billing.import.row.phone_invalid", nameof(ExcelServiceContractsRow.Phone), "12")]
    [InlineData("billing.import.row.phone_required", nameof(ExcelServiceContractsRow.Phone), null)]
    [InlineData("billing.import.row.email_invalid", nameof(ExcelServiceContractsRow.Email), "not-an-email")]
    [InlineData("billing.import.row.country_invalid", nameof(ExcelServiceContractsRow.Country), "ZZ")]
    [InlineData("billing.import.row.country_invalid", nameof(ExcelServiceContractsRow.Country), "Colombia")]
    [InlineData("billing.import.row.service_name_required", nameof(ExcelServiceContractsRow.ServiceName), null)]
    [InlineData("billing.import.row.service_name_required", nameof(ExcelServiceContractsRow.ServiceName), "151")]
    [InlineData("billing.import.row.price_invalid", nameof(ExcelServiceContractsRow.Price), "0")]
    [InlineData("billing.import.row.price_invalid", nameof(ExcelServiceContractsRow.Price), "abc")]
    [InlineData("billing.import.row.price_invalid", nameof(ExcelServiceContractsRow.Price), "150.000")]
    [InlineData("billing.import.row.price_invalid", nameof(ExcelServiceContractsRow.Price), null)]
    [InlineData("billing.import.row.currency_unknown", nameof(ExcelServiceContractsRow.Currency), "XYZ")]
    [InlineData("billing.import.row.cycle_invalid", nameof(ExcelServiceContractsRow.Cycle), "Trimestral")]
    [InlineData("billing.import.row.cycle_invalid", nameof(ExcelServiceContractsRow.Cycle), null)]
    [InlineData("billing.import.row.due_date_invalid", nameof(ExcelServiceContractsRow.DueDate), "mañana")]
    [InlineData("billing.import.row.due_date_invalid", nameof(ExcelServiceContractsRow.DueDate), "1999-12-31")]
    [InlineData("billing.import.row.due_date_invalid", nameof(ExcelServiceContractsRow.DueDate), null)]
    [InlineData("billing.import.row.channel_invalid", nameof(ExcelServiceContractsRow.Channel), "SMS")]
    [InlineData("billing.import.row.assignee_required", nameof(ExcelServiceContractsRow.AssigneeEmail), null)]
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
        var row = ServiceContractsImportRow.From(
            ValidRow() with { Cycle = "semestral", Channel = "email", IdentificationType = "cc", Email = "Pan@Example.com", Currency = "usd" },
            new FakePhones());

        Assert.Equal("CC", row.IdentificationType);
        Assert.Equal("+573001234567", row.PhoneE164);
        Assert.Equal("pan@example.com", row.Email);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(Modules.ServiceContracts.Domain.BillingCycle.Semiannual, row.Cycle);
        Assert.Equal(Modules.ServiceContracts.Domain.BillingChannel.Email, row.Channel);
        Assert.Equal(new DateOnly(2026, 4, 10), row.FirstDueDate);
        Assert.Equal(150000m, row.Price);
        Assert.Equal("CO", row.Country);
    }
}
