using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Decisión del owner 2026-11-16: un cliente inactivo no recibe un negocio nuevo, por ningún camino. «Incompleto» no es
/// «inactivo», y cotizaciones y pedidos no se bloquean: su evento sólo deja de abrir el negocio.</summary>
public sealed class InactiveCustomerDealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DecideTriageCommand Create(ContactHandlersBed bed, Guid? customerId = null) =>
        new(bed.TenantId, bed.ConversationId, "create", null, null, customerId);

    [Fact]
    public async Task TriageCannotCreateADealOnAnInactiveCompany()
    {
        var bed = new ContactHandlersBed();
        bed.Deactivate(bed.Company);

        var failure = await Assert.ThrowsAsync<PipelineDomainException>(() => bed.Triage().HandleAsync(Create(bed), Ct));

        Assert.Equal(PipelineErrorCodes.DealCustomerInactive, failure.Code);
        Assert.Empty(bed.Deals.Items);
        Assert.Empty(bed.Decisions.Items);
    }

    [Fact]
    public async Task TriageCannotCreateADealOnAnInactiveContact()
    {
        var bed = new ContactHandlersBed();
        bed.Deactivate(bed.Person);

        var failure = await Assert.ThrowsAsync<PipelineDomainException>(() => bed.Triage().HandleAsync(Create(bed, bed.Person), Ct));

        Assert.Equal(PipelineErrorCodes.DealCustomerInactive, failure.Code);
        Assert.Empty(bed.Deals.Items);
    }

    [Fact]
    public async Task AnInactiveCompanyDoesNotStopADealOnTheActiveContact()
    {
        var bed = new ContactHandlersBed();
        bed.Deactivate(bed.Company);

        await bed.Triage().HandleAsync(Create(bed, bed.Person), Ct);

        Assert.Equal(bed.Person, Assert.Single(bed.Deals.Items).CustomerId);
    }

    [Fact]
    public async Task AnIncompleteCustomerStillReceivesDeals()
    {
        var bed = new ContactHandlersBed();
        bed.Customers.Refs[bed.Company] = bed.Customers.Refs[bed.Company] with { IsComplete = false };

        await bed.Triage().HandleAsync(Create(bed), Ct);

        Assert.Single(bed.Deals.Items);
    }

    [Fact]
    public async Task DismissingTheThreadOfAnInactiveContactStillWorks()
    {
        var bed = new ContactHandlersBed();
        bed.Deactivate(bed.Person);
        bed.Deactivate(bed.Company);

        // Sin motivo del catálogo el descarte falla por su propia regla, no por el inactivo.
        var failure = await Assert.ThrowsAsync<PipelineDomainException>(
            () => bed.Triage().HandleAsync(new DecideTriageCommand(bed.TenantId, bed.ConversationId, "dismiss", null, null), Ct));
        Assert.Equal(PipelineErrorCodes.TriageDismissReasonRequired, failure.Code);
    }

    [Fact]
    public async Task AManualDealCannotBeCreatedOnAnInactiveCustomer()
    {
        var bed = new ContactHandlersBed();
        bed.Deactivate(bed.Person);

        var failure = await Assert.ThrowsAsync<PipelineDomainException>(
            () => bed.CreateDeal().HandleAsync(new CreateDealCommand(bed.TenantId, bed.Person, "Nuevo", null, null, null, null), Ct));

        Assert.Equal(PipelineErrorCodes.DealCustomerInactive, failure.Code);
        Assert.Empty(bed.Deals.Items);
    }

    [Fact]
    public async Task AManualDealOnAnActiveCustomerIsCreated()
    {
        var bed = new ContactHandlersBed();

        await bed.CreateDeal().HandleAsync(new CreateDealCommand(bed.TenantId, bed.Person, "Nuevo", null, null, null, null), Ct);

        Assert.Single(bed.Deals.Items);
    }

    [Fact]
    public async Task ASentQuotationOfAnInactiveCustomerDoesNotOpenADealAndStaysUnlinked()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        bed.Customers.Inactive.Add(clientId);

        Assert.True(await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7()), ApplierBed.Now, Ct));

        Assert.Empty(bed.Deals.Items);
        Assert.Null(Assert.Single(bed.Quotations.Items).DealId);
    }

    [Fact]
    public async Task ASentQuotationOfAnInactiveCustomerStillLinksItsOpenDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var deal = bed.OpenDeal(clientId);
        bed.Customers.Inactive.Add(clientId);

        Assert.True(await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7()), ApplierBed.Now, Ct));

        Assert.Equal((Guid?)deal.Id, Assert.Single(bed.Quotations.Items).DealId);
    }

    [Fact]
    public async Task AnOrderOfAnInactiveCustomerDoesNotOpenADealAndStaysUnlinked()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        bed.Customers.Inactive.Add(clientId);

        Assert.True(await bed.Applier.ApplyOrderCreatedAsync(bed.OrderFact(clientId, Guid.CreateVersion7(), 500m), ApplierBed.Now, Ct));

        Assert.Empty(bed.Deals.Items);
        Assert.Null(Assert.Single(bed.Orders.Items).DealId);
    }
}
