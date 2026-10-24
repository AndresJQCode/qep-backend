using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Suppliers.UnitTests;

/// <summary>
/// El tope duro de la exportación (50.000 filas) sin Docker: un repositorio que nunca se agota
/// tiene que terminar en 422 antes de construir, subir, publicar o auditar nada.
/// </summary>
public sealed class ExportSuppliersHandlerTests
{
    private static readonly Guid TenantGuid = Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExportPastTheRowCapThrowsTooManyRowsAndTouchesNothing()
    {
        var repository = new EndlessRepository(BuildBatch());
        var builder = new RecordingBuilder();
        var storage = new RecordingStorage();
        var events = new RecordingEvents();
        var audit = new RecordingAudit();
        var unitOfWork = new RecordingUnitOfWork();
        var handler = new ExportSuppliersHandler(
            repository,
            new NoGeography(),
            builder,
            storage,
            events,
            audit,
            unitOfWork,
            new Context(),
            new FixedClock());

        var exception = await Assert.ThrowsAsync<SuppliersDomainException>(() => handler.HandleAsync(
            new ExportSuppliersCommand(TenantGuid, null, null, null),
            TestContext.Current.CancellationToken));

        Assert.Equal("suppliers.export.too_many_rows", exception.Code);
        // 50.000 filas caben; el lote que las pasa (la 50.001) es el que dispara el error.
        Assert.Equal(101, repository.Calls);
        Assert.Equal(0, builder.Calls);
        Assert.Equal(0, storage.Calls);
        Assert.Equal(0, events.Calls);
        Assert.Equal(0, audit.Calls);
        Assert.Equal(0, unitOfWork.Calls);
    }

    private static List<Supplier> BuildBatch()
    {
        var supplier = Supplier.Create(
            TenantGuid,
            new SupplierWriteData(
                "Ferretería",
                null,
                "NIT",
                "1",
                "US",
                null,
                "Miami",
                null,
                null,
                null,
                30,
                null,
                null,
                [],
                []),
            new NoPhones(),
            Now);
        return [.. Enumerable.Repeat(supplier, 500)];
    }

    private sealed class NoPhones : ISupplierPhoneNormalizer
    {
        public string? ToE164(string? phone, string? country) => null;
    }

    private sealed class Context : IExecutionContext
    {
        public Guid SubjectId => Guid.Parse("01900000-0000-7000-8000-0000000000aa");

        public TenantId TenantId => new(TenantGuid);

        public bool HasPermission(string permission) => true;
    }

    private sealed class FixedClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(Now, TimeZoneInfo.Utc));
    }

    // Devuelve siempre un lote completo: el agotamiento que cortaría el recorrido nunca llega.
    private sealed class EndlessRepository(List<Supplier> batch) : ISupplierRepository
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Supplier>> ListForExportAsync(
            Guid tenantId,
            string? search,
            SupplierStatusFilter? status,
            string? category,
            int skip,
            int take,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<Supplier>>(batch);
        }

        public Task<Supplier?> GetAsync(Guid tenantId, SupplierId supplierId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(Supplier supplier) => throw new NotSupportedException();

        public Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
            Guid tenantId,
            string? search,
            SupplierStatusFilter? status,
            string? category,
            int page,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListCategoriesAsync(
            Guid tenantId, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoGeography : ISupplierGeographyLookup
    {
        public Task<SupplierCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SupplierCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingBuilder : ISupplierExportBuilder
    {
        public int Calls { get; private set; }

        public SupplierExportFile Build(
            IReadOnlyList<SupplierResponse> suppliers,
            TenantCalendar calendar,
            CancellationToken cancellationToken)
        {
            Calls++;
            return new SupplierExportFile([], "x.xlsx");
        }
    }

    private sealed class RecordingStorage : ISupplierExportStorage
    {
        public int Calls { get; private set; }

        public Task<SupplierExportUpload> UploadAsync(
            Guid tenantId, string fileName, byte[] content, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SupplierExportUpload("https://r2.invalid/x", Now));
        }
    }

    private sealed class RecordingEvents : ISupplierExportEventPublisher
    {
        public int Calls { get; private set; }

        public void Publish(
            Guid tenantId,
            Guid subjectId,
            string downloadUrl,
            string fileName,
            int supplierCount,
            DateTimeOffset expiresAt,
            DateTimeOffset occurredAt) => Calls++;
    }

    private sealed class RecordingAudit : ISuppliersAuditPublisher
    {
        public int Calls { get; private set; }

        public void Publish(
            Guid tenantId,
            Guid actorId,
            string action,
            string resourceId,
            string outcome,
            DateTimeOffset occurredAt) => Calls++;
    }

    private sealed class RecordingUnitOfWork : ISuppliersUnitOfWork
    {
        public int Calls { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
