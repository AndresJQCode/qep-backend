using BuildingBlocks.Application;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-11-10 §4.5 (A18): el resumen de negocios del hilo de un contacto acepta la empresa de ese contacto y devuelve los
/// negocios de la empresa más los de la persona; el estado de triage se calcula sobre el conjunto (P1).</summary>
public sealed class CustomerSummaryCompanyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GetCustomerDealsSummaryQuery Ask(ContactHandlersBed bed, Guid customerId, bool withConversation = true) =>
        new(bed.TenantId, customerId, withConversation ? bed.ConversationId : null);

    [Fact]
    public async Task TheCompanyIdWithItsContactsConversationReturnsBothSetsOfDeals()
    {
        var bed = new ContactHandlersBed();
        var ofCompany = bed.OpenDeal(bed.Company, "De la empresa");
        var ofPerson = bed.OpenDeal(bed.Person, "De la persona");

        var summary = await bed.Summary().HandleAsync(Ask(bed, bed.Company), Ct);

        Assert.Equal(bed.Company, summary.CustomerId);
        Assert.Equal([ofCompany.Id, ofPerson.Id], summary.OpenDeals.Select(deal => deal.Id));
        Assert.Equal([bed.Company, bed.Person], summary.OpenDeals.Select(deal => deal.CustomerId));
    }

    [Fact]
    public async Task ThePersonsIdReturnsOnlyThePersonsDeals()
    {
        var bed = new ContactHandlersBed();
        bed.OpenDeal(bed.Company, "De la empresa");
        var ofPerson = bed.OpenDeal(bed.Person, "De la persona");

        var summary = await bed.Summary().HandleAsync(Ask(bed, bed.Person), Ct);

        Assert.Equal(bed.Person, summary.CustomerId);
        Assert.Equal(ofPerson.Id, Assert.Single(summary.OpenDeals).Id);
        Assert.Equal(bed.Person, summary.OpenDeals[0].CustomerId);
    }

    [Fact]
    public async Task WithoutConversationOnlyTheRequestedCustomersDeals()
    {
        var bed = new ContactHandlersBed();
        var ofCompany = bed.OpenDeal(bed.Company, "De la empresa");
        bed.OpenDeal(bed.Person, "De la persona");

        var summary = await bed.Summary().HandleAsync(Ask(bed, bed.Company, withConversation: false), Ct);

        Assert.Equal(ofCompany.Id, Assert.Single(summary.OpenDeals).Id);
        Assert.Null(summary.Triage);
    }

    [Fact]
    public async Task AnUnrelatedCustomerWithThatConversationIsNotFound()
    {
        var bed = new ContactHandlersBed();

        var failure = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.Summary().HandleAsync(Ask(bed, bed.Stranger), Ct));

        Assert.Equal(PipelineErrorCodes.TriageConversationNotFound, failure.Code);
    }

    [Fact]
    public async Task TheLastClosedDealIsTheMostRecentOfTheTwo()
    {
        var bed = new ContactHandlersBed();
        bed.ClosedDeal(bed.Company, "Cerrado empresa", ContactHandlersBed.Now.AddDays(-30));
        var recent = bed.ClosedDeal(bed.Person, "Cerrado persona", ContactHandlersBed.Now.AddDays(-3));

        var summary = await bed.Summary().HandleAsync(Ask(bed, bed.Company), Ct);

        Assert.Equal(recent.Id, summary.LastClosedDeal!.Id);
        Assert.Equal(bed.Person, summary.LastClosedDeal.CustomerId);
    }

    [Fact]
    public async Task TriageStatusLooksAtTheWholeSet()
    {
        var bed = new ContactHandlersBed();
        bed.OpenDeal(bed.Company, "De la empresa");

        var viaCompany = await bed.Summary().HandleAsync(Ask(bed, bed.Company), Ct);
        var viaPerson = await bed.Summary().HandleAsync(Ask(bed, bed.Person), Ct);

        Assert.Equal("NotApplicable", viaCompany.Triage!.Status);
        Assert.Equal("NotApplicable", viaPerson.Triage!.Status);
        Assert.Empty(viaPerson.Triage.AvailableActions);
    }

    [Fact]
    public async Task WithoutAnyOpenDealTheContactsThreadIsStillPending()
    {
        var bed = new ContactHandlersBed();

        var summary = await bed.Summary().HandleAsync(Ask(bed, bed.Company), Ct);

        Assert.Equal("Pending", summary.Triage!.Status);
        Assert.Equal(["create", "dismiss"], summary.Triage.AvailableActions);
    }

    [Fact]
    public async Task StalledAndDaysUseTheActivityOfEachDealsOwnCustomer()
    {
        var bed = new ContactHandlersBed();
        var longAgo = ContactHandlersBed.Now.AddDays(-60);
        var ofCompany = bed.OpenDeal(bed.Company, "De la empresa", longAgo);
        var ofPerson = bed.OpenDeal(bed.Person, "De la persona", longAgo);
        // Sólo la persona escribió ayer: su negocio no está estancado; el de la empresa, sin actividad, sí.
        bed.Conversations.Activity[bed.Person] = new PipelineCustomerActivity(bed.Person, ContactHandlersBed.Now.AddDays(-1), [bed.ConversationId]);

        var summary = await bed.Summary().HandleAsync(Ask(bed, bed.Company), Ct);

        Assert.True(summary.OpenDeals.Single(deal => deal.Id == ofCompany.Id).IsStalled);
        Assert.False(summary.OpenDeals.Single(deal => deal.Id == ofPerson.Id).IsStalled);
    }
}
