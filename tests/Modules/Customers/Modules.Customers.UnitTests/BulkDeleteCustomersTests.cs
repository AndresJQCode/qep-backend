using BuildingBlocks.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-11-10 (borrar clientes) §4 y §6: el caso de uso con dobles. Lo que cada adaptador hace en la base lo
/// prueban sus pruebas de integración.</summary>
public sealed class BulkDeleteCustomersTests
{
    private static readonly Guid Actor = Guid.Parse("01900000-0000-7000-8000-0000000000d1");

    // ── Validador ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyIsRejected() => AssertRejected([]);

    [Fact]
    public void MoreThanAHundredIsRejected() =>
        AssertRejected(Enumerable.Range(0, 101).Select(_ => Guid.CreateVersion7()).ToArray());

    [Fact]
    public void AnEmptyGuidIsRejected() => AssertRejected([Guid.CreateVersion7(), Guid.Empty]);

    [Fact]
    public void ExactlyAHundredIsAccepted()
    {
        var ids = Enumerable.Range(0, BulkDeleteCustomersCommand.MaxCustomers).Select(_ => Guid.CreateVersion7()).ToArray();

        Assert.True(new BulkDeleteCustomersValidator().Validate(new BulkDeleteCustomersCommand(Tenant, ids)).IsValid);
    }

    [Fact]
    public void DuplicatesAreAccepted()
    {
        var one = Guid.CreateVersion7();

        Assert.True(new BulkDeleteCustomersValidator().Validate(new BulkDeleteCustomersCommand(Tenant, [one, one])).IsValid);
    }

    private static void AssertRejected(Guid[] ids)
    {
        var result = new BulkDeleteCustomersValidator().Validate(new BulkDeleteCustomersCommand(Tenant, ids));

        Assert.False(result.IsValid);
        Assert.All(result.Errors, error => Assert.Equal("CustomerIds", error.PropertyName));
    }

    // ── Handler ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutThePermissionItIsForbidden()
    {
        var bed = new DeletionBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.Handler(CustomersPermissions.CustomerManage).HandleAsync(
                new BulkDeleteCustomersCommand(Tenant, [Guid.CreateVersion7()]), CancellationToken.None));

        Assert.Empty(bed.Calls);
    }

    public static TheoryData<string> EachReason() =>
    [
        CustomerDeletionBlockReasons.Quotations,
        CustomerDeletionBlockReasons.Orders,
        CustomerDeletionBlockReasons.Loans,
        CustomerDeletionBlockReasons.ServiceContracts,
        CustomerDeletionBlockReasons.BlockedContact,
    ];

    [Theory]
    [MemberData(nameof(EachReason))]
    public async Task EachReasonBlocksAndNothingIsRemoved(string reason)
    {
        var bed = new DeletionBed();
        var blocked = bed.Known(DeletableCustomer("Ana Pérez"));
        bed.References.Blockers[blocked.Id.Value] = [reason];

        var result = await bed.RunAsync(blocked.Id.Value);

        var dto = Assert.Single(result.Blocked);
        Assert.Equal(blocked.Id.Value, dto.CustomerId);
        Assert.Equal("Ana Pérez", dto.Name);
        Assert.Equal([reason], dto.Reasons);
        Assert.Empty(result.Deleted);
        Assert.Empty(result.NotFound);
        Assert.DoesNotContain(blocked.Id.Value, bed.References.Removed);
        Assert.DoesNotContain(blocked.Id.Value, bed.Store.Deleted);
        Assert.Empty(bed.Audit.Detailed);
    }

    [Fact]
    public async Task ReasonsKeepTheContractOrder()
    {
        var bed = new DeletionBed();
        var blocked = bed.Known(DeletableCustomer("Ana Pérez"));
        bed.References.Blockers[blocked.Id.Value] = [CustomerDeletionBlockReasons.Orders, CustomerDeletionBlockReasons.Quotations];

        var result = await bed.RunAsync(blocked.Id.Value);

        Assert.Equal(
            [CustomerDeletionBlockReasons.Quotations, CustomerDeletionBlockReasons.Orders],
            Assert.Single(result.Blocked).Reasons);
    }

    [Fact]
    public async Task DuplicatesAreIgnoredAndForeignOrMergedIdsAreNotFound()
    {
        var bed = new DeletionBed();
        var a = bed.Known(DeletableCustomer("Ana Pérez"));
        // El doble del almacén sólo devuelve lo que conoce: uno de otro tenant o uno fusionado no aparecen, igual que en la base.
        var otherTenant = Guid.CreateVersion7();
        var merged = Guid.CreateVersion7();

        var result = await bed.RunAsync(a.Id.Value, a.Id.Value, otherTenant, merged);

        Assert.Equal([a.Id.Value], result.Deleted);
        Assert.Empty(result.Blocked);
        Assert.Equal([otherTenant, merged], result.NotFound);
        Assert.Equal([a.Id.Value, otherTenant, merged], bed.References.Locked);
    }

    [Fact]
    public async Task AMixIsSplitInRequestOrder()
    {
        var bed = new DeletionBed();
        var b1 = bed.Known(DeletableCustomer("Bloqueado"));
        var d1 = bed.Known(DeletableCustomer("Uno"));
        var d2 = bed.Known(DeletableCustomer("Dos"));
        var n1 = Guid.CreateVersion7();
        bed.References.Blockers[b1.Id.Value] = [CustomerDeletionBlockReasons.Loans];

        var result = await bed.RunAsync(b1.Id.Value, d1.Id.Value, n1, d2.Id.Value);

        Assert.Equal([d1.Id.Value, d2.Id.Value], result.Deleted);
        Assert.Equal(b1.Id.Value, Assert.Single(result.Blocked).CustomerId);
        Assert.Equal([n1], result.NotFound);
        Assert.Equal([d1.Id.Value, d2.Id.Value], bed.References.Removed);
        Assert.Equal([d1.Id.Value, d2.Id.Value], bed.Store.Deleted);
        Assert.Equal([d1.Id.Value, d2.Id.Value], bed.Store.Snapshots.Select(snapshot => snapshot.CustomerId));
    }

    [Fact]
    public async Task LocksAreTakenBeforeBlockersAreRead()
    {
        var bed = new DeletionBed();
        var a = bed.Known(DeletableCustomer("Ana Pérez"));
        var b = bed.Known(DeletableCustomer("Bruno"));
        var missing = Guid.CreateVersion7();

        await bed.RunAsync(a.Id.Value, b.Id.Value, a.Id.Value, missing);

        Assert.Equal([a.Id.Value, b.Id.Value, missing], bed.References.Locked);
        Assert.Equal(
            ["begin", "lock", "find-deletable", "find-blockers", "snapshot-consents", "remove", "delete", "save", "commit"],
            bed.Calls);
    }

    [Fact]
    public async Task EachDeletedCustomerIsAuditedWithItsSnapshot()
    {
        var bed = new DeletionBed();
        var ana = bed.Known(DeletableCustomer("Ana Pérez", phone: "3001112233", document: "123"));
        var bruno = bed.Known(DeletableCustomer("Bruno"));
        bed.References.Counts[ana.Id.Value] = new CustomerDeletionCounts(2, 7, 1, 0, 3);
        bed.Store.MergedCounts[ana.Id.Value] = 1;
        bed.Store.ConsentCounts[ana.Id.Value] = 1;

        await bed.RunAsync(ana.Id.Value, bruno.Id.Value);

        Assert.Equal(2, bed.Audit.Detailed.Count);
        var entry = Assert.Single(bed.Audit.Detailed, item => item.ResourceId == ana.Id.ToString());
        Assert.Equal(Tenant, entry.Tenant);
        Assert.Equal(Actor, entry.Actor);
        Assert.Equal(CustomerAuditActions.Deleted, entry.Action);
        Assert.Equal("customers.customer.deleted", entry.Action);
        Assert.Equal("success", entry.Outcome);
        Assert.Contains("name=Ana Pérez", entry.ChangedFields);
        Assert.Contains("phoneE164=+573001112233", entry.ChangedFields);
        Assert.Contains("identification=CC 123", entry.ChangedFields);
        Assert.Contains("conversations=2", entry.ChangedFields);
        Assert.Contains("messages=7", entry.ChangedFields);
        Assert.Contains("merged=1", entry.ChangedFields);
        Assert.Contains("dataConsents=1", entry.ChangedFields);
        // Sin consentimientos la clave no se escribe: no hay valor que guardar.
        var brunoEntry = Assert.Single(bed.Audit.Detailed, item => item.ResourceId == bruno.Id.ToString());
        Assert.DoesNotContain(brunoEntry.ChangedFields, field => field.StartsWith("dataConsents=", StringComparison.Ordinal));
        Assert.Empty(bed.Audit.Human);
    }

    [Fact]
    public async Task AMediaEventIsWrittenOnlyForCustomersWithKeys()
    {
        var bed = new DeletionBed();
        var withMedia = bed.Known(DeletableCustomer("Con adjuntos"));
        var withoutMedia = bed.Known(DeletableCustomer("Sin adjuntos"));
        bed.References.StorageKeys[withMedia.Id.Value] = ["messaging/a.jpg", "messaging/b.pdf"];

        await bed.RunAsync(withMedia.Id.Value, withoutMedia.Id.Value);

        var released = Assert.Single(bed.Media.Released);
        Assert.Equal(Tenant, released.Tenant);
        Assert.Equal(withMedia.Id.Value, released.CustomerId);
        Assert.Equal(["messaging/a.jpg", "messaging/b.pdf"], released.StorageKeys);
    }

    private static Customer DeletableCustomer(string name, string phone = "3109352187", string document = "900123456") =>
        Customer.Create(
            CustomerId.New(),
            Tenant,
            "CLI08000142",
            name,
            null,
            null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = document },
            null,
            new CustomerContactInfo
            {
                Phone = phone,
                Email = "compras@verde.co",
                Address = "Calle 10 # 45-12",
                Country = "CO",
                CityId = CityId,
            },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now,
            new PlusNormalizer());

    private sealed class DeletionBed
    {
        public List<string> Calls { get; } = [];

        public RecordingAudit Audit { get; } = new();

        public DeletionReferences References { get; }

        public DeletionStore Store { get; }

        public RecordingMediaRelease Media { get; } = new();

        public DeletionBed()
        {
            References = new DeletionReferences(Calls);
            Store = new DeletionStore(Calls);
        }

        public Customer Known(Customer customer)
        {
            Store.Customers.Add(customer);
            return customer;
        }

        public BulkDeleteCustomersHandler Handler(params string[] permissions) => new(
            new RecordingUnitOfWork(Calls),
            References,
            Store,
            Audit,
            Media,
            new EngagementTestBed.Context(Tenant, Actor, permissions),
            new FixedClock(),
            new BulkDeleteCustomersValidator());

        public Task<BulkDeleteCustomersResult> RunAsync(params Guid[] ids) =>
            Handler(CustomersPermissions.CustomerDelete)
                .HandleAsync(new BulkDeleteCustomersCommand(Tenant, ids), CancellationToken.None);
    }
}
