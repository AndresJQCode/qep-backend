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

    private static Customer Create(
        string cuc = "CLI08000142",
        string name = "Verde Esencial S.A.S.",
        Guid? cityId = null,
        CustomerIdentification? identification = null,
        CustomerContactInfo? contact = null,
        CustomerCommercialInfo? commercial = null) =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            cuc,
            name,
            cityId ?? CityId,
            identification ?? Identification(),
            contact ?? CustomerContactInfo.Empty,
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

    // La ciudad es una FK obligatoria de primer nivel (Fase 3): un Guid.Empty no es "sin ciudad",
    // es un dato mal formado, y el dominio lo rechaza antes de que llegue a la FK de base.
    [Fact]
    public void CreateRejectsAnEmptyCityId()
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(cityId: Guid.Empty));

        Assert.Equal("customers.customer.city_required", exception.Code);
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

    [Fact]
    public void ContactInfoTrimsAndNormalizesBlankToNull()
    {
        var customer = Create(contact: new CustomerContactInfo
        {
            Phone = "  310 935 2187  ",
            Email = "   ",
            Address = ""
        });

        Assert.Equal("310 935 2187", customer.Phone);
        Assert.Null(customer.Email);
        Assert.Null(customer.Address);
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
    public void ContactInfoRejectsAnAddressLongerThanTheColumn()
    {
        var exception = Assert.Throws<CustomersDomainException>(
            () => Create(contact: new CustomerContactInfo
            {
                Address = new string('d', CustomerContactInfo.AddressMaxLength + 1)
            }));

        Assert.Equal("customers.customer.address_too_long", exception.Code);
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

    // El PUT reemplaza el recurso entero: un campo ausente se **limpia**. Una implementacion que
    // ignore los null "para no pisar" deja campos imborrables y pasa todas las demas pruebas.
    [Fact]
    public void UpdateClearsTheOptionalFieldsThatArriveNull()
    {
        var customer = Create(contact: new CustomerContactInfo
        {
            Phone = "310 935 2187",
            Email = "compras@verde.co",
            Address = "Calle 10 # 45-12"
        });

        customer.Update(
            "Verde Esencial S.A.S.",
            CityId,
            Identification(),
            CustomerContactInfo.Empty,
            Commercial(),
            ClassificationPrefix,
            Now.AddMinutes(5));

        Assert.Null(customer.Phone);
        Assert.Null(customer.Email);
        Assert.Null(customer.Address);
    }

    // La ciudad y la clasificacion se pueden reemplazar en el Update: un cliente se puede mudar de
    // ciudad o cambiar de categoria comercial.
    [Fact]
    public void UpdateReplacesTheCityAndTheClassification()
    {
        var customer = Create();
        var newCityId = Guid.CreateVersion7();
        var newClassificationId = new ClientClassificationId(Guid.CreateVersion7());

        customer.Update(
            customer.Name,
            newCityId,
            Identification(),
            CustomerContactInfo.Empty,
            Commercial(classificationId: newClassificationId),
            "MAY",
            Now.AddMinutes(5));

        Assert.Equal(newCityId, customer.CityId);
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
            CityId,
            Identification(number: "830-9"),
            CustomerContactInfo.Empty,
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
            CityId,
            Identification(),
            CustomerContactInfo.Empty,
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
            CityId,
            Identification(),
            CustomerContactInfo.Empty,
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
            CityId,
            Identification(),
            CustomerContactInfo.Empty,
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
            CityId,
            Identification(number: "830-9"),
            CustomerContactInfo.Empty,
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
            CityId,
            Identification(number: "   "),
            CustomerContactInfo.Empty,
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
            CityId,
            Identification(),
            CustomerContactInfo.Empty,
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
            CityId,
            Identification(),
            CustomerContactInfo.Empty,
            Commercial(),
            ClassificationPrefix,
            Now.AddMinutes(2));

        Assert.True(customer.IsActive);
        Assert.Equal("Otro", customer.Name);
    }
}
