using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>Task-notices spec 2026-11-22 D-2: the body of <c>read-by-resource</c> is validated before it touches the bell.</summary>
public sealed class MarkResourceReadValidatorTests
{
    private readonly MarkResourceNotificationsReadValidator _validator = new();

    [Theory]
    [InlineData("supporttaskassigned")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("NotAKind")]
    public void RejectsAnUnknownKind(string? kind)
    {
        var result = _validator.Validate(new MarkResourceNotificationsReadCommand(Guid.CreateVersion7(), kind!, Guid.CreateVersion7()));

        Assert.Contains(result.Errors, error => error.PropertyName == "kind");
    }

    [Fact]
    public void RejectsAnEmptyResource()
    {
        var result = _validator.Validate(new MarkResourceNotificationsReadCommand(Guid.CreateVersion7(), "SupportTaskAssigned", Guid.Empty));

        Assert.Contains(result.Errors, error => error.PropertyName == "resourceId");
    }

    [Theory]
    [InlineData("SupportTaskAssigned")]
    [InlineData("SupportTicketAssigned")]
    public void AcceptsAnExactKindWithAResource(string kind)
    {
        var result = _validator.Validate(new MarkResourceNotificationsReadCommand(Guid.CreateVersion7(), kind, Guid.CreateVersion7()));

        Assert.True(result.IsValid);
    }
}
