using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-29: el orden personal de las respuestas rápidas. Primero las del orden guardado de quien llama, en ese orden;
/// después el resto por atajo (el orden de siempre). Un id guardado que ya no existe se salta; al guardar, lo ajeno y lo repetido se
/// descartan sin fallar.</summary>
public sealed class QuickReplyOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");

    private static readonly QuickReply ADomicilio = QuickReply.Create(TenantId, "a-domicilio", "Hola", Now);
    private static readonly QuickReply Horarios = QuickReply.Create(TenantId, "horarios", "Hola", Now);
    private static readonly QuickReply Pago = QuickReply.Create(TenantId, "pago", "Hola", Now);
    private static readonly QuickReply[] ByShortcut = [ADomicilio, Horarios, Pago];

    private static string[] Shortcuts(IEnumerable<QuickReply> replies) => replies.Select(reply => reply.Shortcut).ToArray();

    [Fact]
    public void WithoutASavedOrderTheListStaysByShortcut() =>
        Assert.Equal(["a-domicilio", "horarios", "pago"], Shortcuts(QuickReplyOrder.Arrange(ByShortcut, [])));

    [Fact]
    public void TheSavedOnesGoFirstInTheirOrderAndTheRestFollowByShortcut() =>
        Assert.Equal(["pago", "a-domicilio", "horarios"], Shortcuts(QuickReplyOrder.Arrange(ByShortcut, [Pago.Id, ADomicilio.Id])));

    [Fact]
    public void ASavedIdThatNoLongerExistsIsSkipped() =>
        Assert.Equal(["horarios", "a-domicilio", "pago"], Shortcuts(QuickReplyOrder.Arrange(ByShortcut, [Guid.CreateVersion7(), Horarios.Id])));

    [Fact]
    public void ARepeatedSavedIdKeepsItsFirstPlace() =>
        Assert.Equal(["horarios", "pago", "a-domicilio"], Shortcuts(QuickReplyOrder.Arrange(ByShortcut, [Horarios.Id, Pago.Id, Horarios.Id])));

    [Fact]
    public void NormalizingDropsForeignIdsAndDuplicatesKeepingTheFirstOccurrence()
    {
        var foreign = Guid.CreateVersion7();

        var normalized = QuickReplyOrder.Normalize([Pago.Id, foreign, ADomicilio.Id, Pago.Id], ByShortcut.Select(reply => reply.Id));

        Assert.Equal([Pago.Id, ADomicilio.Id], normalized);
    }

    [Fact]
    public void NormalizingAnEmptyListIsEmpty() => Assert.Empty(QuickReplyOrder.Normalize([], ByShortcut.Select(reply => reply.Id)));

    [Fact]
    public void TheValidatorAcceptsAnEmptyListAndUpToTheCap()
    {
        var validator = new SaveQuickReplyOrderValidator();

        Assert.True(validator.Validate(new SaveQuickReplyOrderCommand(TenantId, [])).IsValid);
        Assert.True(validator.Validate(new SaveQuickReplyOrderCommand(TenantId, Ids(QuickReplyOrder.MaxItems))).IsValid);
    }

    [Fact]
    public void TheValidatorRejectsAMissingListOnItsKey()
    {
        var failure = Assert.Single(new SaveQuickReplyOrderValidator().Validate(new SaveQuickReplyOrderCommand(TenantId, null)).Errors);

        Assert.Equal("quickReplyIds", failure.PropertyName);
    }

    [Fact]
    public void TheValidatorRejectsMoreThanTheCapOnItsKey()
    {
        var failure = Assert.Single(new SaveQuickReplyOrderValidator().Validate(
            new SaveQuickReplyOrderCommand(TenantId, Ids(QuickReplyOrder.MaxItems + 1))).Errors);

        Assert.Equal("quickReplyIds", failure.PropertyName);
    }

    private static Guid[] Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToArray();
}
