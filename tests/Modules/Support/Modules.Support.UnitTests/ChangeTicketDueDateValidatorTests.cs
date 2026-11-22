using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>
/// El validador de <c>PUT /tickets/{id}/due-date</c> (spec 2026-11-22 §5.2): <c>dueOn</c> es una fecha <c>yyyy-MM-dd</c> o null (quita la
/// fecha, D20) y la versión viene del <c>If-Match</c>. Que no sea anterior a hoy lo juzga el dominio, con la zona del tenant.
/// </summary>
public sealed class ChangeTicketDueDateValidatorTests
{
    private static List<string> Errors(string? dueOn, long version = 1) =>
        new ChangeTicketDueDateValidator()
            .Validate(ChangeTicketDueDateCommand.From(Guid.CreateVersion7(), Guid.CreateVersion7(), new ChangeTicketDueDateRequest(dueOn), version))
            .Errors.Select(error => error.PropertyName)
            .ToList();

    [Theory]
    [InlineData("2026-11-30")]
    [InlineData(null)]
    public void AnIsoDateOrNullPasses(string? dueOn) => Assert.Empty(Errors(dueOn));

    [Theory]
    [InlineData("")]
    [InlineData("30/11/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-11-30T00:00:00")]
    [InlineData("pronto")]
    public void AnythingElseFailsOnItsField(string dueOn) => Assert.Equal(["DueOn"], Errors(dueOn));

    [Fact]
    public void TheVersionMustBePositive() => Assert.Equal(["ExpectedVersion"], Errors("2026-11-30", version: 0));
}
