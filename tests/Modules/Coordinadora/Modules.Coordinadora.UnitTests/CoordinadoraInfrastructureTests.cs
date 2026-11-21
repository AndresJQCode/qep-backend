using Microsoft.EntityFrameworkCore;
using Modules.Coordinadora.Domain;
using Modules.Coordinadora.Infrastructure.Api;
using Modules.Coordinadora.Infrastructure.Persistence;

namespace Modules.Coordinadora.UnitTests;

/// <summary>El modelo de EF de Coordinadora contra el snapshot, sin abrir una base (mismo criterio que <c>SiigoDbContextMappingTests</c>).</summary>
public sealed class CoordinadoraDbContextMappingTests
{
    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new CoordinadoraDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TheShipmentsMapToTheirTableWithTheLiveIndex()
    {
        using var context = new CoordinadoraDbContextFactory().CreateDbContext([]);

        var shipment = context.Model.FindEntityType(typeof(CoordinadoraShipment))!;

        Assert.Equal(("shipments", "coordinadora"), (shipment.GetTableName(), shipment.GetSchema()));
        Assert.Equal("text", shipment.FindProperty(nameof(CoordinadoraShipment.RequestJson))!.GetColumnType());
        var live = Assert.Single(shipment.GetIndexes(), index => index.GetDatabaseName() == CoordinadoraDbContext.ShipmentsTenantOrderLiveIndex);
        Assert.True(live.IsUnique);
        Assert.Equal("status IN ('Pending', 'Created', 'Unknown')", live.GetFilter());
        Assert.Null(shipment.FindProperty(nameof(CoordinadoraShipment.IsLive)));
    }

    [Fact]
    public void TheSettingsMapToTheirTableWithTheirDefaults()
    {
        using var context = new CoordinadoraDbContextFactory().CreateDbContext([]);

        var settings = context.Model.FindEntityType(typeof(CoordinadoraSettings))!;

        Assert.Equal(("settings", "coordinadora"), (settings.GetTableName(), settings.GetSchema()));
        Assert.Equal(false, settings.FindProperty(nameof(CoordinadoraSettings.CollectOnDelivery))!.GetDefaultValue());
        Assert.Equal(0L, settings.FindProperty(nameof(CoordinadoraSettings.Version))!.GetDefaultValue());
        Assert.True(settings.FindProperty(nameof(CoordinadoraSettings.Version))!.IsConcurrencyToken);
        Assert.Null(settings.FindProperty(nameof(CoordinadoraSettings.DefaultPackage)));
    }
}

/// <summary>Spec 2026-11-21 §8: las URL van por HTTPS y absolutas en todo ambiente; vacías valen las de producción.</summary>
public sealed class CoordinadoraOptionsValidatorTests
{
    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? ags, string? guides) =>
        new CoordinadoraOptionsValidator().Validate(null, new CoordinadoraOptions { AgsUrl = ags, GuidesUrl = guides });

    [Theory]
    [InlineData("http://ws.coordinadora.com/ags/1.5/server.php", "https://guias.coordinadora.com/ws/guias/1.6/server.php", "AgsUrl")]
    [InlineData("https://ws.coordinadora.com/ags/1.5/server.php", "guias.coordinadora.com", "GuidesUrl")]
    public void AUrlThatIsNotAbsoluteHttpsFailsNamingTheKey(string ags, string guides, string key)
    {
        var result = Validate(ags, guides);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains($"Coordinadora:{key}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("https://sandbox.coordinadora.com/ags/1.5/server.php", "https://sandbox.coordinadora.com/agw/ws/guias/1.6/server.php")]
    public void EmptyOrHttpsUrlsPass(string? ags, string? guides) => Assert.True(Validate(ags, guides).Succeeded);

    [Fact]
    public void EmptyUrlsResolveToProduction()
    {
        var options = new CoordinadoraOptions { AgsUrl = " ", GuidesUrl = null };

        Assert.Equal(new Uri(CoordinadoraOptions.DefaultAgsUrl), options.AgsUri());
        Assert.Equal(new Uri(CoordinadoraOptions.DefaultGuidesUrl), options.GuidesUri());
    }
}

/// <summary>La traducción de la base a los códigos del contrato, sin base.</summary>
public sealed class CoordinadoraUnitOfWorkTranslationTests
{
    [Fact]
    public void AConcurrencyFailureIsAConflict() =>
        Assert.IsType<BuildingBlocks.Application.RequestConcurrencyException>(
            CoordinadoraUnitOfWork.Translate(new DbUpdateConcurrencyException("changed")));

    [Fact]
    public void AnUnknownExceptionIsNotTranslated() => Assert.Null(CoordinadoraUnitOfWork.Translate(new InvalidOperationException("x")));
}
