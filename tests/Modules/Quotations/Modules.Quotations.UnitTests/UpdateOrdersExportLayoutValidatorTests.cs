using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El validador da el campo (spec 2026-09-24): `Columns[i].Header`, `Columns[i].Value`,
/// `Columns[i].Kind`. El dominio da el código; acá sólo se fija qué input marca el formulario.
/// </summary>
public sealed class UpdateOrdersExportLayoutValidatorTests
{
    private readonly UpdateOrdersExportLayoutValidator _validator = new();

    [Fact]
    public void AcceptsAWellFormedCommand()
    {
        Assert.True(_validator.Validate(NewCommand()).IsValid);
    }

    [Fact]
    public void RequiresTheColumns()
    {
        var failure = Assert.Single(_validator.Validate(NewCommand(withoutColumns: true)).Errors);

        Assert.Equal("Columns", failure.PropertyName);
    }

    // Recortado no vacío: sólo espacios es vacío.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankHeaderMarksItsColumn(string? header)
    {
        var columns = DefaultInputs();
        columns[3] = columns[3] with { Header = header };

        var failure = Assert.Single(_validator.Validate(NewCommand(columns)).Errors);

        Assert.Equal("Columns[3].Header", failure.PropertyName);
    }

    // El límite se mide recortado: 64 con espacios alrededor pasa; 65 no.
    [Fact]
    public void AHeaderLongerThanSixtyFourAfterTrimmingMarksItsColumn()
    {
        var accepted = DefaultInputs();
        accepted[0] = accepted[0] with { Header = $"  {new string('h', 64)}  " };
        Assert.True(_validator.Validate(NewCommand(accepted)).IsValid);

        var rejected = DefaultInputs();
        rejected[0] = rejected[0] with { Header = new string('h', 65) };
        var failure = Assert.Single(_validator.Validate(NewCommand(rejected)).Errors);
        Assert.Equal("Columns[0].Header", failure.PropertyName);
    }

    [Fact]
    public void AFixedValueLongerThan128MarksItsColumnAndAnEmptyOneIsValid()
    {
        var accepted = DefaultInputs();
        accepted.Insert(0, Fixed("Bodega", ""));
        Assert.True(_validator.Validate(NewCommand(accepted)).IsValid);

        var rejected = DefaultInputs();
        rejected.Insert(0, Fixed("Bodega", new string('v', 129)));
        var failure = Assert.Single(_validator.Validate(NewCommand(rejected)).Errors);
        Assert.Equal("Columns[0].Value", failure.PropertyName);
    }

    // Ronda de control, hallazgo P2: el tope de 128 sólo aplica a una fija. El valor de una del
    // catálogo no se guarda (el handler lo descarta en ToSetting), así que el validador no debe
    // rechazar un cuerpo por un campo que ni se persiste.
    [Fact]
    public void ACatalogValueIsNotValidatedEvenWhenItExceeds128Characters()
    {
        var columns = DefaultInputs();
        columns[0] = columns[0] with { Value = new string('v', 200) };

        Assert.True(_validator.Validate(NewCommand(columns)).IsValid);
    }

    // Exacto y ordinal, como viaja en el DTO: ni minúsculas, ni mayúsculas, ni el número del enum.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("catalog")]
    [InlineData("FIXED")]
    [InlineData("0")]
    [InlineData("Other")]
    public void AnUnknownKindMarksItsColumn(string? kind)
    {
        var columns = DefaultInputs();
        columns[1] = columns[1] with { Kind = kind };

        var failure = Assert.Single(_validator.Validate(NewCommand(columns)).Errors);

        Assert.Equal("Columns[1].Kind", failure.PropertyName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequiresAPositiveExpectedVersion(long expectedVersion)
    {
        var failure = Assert.Single(_validator.Validate(NewCommand(expectedVersion: expectedVersion)).Errors);

        Assert.Equal("ExpectedVersion", failure.PropertyName);
    }

    private static List<OrdersExportColumnInput> DefaultInputs() =>
        [.. OrdersExportLayout.Effective(stored: null)
            .Select(column => new OrdersExportColumnInput("Catalog", column.Key, column.Header, null, column.Visible))];

    private static OrdersExportColumnInput Fixed(string header, string? value) =>
        new("Fixed", null, header, value, Visible: true);

    // Sin argumentos manda el catálogo entero; withoutColumns manda la lista nula, que es lo que
    // llega con un cuerpo `{}`.
    private static UpdateOrdersExportLayoutCommand NewCommand(
        IReadOnlyList<OrdersExportColumnInput>? columns = null,
        long expectedVersion = 1,
        bool withoutColumns = false) =>
        new(Guid.CreateVersion7(), withoutColumns ? null : columns ?? DefaultInputs(), expectedVersion, "trace");
}
