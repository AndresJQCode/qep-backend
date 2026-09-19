# Dirección de contacto propia del cliente — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El cliente vuelve a tener dirección y ciudad propias (`customers.address`, `customers.city_id`), separadas de la libreta de direcciones de envío: marcar otra dirección como principal ya no cambia el domicilio del cliente, y el `PUT` de la ficha ya no pisa la principal.

**Architecture:** Se reviven los dos vestigios de antes de `CLI-DIR-01`: `CustomerContactInfo.Address` gana `CityId`, y `Customer.Assign(contact)` vuelve a asignar `Address`/`CityId` (con `EnsureValidCityId`, hoy sin caller). Una migración `AddCustomerContactAddress` agrega las columnas con backfill desde la principal, en una sola transacción y con la FK a `geography.cities` escrita a mano, como la de `customer_addresses`. `UpdateCustomer` e `ImportCustomers` dejan de espejar el domicilio en la principal, y todo lo que significa "dónde está el cliente" —DTO plano, listado, filtro por ciudad, export, `QuotationCustomerLookup`, `CustomerReportSource`— lee `customer.Address`/`customer.CityId`. La libreta (`AddAddress`/`UpdateAddress`/`MakeAddressPrincipal`/`RemoveAddress`) y sus endpoints no cambian de contrato.

**Tech Stack:** .NET 10 (SDK `10.0.400`, `rollForward: latestPatch`), EF Core 10 + Npgsql.EntityFrameworkCore.PostgreSQL, FluentValidation, xUnit v3, Testcontainers (`postgres:18-alpine`).

**Spec:** docs/superpowers/specs/2026-09-18-direccion-de-contacto-propia-design.md

## Global Constraints

- **TDD estricto: RED antes que GREEN, con evidencia literal de ambos.** Cada tarea pega en el handoff la salida real de la corrida roja y de la verde.
- **`Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y los comandos `ef`** por archivo bloqueado. Antes de cada corrida: `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- **La suite de integración usa Testcontainers: exige Docker corriendo y tarda decenas de minutos.** Durante el ciclo se corren sólo las clases de prueba tocadas, con `--filter "FullyQualifiedName~<Clase>"`; la suite completa se corre **una vez**, al final (Task 6). Siempre en primer plano, nunca en background.
- **Commits: Conventional Commits, en español y en minúscula, con scope `customers`** (`feat(customers): …`, `docs(customers): …`). **Nunca** `Co-Authored-By` ni otra atribución de IA, aunque una herramienta lo sugiera. Cada commit va con el guard de rama y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada.
- `git add` con rutas explícitas; nunca `git add -A` ni `git add .`. Otra sesión puede estar stageando en el mismo checkout: si `git status --short` muestra en el índice algo que no es tuyo, commitea con `git commit -m "…" -- <tus rutas>` en vez de `git add` + `git commit`.
- **Los comentarios explican el porqué, en español neutro tuteando** (sin voseo), y cuando reflejan un contrato citan `archivo:línea` de la contraparte. Identificadores, códigos de error, mensajes de excepción y nombres de prueba en inglés.
- **Códigos de error del contrato:** `customers.customer.address_too_long` (existente, `CustomerContactInfo.cs:46`), `customers.customer.city_required` (existente, `Customer.cs:506`, emitido por `EnsureValidCityId`) y `customers.customer.address_required` (**nuevo**, para calle vacía). Ningún otro código nuevo. Ningún status HTTP se arma a mano: el mapeo es central en `src/Api/ApiExceptionHandler.cs`.
- **El contrato HTTP no cambia de forma.** `CustomerResponse` conserva `address`, `city`, `department` y `addresses[]`; cambia sólo su significado (domicilio del cliente). Los endpoints `/customers/{id}/addresses*` no cambian.
- **La dirección de contacto sigue siendo obligatoria** (calle y ciudad), como exige hoy el `422` al crear sin dirección (`CreateWithoutAnAddressMarksTheAddressField`).
- **La libreta no se toca desde `Update` ni desde el import.** Al crear, el request sigue sembrando la primera fila de la libreta **además** del contacto (decisión 3 del spec).
- **La FK a `geography.cities` se escribe a mano en la migración**, nunca en el modelo EF (mismo motivo que `CustomersDbContext.cs:162-167`). `CustomersDbContextModelSnapshot.cs` sólo cambia regenerado por `dotnet ef migrations add`, con el factory de diseño y **sin `--startup-project`**: `dotnet ef migrations add <Nombre> --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations`. Las migraciones históricas y sus `Designer.cs` no se tocan.
- **Fuera de alcance** (spec): qué dirección usa la cotización con "igual al cliente"; permitir borrar la principal; vincular una fila de la libreta como contacto; tests unitarios de la libreta que este cambio no necesite. `qep-frontend` no se toca.
- Todo se hace en el checkout `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend`, rama `feature/direccion-de-contacto-propia` desde `develop`. No hace falta worktree: la ruta más larga que nace en este plan (`…\Persistence\Migrations\<timestamp>_AddCustomerContactAddress.Designer.cs`) queda debajo de los 260 caracteres de Windows. Cada bloque de comandos empieza con `Set-Location` a ese checkout.
- Los comandos van en Windows PowerShell 5.1: sin `&&`, con `A; if ($?) { B }`, y `$env:VAR = "…"` en línea aparte. Nunca se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- `AnalysisLevel` es `10.0-recommended` con `TreatWarningsAsErrors`: un `using` o un método privado que queda sin uso se borra en la misma tarea. Archivos nuevos en UTF-8 sin BOM y con LF; `dotnet format` reporta `ENDOFLINE`/`CHARSET` en archivos viejos por `core.autocrlf=true` y en los `Designer.cs` generados: ese ruido se filtra, cualquier otro diagnóstico en una línea que tocaste se corrige.
- **Nunca imprimir el valor de un secreto.** Ningún comando de este plan necesita uno.

---

## Hallazgos contra el código (2026-09-18, sobre `develop` @ `d816e9b`)

1. **Ninguna prueba de integración ejercita hoy los endpoints de la libreta** (`POST …/addresses`, `POST …/addresses/{id}/principal`): `rg -n "IsPrincipal" tests/` sólo encuentra comentarios. Los helpers `AddAddressAsync`/`MakeAddressPrincipalAsync`/`GetAsync` se definen en `CustomersApiHarness` en la Task 3, y sus equivalentes en los harness de Quotations y Reporting en la Task 4.
2. **Migrar a mitad de prueba ya tiene precedente:** `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs` abre un `DbContext` crudo, pide `IMigrator` y migra hasta un id puntual. Para Customers hay una diferencia: `20260822043059_AddCustomerCityAndClassification` y `20260906002234_AddCustomerAddresses` declaran FK hacia `geography.cities`, así que **primero hay que migrar `GeographyDbContext` a la última** y sembrar departamentos y ciudades por SQL (`geography.departments(id, divipola_code, name)`, `geography.cities(id, divipola_code, name, department_id)`); `GeographySeeder` no corre sin el host. Además la FK nueva se valida al crearse, así que las ciudades tienen que existir de verdad —`session_replication_role = replica` no alcanza.
3. **EF mapea `Customer.Address` y `Customer.CityId` por convención en cuanto existen** (propiedades públicas con setter), a columnas `Address`/`CityId` que no están en la base. Entre el commit del dominio (Task 1) y el de la migración (Task 2) la aplicación **no es consistente en runtime**: compila, las pruebas unitarias pasan, pero cualquier consulta a `customers` falla. Por eso las dos tareas van seguidas y no se corre nada de integración ni se levanta `Api` entre ellas.
4. **`CustomerContactInfo` gana miembros `required`**, así que las cuatro construcciones de Application (`CreateCustomer.cs:92-96`, `UpdateCustomer.cs:96-100`, `ImportCustomers.cs:552-556` y `:590-594`) dejan de compilar. Se completan en la Task 1 —sólo para que la solución compile—; los bloques que espejan el domicilio en la principal se borran en la Task 3, que es donde el comportamiento cambia.
5. **`ExportLoadSeeder.CustomersSql` (`:172-188`) inserta en `customers.customers` por SQL crudo**, sin `address` ni `city_id`. En cuanto la migración las vuelva `NOT NULL`, `ExportLoadSeedTests` se pone roja. El spec lo lista en el paso 5 (Bootstrapper); acá el seeder se arregla en la **Task 2**, en el mismo commit que la migración que lo rompe, para que ningún commit deje una prueba existente en rojo.
6. **El filtro del listado por ciudad vive en Infrastructure**, `CustomerRepository.SearchAsync` (`:123-128`), no en `ListCustomers`. El spec pide que "los filtros por departamento/ciudad del listado filtren por el contacto": ese `Where` cambia en la Task 3.
7. **`CustomerWriteRules.cs:74-76` mide `Address` contra `CustomerAddress.AddressMaxLength`** (200, igual que `CustomerContactInfo.AddressMaxLength`). Pasa a apuntar a la constante del contacto: es lo que valida.
8. **`CreateRejectsAnEmptyCityId` (`CustomerTests.cs:155-161`) espera `customers.address.city_required` y sigue igual:** el constructor crea la fila de la libreta (`Customer.cs:55`) **antes** de `Assign(contact)` (`:62`), así que con el fixture pasando `Guid.Empty` a los dos, gana el código de la libreta. La prueba nueva `ContactInfoRejectsAnEmptyCityId` pasa `Guid.Empty` sólo al contacto y ve `customers.customer.city_required`.
9. **`Customer.Update` garantiza todo-o-nada** (`Customer.cs:317-329`, prueba `UpdateLeavesTheCustomerUntouchedWhenALaterFieldIsRejected`). `EnsureValidCityId` tiene que correr en el bloque de normalización, **antes** de asignar el nombre; si sólo corriera dentro de `Assign(contact)`, una ciudad vacía dejaría el nombre nuevo pegado. Se agrega la prueba `UpdateLeavesTheCustomerUntouchedWhenTheCityIsRejected`.
10. **`PrincipalAddress` y `RequirePrincipalAddress()` (`Customer.cs:112-123`) pierden todos sus callers en `src/` después de la Task 3.** El spec no los quita ("deja de usarse en lectura"), así que se conservan y su comentario se actualiza; borrarlos es un follow-up.
11. **Los DTOs de geografía de los harness de Quotations y Reporting son `private`** (`GeographyDepartmentDto`, `GeographyCityDto`), así que los helpers nuevos viven dentro de cada harness. Para leer la ficha completa del cliente se deserializa `Modules.Customers.Application.CustomerResponse`, que los dos harness ya importan.
12. **El timestamp de la migración lo pone `dotnet ef`:** el plan la nombra `<timestamp>_AddCustomerContactAddress.cs` y la prueba la resuelve por sufijo con `MigrationId(context, "_AddCustomerContactAddress")`, como `OrdersMigrationTests`.

**Decisiones de este plan donde el spec deja margen:**

- **`CustomerContactInfo.Address` pasa a `required string` (no `string?`) y `CityId` a `required Guid`.** Los llamadores pasan `command.Address ?? string.Empty`, igual que ya hacen con `CustomerAddressDetails`; `Normalized()` rechaza la cadena vacía con el código nuevo. `Phone` y `Email` siguen `string?` como hoy.
- **La ciudad vacía la rechaza `Customer.EnsureValidCityId`** (spec: "vuelve a tener caller"), llamada desde `Assign(contact)` y desde el bloque de normalización de `Update`. `Normalized()` no la mira: el código `customers.customer.city_required` vive en `Customer`.
- **`CustomerContactInfo.NormalizeOptional` se borra:** queda sin uso cuando `Address` pasa a obligatoria.
- **La prueba de migración es de integración**, `CustomerContactAddressMigrationTests`, con tres casos: backfill desde la principal, cliente sin principal (la migración falla con `23502` y no deja columnas), y `Down`. No hace falta verificación manual.
- **Nombres de prueba que el spec no fija:** `ListShowsAndFiltersByTheCustomerCityNotByThePrincipalAddress` (Customers), `CreateShowsTheCustomerAddressAndKeepsThePrincipalFirstInTheAddressBook` (Quotations), `UpdateLeavesTheCustomerUntouchedWhenTheCityIsRejected` (unitaria).
- **El commit de Bootstrapper también usa scope `customers`:** el adaptador cambia por el módulo, no por el composition root.

## Entrega

| Commit | Tarea |
| --- | --- |
| `docs(customers): plan de dirección de contacto propia del cliente` | 0 |
| `feat(customers): dirección y ciudad de contacto propias en el agregado` | 1 |
| `feat(customers): columnas address y city_id con backfill desde la principal` | 2 |
| `feat(customers): la ficha, el listado y el export leen el domicilio del cliente` | 3 |
| `feat(customers): cotizaciones y reporte leen el domicilio del cliente` | 4 |
| `docs(customers): comentarios que decían que el domicilio era la principal` | 5, sólo si el barrido encuentra algo |
| `fix(customers): …` | 6, sólo si la verificación final pide cambios |

La rama no se publica ni se mergea desde este plan. Orden de deploy (spec § Entrega): backend primero, con migración; frontend después.

---

### Task 0: Rama y baseline

**Files:**
- Ninguno de código. Se commitea este plan.

**Interfaces:**
- Consumes: `develop` @ `d816e9b` o posterior.
- Produces: la rama `feature/direccion-de-contacto-propia` con el plan commiteado, y la salida literal del baseline en el handoff.

- [ ] **Step 1: Comprobar la rama de partida y crear la nueva**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git branch --show-current
git status --short
git fetch origin
git log -1 --oneline develop
git log -1 --oneline origin/develop
git switch -c feature/direccion-de-contacto-propia develop
git branch --show-current
```

Esperado: la rama previa es `develop`; `git status` sólo con `?? docs/superpowers/plans/2026-09-18-direccion-de-contacto-propia.md`; los dos `git log -1` con el **mismo** SHA (si `develop` local está detrás de `origin/develop`, **para y pregunta** antes de crear la rama); el último `git branch --show-current` dice `feature/direccion-de-contacto-propia`.

- [ ] **Step 2: Comprobar herramientas y el spec**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Test-Path docs\superpowers\specs\2026-09-18-direccion-de-contacto-propia-design.md
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado: `True`; `Get-Process` sin salida; `docker info` con una versión; `dotnet ef` con una versión 10.x. Si `dotnet ef` no está: `dotnet tool install --global dotnet-ef` y volver a comprobar.

- [ ] **Step 3: Restore y build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
```

Esperado: `Compilación correcta.` (o `Build succeeded.`) con `0 Advertencia(s)` y `0 Errores`.

- [ ] **Step 4: Baseline del spec — unitarias de Customers y ArchitectureTests**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests/Modules.Customers.UnitTests.csproj --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
```

Esperado: las dos corridas con `Failed: 0` (o `Con error: 0`). Anota en el handoff el número de pruebas correctas de cada una: son el punto de comparación de la Task 6. `CustomerTests` aporta 44 casos hoy (33 métodos, contando las filas de cada `Theory`).

- [ ] **Step 5: Commitear el plan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/direccion-de-contacto-propia") { throw "ABORT: rama equivocada" }; git add docs/superpowers/plans/2026-09-18-direccion-de-contacto-propia.md; git commit -m "docs(customers): plan de dirección de contacto propia del cliente"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit creado y el `Select-String` sin salida.

---

### Task 1: Dominio — `CustomerContactInfo.CityId`, `Customer.Address`/`CityId`, `Assign`

El cliente vuelve a tener domicilio propio en el agregado. La libreta no se toca. Los cuatro `new CustomerContactInfo { … }` de Application se completan sólo para que la solución compile; su comportamiento cambia en la Task 3.

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerContactInfo.cs` (archivo entero salvo `NormalizeRequired`, `NormalizeEmail`, `IsPlausibleEmail`)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs:29-34` (ctor de EF), `:103-123` (docs de `Addresses`/`PrincipalAddress`/`RequirePrincipalAddress`), `:149` (propiedades nuevas después de `Email`), `:247-251` (doc de `RemoveAddress`), `:327` (normalización en `Update`), `:354-363` (`Assign(contact)`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs:92-96`, `UpdateCustomer.cs:96-100`, `ImportCustomers.cs:552-556` y `:590-594`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs`

**Interfaces:**
- Consumes: `CustomersDomainException(string code, string message)`, `CustomerAddressDetails`, `Customer.AddAddress`/`MakeAddressPrincipal` (existentes, sin cambio).
- Produces (namespace `Modules.Customers.Domain`):

```csharp
public sealed record CustomerContactInfo
{
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public required string Address { get; init; }   // Normalized(): customers.customer.address_required / address_too_long
    public required Guid CityId { get; init; }      // la vacía la rechaza Customer.EnsureValidCityId: customers.customer.city_required
    public const int PhoneMaxLength = 32;
    public const int EmailMaxLength = 254;
    public const int AddressMaxLength = 200;
}

public sealed class Customer
{
    public string Address { get; private set; }     // el domicilio del cliente, no la principal
    public Guid CityId { get; private set; }        // FK blanda a geography.cities
    // Create(...) y Update(...) no cambian de firma: ya reciben CustomerContactInfo.
}
```

- [ ] **Step 1: Escribir las pruebas que fallan**

En `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs`, reemplaza el fixture `ValidContact` y el helper `Create` (líneas 45-71) por:

```csharp
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
```

Corrige las tres construcciones sueltas de `CustomerContactInfo`, que ya no compilan sin `Address`/`CityId`:

- `UpdateRejectsABlankEmailOrPhone` (línea 211): `new CustomerContactInfo { Email = email, Phone = phone }` → `ValidContact() with { Email = email, Phone = phone }`.
- `ContactInfoTrimsThePhoneAndTheEmail` (líneas 223-227): `new CustomerContactInfo { Phone = "  310 935 2187  ", Email = "  compras@verde.co  " }` → `ValidContact() with { Phone = "  310 935 2187  ", Email = "  compras@verde.co  " }`.
- `UpdateReplacesThePhoneAndTheEmail` (línea 320): `new CustomerContactInfo { Phone = "604 444 5566", Email = "Ventas@Verde.CO" }` → `ValidContact() with { Phone = "604 444 5566", Email = "Ventas@Verde.CO" }`.

Corrige dos comentarios que afirman que el domicilio es la principal:

- Líneas 152-153 (`CreateRejectsAnEmptyCityId`): reemplaza el comentario por `// El fixture pasa Guid.Empty a la libreta y al contacto; el constructor crea la primera direccion antes de asignar el contacto, asi que el codigo que sale es el de la libreta. El del contacto lo cubre ContactInfoRejectsAnEmptyCityId.`
- Líneas 329-330 (`UpdateReplacesTheClassification`): deja sólo `// La clasificacion se puede reemplazar en el Update: un cliente puede cambiar de categoria comercial.`
- Línea 258 (`ContactInfoRejectsAnAddressLongerThanTheColumn`): agrega encima `// Ya no es un vestigio: la direccion de contacto es el domicilio del cliente (spec 2026-09-18).`

Agrega las pruebas nuevas después de `ContactInfoRejectsAnAddressLongerThanTheColumn`:

```csharp
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
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests/Modules.Customers.UnitTests.csproj --filter "FullyQualifiedName~Modules.Customers.UnitTests.CustomerTests."
```

Esperado: **no compila**. Errores `CS0117: 'CustomerContactInfo' no contiene una definición para 'CityId'` y `CS1061: 'Customer' no contiene una definición para 'Address'` / `'CityId'` (en inglés si el SDK está en inglés). Pega la salida en el handoff.

- [ ] **Step 3: `CustomerContactInfo` con `CityId` y `Address` obligatoria**

Reemplaza en `src/Modules/Customers/Modules.Customers.Domain/CustomerContactInfo.cs` el encabezado, las propiedades y `Normalized()` (líneas 3-48), y **borra** `NormalizeOptional` (líneas 50-68, queda sin uso). `NormalizeRequired`, `NormalizeEmail` e `IsPlausibleEmail` no cambian.

```csharp
namespace Modules.Customers.Domain;

/// <summary>
/// Los datos de contacto del cliente, agrupados: teléfono, correo y su domicilio (calle y
/// ciudad). Los cuatro son obligatorios al escribir.
///
/// Van juntos y no como parámetros sueltos de <c>Create</c>/<c>Update</c> por la misma razón por
/// la que existe <c>CompanyContactInfo</c>. Las propiedades son <c>init</c> y no posicionales, así
/// que sólo se construye por nombre.
///
/// El domicilio es **del cliente, no de su libreta** (spec 2026-09-18). <c>CLI-DIR-01</c> lo había
/// movido a la fila principal de <see cref="CustomerAddress"/>, y desde entonces marcar otra
/// dirección de envío como principal cambiaba dónde está el cliente, y el siguiente PUT de la
/// ficha pisaba esa dirección. La libreta quedó como catálogo de destinos de envío; este par
/// volvió a <see cref="Customer.Address"/> y <see cref="Customer.CityId"/>. La ciudad es un
/// <see cref="Guid"/> y no un id fuertemente tipado de Geography: FK blanda a otro módulo, mismo
/// criterio que <see cref="CustomerAddress.CityId"/>.
/// </summary>
public sealed record CustomerContactInfo
{
    public string? Phone { get; init; }

    public string? Email { get; init; }

    public required string Address { get; init; }

    public required Guid CityId { get; init; }

    // Espejan los anchos de columna. Salen del schema del formulario que ya existe
    // (customer-form.schema.ts); el del correo no esta ahi: 254 es el maximo de una direccion por
    // RFC 5321, el mismo que ya usa CompanyContactInfo.
    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 200;

    // La ciudad no se valida acá sino en Customer.EnsureValidCityId, que es donde vive
    // customers.customer.city_required desde antes de la libreta; Update la comprueba antes de
    // asignar nada para conservar el todo-o-nada.
    internal CustomerContactInfo Normalized() => new()
    {
        Phone = NormalizeRequired(
            Phone,
            PhoneMaxLength,
            "customers.customer.phone_required",
            "The customer phone is required.",
            "customers.customer.phone_too_long",
            $"The customer phone cannot exceed {PhoneMaxLength} characters."),
        Email = NormalizeEmail(Email),
        Address = NormalizeRequired(
            Address,
            AddressMaxLength,
            "customers.customer.address_required",
            "The customer address is required.",
            "customers.customer.address_too_long",
            $"The customer address cannot exceed {AddressMaxLength} characters."),
        CityId = CityId
    };
```

- [ ] **Step 4: `Customer` con `Address`/`CityId`, `Assign` y el todo-o-nada de `Update`**

En `src/Modules/Customers/Modules.Customers.Domain/Customer.cs`:

(a) En el constructor de EF (líneas 29-34), agrega `Address = string.Empty;` después de `IdentificationNumber = string.Empty;`.

(b) Reemplaza la doc de `Addresses` (líneas 103-106) por:

```csharp
    /// <summary>
    /// La libreta de direcciones de envío (CLI-DIR-01): a dónde se le entrega. Siempre hay al
    /// menos una mientras el cliente existe: la primera nace del alta con el mismo par que el
    /// domicilio (decisión 3 del spec 2026-09-18), para que un cliente nuevo tenga a dónde
    /// enviar sin abrir la libreta. Desde ahí son dos datos distintos: editar la ficha no la
    /// toca, y marcar otra principal no mueve <see cref="Address"/> ni <see cref="CityId"/>.
    /// </summary>
```

(c) Reemplaza la doc de `RequirePrincipalAddress` (líneas 115-118) por:

```csharp
    /// <summary>La principal, o una excepcion clara si la consulta no incluyo las direcciones.
    /// Todo cliente creado tiene una; que falte solo puede ser un <c>Include</c> olvidado en el
    /// repositorio. Desde el spec 2026-09-18 la lectura del domicilio no pasa por acá
    /// (<see cref="Address"/>/<see cref="CityId"/>); queda para la libreta.</summary>
```

(d) Después de `public string? Email { get; private set; }` (línea 149) agrega:

```csharp
    /// <summary>
    /// El domicilio del cliente: la calle. Junto con <see cref="CityId"/> es "dónde está el
    /// cliente" — lo que la ficha muestra en los campos planos, la ciudad por la que el listado
    /// y el reporte lo agrupan, y el respaldo de facturación y envío de la cotización
    /// (<c>QuotationResponseComposer.cs:96-110</c>). **No es la principal de la libreta** (spec
    /// 2026-09-18): <see cref="Addresses"/> son destinos de envío y su principal es sólo la que
    /// se ofrece primero. CLI-DIR-01 había fundido las dos cosas y marcar otra principal movía el
    /// domicilio; volvieron a separarse.
    /// </summary>
    public string Address { get; private set; }

    /// <summary>
    /// FK blanda a <c>Modules.Geography</c>: <see cref="Guid"/> y no un id fuertemente tipado de
    /// otro dominio, mismo criterio que <see cref="CustomerAddress.CityId"/>. La FK real
    /// (<c>FK_customers_cities_city_id</c>) la escribe a mano la migración, porque City vive en
    /// otro DbContext.
    /// </summary>
    public Guid CityId { get; private set; }
```

(e) Reemplaza la doc de `RemoveAddress` (líneas 247-251) por:

```csharp
    /// <summary>
    /// Quita una direccion. La principal no se puede quitar: primero hay que nombrar otra. Es la
    /// regla que hace que la cotizacion siempre tenga una direccion de envio que proponer por
    /// defecto; el domicilio del cliente no depende de la libreta.
    /// </summary>
```

(f) En `Update`, después de `var normalizedContact = contact.Normalized();` (línea 327) agrega:

```csharp
        // La ciudad se comprueba acá y no sólo en Assign: hace falta **antes** de asignar el
        // nombre, dentro de la misma garantía de todo-o-nada que el resto del método.
        EnsureValidCityId(normalizedContact.CityId);
```

(g) Reemplaza `Assign(CustomerContactInfo contact)` y su comentario (líneas 354-363) por:

```csharp
    // Asigna los cuatro siempre. Telefono y correo se pueden **limpiar**, no solo setear: una
    // implementacion que ignore los null "para no pisar" deja campos imborrables y pasa todas las
    // demas pruebas. La direccion llega validada por Normalized; la ciudad vacia se rechaza aca
    // con el codigo que este agregado emitia antes de la libreta.
    private void Assign(CustomerContactInfo contact)
    {
        var normalized = contact.Normalized();

        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
        CityId = EnsureValidCityId(normalized.CityId);
    }
```

- [ ] **Step 5: Completar las cuatro construcciones de Application para que compile**

Sólo `Address`/`CityId` en el objeto; nada más cambia en estos archivos hasta la Task 3.

`src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs:92-96` y `UpdateCustomer.cs:96-100`:

```csharp
            new CustomerContactInfo
            {
                Phone = command.Phone,
                Email = command.Email,
                Address = command.Address ?? string.Empty,
                CityId = command.CityId
            },
```

`src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs:552-556` (rama de actualización) y `:590-594` (creación):

```csharp
                new CustomerContactInfo
                {
                    Phone = candidate.Phone,
                    Email = candidate.Email,
                    Address = candidate.Address ?? string.Empty,
                    CityId = candidate.City.CityId
                },
```

- [ ] **Step 6: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests/Modules.Customers.UnitTests.csproj --filter "FullyQualifiedName~Modules.Customers.UnitTests.CustomerTests."
```

Esperado: `Failed: 0`, `Passed: 51` (44 del baseline + 7 casos nuevos: `CreateSeedsTheContactAddressAndTheFirstAddressBookRow`, `MakeAddressPrincipalDoesNotChangeTheContactAddress`, `UpdateChangesTheContactAddressAndLeavesTheAddressBookUntouched`, `ContactInfoRejectsAnEmptyAddress` ×2, `ContactInfoRejectsAnEmptyCityId`, `UpdateLeavesTheCustomerUntouchedWhenTheCityIsRejected`).

- [ ] **Step 7: Build de toda la solución y formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Customers/Modules.Customers.Domain/CustomerContactInfo.cs src/Modules/Customers/Modules.Customers.Domain/Customer.cs src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs
```

Esperado: build con `0 Advertencia(s)` y `0 Errores`; `dotnet format` sin diagnósticos salvo `ENDOFLINE`/`CHARSET`. **No corras pruebas de integración ni levantes `Api` hasta terminar la Task 2** (hallazgo 3).

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/direccion-de-contacto-propia") { throw "ABORT: rama equivocada" }; git add src/Modules/Customers/Modules.Customers.Domain/CustomerContactInfo.cs src/Modules/Customers/Modules.Customers.Domain/Customer.cs src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs; git commit -m "feat(customers): dirección y ciudad de contacto propias en el agregado"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con seis archivos y el `Select-String` sin salida.

---

### Task 2: Infraestructura — configuración EF, migración `AddCustomerContactAddress` y su prueba

Las columnas vuelven a `customers.customers` con backfill desde la principal, en el orden del spec y en una sola transacción. La FK a `geography.cities` va a mano, como en `AddCustomerAddresses`. El seeder de carga sintética se arregla acá porque es esta migración la que lo rompe (hallazgo 5).

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs:66-71` (después de `Email`)
- Create (generado por `dotnet ef`): `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/Migrations/<timestamp>_AddCustomerContactAddress.cs` y `.Designer.cs`; modificado por `dotnet ef`: `CustomersDbContextModelSnapshot.cs`
- Modify: `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:170-198` (`CustomersSql`, `AddressesSql` y sus comentarios)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerContactAddressMigrationTests.cs` (nuevo)

**Interfaces:**
- Consumes: `Customer.Address`/`Customer.CityId` (Task 1); `CustomersDbContext` (público); `GeographyDbContext` (público, `Modules.Geography.Infrastructure.Persistence`); tabla de historial `__ef_migrations_history` en los esquemas `customers` y `geography` (`CustomersDbContextFactory.cs:17-19`, `GeographyDbContextFactory.cs:17-19`).
- Produces: columnas `customers.customers.address varchar(200) NOT NULL` y `city_id uuid NOT NULL`, FK `FK_customers_cities_city_id` (`ON DELETE RESTRICT`), índice `IX_customers_city`; la migración `<timestamp>_AddCustomerContactAddress`.

- [ ] **Step 1: Escribir la prueba de migración que falla**

Crea `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerContactAddressMigrationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Geography.Infrastructure.Persistence;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// La migración que devuelve el domicilio al cliente (spec 2026-09-18), contra una base con un
/// cliente y dos direcciones del esquema anterior. Migra sólo Customers, y hasta una migración
/// puntual, con <see cref="IMigrator"/>: el host de pruebas migra todo a la última al arrancar, y
/// así no habría esquema viejo donde sembrar. Mismo mecanismo que <c>OrdersMigrationTests</c>.
///
/// Geography sí se migra a la última antes: dos migraciones de Customers declaran FK hacia
/// <c>geography.cities</c>, y la FK nueva se valida al crearse, así que las ciudades tienen que
/// existir de verdad. <c>GeographySeeder</c> no corre sin el host, por eso van por SQL.
/// </summary>
public sealed class CustomerContactAddressMigrationTests
{
    private const string LastMigrationBeforeTheContactAddress = "20260906150110_AddCustomerBusinessName";

    private const string DepartmentAId = "01900000-0000-7000-8000-00000000d001";
    private const string DepartmentBId = "01900000-0000-7000-8000-00000000d002";
    private const string CityAId = "01900000-0000-7000-8000-00000000d003";
    private const string CityBId = "01900000-0000-7000-8000-00000000d004";
    private const string ClassificationId = "01900000-0000-7000-8000-00000000d005";
    private const string CustomerId = "01900000-0000-7000-8000-00000000d006";
    private const string AddressAId = "01900000-0000-7000-8000-00000000d007";
    private const string AddressBId = "01900000-0000-7000-8000-00000000d008";

    private const string GeographySql = $"""
        INSERT INTO geography.departments (id, divipola_code, name) VALUES
            ('{DepartmentAId}', '05', 'Antioquia'),
            ('{DepartmentBId}', '11', 'Bogotá, D.C.');
        INSERT INTO geography.cities (id, divipola_code, name, department_id) VALUES
            ('{CityAId}', '05001', 'Medellín', '{DepartmentAId}'),
            ('{CityBId}', '11001', 'Bogotá, D.C.', '{DepartmentBId}');
        """;

    // Un cliente del esquema anterior a la migración: sin address ni city_id propios. La
    // principal está en la ciudad B y la otra en la A, para que el backfill tenga que elegir.
    private const string CustomerSql = $"""
        INSERT INTO customers.client_classifications (
            id, tenant_id, name, prefix, is_active, version, created_at, updated_at)
        VALUES ('{ClassificationId}', '{TenantId}', 'Mediano', 'CLI', true, 1,
                '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        INSERT INTO customers.customers (
            id, tenant_id, cuc, name, business_name, identification_type, identification_number,
            is_active, phone, email, classification_id, with_retention, vat_surplus, version,
            created_at, updated_at)
        VALUES ('{CustomerId}', '{TenantId}', 'CLI05000001', 'Verde Esencial S.A.S.', NULL, 'Nit',
                '900.123.456-1', true, '310 935 2187', 'compras@verde.co', '{ClassificationId}',
                false, false, 1, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        """;

    private const string TwoAddressesWithPrincipalInCityBSql = $"""
        INSERT INTO customers.customer_addresses (
            id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
        VALUES
            ('{AddressAId}', '{CustomerId}', 'Bodega Norte', 'Calle 10 # 45-12', NULL, '{CityAId}',
             false, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z'),
            ('{AddressBId}', '{CustomerId}', 'Oficina', 'Carrera 7 # 71-21', NULL, '{CityBId}',
             true, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        """;

    // El dato roto del spec (§ Bordes): un cliente sin principal. No debería existir, pero si
    // existe la migración tiene que denunciarlo, no inventarle un domicilio.
    private const string TwoAddressesWithoutPrincipalSql = $"""
        INSERT INTO customers.customer_addresses (
            id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
        VALUES
            ('{AddressAId}', '{CustomerId}', 'Bodega Norte', 'Calle 10 # 45-12', NULL, '{CityAId}',
             false, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z'),
            ('{AddressBId}', '{CustomerId}', 'Oficina', 'Carrera 7 # 71-21', NULL, '{CityBId}',
             false, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        """;

    private const string ContactColumnsCountSql = """
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'customers' AND table_name = 'customers'
          AND column_name IN ('address', 'city_id')
        """;

    [Fact]
    public async Task TheBackfillCopiesThePrincipalAddressIntoTheCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheContactAddress, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, GeographySql + CustomerSql + TwoAddressesWithPrincipalInCityBSql);

        await migrator.MigrateAsync(
            MigrationId(context, "_AddCustomerContactAddress"), TestContext.Current.CancellationToken);

        Assert.Equal(Guid.Parse(CityBId), await ScalarAsync<Guid>(
            connectionString, $"SELECT city_id FROM customers.customers WHERE id = '{CustomerId}'"));
        Assert.Equal("Carrera 7 # 71-21", await ScalarAsync<string>(
            connectionString, $"SELECT address FROM customers.customers WHERE id = '{CustomerId}'"));
        // La libreta no se toca: el backfill copia, no mueve.
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            $"SELECT count(*) FROM customers.customer_addresses WHERE customer_id = '{CustomerId}'"));
        Assert.Equal("NO", await ScalarAsync<string>(
            connectionString,
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'customers' "
            + "AND table_name = 'customers' AND column_name = 'city_id'"));
        // El DEFAULT '' solo servia para crear la columna; en el esquema final no queda.
        Assert.True(await ScalarAsync<bool>(
            connectionString,
            "SELECT column_default IS NULL FROM information_schema.columns WHERE table_schema = 'customers' "
            + "AND table_name = 'customers' AND column_name = 'address'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'FK_customers_cities_city_id'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'customers' AND indexname = 'IX_customers_city'"));
    }

    [Fact]
    public async Task ACustomerWithoutAPrincipalAddressStopsTheMigration()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheContactAddress, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, GeographySql + CustomerSql + TwoAddressesWithoutPrincipalSql);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(
            MigrationId(context, "_AddCustomerContactAddress"), TestContext.Current.CancellationToken));

        // 23502 = not_null_violation: el SET NOT NULL del paso 3 encontro el city_id nulo que dejo
        // el backfill. La transaccion deshace los pasos anteriores: no quedan columnas a medias.
        Assert.Equal("23502", exception.SqlState);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, ContactColumnsCountSql));
    }

    [Fact]
    public async Task RevertingRemovesTheColumnsAndLeavesTheAddressBookIntact()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheContactAddress, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, GeographySql + CustomerSql + TwoAddressesWithPrincipalInCityBSql);
        await migrator.MigrateAsync(
            MigrationId(context, "_AddCustomerContactAddress"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(LastMigrationBeforeTheContactAddress, TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, ContactColumnsCountSql));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            $"SELECT count(*) FROM customers.customer_addresses WHERE customer_id = '{CustomerId}'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'FK_customers_cities_city_id'"));
    }

    // Geography entera: Customers referencia geography.cities desde AddCustomerCityAndClassification.
    private static async Task MigrateGeographyToLatestAsync(string connectionString)
    {
        await using var geography = new GeographyDbContext(
            new DbContextOptionsBuilder<GeographyDbContext>()
                .UseNpgsql(
                    connectionString,
                    npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "geography"))
                .Options);
        await geography.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static CustomersDbContext NewCustomersContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "customers"))
            .Options);

    /// <summary>El id completo lleva el timestamp de cuando se generó; el sufijo es lo estable.</summary>
    private static string MigrationId(CustomersDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests/Modules.Customers.IntegrationTests.csproj --filter "FullyQualifiedName~CustomerContactAddressMigrationTests"
```

Esperado: 3 con error, las tres con `System.InvalidOperationException : Sequence contains no matching element` en `MigrationId` — la migración todavía no existe. Pega la salida en el handoff.

- [ ] **Step 3: Configurar las columnas en `CustomersDbContext`**

En `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs`, después del bloque de `Email` (líneas 69-71) y antes del comentario de la clasificación, agrega:

```csharp
        // El domicilio del cliente (spec 2026-09-18): vuelve a customers despues de CLI-DIR-01,
        // separado de la libreta. Misma FK blanda que customer_addresses.city_id: sin navegacion EF
        // hacia City, que vive en GeographyDbContext; la FK real (FK_customers_cities_city_id, ON
        // DELETE RESTRICT) la agrega a mano la migracion AddCustomerContactAddress, con el mismo
        // motivo que se explica en ConfigureCustomerAddress.
        customer.Property(value => value.Address)
            .HasColumnName("address")
            .HasMaxLength(CustomerContactInfo.AddressMaxLength);
        customer.Property(value => value.CityId).HasColumnName("city_id");
        customer.HasIndex(value => value.CityId).HasDatabaseName("IX_customers_city");
```

- [ ] **Step 4: Generar la migración con el factory de diseño**

Sin `--startup-project` (`Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`). No necesita base: compara el modelo con el snapshot.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddCustomerContactAddress --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations
git status --short
```

Esperado: `Done. To undo this action, use 'ef migrations remove'`, y `git status` con cuatro archivos: `CustomersDbContext.cs` modificado, el `<timestamp>_AddCustomerContactAddress.cs` nuevo, su `.Designer.cs` nuevo y `CustomersDbContextModelSnapshot.cs` modificado (más la prueba nueva sin trackear). Abre el `.cs` generado: el `Up` tiene que tener exactamente dos `AddColumn` (`address` con `nullable: false, defaultValue: ""`; `city_id` con `nullable: false, defaultValue: new Guid("00000000-…")`) y un `CreateIndex` `IX_customers_city`. Si el snapshot trae **cualquier otro** cambio —otra columna, otro índice, otra tabla—, **para y pregunta**: el modelo ya venía desalineado.

- [ ] **Step 5: Reescribir `Up`/`Down` en el orden del spec**

Reemplaza el contenido completo de `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/Migrations/<timestamp>_AddCustomerContactAddress.cs` (el `.Designer.cs` y el snapshot **no se tocan**):

```csharp
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// El domicilio vuelve al cliente (spec 2026-09-18): <c>customers.address</c> y
    /// <c>customers.city_id</c>, que <c>AddCustomerAddresses</c> había movido a la fila principal
    /// de la libreta. Desde entonces marcar otra dirección como principal cambiaba dónde está el
    /// cliente, y el siguiente PUT de la ficha pisaba esa dirección.
    ///
    /// El orden no es el que scaffoldea EF: las columnas nacen con default/nulas, se rellenan
    /// desde la principal, y recién entonces se vuelven obligatorias. Ningún cliente cambia de
    /// domicilio visible al desplegar. Si algún cliente no tiene principal, el <c>SET NOT NULL</c>
    /// falla y la migración no aplica: es un dato roto que hay que mirar, no un default. Todo va
    /// en una sola transacción (Npgsql hace DDL transaccional), así que un fallo a mitad no deja
    /// columnas a medio llenar.
    /// </summary>
    public partial class AddCustomerContactAddress : Migration
    {
        // La FK real hacia geography.cities se declara a mano, igual que la de customer_addresses:
        // City vive en otro DbContext y EF no modela relaciones fuera de su ModelBuilder. Postgres
        // la impone igual. Requiere que geography.cities ya exista cuando esta migración corre —
        // Program.cs inicializa Geography antes que Customers.
        private const string CityForeignKey = "FK_customers_cities_city_id";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Las columnas, todavía sin exigir nada: address con default vacío y city_id nula.
            migrationBuilder.AddColumn<string>(
                name: "address",
                schema: "customers",
                table: "customers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "city_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: true);

            // 2. El backfill desde la principal. Todo cliente tiene exactamente una
            // (Customer.ApplyPrincipal); si alguno no, city_id queda nula y el paso 3 lo denuncia.
            migrationBuilder.Sql(@"
                UPDATE customers.customers c
                SET address = a.address,
                    city_id = a.city_id
                FROM customers.customer_addresses a
                WHERE a.customer_id = c.id AND a.is_principal;");

            // 3. Ahora sí obligatorias, y sin el default que sólo servía para crear la columna.
            migrationBuilder.Sql("ALTER TABLE customers.customers ALTER COLUMN city_id SET NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE customers.customers ALTER COLUMN address DROP DEFAULT;");

            // 4. La FK y el índice, con los nombres que tenían antes de CLI-DIR-01.
            migrationBuilder.AddForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customers",
                column: "city_id",
                principalSchema: "geography",
                principalTable: "cities",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "IX_customers_city",
                schema: "customers",
                table: "customers",
                column: "city_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No hay que devolver nada a la libreta: nunca se sacó de ahí.
            migrationBuilder.DropForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_city",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "address",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "city_id",
                schema: "customers",
                table: "customers");
        }
    }
}
```

- [ ] **Step 6: Correr la prueba de migración y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests/Modules.Customers.IntegrationTests.csproj --filter "FullyQualifiedName~CustomerContactAddressMigrationTests"
```

Esperado: `Failed: 0`, `Passed: 3`.

- [ ] **Step 7: Ver en rojo el seeder de carga sintética**

`ExportLoadSeeder.CustomersSql` inserta clientes sin las dos columnas nuevas, que ahora son `NOT NULL` sin default.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~ExportLoadSeedTests.TheSeedFillsItsOwnTenantWithConsistentRowsAndNoSideEffects"
```

Esperado: 1 con error, con `Npgsql.PostgresException : 23502: null value in column "address" of relation "customers" violates not-null constraint`.

- [ ] **Step 8: Arreglar el seeder**

En `src/Bootstrapper/Seeding/ExportLoadSeeder.cs`, reemplaza `CustomersSql` con su comentario (líneas 170-188) y `AddressesSql` con su comentario (líneas 190-198) por:

```csharp
    // El CUC es {prefijo}{departamento DIVIPOLA}{consecutivo de 6}, con el departamento de la ciudad del
    // cliente. greatest(6, …) porque lpad trunca lo que pasa del ancho. El domicilio (address, city_id)
    // es obligatorio desde el spec 2026-09-18; la primera fila de la libreta (AddressesSql) repite el
    // mismo par, como hace Customer.Create.
    private const string CustomersSql = """
        WITH city AS (
            SELECT id, left(divipola_code, 2) AS department
            FROM geography.cities
            ORDER BY divipola_code
            LIMIT 1
        )
        INSERT INTO customers.customers (
            id, tenant_id, cuc, name, business_name, identification_type, identification_number, is_active,
            phone, email, address, city_id, classification_id, with_retention, vat_surplus, version,
            created_at, updated_at)
        SELECT gen_random_uuid(), @tenant,
               'CLI' || city.department || lpad(n::text, greatest(6, length(n::text)), '0'),
               'Cliente de carga ' || n, NULL, 'Nit', (800000000 + n)::text, true,
               NULL, NULL, 'Calle ' || lpad(n::text, greatest(6, length(n::text)), '0') || ' # 10-20', city.id,
               @classification, false, false, 1, @now, @now
        FROM generate_series(1, @customers) AS n
        CROSS JOIN city
        """;

    // La primera fila de la libreta, con el mismo domicilio que el cliente (decisión 3 del spec
    // 2026-09-18): la cotización preselecciona la principal para el envío.
    private const string AddressesSql = """
        INSERT INTO customers.customer_addresses (
            id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
        SELECT gen_random_uuid(), customer.id, 'Principal', customer.address, NULL,
               customer.city_id, true, @now, @now
        FROM customers.customers AS customer
        WHERE customer.tenant_id = @tenant
        """;
```

(`'Calle ' || lpad(n, 6) || ' # 10-20'` es el mismo texto que antes armaba `AddressesSql` con `right(customer.cuc, 6)`: la carga no cambia de forma.)

- [ ] **Step 9: Ver en verde el seeder y las clases de Customers que ya existen**

Con las columnas en la base y el dominio escribiéndolas, la aplicación vuelve a ser consistente: las pruebas existentes de Customers tienen que seguir verdes aunque todavía lean la principal.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~ExportLoadSeedTests"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests/Modules.Customers.IntegrationTests.csproj --filter "FullyQualifiedName~CustomerWriteApiTests|FullyQualifiedName~CustomerApiTests"
```

Esperado: las dos corridas con `Failed: 0`.

- [ ] **Step 10: Formato**

Los archivos generados por `dotnet ef` no se pasan por `--include`: nacen con BOM y `dotnet format` los reescribiría, igual que los demás `Designer.cs` del repo.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs src/Bootstrapper/Seeding/ExportLoadSeeder.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerContactAddressMigrationTests.cs
```

Esperado: ningún diagnóstico que no sea `ENDOFLINE` o `CHARSET`.

- [ ] **Step 11: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/direccion-de-contacto-propia") { throw "ABORT: rama equivocada" }; git add src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/Migrations src/Bootstrapper/Seeding/ExportLoadSeeder.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerContactAddressMigrationTests.cs; git commit -m "feat(customers): columnas address y city_id con backfill desde la principal"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git show --stat HEAD
```

Esperado: el commit con seis archivos (DbContext, migración, Designer, snapshot, seeder, prueba) y el `Select-String` sin salida.

---

### Task 3: Aplicación — la ficha, el listado, el filtro y el export leen el domicilio; `Update` e import dejan de tocar la libreta

Acá cambia el comportamiento visible: `PUT /customers/{id}` escribe sólo el contacto, `POST …/principal` deja los campos planos iguales, y todo lo que resuelve "la ciudad del cliente" lee `customer.CityId`.

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs:71-89`
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs:535-546`
- Modify: `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs:81-82` (comentario)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerMapping.cs:25-27`, `:43-45`, `:80-90`
- Modify: `src/Modules/Customers/Modules.Customers.Application/ListCustomers.cs:135-138`, `:153-159`
- Modify: `src/Modules/Customers/Modules.Customers.Application/ExportCustomers.cs:150-153`, `:168-174`
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs:48-51`
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs:68-76`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs:119-128`, `:181-183`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs` (tres helpers nuevos), `CustomerWriteApiTests.cs`, `CustomerApiTests.cs`, `CustomerStatusAndImportApiTests.cs:500-502` (comentario)

**Interfaces:**
- Consumes: `Customer.Address`/`Customer.CityId` (Task 1), columnas y migración (Task 2), `CustomerAddressRequest(Name, Address, CityId, Phone, IsPrincipal)` (`CustomersDtos.cs:30-35`), rutas `POST /api/v1/tenants/{tenantId}/customers/{customerId}/addresses` y `POST …/addresses/{addressId}/principal` (`CustomerEndpoints.cs:61-91`).
- Produces (en `CustomersApiHarness`, `internal static`):

```csharp
public static Task<CustomerResponse> AddAddressAsync(
    HttpClient client, Guid customerId, Guid cityId, bool isPrincipal = false,
    string name = "Bodega Norte", string address = "Carrera 7 # 71-21", string tenantId = TenantId);
public static Task<CustomerResponse> MakeAddressPrincipalAsync(
    HttpClient client, Guid customerId, Guid addressId, string tenantId = TenantId);
public static Task<CustomerResponse> GetAsync(HttpClient client, Guid customerId, string tenantId = TenantId);
```

- [ ] **Step 1: Helpers del harness**

En `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs`, después de `ListAsync` (línea 317) agrega:

```csharp
    /// <summary>La ficha completa de un cliente, como la devuelve <c>GET /customers/{id}</c>.</summary>
    public static async Task<CustomerResponse> GetAsync(
        HttpClient client, Guid customerId, string tenantId = TenantId)
    {
        var customer = await client.GetFromJsonAsync<CustomerResponse>(
            $"{CustomersUrl(tenantId)}/{customerId}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer;
    }

    /// <summary>Agrega una direccion de envio a la libreta (<c>POST /customers/{id}/addresses</c>)
    /// y devuelve la ficha completa, como hace el endpoint. Otra calle y otra ciudad por defecto:
    /// las pruebas del spec 2026-09-18 necesitan que la libreta y el domicilio sean distinguibles.
    /// El telefono no viaja y llega null, que es lo que admite CustomerAddressRequest.</summary>
    public static async Task<CustomerResponse> AddAddressAsync(
        HttpClient client,
        Guid customerId,
        Guid cityId,
        bool isPrincipal = false,
        string name = "Bodega Norte",
        string address = "Carrera 7 # 71-21",
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            $"{CustomersUrl(tenantId)}/{customerId}/addresses",
            new { name, address, cityId, isPrincipal },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer;
    }

    /// <summary><c>POST /customers/{id}/addresses/{addressId}/principal</c>: la operacion que
    /// antes del spec 2026-09-18 movia el domicilio del cliente.</summary>
    public static async Task<CustomerResponse> MakeAddressPrincipalAsync(
        HttpClient client, Guid customerId, Guid addressId, string tenantId = TenantId)
    {
        var response = await client.PostAsync(
            $"{CustomersUrl(tenantId)}/{customerId}/addresses/{addressId}/principal",
            content: null,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer;
    }
```

- [ ] **Step 2: Las pruebas de escritura que fallan**

En `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWriteApiTests.cs`, después de `UpdateCanChangeTheCityAndTheClassification` (línea 640) agrega:

```csharp
    // El bug que motivo el spec 2026-09-18: marcar otra direccion de la libreta como principal
    // movia el domicilio del cliente. Los campos planos (address/city/department) describen el
    // domicilio y tienen que quedar iguales, en la respuesta y en un GET posterior; addresses[]
    // si cambia de principal.
    [Fact]
    public async Task MakingAnotherAddressPrincipalKeepsTheCustomerAddressAndCity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var cities = await EnsureCitiesAsync(client, 2);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, cities[0].CityId, classification.Id);
        var withTwo = await AddAddressAsync(client, created.Id, cities[1].CityId);
        var warehouse = Assert.Single(withTwo.Addresses, address => !address.IsPrincipal);

        var response = await MakeAddressPrincipalAsync(client, created.Id, warehouse.Id);
        var after = await GetAsync(client, created.Id);

        foreach (var customer in new[] { response, after })
        {
            Assert.Equal(created.Address, customer.Address);
            Assert.Equal(created.City.Id, customer.City.Id);
            Assert.Equal(created.Department.Id, customer.Department.Id);
            Assert.Equal(2, customer.Addresses.Count);
            Assert.Equal(warehouse.Id, customer.Addresses.First().Id);
            Assert.True(customer.Addresses.First().IsPrincipal);
        }
    }

    // Decision 4 del spec: el PUT escribe solo el contacto. La fila principal conserva su calle y
    // su ciudad aunque el domicilio nuevo tenga otras.
    [Fact]
    public async Task UpdateChangesTheCustomerAddressWithoutTouchingTheAddressBook()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var cities = await EnsureCitiesAsync(client, 2);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, cities[0].CityId, classification.Id);
        var principal = Assert.Single(created.Addresses);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(cities[1].CityId, classification.Id, address: "Carrera 7 # 71-21"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("Carrera 7 # 71-21", updated.Address);
        Assert.Equal(cities[1].CityId, updated.City.Id);
        Assert.Equal(cities[1].DepartmentId, updated.Department.Id);
        var stillPrincipal = Assert.Single(updated.Addresses);
        Assert.Equal(principal.Id, stillPrincipal.Id);
        Assert.Equal("Calle 10 # 45-12", stillPrincipal.Address);
        Assert.Equal(cities[0].CityId, stillPrincipal.CityId);
        Assert.True(stillPrincipal.IsPrincipal);
    }
```

En `UpdateCanChangeTheCityAndTheClassification` (líneas 617-640) haz que la ciudad **cambie de verdad** y afirma sobre el contacto: reemplaza `var city = await EnsureCityAsync(client);` por `var cities = await EnsureCitiesAsync(client, 2);`, `CreateCustomerAsync(client, city.CityId, …)` por `CreateCustomerAsync(client, cities[0].CityId, …)`, `NewCustomerBody(city.CityId, newClassification.Id, …)` por `NewCustomerBody(cities[1].CityId, newClassification.Id, …)`, y agrega antes del `Assert.Equal($"GRA{originalSuffix}", updated.Cuc);`:

```csharp
        // La ciudad nueva es la del domicilio; el CUC conserva el departamento de alta.
        Assert.Equal(cities[1].CityId, updated.City.Id);
```

Corrige el comentario de `CreateWithoutAnAddressMarksTheAddressField` (líneas 326-330) por:

```csharp
    // La direccion es obligatoria: es el domicilio del cliente (spec 2026-09-18) y ademas
    // siembra la primera fila de la libreta, cuya ciudad emite el CUC. El rechazo tiene que
    // llegar como validation.failed con el mapa errors, no como el 422 pelado del dominio: es el
    // unico que el formulario sabe leer para marcar el input.
```

- [ ] **Step 3: La prueba del listado que falla**

En `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerApiTests.cs`, después de `ListFiltersByMultipleDepartmentIdsAtOnce` (línea 157) agrega:

```csharp
    // La ciudad de la fila y la del filtro son la misma: la del domicilio del cliente (spec
    // 2026-09-18). Una principal en otra ciudad no lo mueve de fila ni lo hace aparecer al
    // filtrar por esa otra ciudad — mostrar una columna y filtrar por otra deja al usuario con
    // filas que no parecen coincidir.
    [Fact]
    public async Task ListShowsAndFiltersByTheCustomerCityNotByThePrincipalAddress()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var cities = await EnsureCitiesAsync(client, 2);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, cities[0].CityId, classification.Id);
        await AddAddressAsync(client, created.Id, cities[1].CityId, isPrincipal: true);

        var page = await ListAsync(client, string.Empty);
        var byContactCity = await ListAsync(client, $"?cityIds={cities[0].CityId}");
        var byPrincipalCity = await ListAsync(client, $"?cityIds={cities[1].CityId}");

        var item = Assert.Single(page.Items);
        Assert.Equal(cities[0].CityId, item.City.Id);
        Assert.Equal(cities[0].DepartmentId, item.Department.Id);
        Assert.Equal(created.Id, Assert.Single(byContactCity.Items).Id);
        Assert.Empty(byPrincipalCity.Items);
    }
```

Y en `CustomerStatusAndImportApiTests.cs`, reemplaza el comentario de `ImportRowWithoutAnAddressIsRejectedWithItsOwnCode` (líneas 500-502) por:

```csharp
    // La celda Direccion es obligatoria: es el domicilio del cliente y, al crear, siembra ademas
    // la primera fila de la libreta (spec 2026-09-18). En modo actualizacion cambia solo el
    // domicilio. Se rechaza como fila, con su codigo, y no como una excepcion del dominio a mitad
    // del archivo -- que se llevaria puesto el resto del lote.
```

- [ ] **Step 4: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests/Modules.Customers.IntegrationTests.csproj --filter "FullyQualifiedName~MakingAnotherAddressPrincipalKeepsTheCustomerAddressAndCity|FullyQualifiedName~UpdateChangesTheCustomerAddressWithoutTouchingTheAddressBook|FullyQualifiedName~ListShowsAndFiltersByTheCustomerCityNotByThePrincipalAddress|FullyQualifiedName~UpdateCanChangeTheCityAndTheClassification"
```

Esperado: 3 con error y 1 correcta. `MakingAnotherAddressPrincipalKeepsTheCustomerAddressAndCity` falla en `Assert.Equal(created.City.Id, customer.City.Id)` (hoy la respuesta trae la ciudad de la nueva principal); `UpdateChangesTheCustomerAddressWithoutTouchingTheAddressBook` falla en `Assert.Equal("Calle 10 # 45-12", stillPrincipal.Address)` (hoy el PUT pisa la principal); `ListShowsAndFiltersByTheCustomerCityNotByThePrincipalAddress` falla en `Assert.Equal(cities[0].CityId, item.City.Id)`. `UpdateCanChangeTheCityAndTheClassification` pasa antes y después (hoy el espejo en la principal le da la misma ciudad). Pega la salida en el handoff.

- [ ] **Step 5: `UpdateCustomer` deja de tocar la libreta**

En `src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs`, reemplaza desde el comentario `// Los opcionales se mandan siempre` hasta el `now);` que cierra `customer.UpdateAddress(...)` (líneas 71-89) por este comentario; la llamada a `customer.Update(...)` que sigue ya trae `Address`/`CityId` desde la Task 1 y no cambia:

```csharp
        // Los opcionales se mandan siempre, incluidos los null: el PUT reemplaza el recurso
        // entero, asi que un campo ausente se limpia. El CUC no esta en la firma porque no viaja
        // en el request — lo emite el backend al crear. Update si recibe el prefijo de la
        // clasificacion resuelta: lo usa para reescribir el CUC solo si la clasificacion cambio.
        // `address`/`cityId` del request son el **domicilio del cliente** (spec 2026-09-18) y
        // nada mas: la libreta de envio tiene su propio recurso (`/customers/{id}/addresses`) y
        // no se toca desde aca. Espejar el domicilio en la principal era justo el acoplamiento
        // que hacia perder direcciones al marcar otra como principal y volver a guardar.
```

- [ ] **Step 6: `ImportCustomers` deja de tocar la libreta**

En `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs`, borra el bloque `var principal = customer.RequirePrincipalAddress(); customer.UpdateAddress(...)` (líneas 535-546) y deja en su lugar:

```csharp
                // La fila actualiza el domicilio del cliente (address, ciudad) y el resto de la
                // ficha; la libreta de envio no se toca, mismo criterio que UpdateCustomerHandler
                // (spec 2026-09-18).
```

- [ ] **Step 7: `CreateCustomer`, el comentario de la primera fila**

En `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs`, reemplaza el comentario de las líneas 81-82 por:

```csharp
            // La primera direccion de envio nace con el request de alta, con el mismo par que el
            // domicilio de abajo (spec 2026-09-18, decision 3): su ciudad es la que acaba de
            // emitir el CUC, y sin ella la cotizacion no tendria nada que preseleccionar. Desde
            // aca son dos datos distintos: editar la ficha no la toca.
```

- [ ] **Step 8: `CustomerMapping` lee el domicilio**

En `src/Modules/Customers/Modules.Customers.Application/CustomerMapping.cs`:

(a) Líneas 25-27 (dentro de `ToDto`), reemplaza el comentario y la expresión por:

```csharp
        // `address`/`city`/`department` describen el **domicilio del cliente** (spec 2026-09-18):
        // donde esta, lo que la ficha muestra arriba. Las direcciones de envio van en `addresses`,
        // y marcar otra como principal no mueve estos tres campos.
        customer.Address,
```

(b) Líneas 43-45, reemplaza el comentario de `ToAddressDto` por:

```csharp
    // La FK de base garantiza que la ciudad de cada direccion exista, asi que un miss aca es
    // corrupcion de datos: se prefiere un nombre vacio a tirar la ficha entera abajo, que es lo
    // que hace ToDtoAsync con la ciudad del domicilio (esa si es estructural).
```

(c) Líneas 80-90 (dentro de `ToDtoAsync`), reemplaza desde el comentario `// Las ciudades de **todas** sus direcciones` hasta el `"was not found.");` por:

```csharp
        // Las ciudades del domicilio y de todas las direcciones de envio de una vez: el domicilio
        // arma los campos planos del DTO y el resto acompaña a cada fila de la libreta.
        var citiesById = await geographyLookup.FindCitiesAsync(
            customer.Addresses.Select(address => address.CityId).Append(customer.CityId).Distinct().ToArray(),
            cancellationToken);
        var city = citiesById.TryGetValue(customer.CityId, out var contactCity)
            ? contactCity
            : throw new InvalidOperationException(
                $"City '{customer.CityId}' referenced by customer '{customer.Id}' " +
                "was not found.");
```

- [ ] **Step 9: `ListCustomers` y `ExportCustomers` leen el domicilio**

En `src/Modules/Customers/Modules.Customers.Application/ListCustomers.cs`, reemplaza el cálculo de `cityIds` (líneas 135-138) por:

```csharp
        // La ciudad del domicilio y las de la libreta, de una vez: el domicilio arma los campos
        // planos y el resto acompaña a cada direccion de envio del DTO.
        var cityIds = customers
            .SelectMany(customer =>
                customer.Addresses.Select(address => address.CityId).Append(customer.CityId))
            .Distinct()
            .ToArray();
```

y la resolución de `city` (líneas 155-159) por:

```csharp
            var city = citiesById.TryGetValue(customer.CityId, out var cityRef)
                ? cityRef
                : throw new InvalidOperationException(
                    $"City '{customer.CityId}' referenced by customer '{customer.Id}' " +
                    "was not found.");
```

En `src/Modules/Customers/Modules.Customers.Application/ExportCustomers.cs`, exactamente los mismos dos reemplazos: `cityIds` en las líneas 150-153 (mismo bloque con `.Append(customer.CityId)`) y `city` en las líneas 170-174 (mismo bloque con `customer.CityId`).

- [ ] **Step 10: El filtro por ciudad del repositorio, el DTO y el validador**

En `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs`, reemplaza el bloque de `cityIds` en `SearchAsync` (líneas 119-128) por:

```csharp
        // `null` es "sin filtro"; una coleccion (incluso vacia) filtra por esos ids exactos —
        // Departamento ya se tradujo a ids de ciudad en ListCustomersHandler, asi que aca no hay
        // nada que resolver, solo aplicar el IN. Va aca y no en FilteredQuery porque la
        // exportacion no filtra por ciudad. Filtra por la ciudad del **domicilio** del cliente
        // (spec 2026-09-18), no por sus direcciones de envio: es la misma ciudad que la fila
        // muestra, y filtrar por una columna y mostrar otra deja filas que no parecen coincidir.
        if (cityIds is not null)
        {
            query = query.Where(customer => cityIds.Contains(customer.CityId));
        }
```

y el comentario de `FindAsync` (líneas 181-183) por:

```csharp
            // Con las direcciones: los llamadores editan la libreta (ManageCustomerAddresses) y
            // el DTO la devuelve entera, y sin las filas viejas en el change tracker un borrado no
            // se veria. Mismo criterio que QuotationRepository con Items.
```

En `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs`, reemplaza la doc de `Address` en `CustomerDto` (líneas 48-51) por:

```csharp
    /// <summary>La calle del **domicilio del cliente**; su ciudad va en <c>City</c>. Se conserva
    /// plano —y no solo dentro de `Addresses`— porque es lo que la cotizacion y el PDF muestran
    /// como domicilio. Las direcciones de envio van en `Addresses`, y marcar otra como principal
    /// no cambia este campo (spec 2026-09-18).</summary>
```

En `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs`, reemplaza el comentario y la regla de `Address` (líneas 68-76) por:

```csharp
        // Obligatoria: es el domicilio del cliente (CustomerContactInfo.Address) y en el alta
        // ademas siembra la primera fila de la libreta. La regla vive aca y no solo en el dominio
        // para que el rechazo llegue como validation.failed con el mapa errors -- el unico 422 que
        // el formulario sabe leer para marcar el input.
        RuleFor(command => command.Address)
            .NotEmpty()
            .MaximumLength(CustomerContactInfo.AddressMaxLength);
```

- [ ] **Step 11: Correr y ver el GREEN — las cuatro clases de Customers que cambian de lectura**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests/Modules.Customers.IntegrationTests.csproj --filter "FullyQualifiedName~CustomerWriteApiTests|FullyQualifiedName~CustomerApiTests|FullyQualifiedName~CustomerStatusAndImportApiTests|FullyQualifiedName~CustomerExportApiTests"
```

Esperado: build con `0 Errores` y sin advertencias (si `RequirePrincipalAddress` quedara referenciado en algún archivo de `src/` que este plan no tocó, el build lo diría: hoy sus únicos callers son los que este paso y la Task 4 reemplazan); la corrida con `Failed: 0`, incluidas por nombre `MakingAnotherAddressPrincipalKeepsTheCustomerAddressAndCity`, `UpdateChangesTheCustomerAddressWithoutTouchingTheAddressBook`, `ListShowsAndFiltersByTheCustomerCityNotByThePrincipalAddress`, `UpdateCanChangeTheCityAndTheClassification`, `CreateWithoutAnAddressMarksTheAddressField`, `ListResolvesTheCityAndTheClassificationOfEachItem`, `GetResolvesTheCityDepartmentAndClassification`, `ListFiltersByDepartmentIdsAndByCityIds`.

- [ ] **Step 12: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs src/Modules/Customers/Modules.Customers.Application/CustomerMapping.cs src/Modules/Customers/Modules.Customers.Application/ListCustomers.cs src/Modules/Customers/Modules.Customers.Application/ExportCustomers.cs src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWriteApiTests.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerApiTests.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs
```

Esperado: ningún diagnóstico que no sea `ENDOFLINE` o `CHARSET`.

- [ ] **Step 13: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/direccion-de-contacto-propia") { throw "ABORT: rama equivocada" }; git add src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs src/Modules/Customers/Modules.Customers.Application/CustomerMapping.cs src/Modules/Customers/Modules.Customers.Application/ListCustomers.cs src/Modules/Customers/Modules.Customers.Application/ExportCustomers.cs src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWriteApiTests.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerApiTests.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs; git commit -m "feat(customers): la ficha, el listado y el export leen el domicilio del cliente"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con trece archivos y el `Select-String` sin salida.

---

### Task 4: Bootstrapper — `QuotationCustomerLookup` y `CustomerReportSource` leen el domicilio

Los dos adaptadores del composition root son los últimos lectores de la principal: el domicilio maestro que la cotización y el pedido reciben como respaldo de facturación y envío, y la ciudad por la que el reporte de clientes agrupa y filtra.

**Files:**
- Modify: `src/Bootstrapper/QuotationCustomerLookup.cs:33-61`
- Modify: `src/Bootstrapper/CustomerReportSource.cs:186-199`, `:293-304`, `:336-339`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` (dos helpers y un record nuevos), `QuotationApiTests.cs` (una prueba nueva)
- Test: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs` (dos helpers y un record nuevos), `CustomerReportSummaryApiTests.cs:7-18` y `:70-96`

**Interfaces:**
- Consumes: `Customer.Address`/`Customer.CityId` (Task 1); `QuotationCustomerRef` (`IQuotationCustomerLookup.cs:58-81`, sin cambio de firma); `QuotationResponse.Client` (`QuotationClientResponse`, `QuotationsDtos.cs:220-239`); `CustomerReportSummary.ByDepartment` (existente).
- Produces (en cada harness, `internal static`):

```csharp
public sealed record CityInDepartment(Guid CityId, Guid DepartmentId);

// La primera es la misma ciudad que EnsureCityIdAsync: un cliente creado con
// CreateActiveCustomerAsync vive en First.
public static Task<(CityInDepartment First, CityInDepartment Second)> EnsureCityIdsInTwoDepartmentsAsync(HttpClient client);

// POST /customers/{id}/addresses con isPrincipal = true; devuelve el id de la direccion nueva.
public static Task<Guid> AddPrincipalAddressAsync(
    HttpClient client, Guid tenantId, Guid customerId, Guid cityId, string address = "Carrera 7 # 71-21");
```

- [ ] **Step 1: Helpers del harness de Quotations**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`, después de `EnsureCityIdAsync` (línea 186) agrega:

```csharp
    public sealed record CityInDepartment(Guid CityId, Guid DepartmentId);

    /// <summary>Dos ciudades de departamentos distintos, en el orden en que la API devuelve los
    /// departamentos: la primera es la misma que <see cref="EnsureCityIdAsync"/>, asi que un
    /// cliente creado con <see cref="CreateActiveCustomerAsync"/> vive en <c>First</c>.</summary>
    public static async Task<(CityInDepartment First, CityInDepartment Second)> EnsureCityIdsInTwoDepartmentsAsync(
        HttpClient client)
    {
        var departments = await client.GetFromJsonAsync<List<GeographyDepartmentDto>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);

        var found = new List<CityInDepartment>();
        foreach (var department in departments)
        {
            if (found.Count == 2)
            {
                break;
            }

            var cities = await client.GetFromJsonAsync<List<GeographyCityDto>>(
                $"/api/v1/cities?departmentId={department.Id}",
                TestContext.Current.CancellationToken);
            if (cities is { Count: > 0 })
            {
                found.Add(new CityInDepartment(cities[0].Id, department.Id));
            }
        }

        if (found.Count < 2)
        {
            throw new InvalidOperationException(
                "Fewer than two seeded DIVIPOLA departments have at least one city.");
        }

        return (found[0], found[1]);
    }

    /// <summary>Agrega una direccion de envio a la libreta del cliente y la marca principal
    /// (<c>POST /customers/{id}/addresses</c>). Devuelve el id de la fila nueva. Es la operacion
    /// que antes del spec 2026-09-18 movia el domicilio del cliente.</summary>
    public static async Task<Guid> AddPrincipalAddressAsync(
        HttpClient client,
        Guid tenantId,
        Guid customerId,
        Guid cityId,
        string address = "Carrera 7 # 71-21")
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers/{customerId}/addresses",
            new { name = "Oficina", address, cityId, isPrincipal = true },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return Assert.Single(customer.Addresses, item => item.IsPrincipal).Id;
    }
```

(`CustomerResponse` es `Modules.Customers.Application.CustomerResponse`, que el harness ya importa en la línea 14; no choca con el `CustomerResponseDto` privado del final del archivo.)

- [ ] **Step 2: La prueba de cotización que falla**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs`, después de `CreateUsesThePrefixAndCounterConfiguredForTheTenant` (línea 71) agrega:

```csharp
    /// <summary>
    /// El domicilio de la cotizacion es el del cliente (su contacto), no la principal de la libreta
    /// (spec 2026-09-18, decision 5): <c>QuotationResponseComposer.cs:96-110</c> entrega
    /// <c>customer.address</c> como respaldo de facturacion y envio, y <c>addresses[0]</c> es la
    /// principal, la que el selector de envio preselecciona. Con la principal en otra ciudad, las
    /// dos cosas se distinguen.
    /// </summary>
    [Fact]
    public async Task CreateShowsTheCustomerAddressAndKeepsThePrincipalFirstInTheAddressBook()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (home, elsewhere) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var principalAddressId = await AddPrincipalAddressAsync(client, tenantId, clientId, elsewhere.CityId);

        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        Assert.NotNull(quotation.Client);
        Assert.Equal("Calle 10 # 45-12", quotation.Client.Address);
        Assert.Equal(home.CityId, quotation.Client.CityId);
        Assert.Equal(home.DepartmentId, quotation.Client.DepartmentId);
        Assert.Equal(2, quotation.Client.Addresses.Count);
        var principal = quotation.Client.Addresses.First();
        Assert.Equal(principalAddressId, principal.Id);
        Assert.True(principal.IsPrincipal);
        Assert.Equal(elsewhere.CityId, principal.CityId);
    }
```

- [ ] **Step 3: Helpers del harness de Reporting**

En `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs`, después de `EnsureCityIdAsync` (línea 176) agrega el mismo par de helpers y el mismo record que en el Step 1, **textualmente iguales** (los DTOs `GeographyDepartmentDto`/`GeographyCityDto` de este harness tienen la misma forma, y `Modules.Customers.Application` ya está importado en la línea 10). Repetirlos y no compartirlos es el criterio del repo: cada módulo de pruebas tiene su harness completo.

- [ ] **Step 4: La prueba del reporte, renombrada y con la principal en otro departamento**

En `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/CustomerReportSummaryApiTests.cs`:

(a) Reemplaza las líneas 15-17 del `<summary>` de la clase por:

```csharp
/// - El reparto por departamento agrupa por **la ciudad del cliente** (su domicilio, spec
///   2026-09-18), y el departamento vive del otro lado de la frontera de Geography: la consulta
///   agrupa en la base y resuelve el nombre después, y eso solo se ve contra PostgreSQL real.
```

(b) Reemplaza `SummaryGroupsByTheDepartmentOfThePrincipalAddress` entero (líneas 70-96) por:

```csharp
    /// <summary>
    /// El reparto por departamento agrupa por la ciudad del cliente —su domicilio—, y el
    /// departamento vive del otro lado de la frontera de <c>Geography</c>. La libreta no cuenta:
    /// una principal en otro departamento no mueve al cliente de grupo (spec 2026-09-18,
    /// decision 5). Sin eso, el listado diria una ciudad y el panel otra para el mismo cliente.
    /// </summary>
    [Fact]
    public async Task SummaryGroupsByTheDepartmentOfTheCustomerAddress()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var (home, elsewhere) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        var first = await CreateActiveCustomerAsync(client, tenant.TenantId);
        await CreateActiveCustomerAsync(client, tenant.TenantId);
        await AddPrincipalAddressAsync(client, tenant.TenantId, first.Id, elsewhere.CityId);

        var summary = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        // El harness siembra los dos en la misma ciudad, asi que caen en el mismo departamento
        // aunque uno tenga su direccion de envio principal en otro.
        var department = Assert.Single(summary.ByDepartment);
        Assert.Equal(home.DepartmentId, department.Id);
        Assert.False(string.IsNullOrWhiteSpace(department.Label));
        Assert.Equal(1, department.EntityCount);
        Assert.Equal(2, department.Count);
    }
```

- [ ] **Step 5: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~CreateShowsTheCustomerAddressAndKeepsThePrincipalFirstInTheAddressBook"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj --filter "FullyQualifiedName~SummaryGroupsByTheDepartmentOfTheCustomerAddress"
```

Esperado: 1 con error en cada corrida. La de cotizaciones falla en `Assert.Equal("Calle 10 # 45-12", quotation.Client.Address)` con `Actual: Carrera 7 # 71-21` (hoy el lookup entrega la principal); la del reporte falla en `Assert.Single(summary.ByDepartment)` con dos elementos (hoy agrupa por la principal). Pega la salida en el handoff.

- [ ] **Step 6: `QuotationCustomerLookup` lee el domicilio**

En `src/Bootstrapper/QuotationCustomerLookup.cs`, reemplaza desde el comentario `// Las ciudades de todas sus direcciones de una vez` hasta `principalCity?.DepartmentName,` (líneas 33-61) por:

```csharp
        // Las ciudades del domicilio y de toda la libreta de una vez: la cotizacion muestra la
        // libreta completa en su selector de envio, y cada fila necesita el nombre de su ciudad.
        var citiesById = await geographyLookup.FindCitiesAsync(
            customer.Addresses.Select(address => address.CityId).Append(customer.CityId).Distinct().ToArray(),
            cancellationToken);
        citiesById.TryGetValue(customer.CityId, out var contactCity);

        return new QuotationCustomerRef(
            customer.Id.Value,
            customer.TenantId,
            customer.Cuc,
            customer.IsActive,
            customer.Name,
            customer.Phone,
            // El domicilio del cliente (spec 2026-09-18), no la principal de la libreta: es el
            // respaldo de facturacion y envio que QuotationResponseComposer.cs:96-110 entrega a
            // la cotizacion y al pedido, y lo que viaja en el WhatsApp. La principal sigue yendo
            // primera en Addresses, que es lo que el selector de envio preselecciona.
            customer.Address,
            customer.WithRetention,
            customer.VatSurplus,
            customer.Email,
            contactCity?.CityId,
            contactCity?.CityName,
            contactCity?.DepartmentId,
            contactCity?.DepartmentName,
```

El resto del `return` (`customer.Addresses.OrderByDescending(address => address.IsPrincipal)…`, `customer.UpdatedAt`, `customer.BusinessName`) no cambia.

- [ ] **Step 7: `CustomerReportSource` agrupa y filtra por el domicilio**

En `src/Bootstrapper/CustomerReportSource.cs`:

(a) En `RankDepartmentsAsync`, reemplaza desde el comentario `// La proyeccion a tipo anonimo antes del GroupBy` hasta `.ToListAsync(cancellationToken);` (líneas 186-199) por:

```csharp
        // Agrupa por la ciudad del domicilio del cliente (spec 2026-09-18): una columna propia,
        // sin subconsulta a la libreta. El departamento se resuelve despues, del otro lado de la
        // frontera de Geography.
        var byCity = await filtered
            .GroupBy(customer => customer.CityId)
            .Select(group => new { CityId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
```

(b) En `FilterCustomersAsync`, reemplaza el comentario y el `Where` (líneas 297-303) por:

```csharp
            // Un departamento sin ciudades no puede tener clientes: la lista vacia hace que el
            // Contains no matchee nada, que es la respuesta correcta y no "todos".
            // La ciudad del cliente es la de su domicilio (spec 2026-09-18), no la de cada bodega
            // de su libreta: el reporte agrupa y filtra por donde esta el cliente.
            query = query.Where(customer => cityIds.Contains(customer.CityId));
```

(c) En `BuildQueryAsync`, reemplaza la subconsulta (líneas 336-339) por una sola línea:

```csharp
                row.customer.CityId,
```

- [ ] **Step 8: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationApiTests"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj --filter "FullyQualifiedName~CustomerReportSummaryApiTests|FullyQualifiedName~CustomerReportListApiTests"
```

Esperado: build con `0 Errores`; las dos corridas con `Failed: 0`. En la de cotizaciones tienen que pasar, por nombre, `CreateShowsTheCustomerAddressAndKeepsThePrincipalFirstInTheAddressBook` y `CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor` (la cotización "igual al cliente" de siempre). Si no existe una clase `CustomerReportListApiTests`, el filtro simplemente no la encuentra: lo que importa es que `SummaryGroupsByTheDepartmentOfTheCustomerAddress` esté en verde y que ninguna prueba de reportes de clientes se haya puesto roja.

- [ ] **Step 9: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Bootstrapper/QuotationCustomerLookup.cs src/Bootstrapper/CustomerReportSource.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/CustomerReportSummaryApiTests.cs
```

Esperado: ningún diagnóstico que no sea `ENDOFLINE` o `CHARSET`.

- [ ] **Step 10: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/direccion-de-contacto-propia") { throw "ABORT: rama equivocada" }; git add src/Bootstrapper/QuotationCustomerLookup.cs src/Bootstrapper/CustomerReportSource.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/CustomerReportSummaryApiTests.cs; git commit -m "feat(customers): cotizaciones y reporte leen el domicilio del cliente"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con seis archivos y el `Select-String` sin salida.

---

### Task 5: Barrido de comentarios que afirman que el domicilio es la principal

Las Tasks 1-4 ya corrigen, en el mismo commit que el código, cada comentario que invalidan. Esta tarea comprueba que no quedó ninguno. Si el barrido no encuentra nada, la tarea termina sin commit.

**Files:**
- Ninguno salvo que el barrido encuentre algo; en ese caso, sólo comentarios, sin tocar código.

**Interfaces:**
- Consumes: el árbol después de la Task 4.
- Produces: la salida literal de los tres barridos en el handoff.

- [ ] **Step 1: `principal` en Domain y Application de Customers**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
rg -n -i "principal" src/Modules/Customers/Modules.Customers.Domain src/Modules/Customers/Modules.Customers.Application
```

Esperado: sólo coincidencias que hablan de la **libreta** —`Customer.cs` (`PrincipalAddress`, `RequirePrincipalAddress`, `AddAddress`/`UpdateAddress`/`MakeAddressPrincipal`/`RemoveAddress`/`ApplyPrincipal`, `customers.address.principal_not_removable`, los comentarios del constructor sobre "la primera direccion nace principal" y los de `Addresses`/`Address` que dicen explícitamente que la principal **no** es el domicilio), `CustomerAddress.cs` (`IsPrincipal`, `MarkPrincipal`), `ManageCustomerAddresses.cs`, `CustomersDtos.cs` (`IsPrincipal`, y la doc de `Address` que dice que marcar otra principal no lo cambia), `CustomerMapping.cs` (`OrderByDescending(address => address.IsPrincipal)` y la doc de `ToDto`), `CreateCustomer.cs` (la primera fila), `UpdateCustomer.cs` e `ImportCustomers.cs` (los comentarios nuevos que dicen que **no** se toca). Cualquier línea que todavía diga que `address`/`city` del cliente **son** la principal, o que el PUT edita la principal, se corrige acá.

- [ ] **Step 2: `RequirePrincipalAddress` en todo el repo**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
rg -n "RequirePrincipalAddress" src tests
```

Esperado: sólo `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (la definición y su doc, actualizada en la Task 1). Ninguna llamada en `Application`, `Infrastructure`, `Bootstrapper` ni `tests`. Si aparece una llamada, es un lector de la principal que este plan no vio: **para y pregunta** antes de tocarlo.

- [ ] **Step 3: "dirección principal" en prosa**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
rg -n -i "direcci[oó]n principal" src tests
```

Esperado: sólo coincidencias en las migraciones históricas de Customers (`20260906002234_AddCustomerAddresses.cs`, que no se toca) y en textos que describen la libreta (`CustomersDbContext.cs` sobre `IX_customer_addresses_principal`). `CustomerStatusAndImportApiTests.cs:501`, `CustomerWriteApiTests.cs:327`, `CustomerReportSummaryApiTests.cs:15`, `CustomerTests.cs:330`, `ExportLoadSeeder.cs:171`/`:190`, `CustomerReportSource.cs:299` y `UpdateCustomer.cs:75` ya no aparecen: se corrigieron en las Tasks 1-4.

- [ ] **Step 4: Sólo si algo quedó — corregir y commitear**

Corrige el comentario en el archivo que lo tenga, sin tocar código, y:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
if ((git branch --show-current) -ne "feature/direccion-de-contacto-propia") { throw "ABORT: rama equivocada" }; git add <ruta-del-archivo-corregido>; git commit -m "docs(customers): comentarios que decían que el domicilio era la principal"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: build con `0 Errores`, el commit sólo con los archivos corregidos y el `Select-String` sin salida. Si los tres barridos salieron limpios, **no hay commit** en esta tarea.

---

### Task 6: Verificación final

**Files:**
- Ninguno, salvo que la verificación pida un arreglo; en ese caso, el arreglo va con su propia prueba y su commit `fix(customers): …`.

**Interfaces:**
- Consumes: todo lo anterior y los números del baseline (Task 0, Step 4).
- Produces: el handoff con la salida literal de cada comando y la lista de commits.

- [ ] **Step 1: Los comandos de README § Verificación**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet format Backend.slnx --verify-no-changes --no-restore
dotnet build Backend.slnx --no-restore
git status --short
git diff --stat develop HEAD -- "**/packages.lock.json" Directory.Packages.props
```

Esperado: restore sin `NU1004` (ningún `packages.lock.json` cambió); `dotnet format` sin diagnósticos salvo `ENDOFLINE`/`CHARSET` en archivos que esta rama no tocó y en los `Designer.cs`; build con `0 Advertencia(s)` y `0 Errores`; `git status` vacío; el `diff --stat` sin archivos.

- [ ] **Step 2: ArchitectureTests y las unitarias de Customers**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
git diff --stat develop HEAD -- tests/ArchitectureTests
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests/Modules.Customers.UnitTests.csproj --no-build
```

Esperado: `ArchitectureTests` con `Failed: 0` y el mismo número de correctas que en el baseline (`Modules.Customers.Application` sigue sin referenciar EF Core ni Npgsql; ninguna regla cambió, y el `diff --stat` de `tests/ArchitectureTests` está vacío); las unitarias de Customers con `Failed: 0` y **siete** casos más que en el baseline.

- [ ] **Step 3: La suite completa, una sola vez**

Tarda decenas de minutos y exige Docker; corre en primer plano.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
$final = Join-Path $env:TEMP "qep-direccion-contacto-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger trx --results-directory $final
Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique
```

Esperado: la lista de fallidas **vacía**. Si alguna falla, antes de tocar código comprueba si ya fallaba en `develop`: `git worktree add ..\qep-backend-worktrees\baseline develop` y corre sólo esa clase ahí con `dotnet test <csproj> --filter "FullyQualifiedName~<Clase>"`; si también falla en `develop`, se anota en el handoff y no es de este plan; si sólo falla acá, se arregla con su prueba en un commit `fix(customers): …` y se repite este paso. Al terminar, `git worktree remove ..\qep-backend-worktrees\baseline`.

- [ ] **Step 4: Las pruebas que el spec nombra, por nombre**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult | ForEach-Object { "$($_.outcome) $($_.testName)" }
} | Select-String -Pattern "UpdateChangesTheContactAddressAndLeavesTheAddressBookUntouched|MakeAddressPrincipalDoesNotChangeTheContactAddress|CreateSeedsTheContactAddressAndTheFirstAddressBookRow|ContactInfoRejectsAnEmptyAddress|ContactInfoRejectsAnEmptyCityId|ContactInfoRejectsAnAddressLongerThanTheColumn|MakingAnotherAddressPrincipalKeepsTheCustomerAddressAndCity|UpdateChangesTheCustomerAddressWithoutTouchingTheAddressBook|UpdateCanChangeTheCityAndTheClassification|CreateWithoutAnAddressMarksTheAddressField|ListResolvesTheCityAndTheClassificationOfEachItem|GetResolvesTheCityDepartmentAndClassification|ListShowsAndFiltersByTheCustomerCityNotByThePrincipalAddress|SummaryGroupsByTheDepartmentOfTheCustomerAddress|CreateShowsTheCustomerAddressAndKeepsThePrincipalFirstInTheAddressBook|TheBackfillCopiesThePrincipalAddressIntoTheCustomer|ACustomerWithoutAPrincipalAddressStopsTheMigration|RevertingRemovesTheColumnsAndLeavesTheAddressBookIntact"
```

Esperado: cada nombre aparece al menos una vez, todos con `Passed`, y ninguno con `SummaryGroupsByTheDepartmentOfThePrincipalAddress` (el nombre viejo ya no existe).

- [ ] **Step 5: Historial**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git log --format="%h %s" develop..HEAD
git log --format=%B develop..HEAD | Select-String -SimpleMatch "Co-Authored-By"
git diff --stat develop HEAD
```

Esperado: los cinco commits de la tabla de Entrega (seis si la Task 5 encontró algo, más los `fix` que haya pedido el Step 3), en orden; el `Select-String` sin salida; y el `diff --stat` sin un solo archivo de `qep-frontend`, de `k8s/` ni de `Directory.Packages.props`. La rama **no se publica ni se mergea** desde este plan: el handoff dice qué quedó y el owner decide. Recuerda en el handoff el orden de deploy del spec: backend primero (con la migración), frontend después.

---

## Auto-revisión del plan contra el spec

**Cobertura del spec.** Decisión 1 (dirección y ciudad propias) → Tasks 1 y 2. Decisión 2 (revivir vestigios, sin value object nuevo) → Task 1. Decisión 3 (el alta sigue sembrando la primera fila) → Task 1 (`CreateSeedsTheContactAddressAndTheFirstAddressBookRow`) y `CreateCustomer` sin cambio de comportamiento en Task 3. Decisión 4 (`Update`/import sólo contacto) → Task 3, Steps 5-6. Decisión 5 (todo lo que es "dónde está el cliente" lee el contacto) → Task 3 (DTO, listado, filtro, export) y Task 4 (cotización, reporte). Decisión 6 (backfill) → Task 2. Decisión 7 (obligatoria) → Task 1 (`address_required`, `city_required`) y `CustomerWriteRules` en Task 3. Decisión 8 (endpoints de libreta sin cambio) → sin tarea de código; lo verifican `MakingAnotherAddressPrincipalKeepsTheCustomerAddressAndCity` y los helpers del harness. § Contrato: los tres códigos de error están en Task 1; `POST …/principal` y `PUT` en Task 3. § Dominio: el comentario de `Customer.cs:330` era en realidad `CustomerTests.cs:330` y se corrige en Task 1; `EnsureValidCityId` recupera caller en Task 1. § Infraestructura: los cuatro pasos de la migración, `Down`, el snapshot por `dotnet ef` y la FK a mano están en Task 2. § Adaptadores: `QuotationCustomerLookup`, `CustomerReportSource` en Task 4; `ExportLoadSeeder` en Task 2 (hallazgo 5). § Bordes: cliente sin principal → `ACustomerWithoutAPrincipalAddressStopsTheMigration`; PUT con la misma ciudad → cubierto por `UpdateChangesTheCustomerAddressWithoutTouchingTheAddressBook` (la libreta no se toca en ningún caso); borrar la principal → sin cambio; import en modo actualización → comentario corregido en Task 3; concurrencia → sin cambio. § Pruebas: cada nombre del spec tiene su paso, y el barrido final (Task 6, Step 4) los lista. § Orden de trabajo: se respeta salvo el seeder (hallazgo 5) y las cuatro construcciones de Application que se completan en Task 1 para que compile (hallazgo 4); el paso 6 (comentarios en el mismo commit) se cumple dentro de cada tarea y se verifica en Task 5.

**Placeholders.** No hay `TBD`, `TODO`, "similar a la Task N" ni "agregar validación": cada paso de código muestra el código, cada paso de corrida muestra el comando y la salida esperada, y los únicos `<…>` son `<timestamp>` (lo pone `dotnet ef`; la prueba lo resuelve por sufijo) y `<ruta-del-archivo-corregido>`/`<Clase>` en pasos condicionales que sólo corren si algo falló.

**Consistencia de tipos.** `CustomerContactInfo { string? Phone; string? Email; required string Address; required Guid CityId }` se usa igual en Task 1 (dominio y tests), Task 3 (`Address = command.Address ?? string.Empty, CityId = command.CityId`) y en el import (`candidate.Address ?? string.Empty`, `candidate.City.CityId`). `Customer.Address` es `string` y `Customer.CityId` es `Guid` en Task 1, en la configuración EF de Task 2 (`HasMaxLength(CustomerContactInfo.AddressMaxLength)`, `city_id`), en `CustomerMapping`/`ListCustomers`/`ExportCustomers`/`CustomerRepository` (Task 3) y en `QuotationCustomerLookup`/`CustomerReportSource` (Task 4, `GroupBy(customer => customer.CityId)`, `cityIds.Contains(customer.CityId)`). Los helpers de harness tienen la misma firma en Interfaces y en el código: `AddAddressAsync(client, customerId, cityId, isPrincipal, name, address, tenantId)` y `MakeAddressPrincipalAsync(client, customerId, addressId, tenantId)` en Customers; `EnsureCityIdsInTwoDepartmentsAsync(client)` → `(CityInDepartment First, CityInDepartment Second)` y `AddPrincipalAddressAsync(client, tenantId, customerId, cityId, address)` → `Guid` en Quotations y Reporting. El nombre de la migración es `AddCustomerContactAddress` en el comando `dotnet ef`, en la clase, en el sufijo `_AddCustomerContactAddress` de la prueba y en el barrido final.
