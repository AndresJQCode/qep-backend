using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Infrastructure.SecretProtection;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Secreto en reposo» (viene de 6612298): en producción todo exigido —una llave mal
/// pegada en el pipeline se descubre en el deploy—; fuera de producción sólo la activa, para que unos
/// user-secrets mal cargados no tumben las pruebas de integración. Ningún mensaje lleva un valor.
/// </summary>
public sealed class SecretProtectionOptionsValidatorTests
{
    private static readonly string GoodKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly string ShortKey = Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
    private const string NotBase64 = "esto-no-es-base64-SENTINEL";

    private static SecretProtectionOptions Options(string? active, params (string Id, string? Value)[] keys)
    {
        var options = new SecretProtectionOptions { ActiveKeyId = active };
        foreach (var (id, value) in keys)
        {
            options.Keys[id] = value;
        }

        return options;
    }

    private static SecretProtectionOptionsValidator ValidatorFor(string environment) =>
        new(new StubHostEnvironment(environment));

    [Fact]
    public void ProductionWithAnActiveKeyOf32BytesIsValid()
    {
        var result = ValidatorFor(Environments.Production).Validate(null, Options("k1", ("k1", GoodKey)));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProductionWithoutActiveKeyIdFails(string? active)
    {
        var result = ValidatorFor(Environments.Production).Validate(null, Options(active, ("k1", GoodKey)));

        Assert.True(result.Failed);
        Assert.Contains("Integrations:SecretProtection:ActiveKeyId", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ProductionWithoutTheActiveKeyValueFails(string? value)
    {
        var result = ValidatorFor(Environments.Production).Validate(null, Options("k1", ("k1", value)));

        Assert.True(result.Failed);
        Assert.Contains("Integrations:SecretProtection:Keys:k1", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("K1")]
    [InlineData("key-1")]
    [InlineData("k_1")]
    public void ProductionRejectsAKeyIdOutsideThePattern(string id)
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("k1", ("k1", GoodKey), (id, GoodKey)));

        Assert.True(result.Failed);
        Assert.Contains($"Integrations:SecretProtection:Keys:{id}", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadKeyValues))]
    public void ProductionRejectsAnyDeclaredKeyThatIsNot32Bytes(string bad)
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("k1", ("k1", GoodKey), ("k2", bad)));

        Assert.True(result.Failed);
        Assert.Contains("Integrations:SecretProtection:Keys:k2", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(bad, result.FailureMessage, StringComparison.Ordinal);
    }

    public static TheoryData<string> BadKeyValues => new() { ShortKey, NotBase64 };

    [Fact]
    public void OutsideProductionAMissingSectionIsValid()
    {
        var result = ValidatorFor(Environments.Development).Validate(null, new SecretProtectionOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OutsideProductionAMalformedInactiveKeyIsNotValidated()
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, Options("test", ("test", GoodKey), ("k1", NotBase64)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OutsideProductionAMalformedActiveKeyFails()
    {
        var result = ValidatorFor(Environments.Development).Validate(null, Options("k1", ("k1", NotBase64)));

        Assert.True(result.Failed);
        Assert.Contains("Integrations:SecretProtection:Keys:k1", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(NotBase64, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideProductionAnActiveKeyWithoutValueFails()
    {
        var result = ValidatorFor(Environments.Development).Validate(null, Options("k1", ("k1", "")));

        Assert.True(result.Failed);
    }

    // El harness fija Keys:k1 = "" para tapar los user-secrets del developer: vacío es ausente.
    [Fact]
    public void AnEmptyInactiveKeyIsAbsentEvenInProduction()
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("test", ("test", GoodKey), ("k1", "")));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NoFailureMessageEverContainsAKeyValue()
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("k1", ("k1", ShortKey), ("BAD", GoodKey)));

        Assert.True(result.Failed);
        Assert.DoesNotContain(ShortKey, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(GoodKey, result.FailureMessage, StringComparison.Ordinal);
    }

    internal sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Modules.Integrations.UnitTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
