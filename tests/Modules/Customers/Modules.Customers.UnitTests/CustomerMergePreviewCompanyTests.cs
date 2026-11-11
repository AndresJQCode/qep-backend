using BuildingBlocks.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-11-10 contactos de empresa §4.7: la vista previa de la fusión cuenta los contactos que se mueven y avisa
/// el conflicto de dos niveles sin lanzar (el POST de la fusión sí lo rechaza).</summary>
public sealed class CustomerMergePreviewCompanyTests
{
    private static readonly EngagementTestBed.Context Merger =
        new(Tenant, EngagementTestBed.UserA, CustomersPermissions.CustomerMerge);

    private sealed class StubMergeStore(int moved) : ICustomerMergeStore
    {
        public Task<IReadOnlyList<Guid>> FindIdsMergedIntoAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task RepointMergedIntoAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, Guid survivorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountContactsMovedAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            Task.FromResult(moved);

        public Task RepointContactsAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerMergeOwnedCounts> CountOwnedAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult(new CustomerMergeOwnedCounts(0, 0));

        public Task MoveOwnedAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindSurvivorIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindSurvivorIdByIdentificationAsync(Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubReferences : ICustomerMergeReferences
    {
        public Task MoveAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerMergeReferenceCounts> CountAsync(Guid tenantId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            Task.FromResult(new CustomerMergeReferenceCounts(0, 0, 0, 0, 0));
    }

    private static Customer Person(string name, string number) =>
        Customer.Create(
            CustomerId.New(), Tenant, "CLI08000001", name, null, null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = number },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now,
            new PlusNormalizer());

    private static async Task<CustomerMergePreviewResponse> PreviewAsync(
        Customer survivor, Customer merged, int moved, params Customer[] others)
    {
        var repository = new FakeCustomerRepository();
        repository.Known.AddRange([survivor, merged, .. others]);
        var handler = new GetCustomerMergePreviewHandler(
            repository,
            new StubMergeStore(moved),
            new StubReferences(),
            new EngagementTestBed.FakeTags(),
            new FakeCustomerCompanyDirectory([survivor, merged, .. others]),
            Merger,
            new GetCustomerMergePreviewValidator());
        return await handler.HandleAsync(
            new GetCustomerMergePreviewQuery(Tenant, survivor.Id.Value, [merged.Id.Value]), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThePreviewListsContactsEvenAtZero()
    {
        var preview = await PreviewAsync(Person("Quien Queda", "1020304001"), Person("Fusionado", "1020304002"), moved: 0);

        var move = Assert.Single(preview.Moves, item => item.Kind == "Contacts");
        Assert.Equal(0, move.Count);
        Assert.Equal(9, preview.Moves.Count);
        Assert.Equal("Contacts", preview.Moves[^1].Kind);
        Assert.False(preview.CompanyConflict);
    }

    [Fact]
    public async Task ThePreviewCountsTheContactsThatMove()
    {
        var preview = await PreviewAsync(Person("Quien Queda", "1020304001"), Person("Empresa", "1020304002"), moved: 3);

        Assert.Equal(3, Assert.Single(preview.Moves, item => item.Kind == "Contacts").Count);
        Assert.False(preview.CompanyConflict);
    }

    [Fact]
    public async Task ThePreviewFlagsTheCompanyConflictWithoutThrowing()
    {
        var survivor = Person("Quien Queda", "1020304001");
        var parent = Person("Casa Matriz", "1020304003");
        survivor.LinkToCompany(parent, hasContacts: false, Now);

        var preview = await PreviewAsync(survivor, Person("Empresa", "1020304002"), moved: 2, parent);

        Assert.True(preview.CompanyConflict);
        Assert.Equal(2, Assert.Single(preview.Moves, item => item.Kind == "Contacts").Count);
    }

    [Fact]
    public async Task ThePreviewFlagsTheConflictWhenTheSurvivorAlreadyHasContacts()
    {
        // P8: los contactos propios del que queda también cuentan (defensivo: un contacto con contactos no debería existir).
        var survivor = Person("Quien Queda", "1020304001");
        var parent = Person("Casa Matriz", "1020304003");
        var own = Person("Contacto Propio", "1020304004");
        // El agregado no deja armar dos niveles por la vía pública; el handler del vínculo lo avisa con hasContacts. Aquí se
        // arma a propósito el estado imposible que el chequeo defensivo cubre.
        own.LinkToCompany(survivor, hasContacts: false, Now);
        survivor.LinkToCompany(parent, hasContacts: false, Now);

        var preview = await PreviewAsync(survivor, Person("Otro", "1020304002"), moved: 0, parent, own);

        Assert.True(preview.CompanyConflict);
    }
}
