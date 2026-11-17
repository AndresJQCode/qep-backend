using Bootstrapper.Valkey;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// En <c>Production</c> la cadena de Valkey es obligatoria aunque todas las áreas estén apagadas (spec 2026-11-13, D-1):
/// sin ella, prender un área después sería un despliegue con el cliente a medias. Ningún mensaje lleva la cadena ni el
/// host: nombran la clave.
/// </summary>
public sealed class ValkeyOptionsValidatorTests
{
    private const string Key = "ConnectionStrings:Valkey";
    private const string ValidConnectionString = "valkey:6379,user=qep,password=SENTINEL-pw";

    [Fact]
    public void ProductionWithoutTheConnectionStringFails()
    {
        var result = Validator(Environments.Production, connectionString: null).Validate(null, new ValkeyOptions());

        Assert.True(result.Failed);
        Assert.Contains(Key, result.FailureMessage, StringComparison.Ordinal);
    }

    // El pipeline deja el token literal cuando la variable no está en Backend-prod: cuenta como ausente.
    [Fact]
    public void ProductionWithAnUnresolvedPipelineTokenFails()
    {
        var result = Validator(Environments.Production, "#{VALKEY_CONNECTION_STRING}#").Validate(null, new ValkeyOptions());

        Assert.True(result.Failed);
        Assert.DoesNotContain("#{", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionFailsEvenWithEveryAreaOff()
    {
        var result = Validator(Environments.Production, connectionString: null)
            .Validate(null, new ValkeyOptions { Enabled = false });

        Assert.True(result.Failed);
    }

    [Fact]
    public void DevelopmentWithoutTheConnectionStringPasses()
    {
        var result = Validator(Environments.Development, connectionString: null).Validate(null, new ValkeyOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ProductionWithAConnectionStringPasses()
    {
        var result = Validator(Environments.Production, ValidConnectionString).Validate(null, new ValkeyOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ANonPositiveTimeoutFails()
    {
        var result = Validator(Environments.Development, connectionString: null)
            .Validate(null, new ValkeyOptions { OperationTimeoutMs = 0 });

        Assert.True(result.Failed);
    }

    [Fact]
    public void NoFailureMessageCarriesTheValue()
    {
        var result = Validator(Environments.Production, ValidConnectionString)
            .Validate(null, new ValkeyOptions { OperationTimeoutMs = -1 });

        Assert.True(result.Failed);
        Assert.All(result.Failures!, failure => Assert.DoesNotContain("SENTINEL-pw", failure, StringComparison.Ordinal));
    }

    private static ValkeyOptionsValidator Validator(string environment, string? connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [Key] = connectionString })
            .Build();
        return new ValkeyOptionsValidator(new FixedEnvironment(environment), configuration);
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Bootstrapper.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
