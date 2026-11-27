using System.Reflection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>
/// Una fila que actualiza por CUC y deja el documento vacio conserva el guardado. Si ese documento es un NIT/CC/CE con letras
/// (guardado antes de la regla numerica), Update lo rechazaria y, sin try/catch por fila, tumbaria el archivo entero: debe
/// salir como error de esa fila y el resto del archivo seguir.
/// </summary>
public sealed class ImportCustomersLegacyDocumentTests
{
    private static Customer ColombianWithNit(string cuc, string number) =>
        Customer.Create(
            CustomerId.New(), CustomerWriteDoubles.Tenant, cuc, "Verde Esencial", null, null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = number }, null,
            new CustomerContactInfo { Phone = "3109352187", Email = "v@verde.es", Address = "Calle 1", Country = Customer.ColombiaCountryCode, CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id }, Now, new PlusNormalizer());

    [Fact]
    public async Task StoredLegacyLetteredDocumentWithEmptyCellIsARowErrorAndTheRestContinues()
    {
        var legacy = ColombianWithNit("CLI00000001", "900123456");
        typeof(Customer).GetProperty(nameof(Customer.IdentificationNumber), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(legacy, "ACME SAS");
        var healthy = ColombianWithNit("CLI00000002", "900123457");
        var repository = new FakeCustomerRepository();
        repository.Known.Add(legacy);
        repository.Known.Add(healthy);
        var legacyRow = new ExcelCustomerRow(
            2, legacy.Cuc, "Renombrado", null, null, null, "3109352187", null, "Calle 2", null, null, null, null, "No");
        var healthyRow = new ExcelCustomerRow(
            3, healthy.Cuc, "Sano Renombrado", null, null, null, "3109352187", null, "Calle 3", null, null, null, null, "No");
        var handler = new ImportCustomersHandler(
            repository, new FakeCustomerCompanyDirectory([legacy, healthy]), new EngagementTestBed.FakeTags(), new Classifications([]),
            new Geography([]), new TaxRates(), new SequentialCucs(),
            new StubImporter(legacyRow, healthyRow), new UnitOfWork(), new RecordingAudit(),
            new EngagementTestBed.Context(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, CustomersPermissions.CustomerImport),
            new FixedClock(), new TenantDirectory(), new PlusNormalizer());

        var response = await handler.HandleAsync(
            new ImportCustomersCommand(CustomerWriteDoubles.Tenant, "clientes.xlsx", 10, new MemoryStream([1])), CancellationToken.None);

        var error = Assert.Single(response.Errors);
        Assert.Equal(2, error.RowNumber);
        Assert.Equal("customers.import.row.identification_number_not_numeric", error.Code);
        Assert.Equal(nameof(ExcelCustomerRow.IdentificationNumber), error.Field);
        Assert.Equal("Verde Esencial", legacy.Name);
        Assert.Equal("Sano Renombrado", healthy.Name);
    }

    private sealed class StubImporter(params ExcelCustomerRow[] rows) : IExcelCustomerImporter
    {
        public ExcelCustomerImportFile Parse(Stream content, CancellationToken cancellationToken) => new(true, rows);
    }

    private sealed class SequentialCucs : ICucGenerator
    {
        public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> NextBatchAsync(Guid tenantId, int count, CancellationToken cancellationToken) => Task.FromResult(100L);
    }
}
