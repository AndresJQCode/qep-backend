using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-11-10 §4.5: el triage de la conversación de un contacto crea el negocio en la empresa; la decisión conserva a
/// la persona (G11) y el título por defecto es la etiqueta de la empresa (P2).</summary>
public sealed class TriageCompanyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DecideTriageCommand Create(ContactHandlersBed bed, string? title = null) =>
        new(bed.TenantId, bed.ConversationId, "create", title, null);

    [Fact]
    public async Task CreatingFromAContactsConversationPutsTheDealOnTheCompany()
    {
        var bed = new ContactHandlersBed();

        var result = await bed.Triage().HandleAsync(Create(bed), Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal(bed.Company, deal.CustomerId);
        Assert.Equal(bed.ConversationId, deal.OriginConversationId);
        Assert.Equal(bed.Company, result.Deal!.CustomerId);
    }

    [Fact]
    public async Task TheDecisionKeepsTheConversationsCustomer()
    {
        var bed = new ContactHandlersBed();

        await bed.Triage().HandleAsync(Create(bed), Ct);

        var decision = Assert.Single(bed.Decisions.Items);
        Assert.Equal(bed.Person, decision.CustomerId);
        Assert.Equal(TriageDecisionKind.Created, decision.Kind);
    }

    [Fact]
    public async Task TheDefaultTitleIsTheCompanysName()
    {
        var bed = new ContactHandlersBed();

        await bed.Triage().HandleAsync(Create(bed), Ct);

        Assert.Equal("Acme SAS", Assert.Single(bed.Deals.Items).Title);
    }

    [Fact]
    public async Task ATypedTitleWinsOverTheCompanysName()
    {
        var bed = new ContactHandlersBed();

        await bed.Triage().HandleAsync(Create(bed, "Renovación"), Ct);

        Assert.Equal("Renovación", Assert.Single(bed.Deals.Items).Title);
    }

    [Fact]
    public async Task APersonWithoutCompanyBehavesAsToday()
    {
        var bed = new ContactHandlersBed();
        bed.Customers.Companies.Clear();

        await bed.Triage().HandleAsync(Create(bed), Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal(bed.Person, deal.CustomerId);
        Assert.Equal("Ana Pérez", deal.Title);
        Assert.Equal(bed.Person, Assert.Single(bed.Decisions.Items).CustomerId);
    }

    [Fact]
    public async Task ACompanyWithAnOpenDealDoesNotBlockTheContactsTriage()
    {
        // Decisión del owner 2026-11-16: el triage es por contacto; la empresa no bloquea nada.
        var bed = new ContactHandlersBed();
        bed.OpenDeal(bed.Company, "Ya abierto");

        await bed.Triage().HandleAsync(Create(bed), Ct);

        Assert.Equal(2, bed.Deals.Items.Count);
        Assert.Single(bed.Decisions.Items);
    }

    [Fact]
    public async Task AContactWithAnOpenDealOfItsOwnIsNotPending()
    {
        var bed = new ContactHandlersBed();
        bed.OpenDeal(bed.Person, "Ya abierto");

        var failure = await Assert.ThrowsAsync<PipelineDomainException>(() => bed.Triage().HandleAsync(Create(bed), Ct));

        Assert.Equal(PipelineErrorCodes.TriageNotPending, failure.Code);
        Assert.Single(bed.Deals.Items);
        Assert.Empty(bed.Decisions.Items);
    }

    [Fact]
    public async Task ADealBornOnTheCompanyFromThisConversationKeepsItNotPendingAfterANewInbound()
    {
        // Decisión del owner 2026-11-16: un negocio abierto nacido de la conversación cuenta como del contacto, aunque esté en la empresa.
        var bed = new ContactHandlersBed();
        await bed.Triage().HandleAsync(Create(bed), Ct);
        bed.ReceiveInbound(ContactHandlersBed.Now.AddMinutes(5));

        var failure = await Assert.ThrowsAsync<PipelineDomainException>(() => bed.Triage().HandleAsync(Create(bed), Ct));

        Assert.Equal(PipelineErrorCodes.TriageNotPending, failure.Code);
        Assert.Single(bed.Deals.Items);
    }

    [Fact]
    public async Task OnceTheDealFromThisConversationClosesANewInboundMakesItPendingAgain()
    {
        var bed = new ContactHandlersBed();
        await bed.Triage().HandleAsync(Create(bed), Ct);
        Assert.Single(bed.Deals.Items).Win(bed.Pipeline, DealActor.ForUser(bed.MemberId), ContactHandlersBed.Now);
        bed.ReceiveInbound(ContactHandlersBed.Now.AddMinutes(5));

        await bed.Triage().HandleAsync(Create(bed), Ct);

        Assert.Equal(2, bed.Deals.Items.Count);
    }

    [Fact]
    public async Task ChoosingTheContactPutsTheDealOnTheContactWithItsName()
    {
        var bed = new ContactHandlersBed();

        var result = await bed.Triage().HandleAsync(Create(bed) with { CustomerId = bed.Person }, Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Person, "Ana Pérez"), (deal.CustomerId, deal.Title));
        Assert.Equal(bed.Person, result.Deal!.CustomerId);
    }

    [Fact]
    public async Task ChoosingTheCompanyPutsTheDealOnTheCompany()
    {
        var bed = new ContactHandlersBed();

        await bed.Triage().HandleAsync(Create(bed) with { CustomerId = bed.Company }, Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Company, "Acme SAS"), (deal.CustomerId, deal.Title));
    }

    [Fact]
    public async Task ChoosingSomeoneElseIsRejected()
    {
        var bed = new ContactHandlersBed();

        var failure = await Assert.ThrowsAsync<PipelineDomainException>(
            () => bed.Triage().HandleAsync(Create(bed) with { CustomerId = bed.Stranger }, Ct));

        Assert.Equal(PipelineErrorCodes.TriageDealCustomerInvalid, failure.Code);
        Assert.Empty(bed.Deals.Items);
        Assert.Empty(bed.Decisions.Items);
    }

    [Fact]
    public async Task ChoosingTheCompanyWhenTheContactHasNoneIsRejected()
    {
        var bed = new ContactHandlersBed();
        bed.Customers.Companies.Clear();

        var failure = await Assert.ThrowsAsync<PipelineDomainException>(
            () => bed.Triage().HandleAsync(Create(bed) with { CustomerId = bed.Company }, Ct));

        Assert.Equal(PipelineErrorCodes.TriageDealCustomerInvalid, failure.Code);
        Assert.Empty(bed.Deals.Items);
    }
}
