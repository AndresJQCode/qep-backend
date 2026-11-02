using FluentValidation;
using Modules.ApiAccess.Application;

namespace Modules.ApiAccess.UnitTests;

public sealed class ListApiKeyRequestsTests
{
    [Theory]
    [InlineData("success", ApiKeyRequestResult.Success)]
    [InlineData("FAILURE", ApiKeyRequestResult.Failure)]
    public void TheResultIsReadWithoutCase(string value, ApiKeyRequestResult expected) =>
        Assert.Equal(expected, ApiKeyRequestFilter.ParseResult(value));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void AnEmptyResultIsNoFilter(string? value) =>
        Assert.Null(ApiKeyRequestFilter.ParseResult(value));

    [Fact]
    public void AnUnknownResultIsAFieldError()
    {
        var error = Assert.Throws<ValidationException>(() => ApiKeyRequestFilter.ParseResult("maybe"));
        Assert.Equal("result", Assert.Single(error.Errors).PropertyName);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(101, 100)]
    [InlineData(40, 40)]
    public void ThePageSizeStaysInRange(int asked, int expected) =>
        Assert.Equal(expected, ApiKeyRequestPaging.NormalizePageSize(asked));
}
