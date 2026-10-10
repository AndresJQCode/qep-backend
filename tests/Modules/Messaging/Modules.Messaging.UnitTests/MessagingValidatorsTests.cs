using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §6.4, «Validadores»: la clave de <c>errors</c> es el nombre del query string.</summary>
public sealed class MessagingValidatorsTests
{
    private static string[] Keys<T>(FluentValidation.IValidator<T> validator, T instance) =>
        validator.Validate(instance).Errors.Select(error => error.PropertyName).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("Open", null, 1, 30, new string[0])]
    [InlineData(null, null, null, null, new string[0])]
    [InlineData("Resolved", "laura", 2, 50, new string[0])]
    [InlineData("Archived", null, 1, 30, new[] { "status" })]
    [InlineData("open", null, 1, 30, new[] { "status" })]
    [InlineData("Open", null, 0, 30, new[] { "page" })]
    [InlineData("Open", null, 1, 0, new[] { "pageSize" })]
    [InlineData("Open", null, 1, 51, new[] { "pageSize" })]
    public void ListConversationsNamesTheField(string? status, string? search, int? page, int? pageSize, string[] expected) =>
        Assert.Equal(expected, Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), status, search, page, pageSize)));

    [Fact]
    public void ListConversationsRejectsASearchOver100Characters() =>
        Assert.Equal(["search"], Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), "Open", new string('x', 101), 1, 30)));

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
}
