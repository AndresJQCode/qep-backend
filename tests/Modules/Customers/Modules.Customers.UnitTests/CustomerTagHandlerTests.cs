using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.EngagementTestBed;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-11 §4.2: el catálogo de etiquetas (quien gestiona clientes crea; renombrar y borrar es de
/// <c>customers.tag.manage</c>) y el conjunto de etiquetas de un cliente.</summary>
public sealed class CustomerTagHandlerTests
{
    private readonly FakeCustomers _customers = new();
    private readonly FakeTags _tags = new();
    private readonly RecordingAudit _audit = new();
    private readonly CountingUnitOfWork _unitOfWork = new();
    private readonly Customer _customer = NewCustomer();

    public CustomerTagHandlerTests()
    {
        _customers.Items.Add(_customer);
    }

    private CreateTagHandler CreateHandler(params string[] permissions) =>
        new(_tags, _unitOfWork, _audit, new Context(TenantId, UserA, permissions), new FixedClock(), new CreateTagValidator());

    private UpdateTagHandler UpdateHandler(params string[] permissions) =>
        new(_tags, _unitOfWork, _audit, new Context(TenantId, UserA, permissions), new FixedClock(), new UpdateTagValidator());

    private DeleteTagHandler DeleteHandler(params string[] permissions) =>
        new(_tags, _unitOfWork, _audit, new Context(TenantId, UserA, permissions), new FixedClock());

    private readonly FakeCustomerLocks _locks = new();

    private SetCustomerTagsHandler SetHandler(params string[] permissions) =>
        new(_customers, _locks, _tags, _unitOfWork, _audit, new Context(TenantId, UserA, permissions), new FixedClock(), new SetCustomerTagsValidator());

    /// <summary>The customer row lock the chatbot takes (<see cref="ICustomerDataConsentRepository.LockCustomerAsync"/>).
    /// <see cref="OnLock"/> runs when it is granted: what a chatbot change that held the lock left behind.</summary>
    private sealed class FakeCustomerLocks : ICustomerDataConsentRepository
    {
        public List<Guid> Locked { get; } = [];

        public Action? OnLock { get; set; }

        public Task<Guid?> LockCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
        {
            Locked.Add(customerId);
            OnLock?.Invoke();
            return Task.FromResult<Guid?>(customerId);
        }

        public Task<IReadOnlyList<CustomerDataConsent>> ListAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerDataConsent?> FindLatestAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ReplyRecordedAsync(Guid tenantId, Guid replyMessageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(CustomerDataConsent consent) => throw new NotSupportedException();
    }

    private Tag Seed(string name, TagColor color = TagColor.Blue)
    {
        var tag = Tag.Create(TagId.New(), TenantId, name, color, Now);
        _tags.Tags.Add(tag);
        return tag;
    }

    [Fact]
    public async Task AManagerCreatesATagAndItIsAudited()
    {
        var dto = await CreateHandler(Manager).HandleAsync(
            new CreateTagCommand(TenantId, "  Mayoristas ", "green"), TestContext.Current.CancellationToken);

        Assert.Equal("Mayoristas", dto.Name);
        Assert.Equal("green", dto.Color);
        Assert.Equal(0, dto.CustomerCount);
        Assert.Equal(1, dto.Version);
        Assert.Equal(1, _unitOfWork.Saves);
        var recorded = Assert.Single(_audit.Events);
        Assert.Equal(("customers.tag.created", dto.Id.ToString()), (recorded.Action, recorded.ResourceId));
    }

    [Fact]
    public async Task CreatingFromAnotherTenantIsForbidden()
    {
        var handler = new CreateTagHandler(
            _tags, _unitOfWork, _audit, new Context(OtherTenantId, UserA, Manager), new FixedClock(), new CreateTagValidator());

        await Assert.ThrowsAsync<RequestForbiddenException>(() => handler.HandleAsync(
            new CreateTagCommand(TenantId, "VIP", "red"), TestContext.Current.CancellationToken));
        Assert.Empty(_tags.Tags);
    }

    [Theory]
    [InlineData("", "red", "Name")]
    [InlineData("   ", "red", "Name")]
    [InlineData("VIP", "magenta", "Color")]
    [InlineData("VIP", "", "Color")]
    public async Task CreatingWithBadTextFailsValidationOnTheRightField(string name, string color, string field)
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => CreateHandler(Manager).HandleAsync(
            new CreateTagCommand(TenantId, name, color), TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == field);
    }

    [Fact]
    public async Task ANameOverFortyCharactersFailsValidation()
    {
        await Assert.ThrowsAsync<ValidationException>(() => CreateHandler(Manager).HandleAsync(
            new CreateTagCommand(TenantId, new string('a', Tag.NameMaxLength + 1), "red"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheHundredAndFirstTagIsRejectedWith422()
    {
        for (var index = 0; index < Tag.MaxPerTenant; index++)
        {
            Seed($"Etiqueta {index}");
        }

        var error = await Assert.ThrowsAsync<CustomersDomainException>(() => CreateHandler(Manager).HandleAsync(
            new CreateTagCommand(TenantId, "Una más", "red"), TestContext.Current.CancellationToken));

        Assert.Equal("customers.tag.limit_reached", error.Code);
    }

    [Fact]
    public async Task ListReturnsTagsByNameWithTheirCustomerCount()
    {
        var vip = Seed("vip");
        var alpha = Seed("Alfa");
        _tags.Assignments.Add(CustomerTagAssignment.Create(_customer.Id, vip.Id, TenantId, UserA, Now));
        _tags.Tags.Add(Tag.Create(TagId.New(), OtherTenantId, "Ajena", TagColor.Red, Now));

        var list = await new ListTagsHandler(_tags, new Context(TenantId, UserA, CustomersPermissions.CustomerRead))
            .HandleAsync(new ListTagsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(["Alfa", "vip"], list.Select(tag => tag.Name));
        Assert.Equal([0, 1], list.Select(tag => tag.CustomerCount));
        Assert.Equal(alpha.Id.Value, list[0].Id);
    }

    [Fact]
    public async Task ListFromAnotherTenantIsForbidden()
    {
        var handler = new ListTagsHandler(_tags, new Context(OtherTenantId, UserA, CustomersPermissions.CustomerRead));

        await Assert.ThrowsAsync<RequestForbiddenException>(() => handler.HandleAsync(
            new ListTagsQuery(TenantId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenamingNeedsTheTagManagePermission()
    {
        var tag = Seed("VIP");

        await Assert.ThrowsAsync<RequestForbiddenException>(() => UpdateHandler(Manager).HandleAsync(
            new UpdateTagCommand(TenantId, tag.Id.Value, "Nuevo", "red", 1), TestContext.Current.CancellationToken));
        Assert.Equal("VIP", tag.Name);
    }

    [Fact]
    public async Task AnAdminRenamesAndRecolorsAndTheVersionAdvances()
    {
        var tag = Seed("VIP");
        _tags.Assignments.Add(CustomerTagAssignment.Create(_customer.Id, tag.Id, TenantId, UserA, Now));

        var dto = await UpdateHandler(TagAdmin).HandleAsync(
            new UpdateTagCommand(TenantId, tag.Id.Value, "Clientes VIP", "purple", 1), TestContext.Current.CancellationToken);

        Assert.Equal(new TagDto(tag.Id.Value, "Clientes VIP", "purple", 1, 2), dto);
        Assert.Equal("customers.tag.updated", Assert.Single(_audit.Events).Action);
    }

    [Fact]
    public async Task RenamingWithAStaleVersionIsAConcurrencyConflict()
    {
        var tag = Seed("VIP");

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => UpdateHandler(TagAdmin).HandleAsync(
            new UpdateTagCommand(TenantId, tag.Id.Value, "Otro", "red", 9), TestContext.Current.CancellationToken));
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task RenamingAnUnknownTagIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => UpdateHandler(TagAdmin).HandleAsync(
            new UpdateTagCommand(TenantId, Guid.NewGuid(), "Otro", "red", 1), TestContext.Current.CancellationToken));

        Assert.Equal("customers.tag.not_found", error.Code);
    }

    [Fact]
    public async Task DeletingNeedsTheTagManagePermissionAndTakesItsAssignmentsWithIt()
    {
        var tag = Seed("VIP");
        _tags.Assignments.Add(CustomerTagAssignment.Create(_customer.Id, tag.Id, TenantId, UserA, Now));

        await Assert.ThrowsAsync<RequestForbiddenException>(() => DeleteHandler(Manager).HandleAsync(
            new DeleteTagCommand(TenantId, tag.Id.Value), TestContext.Current.CancellationToken));

        await DeleteHandler(TagAdmin).HandleAsync(new DeleteTagCommand(TenantId, tag.Id.Value), TestContext.Current.CancellationToken);

        Assert.Empty(_tags.Tags);
        Assert.Empty(_tags.Assignments);
        Assert.Equal("customers.tag.deleted", Assert.Single(_audit.Events).Action);
    }

    [Fact]
    public async Task DeletingAnUnknownTagIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => DeleteHandler(TagAdmin).HandleAsync(
            new DeleteTagCommand(TenantId, Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Equal("customers.tag.not_found", error.Code);
    }

    [Fact]
    public async Task SettingTheTagsReplacesTheWholeSetAndReturnsItByName()
    {
        var keep = Seed("Zeta");
        var drop = Seed("Beta");
        var add = Seed("Alfa");
        _tags.Assignments.Add(CustomerTagAssignment.Create(_customer.Id, keep.Id, TenantId, UserB, Now.AddDays(-1)));
        _tags.Assignments.Add(CustomerTagAssignment.Create(_customer.Id, drop.Id, TenantId, UserB, Now.AddDays(-1)));

        var result = await SetHandler(Manager).HandleAsync(
            new SetCustomerTagsCommand(TenantId, _customer.Id.Value, [keep.Id.Value, add.Id.Value]), TestContext.Current.CancellationToken);

        Assert.Equal(["Alfa", "Zeta"], result.Select(tag => tag.Name));
        Assert.Equal(
            new HashSet<TagId> { add.Id, keep.Id },
            _tags.Assignments.Select(assignment => assignment.TagId).ToHashSet());
        // La que ya estaba conserva quién la puso y cuándo; la nueva lleva al usuario actual.
        Assert.Equal(UserB, _tags.Assignments.Single(assignment => assignment.TagId == keep.Id).AssignedByUserId);
        Assert.Equal(UserA, _tags.Assignments.Single(assignment => assignment.TagId == add.Id).AssignedByUserId);
        var recorded = Assert.Single(_audit.Events);
        Assert.Equal(("customers.customer.tags_changed", _customer.Id.ToString()), (recorded.Action, recorded.ResourceId));
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task SettingTheTagsLocksTheCustomerAndReadsTheSetUnderTheLock()
    {
        var requested = Enumerable.Range(0, CustomerTagAssignment.MaxPerCustomer).Select(index => Seed($"Etiqueta {index}")).ToArray();
        var byTheBot = Seed("Del bot");
        // A chatbot change that held the row lock commits right before the screen gets it: the screen has to see it.
        _locks.OnLock = () => _tags.Assignments.Add(
            CustomerTagAssignment.Create(_customer.Id, byTheBot.Id, TenantId, Guid.Empty, Now));

        await SetHandler(Manager).HandleAsync(
            new SetCustomerTagsCommand(TenantId, _customer.Id.Value, [.. requested.Select(tag => tag.Id.Value)]),
            TestContext.Current.CancellationToken);

        Assert.Equal([_customer.Id.Value], _locks.Locked);
        Assert.Equal(CustomerTagAssignment.MaxPerCustomer, _tags.Assignments.Count);
        Assert.DoesNotContain(_tags.Assignments, assignment => assignment.TagId == byTheBot.Id);
    }

    [Fact]
    public async Task SettingAnEmptyListClearsTheTags()
    {
        var tag = Seed("VIP");
        _tags.Assignments.Add(CustomerTagAssignment.Create(_customer.Id, tag.Id, TenantId, UserA, Now));

        var result = await SetHandler(Manager).HandleAsync(
            new SetCustomerTagsCommand(TenantId, _customer.Id.Value, []), TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Empty(_tags.Assignments);
    }

    [Fact]
    public async Task SettingATagFromAnotherTenantIs404()
    {
        var foreign = Tag.Create(TagId.New(), OtherTenantId, "Ajena", TagColor.Red, Now);
        _tags.Tags.Add(foreign);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => SetHandler(Manager).HandleAsync(
            new SetCustomerTagsCommand(TenantId, _customer.Id.Value, [foreign.Id.Value]), TestContext.Current.CancellationToken));

        Assert.Equal("customers.tag.not_found", error.Code);
        Assert.Empty(_tags.Assignments);
    }

    [Fact]
    public async Task SettingMoreThanTwentyTagsFailsValidation()
    {
        var ids = Enumerable.Range(0, CustomerTagAssignment.MaxPerCustomer + 1).Select(_ => Guid.NewGuid()).ToArray();

        var error = await Assert.ThrowsAsync<ValidationException>(() => SetHandler(Manager).HandleAsync(
            new SetCustomerTagsCommand(TenantId, _customer.Id.Value, ids), TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "TagIds");
    }

    [Fact]
    public async Task SettingARepeatedTagFailsValidation()
    {
        var tag = Seed("VIP");

        await Assert.ThrowsAsync<ValidationException>(() => SetHandler(Manager).HandleAsync(
            new SetCustomerTagsCommand(TenantId, _customer.Id.Value, [tag.Id.Value, tag.Id.Value]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SettingTagsOfAMissingCustomerIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => SetHandler(Manager).HandleAsync(
            new SetCustomerTagsCommand(TenantId, Guid.NewGuid(), []), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.not_found", error.Code);
    }

    [Fact]
    public async Task SettingTagsFromAnotherTenantIsForbidden()
    {
        var handler = new SetCustomerTagsHandler(
            _customers, _locks, _tags, _unitOfWork, _audit, new Context(OtherTenantId, UserA, Manager), new FixedClock(), new SetCustomerTagsValidator());

        await Assert.ThrowsAsync<RequestForbiddenException>(() => handler.HandleAsync(
            new SetCustomerTagsCommand(TenantId, _customer.Id.Value, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListingACustomersTagsNeedsOnlyRead()
    {
        var tag = Seed("VIP");
        _tags.Assignments.Add(CustomerTagAssignment.Create(_customer.Id, tag.Id, TenantId, UserA, Now));

        var list = await new ListCustomerTagsHandler(_customers, _tags, new Context(TenantId, UserA, CustomersPermissions.CustomerRead))
            .HandleAsync(new ListCustomerTagsQuery(TenantId, _customer.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal("VIP", Assert.Single(list).Name);
    }
}
