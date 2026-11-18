using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>Spec 2026-11-17 (mayorista): los casos de uso de las listas de precios con todos sus puertos falsos.</summary>
public sealed class PriceListHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly FakePriceListRepository _lists = new();
    private readonly FakeDistributorUsage _usage = new();
    private readonly FakeCatalogReader _catalog = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeAuditPublisher _audit = new();
    private readonly FakeClock _clock = new(Now);
    private FakeExecutionContext _context;

    public PriceListHandlersTests()
    {
        _context = new FakeExecutionContext(_tenant, WholesalePermissions.PriceListManage);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatePriceListHandler CreateHandler() =>
        new(_context, _clock, _lists, _catalog, _unitOfWork, _audit, new CreatePriceListValidator());

    private UpdatePriceListHandler UpdateHandler() =>
        new(_context, _clock, _lists, _usage, _catalog, _unitOfWork, _audit, new UpdatePriceListValidator());

    private DeletePriceListHandler DeleteHandler() => new(_context, _clock, _lists, _usage, _unitOfWork, _audit);

    private GetPriceListHandler GetHandler() => new(_context, _lists, _usage, _catalog);

    private ListPriceListsHandler ListHandler() => new(_context, _lists, _usage, _catalog);

    private PriceList SeedList(string name, PriceListVisibility visibility = PriceListVisibility.AllActive, params Guid[] categories) =>
        _lists.Seed(PriceList.Create(_tenant, name, 10m, visibility, categories, Now.AddDays(-1)));

    [Fact]
    public async Task CreateReturnsTheDetailWithTheCategoriesByName()
    {
        var kitchen = _catalog.AddCategory("Cocina");
        var home = _catalog.AddCategory("Hogar");

        var detail = await CreateHandler().HandleAsync(
            new CreatePriceListCommand(_tenant, "  Mayoristas  ", 15m, "Categories", [home, kitchen]), Ct);

        Assert.Equal(("Mayoristas", 15m, "Categories", 1L, 0, Now, Now),
            (detail.Name, detail.DiscountPercent, detail.Visibility, detail.Version, detail.DistributorCount, detail.CreatedAt, detail.UpdatedAt));
        Assert.Equal([new PriceListCategoryDto(kitchen, "Cocina"), new PriceListCategoryDto(home, "Hogar")], detail.Categories);
        Assert.Single(_lists.Lists);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task CreateWithATakenNameIs409NameTaken()
    {
        SeedList("Mayoristas");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => CreateHandler().HandleAsync(
            new CreatePriceListCommand(_tenant, " mayoristas ", 15m, "AllActive", []), Ct));

        Assert.Equal("wholesale.price_list.name_taken", error.Code);
        Assert.Single(_lists.Lists);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task CreateWithACategoryOfAnotherTenantIs422CategoryNotFound()
    {
        var mine = _catalog.AddCategory("Hogar");
        var foreign = Guid.CreateVersion7(); // el lector va acotado al tenant: una ajena no aparece.

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() => CreateHandler().HandleAsync(
            new CreatePriceListCommand(_tenant, "Mayoristas", 15m, "Categories", [mine, foreign]), Ct));

        Assert.Equal("wholesale.price_list.category_not_found", error.Code);
        Assert.IsAssignableFrom<DomainException>(error);
        Assert.Empty(_lists.Lists);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task UpdateWithAStaleVersionIs412()
    {
        var list = SeedList("Mayoristas");

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => UpdateHandler().HandleAsync(
            new UpdatePriceListCommand(_tenant, list.Id.Value, 7, "Mayoristas", 20m, "AllActive", []), Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(10m, list.DiscountPercent);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task UpdateKeepsItsOwnNameButNotAnotherOne()
    {
        var list = SeedList("Mayoristas");
        SeedList("Detal");

        var detail = await UpdateHandler().HandleAsync(
            new UpdatePriceListCommand(_tenant, list.Id.Value, 1, "MAYORISTAS", 20m, "AllActive", []), Ct);
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => UpdateHandler().HandleAsync(
            new UpdatePriceListCommand(_tenant, list.Id.Value, 2, "detal", 20m, "AllActive", []), Ct));

        Assert.Equal(("MAYORISTAS", 20m, 2L), (detail.Name, detail.DiscountPercent, detail.Version));
        Assert.Equal("wholesale.price_list.name_taken", error.Code);
    }

    [Fact]
    public async Task AnUnknownListIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            GetHandler().HandleAsync(new GetPriceListQuery(_tenant, Guid.CreateVersion7()), Ct));

        Assert.Equal("wholesale.price_list.not_found", error.Code);
    }

    [Fact]
    public async Task DeleteAListInUseIs409InUse()
    {
        var list = SeedList("Mayoristas");
        _usage.Counts[list.Id] = 1;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            DeleteHandler().HandleAsync(new DeletePriceListCommand(_tenant, list.Id.Value, 1), Ct));

        Assert.Equal("wholesale.price_list.in_use", error.Code);
        Assert.Single(_lists.Lists);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task DeleteWithAStaleVersionIs412()
    {
        var list = SeedList("Mayoristas");

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            DeleteHandler().HandleAsync(new DeletePriceListCommand(_tenant, list.Id.Value, 2), Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Single(_lists.Lists);
    }

    [Fact]
    public async Task ADeletedCategoryDisappearsFromTheDetailAndThePreview()
    {
        var kept = _catalog.AddCategory("Hogar");
        var deleted = Guid.CreateVersion7(); // guardada en la lista y borrada después del catálogo
        var list = SeedList("Mayoristas", PriceListVisibility.Categories, kept, deleted);
        _catalog.AddProduct("A-1", "Olla", kept, cop: 10000m);

        var detail = await GetHandler().HandleAsync(new GetPriceListQuery(_tenant, list.Id.Value), Ct);
        var preview = await new PreviewPriceListHandler(
                _context,
                new FakeTenantDefaultCurrency(),
                new FakeTenantPriceRounding(new BuildingBlocks.Domain.Pricing.PriceRounding(0, BuildingBlocks.Domain.Pricing.PriceRoundingMode.Nearest)),
                _catalog,
                new PreviewPriceListValidator())
            .HandleAsync(new PreviewPriceListCommand(_tenant, 10m, "Categories", [kept, deleted]), Ct);

        Assert.Equal([new PriceListCategoryDto(kept, "Hogar")], detail.Categories);
        Assert.Equal([kept], Assert.Single(_catalog.SampleCategoryIds)!);
        Assert.Equal("Olla", Assert.Single(preview.Items).Name);
    }

    [Fact]
    public async Task ListCarriesTheDistributorCountPerList()
    {
        var a = SeedList("A");
        var b = SeedList("B");
        _usage.Counts[a.Id] = 3;

        var response = await ListHandler().HandleAsync(new ListPriceListsQuery(_tenant), Ct);

        Assert.Equal([("A", 3), ("B", 0)], response.Items.Select(item => (item.Name, item.DistributorCount)));
        Assert.Equal(b.Id.Value, response.Items[1].Id);
    }

    // Ruling W4: la fila pinta «Creada 02/10/2026» y «2 categorías · Hogar, Cocina» sin otra llamada; categoryCount son las
    // que siguen existiendo, y los nombres de todas las listas salen de una sola lectura del catálogo.
    [Fact]
    public async Task ListCarriesCategoriesAndCreatedAt()
    {
        var home = _catalog.AddCategory("Hogar");
        var kitchen = _catalog.AddCategory("Cocina");
        var gone = Guid.CreateVersion7();
        SeedList("Zeta", PriceListVisibility.Categories, home, kitchen, gone);
        SeedList("Alfa", PriceListVisibility.AllActive);
        SeedList("Media", PriceListVisibility.Categories, kitchen);

        var response = await ListHandler().HandleAsync(new ListPriceListsQuery(_tenant), Ct);

        Assert.Equal(["Alfa", "Media", "Zeta"], response.Items.Select(item => item.Name));
        var zeta = response.Items[2];
        Assert.Equal(2, zeta.CategoryCount);
        Assert.Equal([new PriceListCategoryDto(kitchen, "Cocina"), new PriceListCategoryDto(home, "Hogar")], zeta.Categories);
        Assert.Empty(response.Items[0].Categories);
        Assert.Equal(0, response.Items[0].CategoryCount);
        Assert.All(response.Items, item => Assert.Equal(Now.AddDays(-1), item.CreatedAt));
        Assert.Equal(1, _catalog.FindCategoriesCalls);
    }

    [Fact]
    public async Task EveryWriteIsAudited()
    {
        var created = await CreateHandler().HandleAsync(
            new CreatePriceListCommand(_tenant, "Mayoristas", 15m, "AllActive", []), Ct);
        _clock.UtcNow = Now.AddHours(1);
        await UpdateHandler().HandleAsync(
            new UpdatePriceListCommand(_tenant, created.Id, 1, "Mayoristas", 20m, "AllActive", []), Ct);
        _clock.UtcNow = Now.AddHours(2);
        await DeleteHandler().HandleAsync(new DeletePriceListCommand(_tenant, created.Id, 2), Ct);

        Assert.Equal(
            ["wholesale.price_list.created", "wholesale.price_list.updated", "wholesale.price_list.deleted"],
            _audit.Entries.Select(entry => entry.Action));
        Assert.All(_audit.Entries, entry =>
            Assert.Equal((_tenant, _context.SubjectId, "price_list", created.Id.ToString(), "success"),
                (entry.TenantId, entry.ActorId, entry.ResourceType, entry.ResourceId, entry.Outcome)));
        Assert.Equal([Now, Now.AddHours(1), Now.AddHours(2)], _audit.Entries.Select(entry => entry.OccurredAt));
        Assert.Equal(["name", "discountPercent", "visibility", "categoryIds"], _audit.Entries[0].ChangedFields);
        Assert.Equal(3, _unitOfWork.Saves);
        Assert.Empty(_lists.Lists);
    }

    [Fact]
    public async Task AnotherTenantIsForbidden()
    {
        _context = new FakeExecutionContext(Guid.CreateVersion7(), WholesalePermissions.PriceListManage);
        var list = SeedList("Mayoristas");
        var callsBefore = _lists.Calls;

        var errors = new List<RequestForbiddenException>
        {
            await Assert.ThrowsAsync<RequestForbiddenException>(() => CreateHandler().HandleAsync(
                new CreatePriceListCommand(_tenant, "Otra", 15m, "AllActive", []), Ct)),
            await Assert.ThrowsAsync<RequestForbiddenException>(() => UpdateHandler().HandleAsync(
                new UpdatePriceListCommand(_tenant, list.Id.Value, 1, "Otra", 15m, "AllActive", []), Ct)),
            await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                DeleteHandler().HandleAsync(new DeletePriceListCommand(_tenant, list.Id.Value, 1), Ct)),
            await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                GetHandler().HandleAsync(new GetPriceListQuery(_tenant, list.Id.Value), Ct)),
            await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                ListHandler().HandleAsync(new ListPriceListsQuery(_tenant), Ct)),
            await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                new ListWholesaleCategoriesHandler(_context, _catalog).HandleAsync(new ListWholesaleCategoriesQuery(_tenant), Ct)),
        };

        Assert.All(errors, error => Assert.Equal("authorization.denied", error.Code));
        Assert.Equal(callsBefore, _lists.Calls);
        Assert.Equal(0, _catalog.FindCategoriesCalls);
    }

    [Fact]
    public async Task WithoutThePermissionIsForbidden()
    {
        _context = new FakeExecutionContext(_tenant, WholesalePermissions.DistributorManage);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            ListHandler().HandleAsync(new ListPriceListsQuery(_tenant), Ct));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(0, _lists.Calls);
    }

    // Ruling W5: el árbol plano de categorías para elegir la visibilidad, sin exigir catalog.product.read.
    [Fact]
    public async Task CategoriesAreTheFlatTreeOfTheTenant()
    {
        var home = _catalog.AddCategory("Hogar");
        var kitchen = _catalog.AddCategory("Cocina", home);

        var response = await new ListWholesaleCategoriesHandler(_context, _catalog)
            .HandleAsync(new ListWholesaleCategoriesQuery(_tenant), Ct);

        Assert.Equal(
            [new WholesaleCategoryDto(kitchen, "Cocina", home), new WholesaleCategoryDto(home, "Hogar", null)],
            response.Items);
    }
}
