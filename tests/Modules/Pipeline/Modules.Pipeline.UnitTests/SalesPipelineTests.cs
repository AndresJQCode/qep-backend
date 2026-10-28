using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.1, D10–D12 y D31: el embudo por defecto y las reglas de su disposición. Un rechazo no cambia nada.</summary>
public sealed class SalesPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly IReadOnlySet<Guid> NoDeals = new HashSet<Guid>();

    public static TheoryData<string> BrokenLayouts => new()
    {
        "no-open", "two-won", "won-before-open", "lost-before-won", "no-quotation-stage", "two-quotation-stages",
        "quotation-stage-closing", "open-without-stall", "stall-over-limit", "closing-with-stall", "probability-over-100",
        "negative-probability", "blank-name", "long-name", "duplicate-name", "unknown-id", "repeated-id", "empty", "too-many",
    };

    private static SalesPipeline Default() => SalesPipeline.CreateDefault(TenantId, Now);

    private static string CodeOf(Action action) => Assert.Throws<PipelineDomainException>(action).Code;

    private static StageInput Keep(PipelineStage stage) =>
        new(stage.Id, stage.Name, stage.Kind, stage.Probability, stage.StallDays, stage.IsQuotationStage);

    private static List<StageInput> CurrentLayout(SalesPipeline pipeline) => [.. pipeline.OrderedStages.Select(Keep)];

    [Fact]
    public void TheDefaultPipelineHasTheSeededStagesInOrder()
    {
        var pipeline = Default();
        var stages = pipeline.OrderedStages;

        Assert.Equal((TenantId, "Embudo de ventas", 1L), (pipeline.TenantId, pipeline.Name, pipeline.Version));
        Assert.Equal(["Nuevo", "Calificado", "Cotizado", "Negociación", "Ganado", "Perdido"], stages.Select(stage => stage.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6], stages.Select(stage => stage.Position));
        Assert.Equal([10, 25, 50, 75, 100, 0], stages.Select(stage => stage.Probability));
        Assert.Equal([3, 5, 7, 7, (int?)null, null], stages.Select(stage => stage.StallDays));
        Assert.Equal(
            [StageKind.Open, StageKind.Open, StageKind.Open, StageKind.Open, StageKind.Won, StageKind.Lost],
            stages.Select(stage => stage.Kind));
        Assert.Equal(("Nuevo", "Cotizado", "Ganado", "Perdido"),
            (pipeline.FirstOpenStage.Name, pipeline.QuotationStage.Name, pipeline.WonStage.Name, pipeline.LostStage.Name));
        Assert.All(stages, stage => Assert.Equal(pipeline.Id, stage.PipelineId));
    }

    [Fact]
    public void TheSeededReasonsAreTheOnesOfTheSpec()
    {
        Assert.Equal(["Precio", "Competencia", "Sin respuesta", "Sin presupuesto", "Otro"], PipelineSeed.LossReasons);
        Assert.Equal(["Soporte", "Recompra", "Proveedor", "Spam", "Otro"], PipelineSeed.DismissReasons);
    }

    // D12: renombrar conserva el id; el historial apunta a ids.
    [Fact]
    public void RenamingKeepsTheIdAndBumpsTheVersion()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        var firstId = layout[0].Id!.Value;
        layout[0] = layout[0] with { Name = "  Prospecto " };

        pipeline.ReplaceStages(layout, NoDeals, Now.AddMinutes(1));

        Assert.Equal("Prospecto", pipeline.FindStage(firstId)!.Name);
        Assert.Equal((2L, Now.AddMinutes(1)), (pipeline.Version, pipeline.UpdatedAt));
    }

    [Fact]
    public void ANewOpenStageIsAddedWhereItComes()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        layout.Insert(3, new StageInput(null, "Demostración", StageKind.Open, 40, 4, false));

        pipeline.ReplaceStages(layout, NoDeals, Now);

        Assert.Equal(
            ["Nuevo", "Calificado", "Cotizado", "Demostración", "Negociación", "Ganado", "Perdido"],
            pipeline.OrderedStages.Select(stage => stage.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], pipeline.OrderedStages.Select(stage => stage.Position));
        Assert.NotEqual(Guid.Empty, pipeline.OrderedStages[3].Id);
    }

    [Fact]
    public void RemovingAStageWithoutDealsDropsIt()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        layout.RemoveAt(1);

        pipeline.ReplaceStages(layout, NoDeals, Now);

        Assert.Equal(["Nuevo", "Cotizado", "Negociación", "Ganado", "Perdido"], pipeline.OrderedStages.Select(stage => stage.Name));
    }

    // Review Focus 2: borrar una etapa con negocios no deja negocios huérfanos ni media disposición aplicada.
    [Fact]
    public void RemovingAStageWithDealsIsRefusedAndNothingChanges()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        var qualified = layout[1].Id!.Value;
        layout.RemoveAt(1);
        layout[0] = layout[0] with { Name = "Otro nombre" };

        Assert.Equal(PipelineErrorCodes.StageHasDeals, CodeOf(() => pipeline.ReplaceStages(layout, new HashSet<Guid> { qualified }, Now)));

        Assert.Equal(6, pipeline.Stages.Count);
        Assert.Equal(("Nuevo", 1L), (pipeline.FirstOpenStage.Name, pipeline.Version));
    }

    // Review Focus 2: una abierta que pasa a Won cerraría en silencio sus negocios.
    [Fact]
    public void AStageCannotChangeItsKind()
    {
        var pipeline = Default();
        var layout = CurrentLayout(pipeline);
        List<StageInput> changed = [layout[0], layout[1], layout[2], layout[3] with { Kind = StageKind.Won, StallDays = null }, layout[5]];

        Assert.Equal(PipelineErrorCodes.StageLayoutInvalid, CodeOf(() => pipeline.ReplaceStages(changed, NoDeals, Now)));
        Assert.Equal(1L, pipeline.Version);
    }

    [Theory]
    [MemberData(nameof(BrokenLayouts))]
    public void ABrokenLayoutIsRefusedAndNothingChanges(string brokenCase)
    {
        var pipeline = Default();

        Assert.Equal(PipelineErrorCodes.StageLayoutInvalid, CodeOf(() => pipeline.ReplaceStages(Broken(pipeline, brokenCase), NoDeals, Now)));

        Assert.Equal(1L, pipeline.Version);
        Assert.Equal(6, pipeline.Stages.Count);
    }

    private static List<StageInput> Broken(SalesPipeline pipeline, string brokenCase)
    {
        var s = CurrentLayout(pipeline); // Nuevo, Calificado, Cotizado, Negociación, Ganado, Perdido
        switch (brokenCase)
        {
            case "no-open": return [s[4], s[5]];
            case "two-won": s.Insert(4, new StageInput(null, "Ganado 2", StageKind.Won, 100, null, false)); return s;
            case "won-before-open": return [s[0], s[4], s[1], s[2], s[3], s[5]];
            case "lost-before-won": return [s[0], s[1], s[2], s[3], s[5], s[4]];
            case "no-quotation-stage": s[2] = s[2] with { IsQuotationStage = false }; return s;
            case "two-quotation-stages": s[1] = s[1] with { IsQuotationStage = true }; return s;
            case "quotation-stage-closing": s[2] = s[2] with { IsQuotationStage = false }; s[4] = s[4] with { IsQuotationStage = true }; return s;
            case "open-without-stall": s[0] = s[0] with { StallDays = null }; return s;
            case "stall-over-limit": s[0] = s[0] with { StallDays = 366 }; return s;
            case "closing-with-stall": s[4] = s[4] with { StallDays = 3 }; return s;
            case "probability-over-100": s[0] = s[0] with { Probability = 101 }; return s;
            case "negative-probability": s[5] = s[5] with { Probability = -1 }; return s;
            case "blank-name": s[0] = s[0] with { Name = "   " }; return s;
            case "long-name": s[0] = s[0] with { Name = new string('a', 61) }; return s;
            case "duplicate-name": s[1] = s[1] with { Name = " nuevo " }; return s;
            case "unknown-id": s[0] = s[0] with { Id = Guid.CreateVersion7() }; return s;
            case "repeated-id": s[1] = s[1] with { Id = s[0].Id }; return s;
            case "empty": return [];
            case "too-many":
                s.InsertRange(0, Enumerable.Range(1, 15).Select(index => new StageInput(null, $"Extra {index}", StageKind.Open, 10, 3, false)));
                return s;
            default: throw new ArgumentOutOfRangeException(nameof(brokenCase));
        }
    }
}
