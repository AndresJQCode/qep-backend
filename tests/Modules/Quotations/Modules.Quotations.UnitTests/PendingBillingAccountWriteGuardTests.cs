using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.UnitTests.TestWithholdings;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// A pending billing account (spec 2026-11-16 withholdings by company) is a preview-only value: the company without its
/// bank account. EF ignores <see cref="QuotationBillingAccount.IsPending"/>, so if one ever reached a save it would be
/// written as a real account with an empty bank and number, and read back as complete. The unit of work refuses it before
/// touching the database; these tests need no database because the guard runs first (the connection string points to a
/// closed port, so without the guard the save fails with a connection error instead).
/// </summary>
public sealed class PendingBillingAccountWriteGuardTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount Pending = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = string.Empty,
        AccountNumber = string.Empty,
        Currency = "COP",
        Country = "CO",
        IsPending = true,
    };

    // Never a real database: port 1 is closed, so a save that slips past the guard fails fast and differently.
    private static QuotationsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1")
            .Options);

    private static Quotation NewQuotation(QuotationBillingAccount? account) =>
        Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", Guid.CreateVersion7(), AdvisorId, new DateOnly(2026, 12, 31), null, null,
            QuotationParties.Empty, account, "COP", customerVatSurplus: false, [RetefuenteCo],
            MunicipalContext.None, AdvisorId, Now);

    private static Order NewOrder(QuotationBillingAccount account) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), AdvisorId, "COP", null, IsRetail: false, null, CustomerVatSurplus: false, [RetefuenteCo]),
            QuotationParties.Empty, account, ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19,
                new QuotationProductSnapshot("ABC", "Aceite"))],
            null, AdvisorId, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    [Fact]
    public async Task SavingAQuotationWithAPendingAccountIsRefusedBeforeTheDatabase()
    {
        await using var context = NewContext();
        context.Add(NewQuotation(Pending));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("pending billing account", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingAnOrderWithAPendingAccountIsRefusedBeforeTheDatabase()
    {
        await using var context = NewContext();
        context.Add(NewOrder(Pending));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("pending billing account", exception.Message, StringComparison.Ordinal);
    }

    // The synchronous overload is a separate entry point into EF; the guard has to sit on both.
    [Fact]
    public void SavingSynchronouslyIsRefusedToo()
    {
        using var context = NewContext();
        context.Add(NewOrder(Pending));

        var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        Assert.Contains("pending billing account", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fence on the other side: write commands never carry a company without an account, so the resolver's pending
    /// branch is unreachable from them. Only the previews (queries) declare <c>BillingCompanyId</c>; the interfaces'
    /// default member answers null for everything else.
    /// </summary>
    [Fact]
    public void NoWriteCommandDeclaresABillingCompanyId()
    {
        var bodies = new[] { typeof(IQuotationEdits), typeof(IOrderEdits), typeof(IOrderDraft) };
        var commands = typeof(IQuotationEdits).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.GetInterfaces().Any(contract =>
                    contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICommand<>))
                && bodies.Any(body => body.IsAssignableFrom(type)))
            .ToArray();

        // Not vacuous: CreateQuotation, SaveQuotation, CreateOrder and SaveOrderEdits at least.
        Assert.True(commands.Length >= 4, $"Found only {commands.Length} write commands.");
        Assert.DoesNotContain(commands, command => command.GetProperty("BillingCompanyId") is not null);
    }
}
