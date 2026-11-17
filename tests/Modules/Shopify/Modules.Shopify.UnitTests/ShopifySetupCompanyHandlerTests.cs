using BuildingBlocks.Application;
using FluentValidation;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.StoreHandlersWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.2 y §7, plan Tarea 10: <c>POST …/setup/company</c>. Sin <c>companyId</c> crea la empresa por
/// Companies y la asigna; con él sólo asigna. La versión se revisa antes de crear nada, y si la asignación pierde después de crear,
/// el 412 trae la empresa creada (Review Focus 1).
/// </summary>
public sealed class ShopifySetupCompanyHandlerTests
{
    private static readonly ShopifyShopProfile CopProfile = new(null, null, "COP", true, null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AssignShopifySetupCompanyCommand Create(
        long version = 1, bool? chargesVat = false, ShopifySetupBankAccountInput? bankAccount = null) =>
        new(
            TenantId, ConnectionId, null, "Mi Tienda SAS", "901.234.567-8", "NIT", "CO", Guid.CreateVersion7(), null,
            "Cra 1 # 2-3", "3001112233", "ventas@mitienda.co", chargesVat, bankAccount, version);

    private static AssignShopifySetupCompanyCommand Assign(
        Guid companyId, long version = 1, ShopifySetupBankAccountInput? bankAccount = null) =>
        new(TenantId, ConnectionId, companyId, null, null, null, null, null, null, null, null, null, null, bankAccount, version);

    private static Task<ShopifySetupCompanyResultDto> SendAsync(SetupBed bed, AssignShopifySetupCompanyCommand command) =>
        new AssignShopifySetupCompanyHandler(
                bed.World.Context,
                new ShopifyEnabledModules(),
                bed.World.Connections,
                bed.World.Stores,
                bed.Companies,
                bed.World.Billing,
                bed.World.Memberships,
                bed.World.UnitOfWork,
                new FakeAuditRecorder(bed.World.Database),
                bed.World.Clock,
                new AssignShopifySetupCompanyValidator())
            .HandleAsync(command, Ct);

    [Fact]
    public async Task WithoutCompanyIdItCreatesAndAssigns()
    {
        var bed = new SetupBed(profile: CopProfile);

        var result = await SendAsync(bed, Create());

        var draft = Assert.Single(bed.Companies.Created);
        Assert.False(draft.ChargesVat);
        Assert.Equal(("Mi Tienda SAS", "901.234.567-8", "CO"), (draft.Name, draft.TaxId, draft.Country));
        var store = bed.World.Database.Stores[ConnectionId];
        Assert.Equal(bed.Companies.NextId, store.Billing!.CompanyId);
        Assert.True(result.Created);
        Assert.Equal((bed.Companies.NextId, store.Version), (result.CompanyId, result.StoreVersion));
        Assert.Equal(2, store.Version);
        var audit = Assert.Single(bed.World.Database.Audit);
        Assert.Equal(ShopifyAuditActions.StoreUpdated, audit.Action);
        Assert.Equal("""["billingCompanyId","billingCurrency"]""", audit.Fields);
    }

    [Fact]
    public async Task WithCompanyIdItOnlyAssigns()
    {
        var bed = new SetupBed(profile: CopProfile);
        bed.Companies.Active.Add(new ShopifySetupCompanyRef(CompanyId, "Origen Botánico SAS", "901.1", "NIT", "CO", true, []));

        var result = await SendAsync(bed, Assign(CompanyId));

        Assert.Empty(bed.Companies.Created);
        Assert.False(result.Created);
        Assert.Equal(CompanyId, bed.World.Database.Stores[ConnectionId].Billing!.CompanyId);
    }

    [Fact]
    public async Task RetryWithTheCompanyIdDoesNotCreateAnother()
    {
        var bed = new SetupBed(profile: CopProfile);
        bed.World.UnitOfWork.BeforeSave = saves => saves == 1
            ? Task.FromException(new RequestConcurrencyException("concurrency.conflict", "The store changed."))
            : Task.CompletedTask;
        var conflict = await Assert.ThrowsAsync<ShopifySetupCompanyConflictException>(() => SendAsync(bed, Create()));
        bed.World.UnitOfWork.DiscardPendingChanges();

        var retry = await SendAsync(bed, Assign(conflict.CompanyId));

        Assert.Single(bed.Companies.Created);
        Assert.False(retry.Created);
        Assert.Equal(conflict.CompanyId, bed.World.Database.Stores[ConnectionId].Billing!.CompanyId);
    }

    [Fact]
    public async Task AStaleVersionIs412BeforeCreatingTheCompany()
    {
        var bed = new SetupBed(profile: CopProfile);

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(() => SendAsync(bed, Create(version: 7)));

        Assert.IsNotType<ShopifySetupCompanyConflictException>(exception);
        Assert.Equal("concurrency.conflict", exception.Code);
        Assert.Empty(bed.Companies.Created);
    }

    [Fact]
    public async Task AnAssignmentConflictAfterCreatingReturnsTheCompanyId()
    {
        var bed = new SetupBed(profile: CopProfile);
        bed.World.UnitOfWork.BeforeSave = _ => Task.FromException(new RequestConcurrencyException("concurrency.conflict", "The store changed."));

        var exception = await Assert.ThrowsAsync<ShopifySetupCompanyConflictException>(() => SendAsync(bed, Create()));

        Assert.Equal(bed.Companies.NextId, exception.CompanyId);
        Assert.Equal("concurrency.conflict", exception.Code);
    }

    [Fact]
    public async Task AConflictWhileOnlyAssigningIsAPlain412()
    {
        var bed = new SetupBed(profile: CopProfile);
        bed.Companies.Active.Add(new ShopifySetupCompanyRef(CompanyId, "Origen Botánico SAS", "901.1", "NIT", "CO", true, []));
        bed.World.UnitOfWork.BeforeSave = _ => Task.FromException(new RequestConcurrencyException("concurrency.conflict", "The store changed."));

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(() => SendAsync(bed, Assign(CompanyId)));

        Assert.IsNotType<ShopifySetupCompanyConflictException>(exception);
    }

    [Fact]
    public async Task AnUnknownCompanyIsBillingAccountNotFound()
    {
        var bed = new SetupBed(profile: CopProfile);

        var exception = await Assert.ThrowsAsync<ShopifyDomainException>(() => SendAsync(bed, Assign(Guid.CreateVersion7())));

        Assert.Equal(ShopifyErrorCodes.StoreBillingAccountNotFound, exception.Code);
    }

    [Fact]
    public async Task ABankAccountNotInTheExistingCompanyIsBillingAccountNotFound()
    {
        var bed = new SetupBed(profile: CopProfile);
        bed.Companies.Active.Add(new ShopifySetupCompanyRef(CompanyId, "Origen Botánico SAS", "901.1", "NIT", "CO", true, []));
        bed.World.Billing.Account = null;

        var exception = await Assert.ThrowsAsync<ShopifyDomainException>(() =>
            SendAsync(bed, Assign(CompanyId, bankAccount: new ShopifySetupBankAccountInput("Davivienda", "999", "COP"))));

        Assert.Equal(ShopifyErrorCodes.StoreBillingAccountNotFound, exception.Code);
        Assert.Null(bed.World.Database.Stores.GetValueOrDefault(ConnectionId)?.Billing);
    }

    [Fact]
    public async Task AnExistingAccountIsAssignedAsCompaniesHasIt()
    {
        var bed = new SetupBed(profile: CopProfile);
        bed.Companies.Active.Add(new ShopifySetupCompanyRef(CompanyId, "Origen Botánico SAS", "901.1", "NIT", "CO", true, []));

        await SendAsync(bed, Assign(CompanyId, bankAccount: new ShopifySetupBankAccountInput(" bancolombia ", "123-456", "COP")));

        Assert.Equal(new ShopifyBillingAccount(CompanyId, "Bancolombia", "123-456", "COP"), bed.World.Database.Stores[ConnectionId].Billing);
    }

    [Fact]
    public async Task WithoutAnyCurrencyIs503SyncUnavailable()
    {
        var bed = new SetupBed(profile: null);

        var exception = await Assert.ThrowsAsync<ServiceUnavailableException>(() => SendAsync(bed, Create()));

        Assert.Equal(ShopifyErrorCodes.StoreSyncUnavailable, exception.Code);
        Assert.Empty(bed.Companies.Created);
    }

    [Fact]
    public async Task TheStoreTakesTheShopCurrencyWithoutAccount()
    {
        var bed = new SetupBed(profile: CopProfile);

        await SendAsync(bed, Create());

        var billing = bed.World.Database.Stores[ConnectionId].Billing!;
        Assert.Equal(("COP", (string?)null, (string?)null), (billing.Currency, billing.BankName, billing.AccountNumber));
    }

    [Fact]
    public async Task TheNewAccountGoesToTheCompanyAndTheStore()
    {
        var bed = new SetupBed(profile: null);

        await SendAsync(bed, Create(bankAccount: new ShopifySetupBankAccountInput("Bancolombia", "123-456", "COP")));

        Assert.Equal(new ShopifySetupBankAccount("Bancolombia", "123-456", "COP"), Assert.Single(bed.Companies.Created).BankAccount);
        Assert.Equal(new ShopifyBillingAccount(bed.Companies.NextId, "Bancolombia", "123-456", "COP"), bed.World.Database.Stores[ConnectionId].Billing);
    }

    [Fact]
    public async Task MissingChargesVatOnCreateIsAValidationError()
    {
        var bed = new SetupBed(profile: CopProfile);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => SendAsync(bed, Create(chargesVat: null)));

        Assert.Contains(exception.Errors, error => error.PropertyName == "chargesVat");
        Assert.Empty(bed.Companies.Created);
    }

    [Fact]
    public async Task OnlyTheBankNameIsAValidationError()
    {
        var bed = new SetupBed(profile: CopProfile);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            SendAsync(bed, Create(bankAccount: new ShopifySetupBankAccountInput("Bancolombia", null, "COP"))));

        Assert.Contains(exception.Errors, error => error.PropertyName == "bankAccount.accountNumber");
    }

    [Fact]
    public async Task ACallerWithoutMembershipIsDeniedBeforeCreating()
    {
        var bed = new SetupBed(profile: CopProfile);
        bed.World.Memberships.Members.Clear();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => SendAsync(bed, Create()));

        Assert.Empty(bed.Companies.Created);
    }
}
