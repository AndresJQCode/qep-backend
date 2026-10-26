using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.7: el término de búsqueda por número y la foto del último mensaje.</summary>
public sealed class ConversationReadsTests
{
    // Revisión de la Task 14: un dígito suelto («Laura 2», «Calle 10») no es un número; si lo fuera,
    // wa_id LIKE '%2%' traería casi todas las conversaciones.
    [Theory]
    [InlineData("9999", "9999")]
    [InlineData("300 123 4567", "3001234567")]
    [InlineData("+57 300-123", "57300123")]
    [InlineData("a1234", "1234")]
    [InlineData("Laura 2", null)]
    [InlineData("Calle 10", null)]
    [InlineData("12", null)]
    [InlineData("a123", null)]
    [InlineData("lau", null)]
    public void OnlyATermThatLooksLikeANumberSearchesByNumber(string term, string? expected) =>
        Assert.Equal(expected, ConversationSearchTerms.NumberDigits(term));

    [Fact]
    public void APartialLastMessageSnapshotIsNullNotAnError()
    {
        var row = new ConversationRow(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), UserId: null, WaId: "573001234567", Username: null, "Laura",
            CustomerId: null, AssignedMemberId: null, ConversationStatus.Open, 1,
            null, Guid.CreateVersion7(), LastMessageDirection: null, MessageKind.Text, "hola", MessageStatus.Delivered,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

        var summary = ConversationSummaryBuilder.ToSummary(
            row, new Dictionary<Guid, string>(), new Dictionary<Guid, CustomerRefDto>(), new Dictionary<string, CustomerRefDto>(), new Dictionary<Guid, string>(), null);

        Assert.Null(summary.LastMessage);
    }

    // Spec 2026-10-26 §3.5: quién y cuándo bloqueó viaja en el resumen; la fila de la bandeja pinta el candado sin otra llamada.
    private static ConversationRow BlockedRow(DateTimeOffset? at, Guid? by) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), UserId: null, WaId: "573001234567", Username: null, "Laura",
        CustomerId: null, AssignedMemberId: null, ConversationStatus.Open, 0,
        null, null, null, null, null, null, null,
        DateTimeOffset.UnixEpoch, 1, BlockedAt: at, BlockedByMemberId: by);

    private static ConversationSummary SummaryOf(ConversationRow row, IReadOnlyDictionary<Guid, string> memberNames) =>
        ConversationSummaryBuilder.ToSummary(
            row, new Dictionary<Guid, string>(), new Dictionary<Guid, CustomerRefDto>(), new Dictionary<string, CustomerRefDto>(), memberNames, null);

    [Fact]
    public void ABlockedRowCarriesWhenAndWhoBlocked()
    {
        var at = DateTimeOffset.UnixEpoch.AddDays(3);
        var member = Guid.CreateVersion7();

        var summary = SummaryOf(BlockedRow(at, member), new Dictionary<Guid, string> { [member] = "Beatriz" });

        Assert.Equal(new ConversationBlockDto(at, "Beatriz"), summary.Blocked);
    }

    [Fact]
    public void ABlockerWhoLeftShowsAsDeletedMember()
    {
        var summary = SummaryOf(BlockedRow(DateTimeOffset.UnixEpoch, Guid.CreateVersion7()), new Dictionary<Guid, string>());

        Assert.Equal("Miembro eliminado", summary.Blocked?.BlockedByName);
    }

    [Fact]
    public void AnUnblockedRowCarriesNull() =>
        Assert.Null(SummaryOf(BlockedRow(null, null), new Dictionary<Guid, string>()).Blocked);

    [Fact]
    public async Task BuildAsyncResolvesTheBlockerInTheSameNamesQuery()
    {
        var member = Guid.CreateVersion7();
        var names = new RecordingMemberNames(member);
        var builder = new ConversationSummaryBuilder(
            new FakeConnectionDirectory(), new FakeCustomerDirectory(), names, new FakeResolutionReasonNames(),
            new CallerMembership(new FakeMembershipDirectory(), new FakeExecutionContext(Tenant, Guid.CreateVersion7())), new FakeChatbotNames());

        var summaries = await builder.BuildAsync(Tenant, [BlockedRow(DateTimeOffset.UnixEpoch, member)], TestContext.Current.CancellationToken);

        Assert.Equal([[member]], names.Calls);
        Assert.Equal("Beatriz", summaries[0].Blocked?.BlockedByName);
    }

    private sealed class RecordingMemberNames(Guid memberId) : IMessagingMemberNames
    {
        public List<Guid[]> Calls { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
        {
            Calls.Add([.. memberIds]);
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string> { [memberId] = "Beatriz" });
        }

        public Task<string?> FindDisplayNameAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static ConversationSummaryBuilder BuilderFor(FakeCustomerDirectory customers)
    {
        var context = new FakeExecutionContext(Tenant, Guid.CreateVersion7());
        return new ConversationSummaryBuilder(
            new FakeConnectionDirectory(), customers, new FakeMemberNames(Guid.CreateVersion7()), new FakeResolutionReasonNames(),
            new CallerMembership(new FakeMembershipDirectory(), context), new FakeChatbotNames());
    }

    private static ConversationRow RowOf(Guid? customerId, string? waId, ConversationChannel channel = ConversationChannel.WhatsApp) => new(
        Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), UserId: "CO.1", waId, Username: null, "Laura",
        customerId, AssignedMemberId: null, ConversationStatus.Open, 0,
        null, null, null, null, null, null, null,
        DateTimeOffset.UnixEpoch, 1, channel);

    // Spec 2026-10-24 §4.1: los otros clientes del número, sin el que la conversación ya muestra.
    [Fact]
    public async Task BuildOneListsTheOthersOfTheNumberWithoutTheConversationsCustomer()
    {
        var customers = new FakeCustomerDirectory();
        var own = customers.Add("Luis Alberto");
        var other = new CustomerRefDto(Guid.CreateVersion7(), "Ziba Tienda", true);
        customers.ByPhone.AddRange([new CustomerRefDto(own.CustomerId, own.Name, true), other]);

        var summary = await BuilderFor(customers).BuildOneAsync(Tenant, RowOf(own.CustomerId, "573001234567"), TestContext.Current.CancellationToken);

        Assert.Equal(own.CustomerId, summary.Customer?.Id);
        Assert.Equal([other.Id], summary.SharedPhoneCustomers.Select(customer => customer.Id));
        Assert.Equal(["573001234567"], customers.ListByPhoneCalls);
    }

    // Review Focus 2: la fila vieja sin customer_id muestra al cliente que sale por teléfono; ése tampoco se lista.
    [Fact]
    public async Task ALegacyConversationDoesNotListItsOwnCustomerAsShared()
    {
        var customers = new FakeCustomerDirectory();
        var matched = new CustomerRefDto(Guid.CreateVersion7(), "Luis Alberto", true);
        var other = new CustomerRefDto(Guid.CreateVersion7(), "Ziba Tienda", true);
        customers.ByWaId["573001234567"] = matched;
        customers.ByPhone.AddRange([matched, other]);

        var summary = await BuilderFor(customers).BuildOneAsync(Tenant, RowOf(customerId: null, "573001234567"), TestContext.Current.CancellationToken);

        Assert.Equal(matched.Id, summary.Customer?.Id);
        Assert.Equal([other.Id], summary.SharedPhoneCustomers.Select(customer => customer.Id));
    }

    [Fact]
    public async Task InstagramAndConversationsWithoutWaIdNeverAsk()
    {
        var customers = new FakeCustomerDirectory();
        customers.ByPhone.Add(new CustomerRefDto(Guid.CreateVersion7(), "Ziba Tienda", true));
        var builder = BuilderFor(customers);

        var instagram = await builder.BuildOneAsync(Tenant, RowOf(null, "573001234567", ConversationChannel.Instagram), TestContext.Current.CancellationToken);
        var withoutWaId = await builder.BuildOneAsync(Tenant, RowOf(null, waId: null), TestContext.Current.CancellationToken);

        Assert.Empty(instagram.SharedPhoneCustomers);
        Assert.Empty(withoutWaId.SharedPhoneCustomers);
        Assert.Empty(customers.ListByPhoneCalls);
    }

    [Fact]
    public async Task TheListIsCutAtTen()
    {
        var customers = new FakeCustomerDirectory();
        customers.ByPhone.AddRange(Enumerable.Range(1, 12).Select(n => new CustomerRefDto(Guid.CreateVersion7(), $"Cliente {n}", true)));

        var summary = await BuilderFor(customers).BuildOneAsync(Tenant, RowOf(null, "573001234567"), TestContext.Current.CancellationToken);

        Assert.Equal(ConversationSummaryBuilder.SharedPhoneCustomersCap, summary.SharedPhoneCustomers.Count);
        Assert.Equal(customers.ByPhone.Take(10).Select(customer => customer.Id), summary.SharedPhoneCustomers.Select(customer => customer.Id));
    }
}
