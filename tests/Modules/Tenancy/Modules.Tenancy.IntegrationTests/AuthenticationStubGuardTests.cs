using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Modules.Tenancy.IntegrationTests;

// Regression coverage for ADR 0001 required item #2: the development header-auth
// stub must be impossible to activate outside the Development environment, even
// via an explicit Authentication:UseDevelopmentStub=true override.
public sealed class AuthenticationStubGuardTests
{
    [Fact]
    public void DevelopmentStubCannotStartOutsideDevelopment()
    {
        using var factory = new NonDevelopmentStubFactory();

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Server);
        Assert.Contains("Authentication:UseDevelopmentStub", exception.Message, StringComparison.Ordinal);
    }

    private sealed class NonDevelopmentStubFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Authentication:UseDevelopmentStub", "true");
            // No real Postgres/R2 is reached: the guard throws during service
            // registration, before the database or storage client is touched. These
            // values only need to be present so earlier InvalidOperationException
            // checks (missing connection string, missing R2 config) don't fire first.
            builder.UseSetting(
                "ConnectionStrings:QepDatabase",
                "Host=localhost;Port=5432;Database=test;Username=test;Password=test");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
        }
    }
}
