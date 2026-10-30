using Modules.Billing.Application;
using static Modules.Billing.UnitTests.BillingImportTestDoubles;

namespace Modules.Billing.UnitTests;

/// <summary>
/// El duplicado dentro del archivo (§3.2b, <c>duplicate_service</c>): mismo cliente por la clave de emparejamiento (documento,
/// si no E.164, si no correo) y mismo servicio sin mayúsculas ni espacios extra.
/// </summary>
public sealed class ExcelBillingRowDeduplicatorTests
{
    [Fact]
    public void TheSameCustomerAndServiceTwiceFlagsTheSecondRow()
    {
        var first = ImportRow(rowNumber: 2, serviceName: "Hosting");
        var second = ImportRow(rowNumber: 3, serviceName: " hosting ");

        var (firsts, duplicates) = ExcelBillingRowDeduplicator.Partition([first, second]);

        Assert.Equal([first], firsts);
        Assert.Equal([second], duplicates);
    }

    [Fact]
    public void TheSameCustomerWithTwoServicesIsNotADuplicate()
    {
        var hosting = ImportRow(rowNumber: 2, serviceName: "Hosting");
        var domain = ImportRow(rowNumber: 3, serviceName: "Dominio");

        var (firsts, duplicates) = ExcelBillingRowDeduplicator.Partition([hosting, domain]);

        Assert.Equal([hosting, domain], firsts);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void WithoutADocumentThePhoneIsTheKeyAndThenTheEmail()
    {
        var byPhone = ImportRow(rowNumber: 2, identificationType: null, identificationNumber: null);
        var samePhone = ImportRow(rowNumber: 3, identificationType: null, identificationNumber: null, email: "otro@example.com");
        var byEmail = ImportRow(rowNumber: 4, identificationType: null, identificationNumber: null, phoneE164: null, email: "Pan@Example.com");
        var sameEmail = ImportRow(rowNumber: 5, identificationType: null, identificationNumber: null, phoneE164: null, email: "pan@example.com");

        var (_, duplicates) = ExcelBillingRowDeduplicator.Partition([byPhone, samePhone, byEmail, sameEmail]);

        Assert.Equal([3, 5], duplicates.Select(row => row.RowNumber));
    }
}
