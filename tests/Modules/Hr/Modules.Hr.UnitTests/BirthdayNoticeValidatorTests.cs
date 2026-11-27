using FluentValidation;
using Modules.Hr.Application.BirthdayNotices;

namespace Modules.Hr.UnitTests;

/// <summary>Spec 2026-11-26 §5.1: the shape rules of the birthday notice request (the field map of the 422).</summary>
public sealed class BirthdayNoticeValidatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BirthdayNoticeRequest Request(
        IReadOnlyList<Guid>? managers = null, int? leadDays = 1, bool? whatsApp = false, Guid? connection = null, bool? email = true) =>
        new(managers ?? [Guid.NewGuid()], leadDays, whatsApp, connection, email);

    private static async Task<string[]> FailedFieldsAsync(BirthdayNoticeRequest request)
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => new BirthdayNoticeRequestValidator().ValidateAndThrowAsync(request, Ct));
        return exception.Errors.Select(error => error.PropertyName).Distinct().ToArray();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public async Task LeadDaysOutOfRangeFails(int leadDays) =>
        Assert.Equal(["leadDays"], await FailedFieldsAsync(Request(leadDays: leadDays)));

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task LeadDaysInsideTheRangePass(int leadDays) =>
        await new BirthdayNoticeRequestValidator().ValidateAndThrowAsync(Request(leadDays: leadDays), Ct);

    [Fact]
    public async Task MoreThan20ManagersFails()
    {
        var managers = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToList();

        Assert.Equal(["managerMemberIds"], await FailedFieldsAsync(Request(managers: managers)));
    }

    [Fact]
    public async Task TwentyManagersPassAndAnEmptyListMeansOff()
    {
        var validator = new BirthdayNoticeRequestValidator();

        await validator.ValidateAndThrowAsync(
            Request(managers: Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList()), Ct);
        await validator.ValidateAndThrowAsync(Request(managers: []), Ct);
    }

    [Fact]
    public async Task NullListIsRequired() =>
        Assert.Equal(["managerMemberIds"], await FailedFieldsAsync(Request() with { ManagerMemberIds = null }));
}
