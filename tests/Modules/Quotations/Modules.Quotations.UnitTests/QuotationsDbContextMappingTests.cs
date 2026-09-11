using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La columna de consumidor final, contra el modelo de EF y no contra una base. El nombre en
/// snake_case lo fija el mapeo a mano —por convencion EF la llamaria "BillsToFinalConsumer"— y un
/// nombre equivocado no lo ve el compilador: lo ve la migracion, que crearia otra columna.
/// </summary>
public sealed class QuotationsDbContextMappingTests
{
    [Fact]
    public void BillsToFinalConsumerMapsToItsSnakeCaseColumn()
    {
        // El factory de diseño arma el contexto sin abrir conexion: construir el modelo no la
        // necesita.
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        // El modelo de diseño y no `context.Model`: el de runtime puede recortar anotaciones que
        // solo usan las migraciones.
        var model = context.GetService<IDesignTimeModel>().Model;

        var property = model.FindEntityType(typeof(Quotation))!
            .FindProperty(nameof(Quotation.BillsToFinalConsumer))!;

        Assert.Equal("bills_to_final_consumer", property.GetColumnName());
        Assert.False(property.IsNullable);
    }
}
