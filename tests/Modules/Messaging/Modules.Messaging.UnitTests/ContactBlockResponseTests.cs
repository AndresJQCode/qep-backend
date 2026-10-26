using Modules.Messaging.Application;
using Xunit;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-26 §3.3: la tabla de respuestas de <c>block_users</c>. Lo que Meta no documenta cae en Failed.</summary>
public sealed class ContactBlockResponseTests
{
    private static MessagingGraphResult<BlockUsersResult> Failed2xx(int? code, int? subcode = null, string? trace = null) =>
        new(new BlockUsersResult(false, code, subcode, trace), null);

    private static MessagingGraphResult<BlockUsersResult> Http(int status, int? code, int? subcode = null, string? trace = null) =>
        new(null, new MessagingGraphFailure(status, code, $"http_{status}", Subcode: subcode, FbTraceId: trace));

    // Los enums son internal: un [Theory] público no puede recibirlos como parámetro, así que se recorren aquí.
    private static readonly ContactBlockDirection[] Directions = [ContactBlockDirection.Block, ContactBlockDirection.Unblock];

    [Fact]
    public void AnAppliedAnswerIsApplied()
    {
        foreach (var direction in Directions)
        {
            var answer = ContactBlockResponse.Classify(new MessagingGraphResult<BlockUsersResult>(new BlockUsersResult(true, null), null), direction);

            Assert.Equal(ContactBlockVerdict.Applied, answer.Verdict);
        }
    }

    [Fact]
    public void ClassifyMapsEachCode()
    {
        Assert.Equal(ContactBlockVerdict.WindowClosed, ContactBlockResponse.Classify(Failed2xx(131047), ContactBlockDirection.Block).Verdict);
        Assert.Equal(ContactBlockVerdict.WindowClosed, ContactBlockResponse.Classify(Http(400, 131047), ContactBlockDirection.Block).Verdict);
        Assert.Equal(ContactBlockVerdict.UnblockRejected, ContactBlockResponse.Classify(Failed2xx(131047), ContactBlockDirection.Unblock).Verdict);
        Assert.Equal(ContactBlockVerdict.UnblockRejected, ContactBlockResponse.Classify(Http(400, 131047), ContactBlockDirection.Unblock).Verdict);
        Assert.Equal(ContactBlockVerdict.ConnectionRejected, ContactBlockResponse.Classify(Http(401, 190), ContactBlockDirection.Block).Verdict);
        Assert.Equal(ContactBlockVerdict.ConnectionRejected, ContactBlockResponse.Classify(Http(400, 133010), ContactBlockDirection.Unblock).Verdict);
    }

    [Theory]
    [InlineData(139100)]
    [InlineData(139101)]
    [InlineData(139103)]
    [InlineData(130429)]
    public void CodesWeDoNotRecogniseAreFailed(int code)
    {
        foreach (var direction in Directions)
        {
            Assert.Equal(ContactBlockVerdict.Failed, ContactBlockResponse.Classify(Failed2xx(code), direction).Verdict);
            Assert.Equal(ContactBlockVerdict.Failed, ContactBlockResponse.Classify(Http(400, code), direction).Verdict);
        }
    }

    [Fact]
    public void NoAnswerOrA5xxIsFailed()
    {
        foreach (var direction in Directions)
        {
            Assert.Equal(ContactBlockVerdict.Failed, ContactBlockResponse.Classify(Http(0, null), direction).Verdict);
            Assert.Equal(ContactBlockVerdict.Failed, ContactBlockResponse.Classify(Http(500, null), direction).Verdict);
            Assert.Equal(ContactBlockVerdict.Failed, ContactBlockResponse.Classify(Failed2xx(null), direction).Verdict);
        }
    }

    [Fact]
    public void TheAnswerCarriesCodeSubcodeAndTraceForTheLog()
    {
        var fromFailedUsers = ContactBlockResponse.Classify(Failed2xx(139100, 33, "trace-a"), ContactBlockDirection.Block);
        var fromHttp = ContactBlockResponse.Classify(Http(400, 131047, 7, "trace-b"), ContactBlockDirection.Block);

        Assert.Equal((139100, 33, "trace-a"), (fromFailedUsers.Code, fromFailedUsers.Subcode, fromFailedUsers.FbTraceId));
        Assert.Equal((131047, 7, "trace-b"), (fromHttp.Code, fromHttp.Subcode, fromHttp.FbTraceId));
    }
}
