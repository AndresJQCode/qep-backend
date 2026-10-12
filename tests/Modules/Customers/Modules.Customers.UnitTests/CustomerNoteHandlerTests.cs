using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.EngagementTestBed;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-11 §4.2: notas internas. Cualquiera que gestione clientes crea; el autor edita y borra las
/// suyas; <c>customers.note.moderate</c> borra cualquiera. <c>canEdit</c>/<c>canDelete</c> salen calculados.</summary>
public sealed class CustomerNoteHandlerTests
{
    private readonly FakeCustomers _customers = new();
    private readonly FakeNotes _notes = new();
    private readonly RecordingAudit _audit = new();
    private readonly CountingUnitOfWork _unitOfWork = new();
    private readonly FakeAuthorNames _names = new(new Dictionary<Guid, string> { [UserA] = "Ana Gómez", [UserB] = "Beto Ruiz" });
    private readonly Customer _customer = NewCustomer();

    public CustomerNoteHandlerTests()
    {
        _customers.Items.Add(_customer);
    }

    private CreateCustomerNoteHandler CreateHandler(Guid subject, params string[] permissions) =>
        new(_customers, _notes, _names, _unitOfWork, _audit, new Context(TenantId, subject, permissions), new FixedClock(),
            new CreateCustomerNoteValidator());

    private UpdateCustomerNoteHandler UpdateHandler(Guid subject, params string[] permissions) =>
        new(_customers, _notes, _names, _unitOfWork, _audit, new Context(TenantId, subject, permissions), new FixedClock(),
            new UpdateCustomerNoteValidator());

    private DeleteCustomerNoteHandler DeleteHandler(Guid subject, params string[] permissions) =>
        new(_customers, _notes, _unitOfWork, _audit, new Context(TenantId, subject, permissions), new FixedClock());

    private ListCustomerNotesHandler ListHandler(Guid subject, params string[] permissions) =>
        new(_customers, _notes, _names, new Context(TenantId, subject, permissions));

    private CustomerNote Seed(Guid author, string body = "Nota", DateTimeOffset? at = null)
    {
        var note = CustomerNote.Create(CustomerNoteId.New(), TenantId, _customer.Id, body, author, at ?? Now);
        _notes.Items.Add(note);
        return note;
    }

    [Fact]
    public async Task CreateStoresTheNoteWithItsAuthorAndAudits()
    {
        var dto = await CreateHandler(UserA, Manager).HandleAsync(
            new CreateCustomerNoteCommand(TenantId, _customer.Id.Value, "  Llamar el lunes "), TestContext.Current.CancellationToken);

        var stored = Assert.Single(_notes.Items);
        Assert.Equal("Llamar el lunes", stored.Body);
        Assert.Equal(UserA, stored.AuthorUserId);
        Assert.Equal(dto.Id, stored.Id.Value);
        Assert.Equal(new NoteAuthorDto(UserA, "Ana Gómez"), dto.Author);
        Assert.True(dto.CanEdit);
        Assert.True(dto.CanDelete);
        Assert.Equal(1, dto.Version);
        Assert.Equal(1, _unitOfWork.Saves);
        var recorded = Assert.Single(_audit.Events);
        Assert.Equal(("customers.note.created", stored.Id.ToString(), UserA, TenantId), (recorded.Action, recorded.ResourceId, recorded.Actor, recorded.Tenant));
    }

    [Fact]
    public async Task CreateFromAnotherTenantIsForbiddenBeforeAnythingElse()
    {
        var handler = new CreateCustomerNoteHandler(
            _customers, _notes, _names, _unitOfWork, _audit, new Context(OtherTenantId, UserA, Manager), new FixedClock(),
            new CreateCustomerNoteValidator());

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => handler.HandleAsync(
            new CreateCustomerNoteCommand(TenantId, _customer.Id.Value, string.Empty), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(_notes.Items);
    }

    [Fact]
    public async Task CreateWithoutManageIsForbidden()
    {
        await Assert.ThrowsAsync<RequestForbiddenException>(() => CreateHandler(UserA, CustomersPermissions.CustomerRead).HandleAsync(
            new CreateCustomerNoteCommand(TenantId, _customer.Id.Value, "Hola"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateForAMissingCustomerIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => CreateHandler(UserA, Manager).HandleAsync(
            new CreateCustomerNoteCommand(TenantId, Guid.NewGuid(), "Hola"), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.not_found", error.Code);
    }

    [Fact]
    public async Task CreateWithABlankBodyFailsValidationOnTheBodyField()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => CreateHandler(UserA, Manager).HandleAsync(
            new CreateCustomerNoteCommand(TenantId, _customer.Id.Value, "   "), TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "Body");
    }

    [Fact]
    public async Task CreateWithATooLongBodyFailsValidation()
    {
        await Assert.ThrowsAsync<ValidationException>(() => CreateHandler(UserA, Manager).HandleAsync(
            new CreateCustomerNoteCommand(TenantId, _customer.Id.Value, new string('a', CustomerNote.BodyMaxLength + 1)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListReturnsNewestFirstWithPerNotePermissions()
    {
        var oldMine = Seed(UserA, "vieja", Now.AddDays(-2));
        var newTheirs = Seed(UserB, "nueva", Now);

        var list = await ListHandler(UserA, Manager).HandleAsync(
            new ListCustomerNotesQuery(TenantId, _customer.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal([newTheirs.Id.Value, oldMine.Id.Value], list.Select(note => note.Id));
        Assert.Equal("Beto Ruiz", list[0].Author.DisplayName);
        Assert.False(list[0].CanEdit);
        Assert.False(list[0].CanDelete);
        Assert.True(list[1].CanEdit);
        Assert.True(list[1].CanDelete);
    }

    [Fact]
    public async Task AModeratorCanDeleteAnyNoteButStillCannotEditOthers()
    {
        Seed(UserB);

        var list = await ListHandler(UserA, Moderator).HandleAsync(
            new ListCustomerNotesQuery(TenantId, _customer.Id.Value), TestContext.Current.CancellationToken);

        Assert.False(list.Single().CanEdit);
        Assert.True(list.Single().CanDelete);
    }

    [Fact]
    public async Task ReadOnlyUsersSeeNotesButCannotEditOrDeleteEvenTheirOwn()
    {
        Seed(UserA);

        var list = await ListHandler(UserA, CustomersPermissions.CustomerRead).HandleAsync(
            new ListCustomerNotesQuery(TenantId, _customer.Id.Value), TestContext.Current.CancellationToken);

        Assert.False(list.Single().CanEdit);
        Assert.False(list.Single().CanDelete);
    }

    [Fact]
    public async Task AnAuthorThatNoLongerExistsShowsAsADeletedUser()
    {
        var ghost = Guid.NewGuid();
        Seed(ghost);

        var list = await ListHandler(UserA, Manager).HandleAsync(
            new ListCustomerNotesQuery(TenantId, _customer.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(new NoteAuthorDto(ghost, "Usuario eliminado"), list.Single().Author);
    }

    [Fact]
    public async Task ListFromAnotherTenantIsForbidden()
    {
        var handler = new ListCustomerNotesHandler(_customers, _notes, _names, new Context(OtherTenantId, UserA, Manager));

        await Assert.ThrowsAsync<RequestForbiddenException>(() => handler.HandleAsync(
            new ListCustomerNotesQuery(TenantId, _customer.Id.Value), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheAuthorEditsTheirNoteAndTheVersionAdvances()
    {
        var note = Seed(UserA, "uno");

        var dto = await UpdateHandler(UserA, Manager).HandleAsync(
            new UpdateCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value, "dos", 1), TestContext.Current.CancellationToken);

        Assert.Equal("dos", dto.Body);
        Assert.Equal(2, dto.Version);
        Assert.Equal(1, _unitOfWork.Saves);
        Assert.Equal("customers.note.updated", Assert.Single(_audit.Events).Action);
    }

    [Fact]
    public async Task EditingSomeoneElsesNoteIs403NotAuthor()
    {
        var note = Seed(UserB);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => UpdateHandler(UserA, Moderator).HandleAsync(
            new UpdateCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value, "dos", 1), TestContext.Current.CancellationToken));

        Assert.Equal("customers.note.not_author", error.Code);
        Assert.Equal("nota", note.Body.ToLowerInvariant());
    }

    [Fact]
    public async Task EditingWithAStaleVersionIsAConcurrencyConflict()
    {
        var note = Seed(UserA);

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => UpdateHandler(UserA, Manager).HandleAsync(
            new UpdateCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value, "dos", 7), TestContext.Current.CancellationToken));

        Assert.Equal("Nota", note.Body);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task EditingAnUnknownNoteIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => UpdateHandler(UserA, Manager).HandleAsync(
            new UpdateCustomerNoteCommand(TenantId, _customer.Id.Value, Guid.NewGuid(), "dos", 1), TestContext.Current.CancellationToken));

        Assert.Equal("customers.note.not_found", error.Code);
    }

    [Fact]
    public async Task ANoteOfAnotherCustomerIsNotFoundThroughThisOne()
    {
        var note = Seed(UserA);
        var other = NewCustomer();
        _customers.Items.Add(other);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => UpdateHandler(UserA, Manager).HandleAsync(
            new UpdateCustomerNoteCommand(TenantId, other.Id.Value, note.Id.Value, "dos", 1), TestContext.Current.CancellationToken));

        Assert.Equal("customers.note.not_found", error.Code);
    }

    [Fact]
    public async Task EditingWithAStaleOrZeroVersionFailsValidationWhenItIsNotPositive()
    {
        var note = Seed(UserA);

        await Assert.ThrowsAsync<ValidationException>(() => UpdateHandler(UserA, Manager).HandleAsync(
            new UpdateCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value, "dos", 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheAuthorWithManageDeletesTheirNote()
    {
        var note = Seed(UserA);

        await DeleteHandler(UserA, Manager).HandleAsync(
            new DeleteCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value), TestContext.Current.CancellationToken);

        Assert.Empty(_notes.Items);
        Assert.Equal("customers.note.deleted", Assert.Single(_audit.Events).Action);
    }

    [Fact]
    public async Task ANonAuthorWithoutModerationCannotDelete()
    {
        var note = Seed(UserB);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => DeleteHandler(UserA, Manager).HandleAsync(
            new DeleteCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("customers.note.not_author", error.Code);
        Assert.Single(_notes.Items);
    }

    [Fact]
    public async Task AModeratorDeletesSomeoneElsesNote()
    {
        var note = Seed(UserB);

        await DeleteHandler(UserA, Moderator).HandleAsync(
            new DeleteCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value), TestContext.Current.CancellationToken);

        Assert.Empty(_notes.Items);
    }

    [Fact]
    public async Task AReadOnlyAuthorCannotDeleteTheirOwnNote()
    {
        var note = Seed(UserA);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => DeleteHandler(UserA, CustomersPermissions.CustomerRead).HandleAsync(
            new DeleteCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingFromAnotherTenantIsForbidden()
    {
        var note = Seed(UserA);
        var handler = new DeleteCustomerNoteHandler(
            _customers, _notes, _unitOfWork, _audit, new Context(OtherTenantId, UserA, Moderator), new FixedClock());

        await Assert.ThrowsAsync<RequestForbiddenException>(() => handler.HandleAsync(
            new DeleteCustomerNoteCommand(TenantId, _customer.Id.Value, note.Id.Value), TestContext.Current.CancellationToken));

        Assert.Single(_notes.Items);
    }
}
