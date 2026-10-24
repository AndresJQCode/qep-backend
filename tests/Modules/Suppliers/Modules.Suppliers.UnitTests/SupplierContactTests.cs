using Modules.Suppliers.Domain;

namespace Modules.Suppliers.UnitTests;

public sealed class SupplierContactTests
{
    private static string CodeOf(Action act) =>
        Assert.Throws<SuppliersDomainException>(act).Code;

    [Fact]
    public void CreateTrimsAndLowercasesTheEmail()
    {
        var contact = SupplierContact.Create("  Ana Pérez ", " Compras ", " 3001234567 ", " ANA@Proveedor.CO ");

        Assert.Equal("Ana Pérez", contact.Name);
        Assert.Equal("Compras", contact.Role);
        Assert.Equal("3001234567", contact.Phone);
        Assert.Equal("ana@proveedor.co", contact.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateWithoutNameThrowsNameRequired(string? name) =>
        Assert.Equal(
            "suppliers.supplier.contact_name_required",
            CodeOf(() => SupplierContact.Create(name, null, null, null)));

    [Fact]
    public void CreateWithNameOf121CharactersThrowsTooLong()
    {
        Assert.Equal(
            "suppliers.supplier.contact_name_too_long",
            CodeOf(() => SupplierContact.Create(new string('a', 121), null, null, null)));
        Assert.Equal(
            new string('a', 120),
            SupplierContact.Create(new string('a', 120), null, null, null).Name);
    }

    [Fact]
    public void CreateWithInvalidEmailThrows() =>
        Assert.Equal(
            "suppliers.supplier.contact_email_invalid",
            CodeOf(() => SupplierContact.Create("Ana", null, null, "ana@")));

    [Fact]
    public void CreateWithNullOrBlankRolePhoneAndEmailIsValid()
    {
        var contact = SupplierContact.Create("Ana", null, " ", "");

        Assert.Null(contact.Role);
        Assert.Null(contact.Phone);
        Assert.Null(contact.Email);
    }

    [Fact]
    public void CreateRejectsOverlongRoleAndPhone()
    {
        Assert.Equal(
            "suppliers.supplier.contact_role_too_long",
            CodeOf(() => SupplierContact.Create("Ana", new string('r', 81), null, null)));
        Assert.Equal(
            "suppliers.supplier.contact_phone_too_long",
            CodeOf(() => SupplierContact.Create("Ana", null, new string('1', 33), null)));
    }
}
