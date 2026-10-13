using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.2, §4.3, D13–D19: transiciones del negocio, su historial y el monto que sigue a la cotización
/// principal. Cada cambio de etapa deja su fila pendiente, que la unidad de trabajo guarda en el mismo SaveChanges.</summary>
public sealed class DealTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000d1");
    private static readonly Guid CustomerId = Guid.Parse("01900000-0000-7000-8000-0000000000d2");
    private static readonly Guid OwnerId = Guid.Parse("01900000-0000-7000-8000-0000000000d3");
    private static readonly DealActor Advisor = DealActor.ForUser(OwnerId);

    private static SalesPipeline Pipeline() => SalesPipeline.CreateDefault(TenantId, Now);

    private static PipelineStage Stage(SalesPipeline pipeline, string name) => pipeline.OrderedStages.Single(stage => stage.Name == name);

    private static Deal NewDeal(SalesPipeline pipeline, decimal? amount = 1_000m, DealSource source = DealSource.Manual, Guid? conversationId = null) =>
        Deal.Create(TenantId, pipeline, CustomerId, OwnerId, "  Dotación sede norte ", amount, "COP", null, source, conversationId, Advisor, Now);

    private static string CodeOf(Action action) => Assert.Throws<PipelineDomainException>(action).Code;

    [Fact]
    public void CreateStartsInTheFirstOpenStageAndRecordsTheFirstHistoryRow()
    {
        var pipeline = Pipeline();

        var deal = NewDeal(pipeline);

        Assert.Equal(("Dotación sede norte", 1_000m, "COP"), (deal.Title, deal.Amount, deal.Currency));
        Assert.Equal((pipeline.FirstOpenStage.Id, Now, 1L, false), (deal.StageId, deal.StageEnteredAt, deal.Version, deal.IsClosed));
        Assert.Equal((Now, Now, Now), (deal.CreatedAt, deal.UpdatedAt, deal.LastChangedAt));
        var row = Assert.Single(deal.PullHistory());
        Assert.Equal(((Guid?)null, pipeline.FirstOpenStage.Id, ActorKind.User, (Guid?)OwnerId), (row.FromStageId, row.ToStageId, row.ActorKind, row.ActorMemberId));
        Assert.Equal(((decimal?)1_000m, "COP", 10, deal.Id, pipeline.Id), (row.Amount, row.Currency, row.Probability, row.DealId, row.PipelineId));
        Assert.Equal((1, 1), (row.Ordinal, deal.HistoryCount));
    }

    // P4 del plan: el orden del historial lo da el dominio, no la base. Dos cambios en el mismo SaveChanges (crear y ganar, en el
    // worker) tienen el mismo instante y EF no inserta en orden de Add; el ordinal los separa, y sigue contando entre guardados.
    [Fact]
    public void EveryHistoryRowTakesTheNextOrdinalOfItsDeal()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.Win(pipeline, DealActor.ForSystem, Now);

        var first = deal.PullHistory();
        deal.Reopen(pipeline, null, Advisor, Now.AddDays(1));

        Assert.Equal([1, 2], first.Select(row => row.Ordinal));
        Assert.Equal(3, Assert.Single(deal.PullHistory()).Ordinal);
        Assert.Equal(3, deal.HistoryCount);
    }

    [Fact]
    public void TheOriginConversationIsKeptOnlyForAConversationDeal()
    {
        var pipeline = Pipeline();
        var conversationId = Guid.CreateVersion7();

        Assert.Equal(conversationId, NewDeal(pipeline, source: DealSource.Conversation, conversationId: conversationId).OriginConversationId);
        Assert.Null(NewDeal(pipeline, source: DealSource.Manual, conversationId: conversationId).OriginConversationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankTitleIsRefused(string? title) =>
        Assert.Equal(PipelineErrorCodes.DealTitleInvalid, CodeOf(() =>
            Deal.Create(TenantId, Pipeline(), CustomerId, OwnerId, title, null, "COP", null, DealSource.Manual, null, Advisor, Now)));

    [Fact]
    public void ATitleOverTwoHundredCharactersIsRefused() =>
        Assert.Equal(PipelineErrorCodes.DealTitleInvalid, CodeOf(() =>
            Deal.Create(TenantId, Pipeline(), CustomerId, OwnerId, new string('a', 201), null, "COP", null, DealSource.Manual, null, Advisor, Now)));

    [Theory]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("1000000000000")]
    public void AnInvalidAmountIsRefused(string amount) =>
        Assert.Equal(PipelineErrorCodes.DealAmountInvalid, CodeOf(() => NewDeal(Pipeline(), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))));

    // D34: un negocio puede nacer sin monto.
    [Fact]
    public void ADealCanBeBornWithoutAmount() => Assert.Null(NewDeal(Pipeline(), amount: null).Amount);

    // D13: libre entre abiertas, adelante, atrás o saltando.
    [Fact]
    public void MovingIsFreeBetweenOpenStages()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.PullHistory();

        Assert.True(deal.MoveTo(pipeline, Stage(pipeline, "Negociación").Id, Advisor, Now.AddHours(1)));
        Assert.True(deal.MoveTo(pipeline, Stage(pipeline, "Calificado").Id, Advisor, Now.AddHours(2)));

        var rows = deal.PullHistory();
        Assert.Equal(
            [(Stage(pipeline, "Nuevo").Id, Stage(pipeline, "Negociación").Id), (Stage(pipeline, "Negociación").Id, Stage(pipeline, "Calificado").Id)],
            rows.Select(row => (row.FromStageId!.Value, row.ToStageId)));
        Assert.Equal([75, 25], rows.Select(row => row.Probability));
        Assert.Equal((Stage(pipeline, "Calificado").Id, Now.AddHours(2), 3L), (deal.StageId, deal.StageEnteredAt, deal.Version));
    }

    [Fact]
    public void MovingToTheSameStageChangesNothing()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.PullHistory();

        Assert.False(deal.MoveTo(pipeline, deal.StageId, Advisor, Now.AddHours(1)));

        Assert.Empty(deal.PullHistory());
        Assert.Equal(1L, deal.Version);
    }

    [Fact]
    public void MovingToAClosingOrUnknownStageIsRefused()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);

        Assert.Equal(PipelineErrorCodes.DealStageNotOpen, CodeOf(() => deal.MoveTo(pipeline, pipeline.WonStage.Id, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.StageNotFound, CodeOf(() => deal.MoveTo(pipeline, Guid.CreateVersion7(), Advisor, Now)));
    }

    // D14: ganar a mano se permite (ventas por Pos o por teléfono).
    [Fact]
    public void WinningClosesTheDealInTheWonStage()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.PullHistory();

        deal.Win(pipeline, Advisor, Now.AddDays(1));

        Assert.Equal((pipeline.WonStage.Id, (DateTimeOffset?)Now.AddDays(1), true), (deal.StageId, deal.ClosedAt, deal.IsClosed));
        Assert.Equal(100, Assert.Single(deal.PullHistory()).Probability);
    }

    [Fact]
    public void AClosedDealCannotMoveWinOrLose()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.Win(pipeline, Advisor, Now);

        Assert.Equal(PipelineErrorCodes.DealAlreadyClosed, CodeOf(() => deal.MoveTo(pipeline, Stage(pipeline, "Calificado").Id, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.DealAlreadyClosed, CodeOf(() => deal.Win(pipeline, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.DealAlreadyClosed, CodeOf(() => deal.Lose(pipeline, Guid.CreateVersion7(), null, Advisor, Now)));
    }

    // D14 y P10: perder exige motivo; la nota es opcional y se recorta.
    [Fact]
    public void LosingNeedsAReasonAndKeepsTheNote()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        var reasonId = Guid.CreateVersion7();

        Assert.Equal(PipelineErrorCodes.DealLossReasonRequired, CodeOf(() => deal.Lose(pipeline, null, null, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.DealLossReasonRequired, CodeOf(() => deal.Lose(pipeline, Guid.Empty, null, Advisor, Now)));
        Assert.Equal(PipelineErrorCodes.DealLossNoteInvalid, CodeOf(() => deal.Lose(pipeline, reasonId, new string('a', 501), Advisor, Now)));
        Assert.False(deal.IsClosed);

        deal.Lose(pipeline, reasonId, "  Se fue con la competencia ", Advisor, Now);

        Assert.Equal((pipeline.LostStage.Id, (Guid?)reasonId, "Se fue con la competencia"), (deal.StageId, deal.LossReasonId, deal.LossNote));
        Assert.True(deal.IsClosed);
    }

    [Fact]
    public void ABlankLossNoteIsStoredAsNull()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);

        deal.Lose(pipeline, Guid.CreateVersion7(), "   ", Advisor, Now);

        Assert.Null(deal.LossNote);
    }

    // D15: reabrir vuelve a donde estaba.
    [Fact]
    public void ReopenReturnsToTheLastOpenStageAndClearsTheLoss()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        var negotiation = Stage(pipeline, "Negociación").Id;
        deal.MoveTo(pipeline, negotiation, Advisor, Now);
        deal.Lose(pipeline, Guid.CreateVersion7(), "nota", Advisor, Now);
        deal.PullHistory();

        deal.Reopen(pipeline, negotiation, DealActor.ForSystem, Now.AddDays(2));

        Assert.Equal((negotiation, (DateTimeOffset?)null, (Guid?)null, (string?)null), (deal.StageId, deal.ClosedAt, deal.LossReasonId, deal.LossNote));
        var row = Assert.Single(deal.PullHistory());
        Assert.Equal((pipeline.LostStage.Id, negotiation, ActorKind.System, (Guid?)null), (row.FromStageId!.Value, row.ToStageId, row.ActorKind, row.ActorMemberId));
    }

    // Review Focus 2: la última abierta pudo borrarse (ya no tenía negocios: éste estaba cerrado).
    [Fact]
    public void ReopenFallsBackToTheFirstOpenStageWhenTheLastOneIsGone()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        deal.Win(pipeline, Advisor, Now);

        deal.Reopen(pipeline, Guid.CreateVersion7(), Advisor, Now);

        Assert.Equal(pipeline.FirstOpenStage.Id, deal.StageId);
    }

    [Fact]
    public void ReopeningAnOpenDealIsRefused() =>
        Assert.Equal(PipelineErrorCodes.DealNotClosed, CodeOf(() => NewDeal(Pipeline()).Reopen(Pipeline(), null, Advisor, Now)));

    // D17: el avance automático es sólo hacia adelante y nunca toca un cerrado.
    [Fact]
    public void AdvanceToTheQuotationStageGoesOnlyForward()
    {
        var pipeline = Pipeline();
        var fresh = NewDeal(pipeline);
        var ahead = NewDeal(pipeline);
        ahead.MoveTo(pipeline, Stage(pipeline, "Negociación").Id, Advisor, Now);
        var closed = NewDeal(pipeline);
        closed.Win(pipeline, Advisor, Now);

        Assert.True(fresh.AdvanceToQuotationStage(pipeline, DealActor.ForSystem, Now));
        Assert.False(ahead.AdvanceToQuotationStage(pipeline, DealActor.ForSystem, Now));
        Assert.False(closed.AdvanceToQuotationStage(pipeline, DealActor.ForSystem, Now));

        Assert.Equal(pipeline.QuotationStage.Id, fresh.StageId);
        Assert.Equal(Stage(pipeline, "Negociación").Id, ahead.StageId);
    }

    // D18: con principal, monto y moneda son de sólo lectura; el resto se edita.
    [Fact]
    public void WithAPrimaryQuotationTheAmountIsReadOnly()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline);
        var newOwner = Guid.CreateVersion7();
        deal.SetPrimaryQuotation(Guid.CreateVersion7(), 2_500m, "USD", Now);

        Assert.Equal(PipelineErrorCodes.DealAmountFollowsQuotation, CodeOf(() => deal.Update("Otro", OwnerId, 3_000m, "USD", null, Now)));
        Assert.Equal(PipelineErrorCodes.DealAmountFollowsQuotation, CodeOf(() => deal.Update("Otro", OwnerId, 2_500m, "COP", null, Now)));

        deal.Update(" Otro título ", newOwner, 2_500m, "USD", new DateOnly(2026, 11, 30), Now.AddHours(1));

        Assert.Equal(("Otro título", newOwner, (decimal?)2_500m, "USD"), (deal.Title, deal.OwnerMemberId, deal.Amount, deal.Currency));
        Assert.Equal((new DateOnly(2026, 11, 30), Now.AddHours(1)), (deal.ExpectedCloseDate!.Value, deal.LastChangedAt));
    }

    // D27: sin la principal, el monto queda manual con su último valor.
    [Fact]
    public void FollowingAndClearingThePrimaryKeepsTheLastAmount()
    {
        var pipeline = Pipeline();
        var deal = NewDeal(pipeline, amount: null);
        deal.SetPrimaryQuotation(Guid.CreateVersion7(), 2_500m, "USD", Now);
        var version = deal.Version;

        deal.FollowPrimary(2_500m, "USD", Now);
        Assert.Equal(version, deal.Version);

        deal.FollowPrimary(2_700m, "USD", Now);
        deal.ClearPrimary(Now);

        Assert.Equal(((Guid?)null, (decimal?)2_700m, "USD"), (deal.PrimaryQuotationId, deal.Amount, deal.Currency));
        deal.Update("Manual", OwnerId, 3_000m, "COP", null, Now);
        Assert.Equal((decimal?)3_000m, deal.Amount);
    }

    [Fact]
    public void PullHistoryEmptiesThePendingRows()
    {
        var deal = NewDeal(Pipeline());

        Assert.Single(deal.PullHistory());
        Assert.Empty(deal.PullHistory());
    }
}
