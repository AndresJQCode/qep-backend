using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// El agregado cliente (CLI-01).
///
/// Los anchos y los conjuntos de valores salen del formulario que ya existe en el frontend
/// (<c>features/customers/types/customer-form.schema.ts</c>) y del contrato del slice. El dominio
/// los hace cumplir aca para que un valor invalido salga como 422 con codigo de dominio en vez de
/// llegar a PostgreSQL y volver como 500.
/// </summary>
public sealed class CustomerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid TenantId =
        Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static CustomerIdentification Identification(
        IdentificationType type = IdentificationType.Nit,
        string number = "900.123.456-1") =>
        new() { Type = type, Number = number };

    private static Customer Create(
        string cuc = "CUC-000142",
        string name = "Verde Esencial S.A.S.",
        CustomerIdentification? identification = null,
        CustomerContactInfo? contact = null,
        CustomerCommercialInfo? commercial = null) =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            cuc,
            name,
            identification ?? Identification(),
            contact ?? CustomerContactInfo.Empty,
            commercial ?? CustomerCommercialInfo.Empty,
            Now);

    [Fact]
    public void CreateStartsActiveAtVersionOne()
    {
        var customer = Create();

        Assert.True(customer.IsActive);
        Assert.Equal(1, customer.Version);
        Assert.Equal(Now, customer.CreatedAt);
        Assert.Equal(Now, customer.UpdatedAt);
    }

    // Recortar es parte del invariante, no higiene del llamador: el indice unico de identificacion
    // trata " 900-1" y "900-1" como dos documentos distintos, cosa que nadie leyendo la lista haria.
    [Fact]
    public void CreateTrimsTheIdentifyingFields()
    {
        var customer = Create(
            cuc: "  CUC-000142  ",
            name: "  Verde Esencial  ",
            identification: Identification(number: "  900-1  "));

        Assert.Equal("CUC-000142", customer.Cuc);
        Assert.Equal("Verde Esencial", customer.Name);
        Assert.Equal("900-1", customer.Identification.Number);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankName(string name)
    {
        var exception = Assert.Throws<CustomersDomainException>(() => Create(name: name));

        Assert.Equal("customers.customer.name_required", exception.Code);
    }

    [Fact]
    public void CreateRejectsANameLongerThanTheColumn()
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(name: new string('a', Customer.NameMaxLength + 1)));

        Assert.Equal("customers.customer.name_too_long", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankIdentificationNumber(string number)
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(identification: Identification(number: number)));

        Assert.Equal("customers.customer.identification_number_required", exception.Code);
    }

    [Fact]
    public void CreateRejectsAnIdentificationNumberLongerThanTheColumn()
    {
        var exception = Assert.Throws<CustomersDomainException>(() => Create(
            identification: Identification(
                number: new string('9', CustomerIdentification.NumberMaxLength + 1))));

        Assert.Equal("customers.customer.identification_number_too_long", exception.Code);
    }

    // El CUC lo emite el backend y llega ya formado al agregado; el agregado solo comprueba que
    // llegue. Un cliente sin CUC es un cliente que la grilla pinta con una celda vacia y que nadie
    // puede buscar — la caja de busqueda del listado busca por CUC.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankCuc(string cuc)
    {
        var exception = Assert.Throws<CustomersDomainException>(() => Create(cuc: cuc));

        Assert.Equal("customers.customer.cuc_required", exception.Code);
    }

    [Fact]
    public void ContactInfoTrimsAndNormalizesBlankToNull()
    {
        var customer = Create(contact: new CustomerContactInfo
        {
            Phone = "  310 935 2187  ",
            Email = "   ",
            Address = "",
            Department = "  Antioquia  ",
            City = "  Medellin  "
        });

        Assert.Equal("310 935 2187", customer.Phone);
        Assert.Null(customer.Email);
        Assert.Null(customer.Address);
        Assert.Equal("Antioquia", customer.Department);
        Assert.Equal("Medellin", customer.City);
    }

    // Mismo criterio que CompanyContactInfo: "Compras@Verde.CO" y "compras@verde.co" son la misma
    // casilla, y dejar las dos formas en base obliga a cada consumidor a normalizar de nuevo.
    [Fact]
    public void ContactInfoLowercasesTheEmail()
    {
        var customer = Create(contact: new CustomerContactInfo
        {
            Email = "Compras@VerdeEsencial.CO"
        });

        Assert.Equal("compras@verdeesencial.co", customer.Email);
    }

    [Theory]
    [InlineData("no-arroba")]
    [InlineData("con espacio@verde.co")]
    [InlineData("@verde.co")]
    public void ContactInfoRejectsAnInvalidEmail(string email)
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(contact: new CustomerContactInfo { Email = email }));

        Assert.Equal("customers.customer.email_invalid", exception.Code);
    }

    [Fact]
    public void ContactInfoRejectsADepartmentLongerThanTheColumn()
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(contact: new CustomerContactInfo
            {
                Department = new string('d', CustomerContactInfo.DepartmentMaxLength + 1)
            }));

        Assert.Equal("customers.customer.department_too_long", exception.Code);
    }

    // withRetention es obligatorio en el formulario y no tiene "sin definir": un cliente o retiene
    // o no. Por eso es bool y no bool?, y por eso Empty lo deja en false.
    [Fact]
    public void CommercialInfoDefaultsToNoRetentionAndNoPriceList()
    {
        var customer = Create();

        Assert.False(customer.WithRetention);
        Assert.Null(customer.PriceListId);
        Assert.Null(customer.Classification);
    }

    [Fact]
    public void CommercialInfoKeepsWhatItIsGiven()
    {
        var priceListId = Guid.CreateVersion7();

        var customer = Create(commercial: new CustomerCommercialInfo
        {
            Classification = CustomerClassification.Mediano,
            PriceListId = priceListId,
            WithRetention = true
        });

        Assert.Equal(CustomerClassification.Mediano, customer.Classification);
        Assert.Equal(priceListId, customer.PriceListId);
        Assert.True(customer.WithRetention);
    }

    // El PUT reemplaza el recurso entero: un campo ausente se **limpia**. Una implementacion que
    // ignore los null "para no pisar" deja campos imborrables y pasa todas las demas pruebas.
    [Fact]
    public void UpdateClearsTheOptionalFieldsThatArriveNull()
    {
        var customer = Create(contact: new CustomerContactInfo
        {
            Phone = "310 935 2187",
            Email = "compras@verde.co",
            Address = "Calle 10 # 45-12",
            Department = "Antioquia",
            City = "Medellin"
        });

        customer.Update(
            "Verde Esencial S.A.S.",
            Identification(),
            CustomerContactInfo.Empty,
            CustomerCommercialInfo.Empty,
            Now.AddMinutes(5));

        Assert.Null(customer.Phone);
        Assert.Null(customer.Email);
        Assert.Null(customer.Address);
        Assert.Null(customer.Department);
        Assert.Null(customer.City);
    }

    // El CUC no viaja en el request y Update no lo toca: lo emite el backend una sola vez, al
    // crear. Un CUC editable convierte en mutable el identificador con el que el usuario habla del
    // cliente por telefono.
    [Fact]
    public void UpdateNeverChangesTheCuc()
    {
        var customer = Create(cuc: "CUC-000142");

        customer.Update(
            "Otro Nombre",
            Identification(number: "830-9"),
            CustomerContactInfo.Empty,
            CustomerCommercialInfo.Empty,
            Now.AddMinutes(5));

        Assert.Equal("CUC-000142", customer.Cuc);
    }

    [Fact]
    public void UpdateIncrementsTheConcurrencyToken()
    {
        var customer = Create();
        var later = Now.AddMinutes(5);

        customer.Update(
            "Otro",
            Identification(number: "830-9"),
            CustomerContactInfo.Empty,
            CustomerCommercialInfo.Empty,
            later);

        Assert.Equal(2, customer.Version);
        Assert.Equal(later, customer.UpdatedAt);
        Assert.Equal(Now, customer.CreatedAt);
    }

    // Update valida todo antes de asignar nada. Sin eso, un nombre valido seguido de un documento
    // invalido deja el nombre nuevo pegado en la instancia que EF sigue rastreando, aunque el
    // llamador se lleve un 422 que dice que no se guardo nada. Es el mismo defecto que EMP-08
    // corrigio en Company.
    [Fact]
    public void UpdateLeavesTheCustomerUntouchedWhenALaterFieldIsRejected()
    {
        var customer = Create(name: "Verde Esencial");

        Assert.Throws<CustomersDomainException>(() => customer.Update(
            "Nombre nuevo",
            Identification(number: "   "),
            CustomerContactInfo.Empty,
            CustomerCommercialInfo.Empty,
            Now.AddMinutes(5)));

        Assert.Equal("Verde Esencial", customer.Name);
        Assert.Equal(1, customer.Version);
    }

    [Fact]
    public void UpdateRejectsAnInactiveCustomer()
    {
        var customer = Create();
        customer.Deactivate(Now);

        var exception = Assert.Throws<CustomersDomainException>(() => customer.Update(
            "Otro",
            Identification(),
            CustomerContactInfo.Empty,
            CustomerCommercialInfo.Empty,
            Now));

        Assert.Equal("customers.customer.inactive", exception.Code);
    }

    [Fact]
    public void DeactivateTwiceIsRejected()
    {
        var customer = Create();
        customer.Deactivate(Now);

        var exception = Assert.Throws<CustomersDomainException>(() => customer.Deactivate(Now));

        Assert.Equal("customers.customer.already_inactive", exception.Code);
    }

    [Fact]
    public void ActivateAnAlreadyActiveCustomerIsRejected()
    {
        var customer = Create();

        var exception = Assert.Throws<CustomersDomainException>(() => customer.Activate(Now));

        Assert.Equal("customers.customer.already_active", exception.Code);
    }

    /// <summary>
    /// Sin <c>Activate</c>, un cliente inactivo seria terminal: <c>Update</c> abre con
    /// <c>EnsureActive</c> y nada devuelve <c>IsActive</c> a true.
    ///
    /// `CLI-01` no lo pide —solo lista <c>/deactivate</c>—, pero es exactamente la falta que
    /// `CAT-07` tuvo que corregir en producto despues de entregarlo y que `EMP-08` ya nacio
    /// cubriendo. No estrena permiso: reactivar es administrar.
    /// </summary>
    [Fact]
    public void ActivateRestoresEditability()
    {
        var customer = Create();
        customer.Deactivate(Now);

        customer.Activate(Now.AddMinutes(1));
        customer.Update(
            "Otro",
            Identification(),
            CustomerContactInfo.Empty,
            CustomerCommercialInfo.Empty,
            Now.AddMinutes(2));

        Assert.True(customer.IsActive);
        Assert.Equal("Otro", customer.Name);
    }
}
