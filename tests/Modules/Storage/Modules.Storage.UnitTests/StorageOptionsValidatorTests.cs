using Modules.Storage.Infrastructure;

namespace Modules.Storage.UnitTests;

public sealed class StorageOptionsValidatorTests
{
    private readonly StorageOptionsValidator _validator = new();

    [Fact]
    public void MissingR2CredentialsFails()
    {
        var result = _validator.Validate(name: null, new StorageOptions());
        Assert.True(result.Failed);
    }

    [Fact]
    public void R2WithCredentialsIsValid()
    {
        var options = new StorageOptions
        {
            R2 = new R2Options
            {
                AccountId = "acct",
                AccessKeyId = "key",
                SecretAccessKey = "secret",
                Bucket = "qep",
            },
        };

        var result = _validator.Validate(name: null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NonPositivePresignedExpiryFails()
    {
        var options = new StorageOptions
        {
            PresignedUrlMinutes = 0,
            R2 = new R2Options
            {
                AccountId = "acct",
                AccessKeyId = "key",
                SecretAccessKey = "secret",
                Bucket = "qep",
            },
        };

        var result = _validator.Validate(name: null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void PublicBucketAndBaseUrlMustBeConfiguredTogether()
    {
        var options = new StorageOptions
        {
            R2 = new R2Options
            {
                AccountId = "acct",
                AccessKeyId = "key",
                SecretAccessKey = "secret",
                Bucket = "private",
                PublicBucket = "public",
            },
        };

        Assert.True(_validator.Validate(name: null, options).Failed);
    }
}
