using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// El agregado cliente (CLI-01, Fase 3/4).
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

    private static readonly Guid CityId =
        Guid.Parse("01900000-0000-7000-8000-000000000010");

    private static readonly ClientClassificationId ClassificationId =
        new(Guid.Parse("01900000-0000-7000-8000-000000000020"));

    private const string ClassificationPrefix = "MED";

    private static CustomerIdentification Identification(
        IdentificationType type = IdentificationType.Nit,
        string number = "900.123.456-1") =>
        new() { Type = type, Number = number };

    private static CustomerCommercialInfo Commercial(
        ClientClassificationId? classificationId = null,
        bool withRetention = false,
        bool vatSurplus = false) =>
        new()
        {
            ClassificationId = classificationId ?? ClassificationId,
            WithRetention = withRetention,
            VatSurplus = vatSurplus
        };

    private static readonly Guid OtherCityId =
        Guid.Parse("01900000-0000-7000-8000-000000000011");

    private static CustomerContactInfo ValidContact(Guid? cityId = null) =>
        new()
        {
            Phone = "310 935 2187",
            Email = "compras@verde.co",
            Address = "Calle 10 # 45-12",
            CityId = cityId ?? CityId
        };

    // Una direccion de envio distinta del domicilio, para probar que la libreta y el contacto
    // son dos datos: otra calle y otra ciudad.
    private static CustomerAddressDetails Warehouse(Guid? cityId = null) =>
        new()
        {
            Name = "Bodega Norte",
            Address = "Carrera 7 # 71-21",
            CityId = cityId ?? OtherCityId
        };

    // `cityId` alimenta las dos cosas que nacen del alta: la primera fila de la libreta y el
    // domicilio del cliente (decision 3 del spec 2026-09-18). El constructor crea la libreta
    // antes de asignar el contacto, asi que con Guid.Empty el codigo que sale es el de la
    // libreta (ver CreateRejectsAnEmptyCityId).
    private static Customer Create(
        string cuc = "CLI08000142",
        string name = "Verde Esencial S.A.S.",
        string? businessName = null,
        Guid? cityId = null,
        CustomerIdentification? identification = null,
        CustomerContactInfo? contact = null,
        CustomerCommercialInfo? commercial = null) =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            cuc,
            name,
            businessName,
            new CustomerAddressDetails
            {
                Name = name,
                Address = "Calle 10 # 45-12",
                CityId = cityId ?? CityId
            },
            identification ?? Identification(),
            contact ?? ValidContact(cityId),
            commercial ?? Commercial(),
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
            cuc: "  CLI08000142  ",
            name: "  Verde Esencial  ",
            identification: Identification(number: "  900-1  "));

        Assert.Equal("CLI08000142", customer.Cuc);
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

    // El CUC lo emite el backend (ICucGenerator + CucFormatter) y llega ya formado al agregado;
    // el agregado solo comprueba que llegue. Un cliente sin CUC es un cliente que la grilla pinta
    // con una celda vacia y que nadie puede buscar — la caja de busqueda del listado busca por CUC.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankCuc(string cuc)
    {
        var exception = Assert.Throws<CustomersDomainException>(() => Create(cuc: cuc));

        Assert.Equal("customers.customer.cuc_required", exception.Code);
    }

    // El fixture pasa Guid.Empty a la libreta y al contacto; el constructor crea la primera
    // direccion antes de asignar el contacto, asi que el codigo que sale es el de la libreta. El
    // del contacto lo cubre ContactInfoRejectsAnEmptyCityId.
    [Fact]
    public void CreateRejectsAnEmptyCityId()
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(cityId: Guid.Empty));

        Assert.Equal("customers.address.city_required", exception.Code);
    }

    // Misma razon que la ciudad: la clasificacion es una FK obligatoria (Fase 3), no el viejo enum
    // opcional Pequeno/Mediano/Grande.
    [Fact]
    public void CreateRejectsAnEmptyClassificationId()
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(commercial: Commercial(classificationId: new ClientClassificationId(Guid.Empty))));

        Assert.Equal("customers.customer.classification_required", exception.Code);
    }

    // El correo y el telefono son obligatorios al crear y al editar. Vacio y solo espacios cuentan
    // como ausente, igual que null: el formulario manda "" cuando el usuario borra el input.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankEmail(string? email)
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(contact: ValidContact() with { Email = email }));

        Assert.Equal("customers.customer.email_required", exception.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankPhone(string? phone)
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(contact: ValidContact() with { Phone = phone }));

        Assert.Equal("customers.customer.phone_required", exception.Code);
    }

    [Theory]
    [InlineData(null, "310 935 2187", "customers.customer.email_required")]
    [InlineData("compras@verde.co", "  ", "customers.customer.phone_required")]
    public void UpdateRejectsABlankEmailOrPhone(string? email, string? phone, string expectedCode)
    {
        var customer = Create();

        var exception = Assert.Throws<CustomersDomainException>(() => customer.Update(
            customer.Name,
            businessName: null,
            Identification(),
            ValidContact() with { Email = email, Phone = phone },
            Commercial(),
            ClassificationPrefix,
            Now.AddMinutes(5)));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(1, customer.Version);
    }

    [Fact]
    public void ContactInfoTrimsThePhoneAndTheEmail()
    {
        var customer = Create(contact: ValidContact() with
        {
            Phone = "  310 935 2187  ",
            Email = "  compras@verde.co  "
        });

        Assert.Equal("310 935 2187", customer.Phone);
        Assert.Equal("compras@verde.co", customer.Email);
    }

    // Mismo criterio que CompanyContactInfo: "Compras@Verde.CO" y "compras@verde.co" son la misma
    // casilla, y dejar las dos formas en base obliga a cada consumidor a normalizar de nuevo.
    [Fact]
    public void ContactInfoLowercasesTheEmail()
    {
        var customer = Create(contact: ValidContact() with
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
            () => Create(contact: ValidContact() with { Email = email }));

        Assert.Equal("customers.customer.email_invalid", exception.Code);
    }

    // Ya no es un vestigio: la direccion de contacto es el domicilio del cliente (spec
    // 2026-09-18).
    [Fact]
    public void ContactInfoRejectsAnAddressLongerThanTheColumn()
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(contact: ValidContact() with
            {
                Address = new string('d', CustomerContactInfo.AddressMaxLength + 1)
            }));

        Assert.Equal("customers.customer.address_too_long", exception.Code);
    }

    // Decision 3 del spec 2026-09-18: el alta guarda el domicilio en el cliente **y** siembra la
    // primera fila de la libreta con el mismo par. Son dos datos desde el nacimiento, no uno
    // derivado del otro.
    [Fact]
    public void CreateSeedsTheContactAddressAndTheFirstAddressBookRow()
    {
        var customer = Create();

        Assert.Equal("Calle 10 # 45-12", customer.Address);
        Assert.Equal(CityId, customer.CityId);
        var principal = Assert.Single(customer.Addresses);
        Assert.True(principal.IsPrincipal);
        Assert.Equal("Calle 10 # 45-12", principal.Address);
        Assert.Equal(CityId, principal.CityId);
    }

    // El bug que motivo el spec: marcar otra direccion de la libreta como principal movia el
    // domicilio del cliente. La libreta cambia de principal; el domicilio no se entera.
    [Fact]
    public void MakeAddressPrincipalDoesNotChangeTheContactAddress()
    {
        var customer = Create();
        var warehouse = customer.AddAddress(Warehouse(), isPrincipal: false, Now.AddMinutes(1));

        customer.MakeAddressPrincipal(warehouse.Id, Now.AddMinutes(2));

        Assert.Equal("Calle 10 # 45-12", customer.Address);
        Assert.Equal(CityId, customer.CityId);
        Assert.True(warehouse.IsPrincipal);
        Assert.Equal(warehouse.Id, customer.PrincipalAddress?.Id);
    }

    // Decision 4: el PUT escribe solo el contacto. La libreta —incluida la principal— queda como
    // estaba, aunque el domicilio nuevo tenga otra calle y otra ciudad.
    [Fact]
    public void UpdateChangesTheContactAddressAndLeavesTheAddressBookUntouched()
    {
        var customer = Create();
        var principal = Assert.Single(customer.Addresses);

        customer.Update(
            customer.Name,
            businessName: null,
            Identification(),
            ValidContact(OtherCityId) with { Address = "Carrera 7 # 71-21" },
            Commercial(),
            ClassificationPrefix,
            Now.AddMinutes(5));

        Assert.Equal("Carrera 7 # 71-21", customer.Address);
        Assert.Equal(OtherCityId, customer.CityId);
        var stillPrincipal = Assert.Single(customer.Addresses);
        Assert.Same(principal, stillPrincipal);
        Assert.Equal("Calle 10 # 45-12", stillPrincipal.Address);
        Assert.Equal(CityId, stillPrincipal.CityId);
        Assert.True(stillPrincipal.IsPrincipal);
    }

    // Codigo nuevo, mismo estilo que customers.address.address_required: la calle del domicilio
    // es obligatoria, como antes de CLI-DIR-01.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ContactInfoRejectsAnEmptyAddress(string address)
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(contact: ValidContact() with { Address = address }));

        Assert.Equal("customers.customer.address_required", exception.Code);
    }

    // El codigo existente de Customer.EnsureValidCityId, que desde CLI-DIR-01 no tenia caller.
    // Solo el contacto lleva Guid.Empty: la libreta del fixture nace con ciudad valida.
    [Fact]
    public void ContactInfoRejectsAnEmptyCityId()
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(contact: ValidContact() with { CityId = Guid.Empty }));

        Assert.Equal("customers.customer.city_required", exception.Code);
    }

    // Misma garantia de todo-o-nada que UpdateLeavesTheCustomerUntouchedWhenALaterFieldIsRejected,
    // para la ciudad: si EnsureValidCityId corriera solo dentro de Assign, el nombre nuevo ya
    // estaria pegado cuando la ciudad vacia se rechaza.
    [Fact]
    public void UpdateLeavesTheCustomerUntouchedWhenTheCityIsRejected()
    {
        var customer = Create(name: "Verde Esencial");

        var exception = Assert.Throws<CustomersDomainException>(() => customer.Update(
            "Nombre nuevo",
            businessName: null,
            Identification(),
            ValidContact(Guid.Empty),
            Commercial(),
            ClassificationPrefix,
            Now.AddMinutes(5)));

        Assert.Equal("customers.customer.city_required", exception.Code);
        Assert.Equal("Verde Esencial", customer.Name);
        Assert.Equal(CityId, customer.CityId);
        Assert.Equal(1, customer.Version);
    }

    // withRetention es obligatorio en el formulario y no tiene "sin definir": un cliente o retiene
    // o no. Por eso es bool y no bool?, y por eso Empty lo deja en false.
    [Fact]
    public void CommercialInfoDefaultsToNoRetention()
    {
        var customer = Create();

        Assert.False(customer.WithRetention);
    }

    [Fact]
    public void CommercialInfoKeepsWhatItIsGiven()
    {
        var classificationId = new ClientClassificationId(Guid.CreateVersion7());

        var customer = Create(commercial: Commercial(
            classificationId: classificationId,
            withRetention: true));

        Assert.Equal(classificationId, customer.ClassificationId);
        Assert.True(customer.WithRetention);
    }

    // Mismo criterio que withRetention: bool y no bool?, sin "sin definir".
    [Fact]
    public void CommercialInfoDefaultsToNoVatSurplus()
    {
        var customer = Create();

        Assert.False(customer.VatSurplus);
    }

    [Fact]
    public void CommercialInfoKeepsTheVatSurplusItIsGiven()
    {
        var customer = Create(commercial: Commercial(vatSurplus: true));

        Assert.True(customer.VatSurplus);
    }

    // El PUT reemplaza el recurso entero: el telefono y el correo que llegan pisan los guardados.
    [Fact]
    public void UpdateReplacesThePhoneAndTheEmail()
    {
        var customer = Create();

        customer.Update(
            "Verde Esencial S.A.S.",
            businessName: null,
            Identification(),
            ValidContact() with { Phone = "604 444 5566", Email = "Ventas@Verde.CO" },
            Commercial(),
            ClassificationPrefix,
            Now.AddMinutes(5));

        Assert.Equal("604 444 5566", customer.Phone);
        Assert.Equal("ventas@verde.co", customer.Email);
    }

    // La clasificacion se puede reemplazar en el Update: un cliente puede cambiar de categoria
    // comercial.
    [Fact]
    public void UpdateReplacesTheClassification()
    {
        var customer = Create();
        var newClassificationId = new ClientClassificationId(Guid.CreateVersion7());

        customer.Update(
            customer.Name,
            businessName: null,
            Identification(),
            ValidContact(),
            Commercial(classificationId: newClassificationId),
            "MAY",
            Now.AddMinutes(5));

        Assert.Equal(newClassificationId, customer.ClassificationId);
    }

    // El CUC no viaja en el request y Update no lo toca por si solo — pero regla de negocio
    // confirmada, si la clasificacion cambia, si reescribe el prefijo. Sin clasificacion nueva
    // (mismo Commercial() de siempre) el CUC entero se conserva.
    [Fact]
    public void UpdateKeepsTheCucWhenTheClassificationDoesNotChange()
    {
        var customer = Create(cuc: "CLI08000142");

        customer.Update(
            "Otro Nombre",
            businessName: null,
            Identification(number: "830-9"),
            ValidContact(),
            Commercial(),
            ClassificationPrefix,
            Now.AddMinutes(5));

        Assert.Equal("CLI08000142", customer.Cuc);
    }

    // La regla de negocio explicita: "cuando cambie el tamano del cliente, cambiara unicamente el
    // prefijo; el departamento y el consecutivo se conservaran". El CUC original tiene depto "08"
    // y consecutivo "000142" — esos ocho caracteres finales deben sobrevivir intactos, solo
    // cambia lo que viene antes.
    [Fact]
    public void UpdateRewritesOnlyThePrefixWhenTheClassificationChanges()
    {
        var customer = Create(cuc: "CLI08000142");
        var newClassificationId = new ClientClassificationId(Guid.CreateVersion7());

        customer.Update(
            customer.Name,
            businessName: null,
            Identification(),
            ValidContact(),
            Commercial(classificationId: newClassificationId),
            "MAY",
            Now.AddMinutes(5));

        Assert.Equal("MAY08000142", customer.Cuc);
    }

    // Los mismos ocho caracteres finales que UpdateRewritesOnlyThePrefixWhenTheClassificationChanges
    // prueba que sobreviven a un cambio de prefijo — la importacion masiva (Fase 8) matchea un
    // cliente existente por este mismo valor, no por el CUC completo.
    [Fact]
    public void StableSuffixOfReturnsTheLastEightCharacters()
    {
        Assert.Equal("08000142", Customer.StableSuffixOf("CLI08000142"));
        Assert.Equal("08000142", Customer.StableSuffixOf("MAY08000142"));
    }

    // Elegir de nuevo la misma clasificacion (mismo Id) no reescribe nada, aunque el prefijo que
    // llegue sea, por lo que fuera, distinto al que tiene guardado: sin cambio de clasificacion no
    // hay motivo de negocio para tocar el CUC.
    [Fact]
    public void UpdateKeepsTheCucWhenTheClassificationIdDoesNotChangeEvenIfAnotherPrefixArrives()
    {
        var customer = Create(cuc: "CLI08000142");

        customer.Update(
            customer.Name,
            businessName: null,
            Identification(),
            ValidContact(),
            Commercial(),
            "OTRO",
            Now.AddMinutes(5));

        Assert.Equal("CLI08000142", customer.Cuc);
    }

    // Defensivo: el prefijo llega ya validado desde ClientClassification.Prefix (no vacio, hasta
    // 20 caracteres), pero Update lo revalida siempre, cambie o no la clasificacion — un llamador
    // que rompa ese contrato tiene que enterarse con un codigo de dominio, no con un CUC corrupto.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateRejectsABlankClassificationPrefix(string prefix)
    {
        var customer = Create();

        var exception = Assert.Throws<CustomersDomainException>(() => customer.Update(
            customer.Name,
            businessName: null,
            Identification(),
            ValidContact(),
            Commercial(),
            prefix,
            Now.AddMinutes(5)));

        Assert.Equal("customers.customer.classification_prefix_required", exception.Code);
    }

    [Fact]
    public void UpdateIncrementsTheConcurrencyToken()
    {
        var customer = Create();
        var later = Now.AddMinutes(5);

        customer.Update(
            "Otro",
            businessName: null,
            Identification(number: "830-9"),
            ValidContact(),
            Commercial(),
            ClassificationPrefix,
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
            businessName: null,
            Identification(number: "   "),
            ValidContact(),
            Commercial(),
            ClassificationPrefix,
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
            businessName: null,
            Identification(),
            ValidContact(),
            Commercial(),
            ClassificationPrefix,
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
            businessName: null,
            Identification(),
            ValidContact(),
            Commercial(),
            ClassificationPrefix,
            Now.AddMinutes(2));

        Assert.True(customer.IsActive);
        Assert.Equal("Otro", customer.Name);
    }
}
