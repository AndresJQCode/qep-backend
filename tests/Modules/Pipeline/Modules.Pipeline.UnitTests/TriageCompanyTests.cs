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
    public async Task ACompanyWithAnOpenDealMakesTheContactsConversationNotPending()
    {
        var bed = new ContactHandlersBed();
        bed.OpenDeal(bed.Company, "Ya abierto");

        var failure = await Assert.ThrowsAsync<PipelineDomainException>(() => bed.Triage().HandleAsync(Create(bed), Ct));

        Assert.Equal(PipelineErrorCodes.TriageNotPending, failure.Code);
        Assert.Single(bed.Deals.Items);
        Assert.Empty(bed.Decisions.Items);
    }
}
