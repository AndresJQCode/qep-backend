using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// CAT-04 — las cinco propiedades nuevas de `Product`.
/// </summary>
public sealed class ProductDetailsApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static readonly string[] All =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage,
        // CAT-05: desde que el imageFileId se valida, las pruebas que asignan portada necesitan
        // un archivo real, y crearlo pide este permiso.
        StoragePermissions.FileUpload
    ];

    /// <summary>
    /// Deja un archivo del tenant en `Available` y devuelve su id.
    ///
    /// **Existe desde `CAT-05`.** Antes estas pruebas usaban un `Guid.CreateVersion7()` cualquiera
    /// como `imageFileId`, porque nadie lo verificaba — que es precisamente el hueco que `CAT-05`
    /// vino a cerrar. Las dos pruebas que lo hacían se pusieron rojas con
    /// `Expected: Created / Actual: UnprocessableEntity`, y eso es la corrección funcionando.
    ///
    /// El estado se fuerza por SQL: completar la subida de verdad exige escribir en R2 y que el
    /// escáner apruebe, y ninguna de las dos cosas existe en una prueba.
    /// </summary>
    private static async Task<Guid> UploadImageAsync(
        HttpClient client,
        string tenantId,
        PostgreSqlContainer database)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.CreateVersion7(),
                ownerType = nameof(FileOwnerType.Product),
                name = "portada.png",
                mimeType = "image/png",
                sizeBytes = 2048
            },
            TestContext.Current.CancellationToken);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var fileId = (await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).GetProperty("fileResourceId").GetGuid();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE storage.file_resources SET status = 'Available' WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        return fileId;
    }

    // CA-CAT-04-01
    [Fact]
    public async Task CreateWithAllTheDetailsPersistsThemAndGetReturnsThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var taxRateId = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        var image = await UploadImageAsync(client, TenantId, database);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            description = "Cera de soja, 200 g",
            imageFileId = image,
            price = 45000.50m,
            currency = "COP",
            taxRateId
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.Equal("Cera de soja, 200 g", created.Description);
        Assert.Equal(image, created.ImageFileId);
        Assert.Equal(45000.50m, created.Price);
        Assert.Equal("COP", created.Currency);
        Assert.Equal(taxRateId, created.TaxRateId);

        // Releído desde la base, no desde la respuesta de la escritura: eso probaría el mapeo de
        // salida, no la persistencia.
        var fetched = await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            TestContext.Current.CancellationToken));
        Assert.Equal(45000.50m, fetched.Price);
        Assert.Equal(taxRateId, fetched.TaxRateId);
    }

    // CA-CAT-04-02: los cinco son opcionales.
    [Fact]
    public async Task CreateWithoutAnyDetailReturnsCreatedWithThemNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await CreateProductAsync(
            client, TenantId, new { name = "Vela de soja", code = "VS-001" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.Null(created.Description);
        Assert.Null(created.ImageFileId);
        Assert.Null(created.Price);
        Assert.Null(created.Currency);
        Assert.Null(created.TaxRateId);
    }

    // CA-CAT-04-03: se puede limpiar, no sólo setear. Sin esta prueba, una implementación que
    // ignore los null "para no pisar" pasa todo lo demás y deja campos imborrables.
    [Fact]
    public async Task UpdateWithNullDetailsClearsThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var taxRateId = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        var created = await ReadProductAsync(await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            description = "Cera de soja",
            imageFileId = await UploadImageAsync(client, TenantId, database),
            price = 45000m,
            currency = "COP",
            taxRateId
        }));

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new { name = "Vela de soja", code = "VS-001" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadProductAsync(response);
        Assert.Null(updated.Description);
        Assert.Null(updated.ImageFileId);
        Assert.Null(updated.Price);
        Assert.Null(updated.Currency);
        Assert.Null(updated.TaxRateId);
    }

    // CA-CAT-04-04
    [Fact]
    public async Task CreateWithANegativePriceIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            price = -1m,
            currency = "COP"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("Price", body, StringComparison.OrdinalIgnoreCase);
    }

    // CA-CAT-04-05
    [Theory]
    [InlineData("CO")]
    [InlineData("COPX")]
    public async Task CreateWithACurrencyThatIsNotThreeCharactersIsUnprocessable(string currency)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            price = 1000m,
            currency
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // CA-CAT-04-05, segunda mitad: "cop" entra y sale "COP".
    [Fact]
    public async Task CreateNormalizesTheCurrencyToUppercase()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var created = await ReadProductAsync(await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            price = 1000m,
            currency = "cop"
        }));

        Assert.Equal("COP", created.Currency);
    }

    /// <summary>
    /// CA-CAT-04-06, los dos sentidos: una guarda escrita en uno solo deja pasar el otro.
    ///
    /// **El código cambió al corregir el hallazgo `A`, y es a propósito.** Antes este caso lo
    /// rechazaba sólo el invariante de dominio y salía como `catalog.product.price_currency_mismatch`
    /// sin mapa por campo; ahora lo ataja el validador y sale como `validation.failed` **con**
    /// `errors`, igual que los otros dos invariantes de CAT-04 —precio negativo y largo de
    /// moneda—, que siempre tuvieron regla. La tabla de «Riesgos» del spec pedía «invariante de
    /// dominio **y** validador»; con los dos, el que responde primero es el validador.
    ///
    /// El código de dominio no desapareció: sigue siendo la red de abajo para quien llame al
    /// agregado sin pasar por el validador, y lo cubren las unitarias de `ProductTests`.
    /// </summary>
    [Fact]
    public async Task PriceWithoutCurrencyAndCurrencyWithoutPriceAreBothUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var priceOnly = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            price = 45000m
        });
        Assert.Contains("Currency", (await ReadValidationErrorsAsync(priceOnly)).Keys);

        var currencyOnly = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de cera",
            code = "VC-002",
            currency = "COP"
        });
        Assert.Contains("Price", (await ReadValidationErrorsAsync(currencyOnly)).Keys);
    }

    /// <summary>
    /// CA-CAT-04-07 — el criterio que justifica que este slice lleve revisión de riesgo.
    ///
    /// La foreign key `catalog.products.tax_rate_id → catalog.tax_rates(id)` garantiza que la
    /// fila exista, y **nada más**: no sabe de tenants. Sin la verificación explícita del
    /// handler, un producto del tenant A puede apuntar a una tasa del tenant B y la base lo
    /// acepta sin una queja. La respuesta sería un 201 perfectamente normal.
    ///
    /// Es una fuga entre tenants que ninguna prueba de status HTTP encuentra por su cuenta.
    /// </summary>
    [Fact]
    public async Task ATaxRateFromAnotherTenantIsRejectedAndNothingIsPersisted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, All);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, All);

        // La tasa existe de verdad — en el otro tenant. La FK la aceptaría.
        var foreignTaxRateId = await CreateTaxRateAsync(other, OtherTenantId, "IVA ajeno", 19);

        var response = await CreateProductAsync(owner, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            taxRateId = foreignTaxRateId
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "catalog.product.tax_rate_not_found",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        Assert.Empty(await ListAsync(owner, TenantId));
    }

    // CA-CAT-04-08: id inexistente en cualquier tenant -> 422, no 500 por violación de FK.
    [Fact]
    public async Task AnUnknownTaxRateIsUnprocessableAndNotAServerError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            taxRateId = Guid.CreateVersion7()
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "catalog.product.tax_rate_not_found",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // CA-CAT-04-09: inactivar una tasa no debe romper los productos que ya la usaban, ni impedir
    // corregir uno mientras se decide su reemplazo.
    [Fact]
    public async Task AnInactiveTaxRateFromTheSameTenantIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var taxRateId = await CreateTaxRateAsync(client, TenantId, "IVA viejo", 16);
        var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{taxRateId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            taxRateId
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(taxRateId, (await ReadProductAsync(response)).TaxRateId);
    }

    // CA-CAT-04-10
    [Fact]
    public async Task UpdatingTheDetailsWritesExactlyOneAuditEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var created = await ReadProductAsync(await CreateProductAsync(
            client, TenantId, new { name = "Vela de soja", code = "VS-001" }));

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new
            {
                name = "Vela de soja",
                code = "VS-001",
                description = "Cera de soja, 200 g",
                price = 45000m,
                currency = "COP"
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(await QueryAuditEventsAsync(connection, "catalog.product.updated"));
    }

    /// <summary>
    /// CA-CAT-04-11 — los productos anteriores a la migración siguen legibles.
    ///
    /// La fila se inserta por SQL con **sólo las columnas viejas**, que es exactamente la forma
    /// que tiene un producto cargado antes de `AddProductDetails`. Si alguna de las cinco
    /// columnas nuevas hubiera nacido `NOT NULL`, este `INSERT` fallaría; y si el mapeo no
    /// tolerara nulos, el `GET` reventaría.
    /// </summary>
    [Fact]
    public async Task ProductsCreatedBeforeTheMigrationRemainReadableWithNullDetails()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        // Fuerza el arranque de la app, y con él las migraciones, antes de insertar.
        Assert.Empty(await ListAsync(client, TenantId));

        var legacyId = Guid.CreateVersion7();
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO catalog.products
                    (id, tenant_id, name, code, is_active, version, created_at, updated_at)
                VALUES (@id, @tenantId, 'Producto viejo', 'OLD-001', true, 1, now(), now())
                """,
                connection);
            command.Parameters.AddWithValue("id", legacyId);
            command.Parameters.AddWithValue("tenantId", Guid.Parse(TenantId));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var fetched = await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{legacyId}",
            TestContext.Current.CancellationToken));

        Assert.Equal("Producto viejo", fetched.Name);
        Assert.Null(fetched.Description);
        Assert.Null(fetched.ImageFileId);
        Assert.Null(fetched.Price);
        Assert.Null(fetched.Currency);
        Assert.Null(fetched.TaxRateId);
    }

    /// <summary>
    /// Hallazgo `A` de la revisión de 4 lentes, en los dos sentidos.
    ///
    /// El caso ya devolvía **422**, y por eso la prueba de `CA-CAT-04-06` pasaba: afirmaba sobre
    /// el status y el código de dominio. Lo que no llevaba es el mapa `errors` por campo, porque
    /// el emparejamiento precio/moneda lo rechazaba **sólo** el invariante de dominio y ningún
    /// validador. Un formulario recibía un 422 sin saber qué input marcar.
    ///
    /// La tabla de «Riesgos» de este spec pedía «invariante de dominio **y** validador». Esta
    /// prueba afirma sobre `errors`, que es la parte que la anterior no miraba.
    /// </summary>
    [Fact]
    public async Task APriceWithoutCurrencyNamesTheCurrencyFieldInTheErrorMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            price = 45000m
        });

        var errors = await ReadValidationErrorsAsync(response);
        Assert.Contains("Currency", errors.Keys);
    }

    // El sentido inverso apunta al otro campo: quien mandó moneda sin precio tiene que corregir
    // el precio, no la moneda. Una regla escrita en un solo sentido deja pasar el otro.
    [Fact]
    public async Task ACurrencyWithoutPriceNamesThePriceFieldInTheErrorMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            currency = "COP"
        });

        var errors = await ReadValidationErrorsAsync(response);
        Assert.Contains("Price", errors.Keys);
    }

    /// <summary>
    /// Hallazgo `F` — la regla del validador comprobaba sólo el largo, mientras el dominio además
    /// exige letras. `"123"` atravesaba el validador y lo rechazaba el dominio, con la misma
    /// forma del hallazgo `A`: 422 con código, sin mapa por campo.
    /// </summary>
    [Fact]
    public async Task ACurrencyOfThreeNonLettersNamesTheCurrencyFieldInTheErrorMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            price = 1000m,
            currency = "123"
        });

        var errors = await ReadValidationErrorsAsync(response);
        Assert.Contains("Currency", errors.Keys);
    }

    /// <summary>
    /// Hallazgo `D` — las reglas estaban duplicadas textualmente entre `CreateProductValidator` y
    /// `UpdateProductValidator`, así que corregir una sola dejaba `POST` y `PUT` validando
    /// distinto. Esta prueba ejerce el **mismo** caso por el otro verbo: es la que se pone roja
    /// si alguien vuelve a duplicar y arregla una sola copia.
    /// </summary>
    [Fact]
    public async Task ThePutEnforcesTheSameDetailRulesAsThePost()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var created = await ReadProductAsync(await CreateProductAsync(
            client, TenantId, new { name = "Vela de soja", code = "VS-001" }));

        var priceOnly = await client.PutAsJsonAsync(
            "/api/v1/tenants/" + TenantId + "/catalog/products/" + created.Id,
            new { name = "Vela de soja", code = "VS-001", price = 45000m },
            TestContext.Current.CancellationToken);
        Assert.Contains("Currency", (await ReadValidationErrorsAsync(priceOnly)).Keys);

        var badCurrency = await client.PutAsJsonAsync(
            "/api/v1/tenants/" + TenantId + "/catalog/products/" + created.Id,
            new { name = "Vela de soja", code = "VS-001", price = 1000m, currency = "123" },
            TestContext.Current.CancellationToken);
        Assert.Contains("Currency", (await ReadValidationErrorsAsync(badCurrency)).Keys);
    }

    /// <summary>
    /// Un 422 de validación tiene dos mitades y las pruebas viejas miraban una sola: el status y
    /// el código salían bien mientras el mapa por campo faltaba. Este helper exige las dos.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string[]>> ReadValidationErrorsAsync(
        HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());

        var errors = problem.TryGetProperty("errors", out var map)
            ? map.Deserialize<Dictionary<string, string[]>>()
            : null;
        Assert.NotNull(errors);
        return errors;
    }

    /// <summary>
    /// Hallazgo `B` — la violación de foreign key sobre `FK_products_tax_rates_tax_rate_id` no
    /// estaba traducida. `CatalogUnitOfWork` traducía las dos violaciones de índice único y no
    /// ésta, que la migración `AddProductDetails` estrena con `RESTRICT`.
    ///
    /// **Se salta el handler a propósito.** Por HTTP este caso no llega: `ProductTaxRateResolver`
    /// lo frena antes, y `CA-CAT-04-08` ya lo cubre. Lo que se prueba acá es la red de abajo —la
    /// que se activa cuando la fila desaparece entre la verificación y el commit, que es justo el
    /// escenario para el que se puso el `RESTRICT`. Sin traducir, sale **500 server.unexpected**
    /// y, por el hallazgo `C`, con el nombre de la constraint adentro del mensaje.
    /// </summary>
    [Fact]
    public async Task AForeignKeyViolationOnTheTaxRateIsTranslatedInsteadOfCrashing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        // Fuerza el arranque de la app, y con él las migraciones, antes de tocar la base.
        Assert.Empty(await ListAsync(client, TenantId));

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>();

        repository.Add(Product.Create(
            ProductId.New(),
            Guid.Parse(TenantId),
            "Vela de soja",
            "VS-001",
            ProductDetails.Empty with { TaxRateId = TaxRateId.New() },
            DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAsync<CatalogDomainException>(
            async () => await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("catalog.product.tax_rate_not_found", error.Code);
    }

    private static async Task<Guid> CreateTaxRateAsync(
        HttpClient client,
        string tenantId,
        string name,
        int percentage)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates",
            new { name, percentage },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TaxRateResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client,
        string tenantId,
        object payload) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            payload,
            TestContext.Current.CancellationToken);

    private static async Task<ProductResponse> ReadProductAsync(HttpResponseMessage response)
    {
        // El status se comprueba acá para que un 500 no salga como NullReference diez líneas
        // después. Es la lección del helper de TaxRateApiTests.
        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    private static async Task<IReadOnlyCollection<ProductResponse>> ListAsync(
        HttpClient client,
        string tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ProductsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Items;
    }

    private static async Task<IReadOnlyList<string>> QueryAuditEventsAsync(
        NpgsqlConnection connection,
        string action)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text
            FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'action' = @action
            ORDER BY occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("action", action);

        var payloads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
