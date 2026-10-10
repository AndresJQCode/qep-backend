using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.7: el término de búsqueda por número y la foto del último mensaje.</summary>
public sealed class ConversationReadsTests
{
    // Revisión de la Task 14: un dígito suelto («Laura 2», «Calle 10») no es un número; si lo fuera,
    // wa_id LIKE '%2%' traería casi todas las conversaciones.
    [Theory]
    [InlineData("9999", "9999")]
    [InlineData("300 123 4567", "3001234567")]
    [InlineData("+57 300-123", "57300123")]
    [InlineData("a1234", "1234")]
    [InlineData("Laura 2", null)]
    [InlineData("Calle 10", null)]
    [InlineData("12", null)]
    [InlineData("a123", null)]
    [InlineData("lau", null)]
    public void OnlyATermThatLooksLikeANumberSearchesByNumber(string term, string? expected) =>
        Assert.Equal(expected, ConversationSearchTerms.NumberDigits(term));

    [Fact]
    public void APartialLastMessageSnapshotIsNullNotAnError()
    {
        var row = new ConversationRow(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", ConversationStatus.Open, 1,
            null, Guid.CreateVersion7(), LastMessageDirection: null, MessageKind.Text, "hola", MessageStatus.Delivered,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

        var summary = ConversationSummaryBuilder.ToSummary(row, new Dictionary<Guid, string>(), new Dictionary<string, CustomerRefDto>());

        Assert.Null(summary.LastMessage);
    }
}
