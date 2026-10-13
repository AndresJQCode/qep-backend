using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §6 y P8 del plan: el catálogo de motivos se reemplaza entero; lo que deja de venir se desactiva
/// (el historial lo nombra), y una disposición inválida no cambia nada.</summary>
public sealed class ReasonCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

    public static TheoryData<string> BrokenCatalogs => new() { "empty", "blank", "long", "duplicate", "unknown-id", "other-kind-id", "repeated-id", "too-many" };

    private static List<PipelineReason> Three() =>
    [
        PipelineReason.Create(TenantId, ReasonKind.Loss, "Precio", 1, Now),
        PipelineReason.Create(TenantId, ReasonKind.Loss, "Competencia", 2, Now),
        PipelineReason.Create(TenantId, ReasonKind.Loss, "Otro", 3, Now),
    ];

    [Fact]
    public void ReplacingRenamesReordersAddsAndDeactivates()
    {
        var current = Three();
        var (price, competition, other) = (current[0], current[1], current[2]);

        var added = ReasonCatalog.Replace(TenantId, ReasonKind.Loss, current,
            [new ReasonInput(other.Id, " Otra razón "), new ReasonInput(null, "Sin presupuesto"), new ReasonInput(price.Id, "Precio")], Later);

        var created = Assert.Single(added);
        Assert.Equal((TenantId, ReasonKind.Loss, "Sin presupuesto", 2, true), (created.TenantId, created.Kind, created.Name, created.Position, created.IsActive));
        Assert.Equal(("Otra razón", 1, true), (other.Name, other.Position, other.IsActive));
        Assert.Equal((3, true), (price.Position, price.IsActive));
        Assert.Equal((false, Later), (competition.IsActive, competition.UpdatedAt));
    }

    [Fact]
    public void NamingAnInactiveReasonReactivatesIt()
    {
        var current = Three();
        ReasonCatalog.Replace(TenantId, ReasonKind.Loss, current, [new ReasonInput(current[0].Id, "Precio")], Now);

        ReasonCatalog.Replace(TenantId, ReasonKind.Loss, current, [new ReasonInput(current[0].Id, "Precio"), new ReasonInput(current[1].Id, "Competencia")], Later);

        Assert.True(current[1].IsActive);
        Assert.False(current[2].IsActive);
    }

    [Theory]
    [MemberData(nameof(BrokenCatalogs))]
    public void ABrokenCatalogIsRefusedAndNothingChanges(string brokenCase)
    {
        var current = Three();
        current.Add(PipelineReason.Create(TenantId, ReasonKind.Dismiss, "Spam", 1, Now));
        List<ReasonInput> inputs = brokenCase switch
        {
            "empty" => [],
            "blank" => [new ReasonInput(null, "  ")],
            "long" => [new ReasonInput(null, new string('a', 81))],
            "duplicate" => [new ReasonInput(current[0].Id, "Precio"), new ReasonInput(null, " precio ")],
            "unknown-id" => [new ReasonInput(Guid.CreateVersion7(), "Precio")],
            "other-kind-id" => [new ReasonInput(current[3].Id, "Spam")],
            "repeated-id" => [new ReasonInput(current[0].Id, "Precio"), new ReasonInput(current[0].Id, "Precio 2")],
            "too-many" => [.. Enumerable.Range(1, 31).Select(index => new ReasonInput(null, $"Motivo {index}"))],
            _ => throw new ArgumentOutOfRangeException(nameof(brokenCase)),
        };

        var error = Assert.Throws<PipelineDomainException>(() => ReasonCatalog.Replace(TenantId, ReasonKind.Loss, current, inputs, Later));

        Assert.Equal(PipelineErrorCodes.ReasonLayoutInvalid, error.Code);
        Assert.All(current, reason => Assert.Equal((true, Now), (reason.IsActive, reason.UpdatedAt)));
    }
}
