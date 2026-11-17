using Bootstrapper;
using FluentValidation;
using FluentValidation.Results;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-29 §8.3 y P-4: el adaptador del borrador de cotización del agente. Pasa la petición de Ai tal cual a Quotations y traduce sus
/// rechazos: un cliente que no se puede cotizar es <c>CustomerNotReady</c> (el agente traspasa); lo demás vuelve al modelo con su código.
/// </summary>
public sealed class AiQuotationDraftsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly Guid Advisor = Guid.CreateVersion7();
    private static readonly Guid Variant = Guid.CreateVersion7();
    private static readonly AgentBillingAccount Account = new(Guid.CreateVersion7(), "Bancolombia", "123-456", "COP");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRequestReachesQuotationsAsIsAndTheResultCarriesTheAdvisor()
    {
        var writer = new FakeWriter { Next = new ExternalQuotationResult(Guid.CreateVersion7(), "COT-0007", Advisor) };

        var result = await new AiQuotationDrafts(writer).CreateAsync(Request(), Ct);

        Assert.Equal(AiQuotationDraftResult.Created("COT-0007", Advisor), result);
        var draft = Assert.Single(writer.Drafts);
        Assert.Equal((Tenant, Customer, Advisor, "Nota", "ai:job:call"), (draft.TenantId, draft.ClientId, draft.AdvisorMembershipId, draft.Notes, draft.ExternalKey));
        Assert.Equal(new QuotationBillingAccountRequest(Account.CompanyId, "Bancolombia", "123-456", "COP"), draft.BillingAccount);
        Assert.Equal([new QuotationItemAddition(Variant, 4m)], draft.Items);
    }

    [Theory]
    [InlineData("quotation.quotation.client_not_found")]
    [InlineData("quotation.quotation.client_incomplete")]
    [InlineData("quotation.quotation.client_cuc_missing")]
    [InlineData("quotation.quotation.client_inactive")]
    public async Task ACustomerQuotationsCannotQuoteIsNotReady(string code)
    {
        var writer = new FakeWriter { Throw = new QuotationsDomainException(code, "no") };

        Assert.Equal(AiQuotationDraftResult.CustomerNotReady(code), await new AiQuotationDrafts(writer).CreateAsync(Request(), Ct));
    }

    [Fact]
    public async Task AnyOtherRejectionGoesBackWithItsCode()
    {
        var writer = new FakeWriter { Throw = new QuotationsDomainException("quotation.billing.account_not_found", "no") };

        Assert.Equal(
            AiQuotationDraftResult.Rejected("quotation.billing.account_not_found"),
            await new AiQuotationDrafts(writer).CreateAsync(Request(), Ct));
    }

    [Fact]
    public async Task AValidationFailureIsRejectedAsValidationFailed()
    {
        var writer = new FakeWriter { Throw = new ValidationException([new ValidationFailure("Items", "duplicada")]) };

        Assert.Equal(AiQuotationDraftResult.Rejected("validation.failed"), await new AiQuotationDrafts(writer).CreateAsync(Request(), Ct));
    }

    private static AiQuotationDraftRequest Request() =>
        new(Tenant, Customer, Advisor, Account, [new AiQuotationDraftLine(Variant, 4)], "Nota", "ai:job:call");

    private sealed class FakeWriter : IExternalQuotationWriter
    {
        public List<ExternalQuotationDraft> Drafts { get; } = [];

        public ExternalQuotationResult? Next { get; init; }

        public Exception? Throw { get; init; }

        public Task<ExternalQuotationResult> CreateDraftAsync(ExternalQuotationDraft draft, CancellationToken cancellationToken)
        {
            Drafts.Add(draft);
            return Throw is not null ? Task.FromException<ExternalQuotationResult>(Throw) : Task.FromResult(Next!);
        }
    }
}
