using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §6.4, «Validadores»: la clave de <c>errors</c> es el nombre del query string.</summary>
public sealed class MessagingValidatorsTests
{
    private static string[] Keys<T>(FluentValidation.IValidator<T> validator, T instance) =>
        validator.Validate(instance).Errors.Select(error => error.PropertyName).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("Open", null, null, 30, new string[0])]
    [InlineData(null, null, null, null, new string[0])]
    [InlineData("Resolved", "laura", null, 50, new string[0])]
    [InlineData("Archived", null, null, 30, new[] { "status" })]
    [InlineData("open", null, null, 30, new[] { "status" })]
    [InlineData("Open", null, null, 0, new[] { "pageSize" })]
    [InlineData("Open", null, null, 51, new[] { "pageSize" })]
    public void ListConversationsNamesTheField(string? status, string? search, string? cursor, int? pageSize, string[] expected) =>
        Assert.Equal(expected, Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), status, search, cursor, pageSize)));

    [Fact]
    public void ListConversationsRejectsASearchOver100Characters() =>
        Assert.Equal(["search"], Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), "Open", new string('x', 101), null, 30)));

    // El cursor es opaco: lo que no salió de ConversationCursor.Encode es validation.failed con la clave «cursor».
    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-cursor")]
    [InlineData("MTIz")]
    public void ListConversationsRejectsACursorItDidNotIssue(string cursor) =>
        Assert.Equal(["cursor"], Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), null, null, cursor, null)));

    [Fact]
    public void ListConversationsAcceptsAnIssuedCursor()
    {
        var cursor = ConversationCursor.Encode(new ConversationCursor(DateTimeOffset.UtcNow, Guid.CreateVersion7()));

        Assert.Empty(Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), null, null, cursor, null)));
    }

    // Postgres guarda microsegundos: el cursor tiene que devolver exactamente la posición que leyó de la base.
    [Fact]
    public void AConversationCursorRoundTripsItsPosition()
    {
        var position = new ConversationCursor(new DateTimeOffset(2026, 10, 19, 13, 45, 7, TimeSpan.Zero).AddTicks(1234560), Guid.CreateVersion7());

        Assert.True(ConversationCursor.TryDecode(ConversationCursor.Encode(position), out var decoded));
        Assert.Equal(position, decoded);
    }

    // Spec 2026-10-10 §5.1: assigned ∈ me|none|all (default all); otro valor, con la clave «assigned».
    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("me", new string[0])]
    [InlineData("none", new string[0])]
    [InlineData("all", new string[0])]
    [InlineData("mine", new[] { "assigned" })]
    [InlineData("ME", new[] { "assigned" })]
    [InlineData("", new[] { "assigned" })]
    public void ListConversationsNamesTheAssigned(string? assigned, string[] expected) =>
        Assert.Equal(expected, Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), null, null, null, null, assigned)));

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData(1, new string[0])]
    [InlineData(100, new string[0])]
    [InlineData(0, new[] { "limit" })]
    [InlineData(101, new[] { "limit" })]
    public void ListMessagesNamesTheLimit(int? limit, string[] expected) =>
        Assert.Equal(expected, Keys(new ListMessagesValidator(), new ListMessagesQuery(Guid.CreateVersion7(), Guid.CreateVersion7(), limit, null)));

    [Fact]
    public void ListMessagesRejectsAnEmptyBefore() =>
        Assert.Equal(["before"], Keys(new ListMessagesValidator(), new ListMessagesQuery(Guid.CreateVersion7(), Guid.CreateVersion7(), 50, Guid.Empty)));

    // Spec 2026-10-10 §5.1: transferir exige a quién, con la clave «memberId».
    [Fact]
    public void TransferNamesAMissingOrEmptyMemberId()
    {
        Assert.Equal(["memberId"], Keys(new TransferConversationValidator(), new TransferConversationCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), null, 1)));
        Assert.Equal(["memberId"], Keys(new TransferConversationValidator(), new TransferConversationCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, 1)));
        Assert.Empty(Keys(new TransferConversationValidator(), new TransferConversationCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1)));
    }
}
