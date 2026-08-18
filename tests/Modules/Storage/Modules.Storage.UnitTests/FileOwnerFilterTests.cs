using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// CAT-09 — la regla del filtro por owner del listado de archivos, sin base de por medio.
///
/// Las dos cosas que decide: que un <c>ownerType</c> inválido **falle** en vez de ignorarse
/// —el error que CAT-05 ya corrigió una vez en el POST— y que los dos campos vayan juntos,
/// porque un <c>ownerId</c> sin tipo es un Guid que podría pertenecer a cualquiera de los
/// tres owners que comparten la columna.
/// </summary>
public sealed class FileOwnerFilterTests
{
    private static readonly Guid OwnerId = Guid.CreateVersion7();

    // CA-CAT-09-04: sin ninguno de los dos, no hay filtro y el listado se comporta como antes.
    [Fact]
    public void ResolveReturnsNoFilterWhenNeitherFieldIsSent()
    {
        Assert.Null(FileOwnerFilter.Resolve(ownerId: null, ownerType: null));
    }

    [Fact]
    public void ResolveAcceptsBothFieldsTogether()
    {
        var filter = FileOwnerFilter.Resolve(OwnerId, "Product");

        Assert.NotNull(filter);
        Assert.Equal(OwnerId, filter.OwnerId);
        Assert.Equal(FileOwnerType.Product, filter.OwnerType);
    }

    // El parseo es case-insensitive, igual que el del POST.
    [Theory]
    [InlineData("product")]
    [InlineData("PRODUCT")]
    [InlineData("  Product  ")]
    public void ResolveParsesTheOwnerTypeIgnoringCaseAndSurroundingSpace(string ownerType)
    {
        var filter = FileOwnerFilter.Resolve(OwnerId, ownerType);

        Assert.NotNull(filter);
        Assert.Equal(FileOwnerType.Product, filter.OwnerType);
    }

    // CA-CAT-09-02: un ownerType que no existe FALLA. No se ignora el filtro ni se devuelve la
    // lista completa, que es lo que hace hoy el filtro de status y es justo lo que no se repite.
    [Theory]
    [InlineData("Producto")]
    [InlineData("Usuario")]
    public void ResolveRejectsAnOwnerTypeThatDoesNotExist(string ownerType)
    {
        var error = Assert.Throws<StorageDomainException>(() =>
            FileOwnerFilter.Resolve(OwnerId, ownerType));

        Assert.Equal("storage.file.owner_type_invalid", error.Code);
    }

    // Un ownerType vacío o en blanco NO es un tipo inválido: es un tipo que no se mandó. La
    // primera versión de esta prueba esperaba owner_type_invalid y falló, que fue lo que obligó
    // a decidirlo. Gana «incompleto» porque es lo más útil para quien llama: mandar
    // `?ownerId=X&ownerType=` es exactamente el caso de tener medio filtro armado, y decirle
    // «el tipo no es válido» lo manda a revisar un valor que nunca escribió.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyOwnerTypeCountsAsMissingAndNotAsInvalid(string ownerType)
    {
        var error = Assert.Throws<StorageDomainException>(() =>
            FileOwnerFilter.Resolve(OwnerId, ownerType));

        Assert.Equal("storage.file.owner_filter_incomplete", error.Code);
    }

    // Enum.TryParse acepta el número crudo, y eso no es contrato: "4" no es Product aunque el
    // enum lo valga. Mismo descarte que hace el POST desde CAT-05.
    [Theory]
    [InlineData("4")]
    [InlineData("0")]
    public void ResolveRejectsTheRawEnumNumber(string ownerType)
    {
        var error = Assert.Throws<StorageDomainException>(() =>
            FileOwnerFilter.Resolve(OwnerId, ownerType));

        Assert.Equal("storage.file.owner_type_invalid", error.Code);
    }

    // CA-CAT-09-03: los dos juntos o ninguno. Medio filtro no es medio resultado, es un
    // resultado que nadie pidió.
    [Fact]
    public void ResolveRejectsAnOwnerIdWithoutItsType()
    {
        var error = Assert.Throws<StorageDomainException>(() =>
            FileOwnerFilter.Resolve(OwnerId, ownerType: null));

        Assert.Equal("storage.file.owner_filter_incomplete", error.Code);
    }

    [Fact]
    public void ResolveRejectsAnOwnerTypeWithoutItsId()
    {
        var error = Assert.Throws<StorageDomainException>(() =>
            FileOwnerFilter.Resolve(ownerId: null, ownerType: "Product"));

        Assert.Equal("storage.file.owner_filter_incomplete", error.Code);
    }

    // El id incompleto se detecta antes que el tipo inválido: si faltan los dos datos, el
    // problema es la forma de la petición, no el valor.
    [Fact]
    public void AnIncompleteFilterIsReportedBeforeAnInvalidType()
    {
        var error = Assert.Throws<StorageDomainException>(() =>
            FileOwnerFilter.Resolve(ownerId: null, ownerType: "Producto"));

        Assert.Equal("storage.file.owner_filter_incomplete", error.Code);
    }
}
