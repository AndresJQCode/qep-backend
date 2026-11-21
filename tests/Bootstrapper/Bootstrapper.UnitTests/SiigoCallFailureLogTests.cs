using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Platform.Application;
using Modules.Platform.Domain;
using Modules.Siigo.Application;
using Xunit;

namespace Bootstrapper.UnitTests;

public sealed class SiigoCallFailureLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);
    private static readonly SiigoCallFailure Failure = new(400, "http_400", "curl.exe -X POST \"https://api.siigo.com/v1/invoices\"\nResponse: HTTP 400");

    private sealed class RecordingLog : IRequestFailureLog
    {
        public List<RequestFailure> Recorded { get; } = [];

        public Task RecordAsync(RequestFailure failure, CancellationToken cancellationToken)
        {
            Recorded.Add(failure);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static (SiigoCallFailureLog Adapter, RecordingLog Log) Build(string? method, string path = "/api/v1/tenants/01900000-0000-7000-8000-000000000003/siigo/orders/x/invoice")
    {
        var log = new RecordingLog();
        var accessor = new HttpContextAccessor();
        if (method is not null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            accessor.HttpContext = context;
        }

        return (new SiigoCallFailureLog(accessor, log, new FixedClock(), NullLogger<SiigoCallFailureLog>.Instance), log);
    }

    [Fact]
    public async Task AFailureInAWriteRequestGoesToTheRequestLogWithTheCurlInItsDetail()
    {
        var (adapter, log) = Build("POST");

        await adapter.RecordAsync(Failure, TestContext.Current.CancellationToken);

        var row = Assert.Single(log.Recorded);
        Assert.Equal("POST", row.Method);
        Assert.Equal("siigo", row.Module);
        Assert.Equal(Guid.Parse("01900000-0000-7000-8000-000000000003"), row.TenantId);
        Assert.Equal("siigo.call_failed", row.ErrorCode);
        Assert.Equal(400, row.StatusCode);
        Assert.Contains("curl.exe -X POST", row.Detail, StringComparison.Ordinal);
        Assert.Equal(Now, row.OccurredAt);
    }

    [Fact]
    public async Task AReadRequestLeavesNothingLikeTheRestOfTheLog()
    {
        var (adapter, log) = Build("GET");

        await adapter.RecordAsync(Failure, TestContext.Current.CancellationToken);

        Assert.Empty(log.Recorded);
    }

    [Fact]
    public async Task WithoutARequestThereIsNothingToRecord()
    {
        var (adapter, log) = Build(method: null);

        await adapter.RecordAsync(Failure, TestContext.Current.CancellationToken);

        Assert.Empty(log.Recorded);
    }

    [Fact]
    public async Task ANetworkFailureWithoutStatusIsRecordedAsBadGateway()
    {
        var (adapter, log) = Build("POST");

        await adapter.RecordAsync(Failure with { Status = null, Reason = "timeout" }, TestContext.Current.CancellationToken);

        Assert.Equal(502, Assert.Single(log.Recorded).StatusCode);
    }
}
