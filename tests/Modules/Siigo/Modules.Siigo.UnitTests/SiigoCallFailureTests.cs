using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

public sealed class SiigoCallFailureTests
{
    private static SiigoCallFailure Create(string curl = "curl.exe -X POST \"https://api.siigo.com/v1/invoices\"", string? response = "{}") =>
        SiigoCallFailure.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SiigoCallOperation.InvoiceCreate,
            "POST",
            "v1/invoices",
            400,
            "http_400",
            "abc123",
            null,
            curl,
            response,
            Guid.CreateVersion7(),
            "00-trace-span-01",
            new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero));

    [Fact]
    public void CreateTruncatesTheCurlAndTheResponseToTheirCaps()
    {
        var failure = Create(new string('c', SiigoCallFailure.RequestCurlMaxLength + 1), new string('r', SiigoCallFailure.ResponseBodyMaxLength + 1));

        Assert.Equal(SiigoCallFailure.RequestCurlMaxLength, failure.RequestCurl.Length);
        Assert.Equal(SiigoCallFailure.ResponseBodyMaxLength, failure.ResponseBody!.Length);
    }

    [Fact]
    public void CreateKeepsAnEmptyResponseAsNull()
    {
        Assert.Null(Create(response: "  ").ResponseBody);
        Assert.Null(Create(response: null).ResponseBody);
    }

    [Fact]
    public void CreateKeepsTheFieldsAsGiven()
    {
        var failure = Create();

        Assert.NotEqual(Guid.Empty, failure.Id);
        Assert.Equal(SiigoCallOperation.InvoiceCreate, failure.Operation);
        Assert.Equal<(string, string, int?, string, string?)>(
            ("POST", "v1/invoices", 400, "http_400", "abc123"),
            (failure.Method, failure.Path, failure.Status, failure.Reason, failure.IdempotencyKey));
    }

    [Fact]
    public void ToStringDoesNotPrintTheCurl()
    {
        var failure = Create("curl.exe SENTINEL-CURL");

        Assert.DoesNotContain("SENTINEL-CURL", failure.ToString(), StringComparison.Ordinal);
    }
}
