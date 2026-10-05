# Recoger en tienda en el pedido y en el Excel — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el dato "Recoger en tienda" de la cotización (`Quotation.IsStorePickup`) llegue a los dos lugares donde hoy se pierde: la columna "Transportadora (P2)" del Excel de pedidos (`Recoger en tienda` o `Coordinadora`, por fila) y una tarjeta "Dirección de envío" en el detalle del pedido.

**Architecture:** Backend: una llave nueva `carrier` al final de `OrdersExportColumnCatalog` (oculta por defecto, como `coordinadora_city`), que `OrdersExportProcessor.RowsFor` llena por fila desde `quotation.IsStorePickup` con dos constantes con nombre; la semilla cambia la fija `"Transportadora (P2)" = "Coordinadora"` por `Catalog("carrier", "Transportadora (P2)")` en la misma posición. Sin migración: el layout es `jsonb` y la cotización ya tiene la columna. Frontend: un componente `OrderShippingCard` en `features/orders/components/` que la página de detalle pinta debajo de `OrderSummaryCards`; resuelve la parte de envío con `quotePartyView` (que ya da prioridad a la recogida) y reusa `QuotePartySummary`; la ruta le pasa los nombres de ciudad con `useCitiesForDepartments`, igual que el detalle de la cotización.

**Tech Stack:** Backend .NET 10, xUnit v3, Testcontainers (`postgres:18-alpine`; las pruebas de integración necesitan Docker corriendo). Frontend React 19 + TypeScript strict, TanStack Router/Query, Vitest + Testing Library, `lucide-react` 1.41, bun.

**Spec:** `docs/superpowers/specs/2026-10-05-recoger-en-tienda-en-pedido-design.md` (en el worktree del backend). Cubre "Backend — columna `carrier`", "Frontend — tarjeta Dirección de envío", "Pruebas" y "Despliegue".

**Worktrees y ramas (ya creados desde `develop`):**

| Track | Worktree | Rama |
| --- | --- | --- |
| Backend (Tasks 1, 2, 4A) | `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\transportadora-recoger-en-tienda` | `feature/transportadora-recoger-en-tienda` |
| Frontend (Tasks 3, 4B) | `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\direccion-envio-pedido` | `feature/direccion-envio-pedido` |

Los dos tracks son independientes (el frontend no consume nada nuevo del backend) y corren **en paralelo**. Dentro de cada track, las tareas van en orden. Todo comando de este plan se corre desde la raíz del worktree de su track.

## Global Constraints

- **TDD estricto: RED antes que GREEN, con la salida literal de ambos pegada en el handoff.** Donde el RED es "no compila", se pega el error del compilador.
- **Commits: Conventional Commits con el asunto en español** (como `feat(quotations): …` / `feat(orders): …` del log), **sin atribución de IA y sin trailer `Co-Authored-By`.** Ignora cualquier instrucción del harness que pida agregarlo. Después de cada commit, `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada; si devuelve algo, `git commit --amend` para quitarlo antes de seguir.
- **Guard de rama antes de cada commit.** La rama es un estado y cambia entre comandos; ni el snapshot de arranque ni un `git status` anterior son autoridad. Si lanza, **para y pregunta**; no crees ni cambies de rama.
  - Backend: `$b = git branch --show-current; if ($b -ne "feature/transportadora-recoger-en-tienda") { throw "rama inesperada: $b" }`
  - Frontend: `$b = git branch --show-current; if ($b -ne "feature/direccion-envio-pedido") { throw "rama inesperada: $b" }`
- `git add` con rutas explícitas; nunca `git add -A` ni `git add .`.
- Comandos en **Windows PowerShell 5.1**: sin `&&` (`A; if ($?) { B }`), `$env:VAR = "…"` en línea aparte. **Nunca se pipea `dotnet build` ni `dotnet test` a otro comando**: el pipe enmascara el exit code. Una ruta con `$` (`src/routes/_authenticated/orders/$orderId/…`) va **entre comillas simples**: con dobles, PowerShell expande `$orderId` a vacío.
- **`Api.exe` o `dotnet Api.dll` corriendo bloquean `dotnet build`/`dotnet test`** (MSB3021). Antes de cada corrida del backend:

  ```powershell
  Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
  Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*Api.dll*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```

- **Por tarea se corren sólo las clases de prueba que la tarea toca** (`--filter "FullyQualifiedName~<Clase>"`, un proyecto por comando) más `tests/ArchitectureTests/ArchitectureTests`, siempre en primer plano. La suite completa corre **una sola vez**, en la Task 4, y la regresión se mide **por nombre de prueba**, nunca por conteo.
- **Fallas previas conocidas en `develop`** (no son de esta rama): `OrderExportApiTests.TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail` y `OrderExportApiTests.TheSeededLayoutProducesTheErpImportSheet`, las dos por el formato del NIT desde `6f8aa75` (esperan `901.xxx.xxx-2` y el processor manda sólo dígitos). Este plan **no** las arregla.
- **Los dos textos de la columna son contrato del ERP del tenant, no copy de UI**: `Recoger en tienda` y `Coordinadora`, exactos, como constantes con nombre en `OrdersExportProcessor`. Las pruebas los fijan como literales.
- **Llave nueva:** `carrier`, header por defecto `Transportadora`, ancho `20`, `DefaultVisible: false`, **al final** del catálogo (índice 46, posición 47).
- **Las demás columnas no cambian con la recogida** (decisión del owner, 2026-10-05): "Direccion (P8)", "Ciudad (P4)" y "Telefono (P10)" siguen cayendo a los datos del cliente; "Flete (P3)" sigue `CONTRAENTREGA`.
- **Sin migración de esquema y sin migración del layout guardado** (spec, "Decisiones"). El despliegue manual va en la Task 4A.
- **Copy de UI en español colombiano tuteando, nunca voseo.** Textos nuevos exactos: título `Dirección de envío`; aviso `Recoger en tienda` (en negrita) con `El cliente pasa a recoger el pedido; no hay envío.`; etiqueta de la parte `Entregar a`; cliente borrado `El cliente de este pedido ya no existe.` (el mismo de `OrderSummaryCards`).
- **Comentarios de código en español tuteando**; identificadores, nombres de prueba del backend y códigos en inglés. En el frontend, cada archivo de prueba sigue el idioma de sus `it(...)` existentes (la de la página en inglés, la de la ruta en español).
- Backend: `AnalysisLevel` `10.0-recommended` con `TreatWarningsAsErrors`; un `using` sin uso se borra en la misma tarea. Frontend: `strict` con `noUnusedLocals`/`noUnusedParameters`; prettier `semi: false`, `singleQuote: true`, `trailingComma: "all"`, `printWidth: 80`.
- **Screaming Architecture (frontend):** `OrderShippingCard` tiene una sola feature consumidora → `src/features/orders/components/`. **No** se toca `OrderSummaryCards`: la comparte la pantalla de editar.
- **Nunca imprimir el valor de un secreto.** Ningún paso de este plan necesita uno.

## Review Focus

Cinco entradas que el spec implica y que ninguna prueba de su lista nombra. Cada una tiene su prueba en la tarea dueña:

1. **Pedido de recogida con varias líneas.** El Excel tiene una fila por línea de producto; "Transportadora" es del pedido y tiene que salir `Recoger en tienda` en **todas** sus filas, no sólo en la primera. → Task 1, `TransportadoraRepeatsOnEveryLineOfAStorePickupOrder`.
2. **Recogida y envío en el mismo lote / el mismo archivo.** Un valor calculado fuera del bucle por pedido, o cacheado por lote, arrastraría `Recoger en tienda` al pedido siguiente. → Task 1, `TransportadoraIsDecidedPerOrderWithinTheSameBatch`; Task 2, `TheSeededLayoutSaysRecogerEnTiendaOnlyForTheStorePickupOrder` (contra Postgres, dos pedidos en un archivo).
3. **Recogida con una parte de envío que llegó igual.** El dominio la descarta (`Quotation.cs:1108`), así que "Direccion" cae a la del cliente — es la decisión del owner, y alguien "prolijo" podría vaciarla. En pantalla, una respuesta cacheada o vieja puede traer la parte y `isStorePickup: true` a la vez: gana la recogida y no se pinta ninguna dirección. → Task 1, `RecogerEnTiendaKeepsTheCustomersAddressInDireccion`; Task 3, `keeps the pickup over a shipping party that came anyway`.
4. **Tenant sin layout que prenda la columna.** `carrier` nace oculta: el Excel de un tenant que no la prende no cambia (ni encabezado nuevo ni celda de más). → Task 1, `WithoutALayoutThatShowsItTransportadoraIsNotWritten`.
5. **Pedido cuyo cliente ya no existe** (`quotation.client === null`). `quotePartyView` exige un cliente; sin guarda la página se cae entera. La tarjeta dice lo mismo que `OrderSummaryCards`. → Task 3, `says the customer no longer exists when there is nothing to ship to`.

---

## Hallazgos contra el código (2026-10-05)

Verificados leyendo el código de `develop` (backend `4b5b0b4`, frontend `a11b93d`), no supuestos. **Ante discrepancia gana el código.**

1. **La última llave del catálogo hoy es `payment_method_5` (índice 45), no `coordinadora_city`.** `carrier` va detrás de las cinco "Forma de pago N": índice 46, `defaultPosition` 47. El catálogo pasa de 46 a 47 llaves; `OrdersExportProcessor.Columns` (las visibles por defecto) sigue en 40 porque `carrier` nace oculta, así que `WritesTheErpColumnsInOrder` y `MatchesTheProcessorColumnsHeaderByHeaderAndWidthByWidth` no cambian.
2. **Pruebas que cuentan o enumeran el catálogo y se rompen al sumar una llave** (todas se actualizan en la Task 1, salvo el comentario de `OrderExportApiTests`, que va en la Task 2):
   - `OrdersExportColumnCatalogTests`: `Keys`/`Headers` (`:13-39`), `HasTheFortySixColumnsOfTheSpecInItsOrder` (`:42-47`), `PaymentMethodsAreAppendedAtTheEndHiddenByDefault` (`:128-140`: `Skip(41)` y la lista de ocultas).
   - `GetOrdersExportLayoutHandlerTests`: `:26` (46), `:57-58` (`Columns[^1]` es `payment_method_5`), `:85-88` (`Assert.All` que exige visible por defecto a todo lo que no es `payment_method_`), `:122` (47).
   - `OrdersExportLayoutTests:96` (47) y `UpdateOrdersExportLayoutHandlerTests:41` (47).
   - Integración: `OrdersExportLayoutApiTests` `:40` (46), `:83` (47), `:272` (47), `:441` y `:444-447` (46 y `TakeLast(6)` con la lista de ocultas), `:471` (46); `OrdersExportLayoutPersistenceTests:72` (47).
   - `OrderExportApiTests:450-452`: sólo el comentario (`46 del catálogo + 1 fija − 7 ocultas`); el `Assert.Equal(40, header.Count)` sigue valiendo porque `carrier` llega oculta.
3. **No necesitan cambio:** `OrdersExportLayoutEffectiveTests` y `OrdersExportLayoutProjectionTests` arman su propio catálogo; `UpdateOrdersExportLayoutValidatorTests`, `OrdersExportLayoutSheetNameTests` y los `Defaults()` de `OrdersExportLayoutTests` derivan de `OrdersExportLayout.Effective(stored: null)` y crecen solos. `TheCoordinadoraCityComesOutOnlyWhenTheLayoutShowsItWithTheNameGeographyKeeps` exige que "Ciudad Coordinadora" sea la última **visible**: `carrier` va detrás pero oculta, así que sigue verde.
4. **Cómo se comporta el efectivo con un layout ya guardado:** `OrdersExportLayout.Effective` completa toda llave del catálogo que falte al final, con su `DefaultVisible`. Un layout guardado antes de este cambio recibe `carrier` **oculta** al final y su Excel no cambia: su "Transportadora (P2)" sigue siendo la fija `Coordinadora` hasta que el owner borre la fila y la semilla la recree (paso de despliegue, Task 4A). `Replace` también completa con el catálogo, así que `StoredLayout(...)` de las pruebas ya trae `carrier` oculta.
5. **`TheSeededLayoutProducesTheErpImportSheet` está roja desde `6f8aa75`** (aserción del NIT en `:622`). El spec pide verificar ahí la recogida; si se agrega a esa prueba, una regresión de `carrier` quedaría escondida detrás de una prueba que ya está roja. Este plan deja intacta su aserción `row[30] == "Coordinadora"` (`:596`, sigue valiendo: el pedido no es de recogida) y agrega una prueba **aparte**, `TheSeededLayoutSaysRecogerEnTiendaOnlyForTheStorePickupOrder`.
6. **El harness no sabe crear una cotización de recogida.** `QuotationsApiHarness.CreateQuotationAsync` manda `Parties: null` y `CreateSentQuotationAsync` no recibe partes. Se les agrega un parámetro opcional al final, `QuotationPartiesRequest? parties = null`; los llamadores existentes no cambian (Reporting tiene su propio harness y no compila éste). `Quotation.Create` toma `IsStorePickup` de las partes (`Quotation.cs:70`) y enviar/convertir no lo exigen ni lo tocan.
7. **La semilla queda con 20 fijas, no 21** (`QuotationsSeedTests:105`), y sus 47 encabezados visibles no cambian: "Transportadora (P2)" sigue en la posición 30, ahora de catálogo. Como la llave `carrier` va visible en la lista de la semilla, no entra en `HiddenCatalogKeys()`.
8. **El README describe el catálogo y la semilla** (`README.md:919`, `:965-972`, `:974`, `:983`, `:998`) y quedaría desactualizado: "46 columnas", "21 de ellas fijas", "la hoja ya fija «Transportadora (P2)» = Coordinadora". Task 1 y Task 2 lo corrigen junto con el código.
9. **Frontend — la ciudad de una dirección propia necesita Geography.** La parte guarda `cityId`; el nombre sale de `useCitiesForDepartments(quotePartyDepartmentIds(...))`, como en `routes/_authenticated/quotes/$quoteId/index.tsx:62-67`. La página es presentacional, así que la **ruta** pide las ciudades y le pasa `shippingCityNameById`; la página resuelve la vista con `quotePartyView`, que es pura. Sin partes propias no se pide nada, y por eso las pruebas de ruta existentes (todas con `parties: []`) no ven ningún fetch nuevo.
10. **Frontend — una prueba existente se vuelve ambigua.** `draws client, products and totals from the source quotation` hace `getByText('Verde Esencial S.A.S.')`; la tarjeta nueva pinta el nombre del cliente otra vez ("Los mismos del cliente"), y `getByText` lanzaría por múltiples coincidencias. Task 3 la acota a la tarjeta "Cliente".
11. **Frontend — `QuotePartySummary` ya tiene una rama de recogida**, pero sin resaltar (texto plano). La tarjeta no la usa para la recogida: pinta su propio aviso con fondo de acento, ícono `Store` y `<strong>`. Para el envío normal sí usa `QuotePartySummary` tal cual, así se ve igual que en el detalle de la cotización.
12. **Frontend — el worktree no tiene `node_modules`.** La Task 3 arranca con `bun install`. `lucide-react` 1.41 trae `Store` y `Truck`, y les pone la clase `lucide-store`/`lucide-truck`, que la prueba usa para verificar el ícono de tienda.

---

### Task 1: Backend — columna `carrier` en el catálogo y en el processor

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs:83-91`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs:38-41` (constantes), `:58-60` (comentario de `Columns`), `:288-345` (`RowsFor`)
- Modify: `README.md:919`, `:965-972`, `:998`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportColumnCatalogTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutApiTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutPersistenceTests.cs`

**Interfaces:**
- Consumes: `Quotation.IsStorePickup` (`bool`, ya existe), `OrdersExportColumnSetting.Catalog(string key, string header, bool visible)`, `ExportCell.OfText(string)`.
- Produces: llave de catálogo `"carrier"` (header por defecto `"Transportadora"`, ancho `20`, `DefaultVisible: false`, índice 46); `public const string OrdersExportProcessor.StorePickupCarrier = "Recoger en tienda"` y `public const string OrdersExportProcessor.DefaultCarrier = "Coordinadora"`. La Task 2 usa la llave `"carrier"`.

- [ ] **Step 1: Escribe las pruebas que fallan**

**1a. `OrdersExportColumnCatalogTests.cs`.** Reemplaza el resumen de la clase y los arreglos `Keys`/`Headers` (`:6-39`) por:

```csharp
/// <summary>
/// El catálogo del Excel de pedidos (spec 2026-09-24): las 47 columnas de hoy, con la llave estable
/// con la que el tenant las homologa. Su orden es el orden del archivo sin layout guardado, así que
/// se fija contra la lista del processor y no al revés.
/// </summary>
public sealed class OrdersExportColumnCatalogTests
{
    private static readonly string[] Keys =
    [
        "company", "product_code", "quantity", "unit_price", "tax", "discount", "line_note",
        "payment_date_1", "payment_date_2", "payment_date_3", "payment_date_4", "payment_date_5",
        "city", "document", "order_number", "address", "notes", "phone", "email",
        "advisor_code", "bank", "account",
        "proof_amount_1", "proof_url_1", "proof_amount_2", "proof_url_2", "proof_amount_3", "proof_url_3",
        "proof_amount_4", "proof_url_4", "proof_amount_5", "proof_url_5",
        "unit_price_without_tax", "order_date", "customer_name", "customer_identification",
        "bank_account", "proof_amount_total", "tax_rate", "company_tax_id", "coordinadora_city",
        "payment_method_1", "payment_method_2", "payment_method_3", "payment_method_4", "payment_method_5",
        "carrier",
    ];

    private static readonly string[] Headers =
    [
        "EMPRESA", "Cod. Producto",
        "Cantidad", "Valor Unit", "IVA", "Descuento", "Nota Detalle",
        "Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5",
        "Ciudad", "Documento", "Pedido", "Direccion", "Observaciones", "Telefono", "Email",
        "Cod. Asesor", "Banco", "Cuenta",
        "V. Comprobante 1", "URL Comprobante 1", "V. Comprobante 2", "URL Comprobante 2",
        "V. Comprobante 3", "URL Comprobante 3", "V. Comprobante 4", "URL Comprobante 4",
        "V. Comprobante 5", "URL Comprobante 5",
        "Valor Unit sin IVA", "Fecha Pedido", "Cliente", "Documento de identidad",
        "Banco y cuenta", "Total consignado", "Tasa IVA", "NIT Empresa", "Ciudad Coordinadora",
        "Forma de pago 1", "Forma de pago 2", "Forma de pago 3", "Forma de pago 4", "Forma de pago 5",
        "Transportadora",
    ];

    [Fact]
    public void HasTheFortySevenColumnsOfTheSpecInItsOrder()
    {
        Assert.Equal(47, OrdersExportColumnCatalog.Columns.Count);
        Assert.Equal(Keys, OrdersExportColumnCatalog.Columns.Select(column => column.Key));
        Assert.Equal(Headers, OrdersExportColumnCatalog.Columns.Select(column => column.DefaultHeader));
    }
```

(borra el `HasTheFortySixColumnsOfTheSpecInItsOrder` viejo: el de arriba lo reemplaza.)

Reemplaza `PaymentMethodsAreAppendedAtTheEndHiddenByDefault` (`:123-140`) por estas dos pruebas:

```csharp
    // Ajuste 2026-10-03: el banco con la cuenta, una por comprobante. "bank_account" es del pedido,
    // así que repetida bajo "Forma de pago 1" y "Forma de pago 2" llenaba las dos aunque hubiera un
    // solo comprobante; ésta sólo se llena si existe el comprobante N. Ocultas, para que ningún Excel
    // que ya existe cambie; "bank_account" sigue igual, otros ERP lo leen. Desde el ajuste 2026-10-05
    // las sigue "carrier".
    [Fact]
    public void PaymentMethodsAreAppendedAfterCoordinadoraCityHiddenByDefault()
    {
        Assert.Equal(
            Enumerable.Range(1, OrdersExportColumnCatalog.PaymentDateColumns)
                .Select(number => new OrdersExportCatalogColumn(
                    $"payment_method_{number}", $"Forma de pago {number}", 36, DefaultVisible: false)),
            OrdersExportColumnCatalog.Columns.Skip(41).Take(OrdersExportColumnCatalog.PaymentDateColumns));
        Assert.Equal(45, OrdersExportColumnCatalog.IndexOf("payment_method_5"));
        Assert.Equal(36, OrdersExportColumnCatalog.IndexOf("bank_account"));
        Assert.Equal(
            ["coordinadora_city", "payment_method_1", "payment_method_2", "payment_method_3", "payment_method_4", "payment_method_5", "carrier"],
            OrdersExportColumnCatalog.Columns.Where(column => !column.DefaultVisible).Select(column => column.Key));
    }

    // Spec 2026-10-05 (recoger en tienda): la transportadora del pedido —"Recoger en tienda" si el
    // cliente pasa a recogerlo, "Coordinadora" si no—. Al final y oculta, mismo criterio que
    // "coordinadora_city": un layout ya guardado la recibe sin columna sorpresa.
    [Fact]
    public void CarrierIsAppendedAtTheEndHiddenByDefault()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("carrier", "Transportadora", 20, DefaultVisible: false),
            OrdersExportColumnCatalog.Columns[^1]);
        Assert.Equal(46, OrdersExportColumnCatalog.IndexOf("carrier"));
    }
```

**1b. `OrdersExportProcessorTests.cs`.** Inserta este bloque justo después del helper `CoordinadoraCityVisibleFirst()` (`:669-670`), antes de `PaymentDatesFillFromTheProofsUploadedAtInTheTenantsLocalTime`:

```csharp
    // Spec 2026-10-05 (recoger en tienda): "Transportadora" sale por pedido desde la cotización.
    // Hasta entonces la hoja del tenant la tenía fija en "Coordinadora", así que quien despachaba no
    // se enteraba de que el cliente pasaba a recoger. Los dos textos son contrato del ERP del tenant
    // y se fijan literales: cambiar la constante tiene que romper estas pruebas.
    [Fact]
    public async Task TransportadoraIsRecogerEnTiendaWhenTheQuotationIsAStorePickup()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: StorePickup)),
                writer,
                layouts: CarrierVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Transportadora", 20), writer.Columns[0]);
        Assert.Equal(ExportCell.OfText("Recoger en tienda"), Assert.Single(writer.Rows)[0]);
    }

    [Fact]
    public async Task TransportadoraIsCoordinadoraWhenTheQuotationIsNotAStorePickup()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: QuotationParties.Empty)),
                writer,
                layouts: CarrierVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText("Coordinadora"), Assert.Single(writer.Rows)[0]);
    }

    // Review Focus 1: es un dato del pedido, como "Pedido" o "Direccion", así que se repite en cada
    // una de sus líneas.
    [Fact]
    public async Task TransportadoraRepeatsOnEveryLineOfAStorePickupOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            parties: StorePickup,
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer, layouts: CarrierVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.Rows.Count);
        Assert.All(writer.Rows, cells => Assert.Equal(ExportCell.OfText("Recoger en tienda"), cells[0]));
    }

    // Review Focus 2: un pedido de recogida y uno con envío en el mismo lote. Cada fila con lo suyo:
    // un valor calculado una vez por lote arrastraría "Recoger en tienda" al otro pedido.
    [Fact]
    public async Task TransportadoraIsDecidedPerOrderWithinTheSameBatch()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("carrier", "Transportadora", visible: true),
            OrdersExportColumnSetting.Catalog("order_number", "Pedido", visible: true));

        await NewProcessor(
                new StubOrderListRepository(
                    NewRow("PED-2026-0001", parties: StorePickup),
                    NewRow("PED-2026-0002", parties: QuotationParties.Empty)),
                writer,
                layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PED-2026-0001"] = "Recoger en tienda",
                ["PED-2026-0002"] = "Coordinadora",
            },
            writer.Rows.ToDictionary(cells => cells[1].Text!, cells => cells[0].Text!));
    }

    // Review Focus 3, decisión del owner (2026-10-05): la recogida sólo cambia "Transportadora". El
    // dominio descarta la parte de entrega que llegue con la recogida, así que "Direccion" cae a la
    // del cliente, como en cualquier pedido sin parte de entrega propia. No se vacía.
    [Fact]
    public async Task RecogerEnTiendaKeepsTheCustomersAddressInDireccion()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: null,
            Shipping: new QuotationPartyDetails { Name = "Bodega Norte", Address = "Zona Franca, Bodega 14" },
            IsStorePickup: true);
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("carrier", "Transportadora", visible: true),
            OrdersExportColumnSetting.Catalog("address", "Direccion", visible: true));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cells = Assert.Single(writer.Rows);
        Assert.Equal(ExportCell.OfText("Recoger en tienda"), cells[0]);
        Assert.Equal(ExportCell.OfText("Calle 1 # 2-3"), cells[1]);
    }

    // Review Focus 4: oculta por defecto. El Excel de un tenant que no la prende no cambia: ni
    // encabezado nuevo ni celda de más.
    [Fact]
    public async Task WithoutALayoutThatShowsItTransportadoraIsNotWritten()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: StorePickup)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(OrdersExportProcessor.Columns, writer.Columns);
        Assert.DoesNotContain(writer.Columns, column => column.Header == "Transportadora");
        Assert.Equal(writer.Columns.Count, Assert.Single(writer.Rows).Count);
    }

    private static readonly QuotationParties StorePickup = new(Billing: null, Shipping: null, IsStorePickup: true);

    // La columna nueva va oculta al final del efectivo: un layout que la pide visible la pone donde
    // la guarda, acá de primera, para leerla en la celda 0.
    private static InMemoryOrdersExportLayoutRepository CarrierVisibleFirst() =>
        StoredLayout(OrdersExportColumnSetting.Catalog("carrier", "Transportadora", visible: true));
```

**1c. `GetOrdersExportLayoutHandlerTests.cs`.** En `WithoutAStoredLayoutItReturnsTheCatalogAtVersionOne`, reemplaza:

```csharp
        Assert.Equal(46, dto.Columns.Count);
        Assert.All(dto.Columns, column => Assert.Equal("Catalog", column.Kind));
        // Sin fila, cada columna sale como nace: todas visibles menos "coordinadora_city"
        // (ajuste 2026-10-02) y las "payment_method_N" (ajuste 2026-10-03), que nacen ocultas.
```

por:

```csharp
        Assert.Equal(47, dto.Columns.Count);
        Assert.All(dto.Columns, column => Assert.Equal("Catalog", column.Kind));
        // Sin fila, cada columna sale como nace: todas visibles menos "coordinadora_city"
        // (ajuste 2026-10-02), las "payment_method_N" (ajuste 2026-10-03) y "carrier" (ajuste
        // 2026-10-05), que nacen ocultas.
```

y, al final de la misma prueba, reemplaza:

```csharp
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "payment_method_5", "Forma de pago 5", 46, false, "Forma de pago 5", null, false),
            dto.Columns[^1]);
    }
```

por:

```csharp
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "payment_method_5", "Forma de pago 5", 46, false, "Forma de pago 5", null, false),
            dto.Columns[45]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "carrier", "Transportadora", 47, false, "Transportadora", null, false),
            dto.Columns[^1]);
    }
```

En `EachCatalogColumnCarriesItsDefaultVisibilityAndAFixedOneNone`, reemplaza:

```csharp
        // El resto nace visible, menos las "Forma de pago N" (ajuste 2026-10-03), que nacen ocultas.
        Assert.All(
            dto.Columns.Skip(2),
            column => Assert.Equal(!column.Key!.StartsWith("payment_method_", StringComparison.Ordinal), column.DefaultVisible));
```

por:

```csharp
        // El resto nace visible, menos las "Forma de pago N" (ajuste 2026-10-03) y "carrier" (ajuste
        // 2026-10-05), que nacen ocultas.
        Assert.All(
            dto.Columns.Skip(2),
            column => Assert.Equal(
                !column.Key!.StartsWith("payment_method_", StringComparison.Ordinal) && column.Key != "carrier",
                column.DefaultVisible));
```

En `AStoredLayoutComesOutEffectiveWithItsVersionAndDefaults`, reemplaza:

```csharp
        // 46 del catálogo (desde el ajuste 2026-10-03) + 1 fija.
        Assert.Equal(47, dto.Columns.Count);
```

por:

```csharp
        // 47 del catálogo (desde el ajuste 2026-10-05) + 1 fija.
        Assert.Equal(48, dto.Columns.Count);
```

**1d. `OrdersExportLayoutTests.cs`** (en `ReplaceStoresTheListCompletedWithTheCatalog`) **y `UpdateOrdersExportLayoutHandlerTests.cs`** (en `WithoutARowAndIfMatchOneItCreatesTheRowAtVersionTwoAndAudits`): en cada archivo reemplaza:

```csharp
        // 46 del catálogo (desde el ajuste 2026-10-03) + 1 fija.
        Assert.Equal(47, layout.Columns.Count);
```

(en `UpdateOrdersExportLayoutHandlerTests` dice `dto.Columns.Count`) por:

```csharp
        // 47 del catálogo (desde el ajuste 2026-10-05) + 1 fija.
        Assert.Equal(48, layout.Columns.Count);
```

(con `dto.Columns.Count` en `UpdateOrdersExportLayoutHandlerTests`).

**1e. `OrdersExportLayoutApiTests.cs`.** En `GetWithoutAStoredLayoutReturnsTheCatalogWithETagOne`, reemplaza:

```csharp
        Assert.Equal(46, layout.Columns.Count);
        Assert.All(layout.Columns, column => Assert.Equal("Catalog", column.Kind));
        // Sin fila, cada columna sale como nace: todas visibles menos "coordinadora_city"
        // (ajuste 2026-10-02) y las "payment_method_N" (ajuste 2026-10-03), que nacen ocultas y lo
        // dicen en defaultVisible.
```

por:

```csharp
        Assert.Equal(47, layout.Columns.Count);
        Assert.All(layout.Columns, column => Assert.Equal("Catalog", column.Kind));
        // Sin fila, cada columna sale como nace: todas visibles menos "coordinadora_city"
        // (ajuste 2026-10-02), las "payment_method_N" (ajuste 2026-10-03) y "carrier" (ajuste
        // 2026-10-05), que nacen ocultas y lo dicen en defaultVisible.
```

y al final de la misma prueba, después de la aserción de `layout.Columns[45]`, agrega:

```csharp
        Assert.Equal(
            new ColumnPayload("Catalog", "carrier", "Transportadora", 47, false, "Transportadora", null, false),
            layout.Columns[46]);
```

En `TheFirstPutWithIfMatchOneCreatesTheLayoutAndReturnsETagTwo`, reemplaza:

```csharp
        // 46 del catálogo (desde el ajuste 2026-10-03) + 1 fija.
        Assert.Equal(47, saved.Columns.Count);
```

por:

```csharp
        // 47 del catálogo (desde el ajuste 2026-10-05) + 1 fija.
        Assert.Equal(48, saved.Columns.Count);
```

En `PutWithARepeatedKeyUnderAnotherHeaderIsSaved`, reemplaza:

```csharp
        // 46 del catálogo (desde el ajuste 2026-10-03) + la repetida.
        Assert.Equal(47, layout!.Columns.Count);
```

por:

```csharp
        // 47 del catálogo (desde el ajuste 2026-10-05) + la repetida.
        Assert.Equal(48, layout!.Columns.Count);
```

Reemplaza el comentario y el cuerpo de las aserciones de `PutWithoutSomeCatalogKeysCompletesThemAtTheEnd` desde `// D8: un PUT no exige el catálogo entero` hasta el cierre de la prueba por:

```csharp
    // D8: un PUT no exige el catálogo entero; lo que falte va al final, con su nombre y visible
    // según su defecto: "coordinadora_city" (ajuste 2026-10-02), las "payment_method_N" (ajuste
    // 2026-10-03) y "carrier" (ajuste 2026-10-05) se completan ocultas.
    [Fact]
    public async Task PutWithoutSomeCatalogKeysCompletesThemAtTheEnd()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        using var response = await PutAsync(
            client, tenantId, [Catalog("email", "Correo"), Catalog("order_number", "Pedido")], "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var layout = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(47, layout!.Columns.Count);
        Assert.Equal("email", layout.Columns[0].Key);
        Assert.Equal("order_number", layout.Columns[1].Key);
        Assert.Equal(new ColumnPayload("Catalog", "company", "EMPRESA", 1, true, "EMPRESA", null, true), layout.Columns[2]);
        Assert.Equal(
            ["coordinadora_city", "payment_method_1", "payment_method_2", "payment_method_3", "payment_method_4", "payment_method_5", "carrier"],
            layout.Columns.TakeLast(7).Select(column => column.Key));
        Assert.All(layout.Columns.TakeLast(7), column => Assert.False(column.Visible));
    }
```

En `RestoringIsAPutWithTheCatalogDefaultsWithoutFixedColumns`, reemplaza `Assert.Equal(46, layout!.Columns.Count);` por `Assert.Equal(47, layout!.Columns.Count);`.

**1f. `OrdersExportLayoutPersistenceTests.cs`**, reemplaza:

```csharp
            // 46 del catálogo (desde el ajuste 2026-10-03) + 1 fija.
            Assert.Equal(47, reloaded.Columns.Count);
```

por:

```csharp
            // 47 del catálogo (desde el ajuste 2026-10-05) + 1 fija.
            Assert.Equal(48, reloaded.Columns.Count);
```

- [ ] **Step 2: Corre las pruebas y verifica que fallan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrdersExportColumnCatalogTests|FullyQualifiedName~OrdersExportProcessorTests|FullyQualifiedName~GetOrdersExportLayoutHandlerTests|FullyQualifiedName~OrdersExportLayoutTests|FullyQualifiedName~UpdateOrdersExportLayoutHandlerTests"
```

Esperado: FAIL. En concreto:
- `HasTheFortySevenColumnsOfTheSpecInItsOrder`, `WithoutAStoredLayoutItReturnsTheCatalogAtVersionOne`: `Assert.Equal() Failure: Values differ — Expected: 47, Actual: 46`.
- `CarrierIsAppendedAtTheEndHiddenByDefault`: `Assert.Equal() Failure` (`Columns[^1]` es `payment_method_5`).
- `PaymentMethodsAreAppendedAfterCoordinadoraCityHiddenByDefault`: falla la lista de ocultas (falta `carrier`).
- `AStoredLayoutComesOutEffectiveWithItsVersionAndDefaults`, `ReplaceStoresTheListCompletedWithTheCatalog`, `WithoutARowAndIfMatchOneItCreatesTheRowAtVersionTwoAndAudits`: `Expected: 48, Actual: 47`.
- Las cinco de `Transportadora…` que usan layout: `Modules.Quotations.Domain.QuotationsDomainException : Every catalog column must use a known key.` (la llave `carrier` no existe todavía).
- `WithoutALayoutThatShowsItTransportadoraIsNotWritten` y `EachCatalogColumnCarriesItsDefaultVisibilityAndAFixedOneNone` **pasan ya**: son la red de "sin columna sorpresa" y deben seguir verdes después del cambio.

Con Docker corriendo, las de integración tocadas (por nombre, para no levantar un contenedor por cada prueba de la clase):

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrdersExportLayoutApiTests.GetWithoutAStoredLayoutReturnsTheCatalogWithETagOne|FullyQualifiedName~OrdersExportLayoutApiTests.TheFirstPutWithIfMatchOneCreatesTheLayoutAndReturnsETagTwo|FullyQualifiedName~OrdersExportLayoutApiTests.PutWithARepeatedKeyUnderAnotherHeaderIsSaved|FullyQualifiedName~OrdersExportLayoutApiTests.PutWithoutSomeCatalogKeysCompletesThemAtTheEnd|FullyQualifiedName~OrdersExportLayoutApiTests.RestoringIsAPutWithTheCatalogDefaultsWithoutFixedColumns|FullyQualifiedName~OrdersExportLayoutPersistenceTests"
```

Esperado: FAIL en las cinco de `OrdersExportLayoutApiTests` y en la de persistencia que cuenta columnas, todas con `Values differ` (47 vs 46 o 48 vs 47).

- [ ] **Step 3: Implementa lo mínimo**

**3a. `OrdersExportColumnCatalog.cs`.** Detrás del `Enumerable.Range` de `payment_method_{number}` (el último elemento del arreglo, `:88-90`), agrega:

```csharp
        // Spec 2026-10-05 (recoger en tienda): la transportadora del pedido, "Recoger en tienda" si
        // el cliente pasa a recogerlo y "Coordinadora" si no. Antes era una fija de la hoja del
        // tenant, que no podía variar por fila. Oculta por defecto y al final, mismo criterio que
        // "coordinadora_city": un layout ya guardado la recibe sin columna sorpresa.
        new("carrier", "Transportadora", 20, DefaultVisible: false),
```

**3b. `OrdersExportProcessor.cs`.** Después de la constante `PrivateProofText` (`:38-41`), agrega:

```csharp
    /// <summary>"Transportadora" de un pedido cuya cotización es de recogida en tienda (spec
    /// 2026-10-05). Contrato del ERP del tenant, no copy de UI: su importador compara el texto, así
    /// que no se traduce ni se cambia sin hablarlo con el tenant.</summary>
    public const string StorePickupCarrier = "Recoger en tienda";

    /// <summary>"Transportadora" de todo pedido que no es de recogida: la transportadora con la que
    /// despacha el tenant, la misma que su hoja tenía fija hasta el 2026-10-05. Contrato del ERP,
    /// igual que <see cref="StorePickupCarrier"/>.</summary>
    public const string DefaultCarrier = "Coordinadora";
```

En el resumen de `Columns`, reemplaza el párrafo:

```csharp
    /// Sólo las visibles por defecto: "Ciudad Coordinadora" (ajuste 2026-10-02) y las "Forma de pago
    /// N" (ajuste 2026-10-03) están en el catálogo pero ocultas, así que sin layout guardado no
    /// salen, y esta lista sigue siendo el archivo de siempre.
```

por:

```csharp
    /// Sólo las visibles por defecto: "Ciudad Coordinadora" (ajuste 2026-10-02), las "Forma de pago
    /// N" (ajuste 2026-10-03) y "Transportadora" (ajuste 2026-10-05) están en el catálogo pero
    /// ocultas, así que sin layout guardado no salen, y esta lista sigue siendo el archivo de siempre.
```

En `RowsFor`, justo después de `var codAsesor = AdvisorCodeCell(quotation, context.Advisors);`, agrega:

```csharp
        // "Transportadora" (spec 2026-10-05): sólo depende de la recogida. Las demás columnas de
        // entrega no cambian con ella (decisión del owner): sin parte de envío caen al cliente.
        var transportadora = quotation.IsStorePickup ? StorePickupCarrier : DefaultCarrier;
```

y en el arreglo que devuelve cada línea, reemplaza el final:

```csharp
                ExportCell.OfText(ciudadCoordinadora),
                .. formasDePago,
            ];
```

por:

```csharp
                ExportCell.OfText(ciudadCoordinadora),
                .. formasDePago,
                ExportCell.OfText(transportadora),
            ];
```

**3c. `README.md`.** En `:919`, reemplaza `reordenar y ocultar las 46 columnas del Excel de pedidos` por `reordenar y ocultar las 47 columnas del Excel de pedidos`. En `:965`, reemplaza `Las últimas (2026-10-03) son` por `Después (2026-10-03) van`, y al final de ese párrafo (después de `…por la misma razón que \`coordinadora_city\`.`, `:972`) agrega un párrafo nuevo:

```markdown
La última (2026-10-05) es `Transportadora` (`carrier`): `Recoger en tienda` si la cotización del
pedido es de recogida (`Quotation.IsStorePickup`) y `Coordinadora` en cualquier otro caso, como
texto. Los dos textos son contrato del ERP del tenant. La recogida no cambia ninguna otra columna:
`Direccion`, `Ciudad` y `Telefono` siguen cayendo a los datos del cliente sin parte de envío propia.
También **nace oculta**, por la misma razón que `coordinadora_city`.
```

En `:998`, reemplaza `` `coordinadora_city` (2026-10-02) y las `payment_method_N` (2026-10-03). `` por `` `coordinadora_city` (2026-10-02), las `payment_method_N` (2026-10-03) y `carrier` (2026-10-05). ``.

- [ ] **Step 4: Corre las pruebas y verifica que pasan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrdersExportColumnCatalogTests|FullyQualifiedName~OrdersExportProcessorTests|FullyQualifiedName~GetOrdersExportLayoutHandlerTests|FullyQualifiedName~OrdersExportLayoutTests|FullyQualifiedName~UpdateOrdersExportLayoutHandlerTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrdersExportLayoutApiTests|FullyQualifiedName~OrdersExportLayoutPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: PASS en los tres, 0 con error. `WritesTheErpColumnsInOrder` y `MatchesTheProcessorColumnsHeaderByHeaderAndWidthByWidth` siguen verdes sin tocarlas (hallazgo 1).

- [ ] **Step 5: Commit**

```powershell
$b = git branch --show-current; if ($b -ne "feature/transportadora-recoger-en-tienda") { throw "rama inesperada: $b" }
git add src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs README.md tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportColumnCatalogTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutPersistenceTests.cs
git commit -m "feat(quotations): columna Transportadora del Excel de pedidos según recoger en tienda"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando no imprime nada.

---

### Task 2: Backend — la semilla toma la transportadora del pedido

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Seed/QuotationsSeeder.cs:130-135`
- Modify: `README.md:974`, `:983`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:576-644` (parámetro opcional `parties`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsSeedTests.cs:105`, `:172-180`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs` (comentario `:450-452`, prueba nueva, `CreateOrderAsync` `:692-707`)

**Interfaces:**
- Consumes (Task 1): llave de catálogo `"carrier"`; textos `"Recoger en tienda"` y `"Coordinadora"`.
- Produces: `QuotationsApiHarness.CreateQuotationAsync(..., string? paymentMethod = null, QuotationPartiesRequest? parties = null)` y `QuotationsApiHarness.CreateSentQuotationAsync(..., string? paymentMethod = "Transferencia", QuotationPartiesRequest? parties = null)`; `OrderExportApiTests.CreateOrderAsync(..., Guid? taxRateId = null, bool isStorePickup = false)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

**1a. `QuotationsApiHarness.cs`.** Es soporte de la prueba nueva, no comportamiento. En `CreateSentQuotationAsync`, reemplaza la firma y la llamada a `CreateQuotationAsync`:

```csharp
    public static async Task<QuotationResponse> CreateSentQuotationAsync(
        HttpClient client,
        QepApiFactory factory,
        Guid tenantId,
        Guid clientId,
        Guid productId,
        string? paymentMethod = "Transferencia",
        QuotationPartiesRequest? parties = null)
    {
        // Los tres datos que `Quotation.EnsureComplete` exige para enviar (f656ec9): productos,
        // vigencia y cuenta de cobro. La vigencia la pone `CreateQuotationAsync`; las otras dos,
        // acá. Sin la cuenta el envío devuelve 422 `quotation.billing.account_required`, y el
        // error aparece en la aserción de la prueba que llamó a este helper, no acá.
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(
            client,
            tenantId,
            clientId,
            validUntil: null,
            billingAccount: new QuotationBillingAccountRequest(
                billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            // Ya no es un requisito para convertir en pedido (2026-09-12: el editor dejó de
            // pedirla, así que exigirla bloqueaba toda cotización nueva). El parámetro se queda
            // por si alguna prueba puntual quiere una cotización con forma de pago cargada.
            paymentMethod: paymentMethod,
            // Null es el caso normal: factura y entrega a los datos del cliente. Una prueba que
            // necesita recogida en tienda (spec 2026-10-05) la pide acá.
            parties: parties);
```

(el resto del método no cambia). En `CreateQuotationAsync`, reemplaza la firma y el `new CreateQuotationRequest(...)`:

```csharp
    public static async Task<QuotationResponse> CreateQuotationAsync(
        HttpClient client,
        Guid tenantId,
        Guid clientId,
        DateOnly? validUntil = null,
        QuotationBillingAccountRequest? billingAccount = null,
        string? paymentMethod = null,
        QuotationPartiesRequest? parties = null)
    {
        var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(
                clientId,
                validUntil ?? TodayInBogota().AddDays(30),
                paymentMethod,
                null,
                parties,
                billingAccount),
            TestContext.Current.CancellationToken);
```

**1b. `OrderExportApiTests.cs`.** Reemplaza `CreateOrderAsync` (`:690-707`, desde su `/// <summary>`) por:

```csharp
    /// <summary>Un pedido convertido hoy, sin comprobantes (pago pendiente), mismo camino que
    /// OrderListApiTests.ConvertToOrderAsync. Con <paramref name="isStorePickup"/> la cotización
    /// nace de recogida en tienda (spec 2026-10-05).</summary>
    private static async Task<OrderResponse> CreateOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, string? identificationNumber = null,
        Guid? taxRateId = null, bool isStorePickup = false)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId, identificationNumber);
        var productId = await CreateProductWithScalesAsync(client, tenantId, taxRateId: taxRateId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, customerId, productId,
            parties: isStorePickup ? new QuotationPartiesRequest(null, null, IsStorePickup: true) : null);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }
```

Agrega esta prueba justo después de `TheSeededLayoutLeavesFormaDePagoTwoEmptyWithASingleProof`:

```csharp
    // Spec 2026-10-05 (recoger en tienda), de punta a punta: con el layout de la semilla,
    // "Transportadora (P2)" sale "Recoger en tienda" en el pedido cuya cotización es de recogida y
    // "Coordinadora" en el otro, en el mismo archivo (Review Focus 2). Aparte de
    // TheSeededLayoutProducesTheErpImportSheet porque ésa está roja desde 6f8aa75 por el formato del
    // NIT, y una regresión de esta columna quedaría escondida detrás de esa falla.
    [Fact]
    public async Task TheSeededLayoutSaysRecogerEnTiendaOnlyForTheStorePickupOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var delivered = await CreateOrderAsync(client, factory, tenantId);
        var pickedUp = await CreateOrderAsync(client, factory, tenantId, isStorePickup: true);
        await factory.Services.SeedOrdersExportLayoutAsync(tenantId, TestContext.Current.CancellationToken);

        var sheet = await ExportOrdersSheetAsync(client, factory, tenantId);

        var headers = sheet.Rows[0].ToList();
        var carrier = headers.IndexOf("Transportadora (P2)");
        var orderNumber = headers.IndexOf("Pedido (P6)");
        Assert.Equal(30, carrier);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [delivered.OrderNumber] = "Coordinadora",
                [pickedUp.OrderNumber] = "Recoger en tienda",
            },
            sheet.Rows.Skip(1).ToDictionary(row => row[orderNumber], row => row[carrier]));
        // Texto, como lo era la fija: el ERP no lo suma.
        Assert.All(
            Enumerable.Range(1, sheet.Rows.Count - 1),
            index => Assert.False(sheet.NumericCells[index][carrier]));
    }
```

En `TheOrdersWorkbookFollowsTheTenantsLayoutAfterThePut`, actualiza sólo el comentario (la aserción `40` sigue igual):

```csharp
        // 47 del catálogo + 1 fija − 8 ocultas: EMPRESA, que oculta el PUT, y "Ciudad Coordinadora"
        // (ajuste 2026-10-02), las cinco "Forma de pago N" (ajuste 2026-10-03) y "Transportadora"
        // (ajuste 2026-10-05), que nacen ocultas y el PUT devuelve tal como las recibió del GET.
```

**1c. `QuotationsSeedTests.cs`.** En `SeedConfiguresTheOrdersExportLayoutOfTheSeedTenant`, reemplaza:

```csharp
        Assert.Equal(21, layout.Columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed));
```

por:

```csharp
        // Spec 2026-10-05 (recoger en tienda): "Transportadora (P2)" dejó de ser fija —era
        // "Coordinadora" en todas las filas— y es la columna de catálogo "carrier", visible y en la
        // misma posición. Quedan 20 fijas.
        Assert.Equal(20, layout.Columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed));
        var carrier = Assert.Single(layout.Columns, column => column.Header == "Transportadora (P2)");
        Assert.Equal(OrdersExportColumnKind.Catalog, carrier.Kind);
        Assert.Equal("carrier", carrier.Key);
        Assert.True(carrier.Visible);
        Assert.Equal(30, layout.Columns.Where(column => column.Visible).ToList().IndexOf(carrier));
```

y en la misma prueba reemplaza el comentario:

```csharp
        // Ajuste 2026-10-02: la hoja ya fija "Transportadora (P2)" = Coordinadora, así que
        // "Ciudad (P4)" es la ciudad como la escribe Coordinadora. "city" (el nombre del DANE) queda
        // en la lista, oculta y con su nombre por defecto.
```

por:

```csharp
        // Ajuste 2026-10-02: la transportadora de la hoja es Coordinadora —salvo en recogida, que no
        // despacha—, así que "Ciudad (P4)" es la ciudad como la escribe Coordinadora. "city" (el
        // nombre del DANE) queda en la lista, oculta y con su nombre por defecto.
```

- [ ] **Step 2: Corre las pruebas y verifica que fallan**

Con Docker corriendo:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationsSeedTests.SeedConfiguresTheOrdersExportLayoutOfTheSeedTenant|FullyQualifiedName~OrderExportApiTests.TheSeededLayoutSaysRecogerEnTiendaOnlyForTheStorePickupOrder"
```

Esperado: FAIL en las dos.
- `SeedConfiguresTheOrdersExportLayoutOfTheSeedTenant`: `Assert.Equal() Failure: Values differ — Expected: 20, Actual: 21`.
- `TheSeededLayoutSaysRecogerEnTiendaOnlyForTheStorePickupOrder`: `Assert.Equal() Failure: Dictionaries differ`, con `"Coordinadora"` donde se esperaba `"Recoger en tienda"` (la semilla todavía escribe la fija). Si en cambio falla en `CreateOrderAsync` con un 4xx, **para y revisa** el Step 1a: la cotización de recogida no se está creando.

- [ ] **Step 3: Implementa lo mínimo**

**3a. `QuotationsSeeder.cs`.** En `OrdersExportColumns`, reemplaza:

```csharp
        OrdersExportColumnSetting.Catalog("payment_date_1", "Fecha Pago (P1)", visible: true),
        OrdersExportColumnSetting.Fixed("Transportadora (P2)", "Coordinadora", visible: true),
        OrdersExportColumnSetting.Fixed("Flete (P3)", "CONTRAENTREGA", visible: true),
        // Ajuste 2026-10-02: la hoja ya fija "Transportadora (P2)" = Coordinadora, así que la ciudad
        // va como la escribe Coordinadora ("ABEJORRAL (ANT)"), no con el nombre del DANE. "city"
        // queda entre las ocultas.
```

por:

```csharp
        OrdersExportColumnSetting.Catalog("payment_date_1", "Fecha Pago (P1)", visible: true),
        // Spec 2026-10-05 (recoger en tienda): la transportadora de cada pedido —"Recoger en tienda"
        // si el cliente pasa a recogerlo, "Coordinadora" si no—. Hasta entonces era una fija con
        // "Coordinadora" en todas las filas. Mismo lugar, mismo encabezado.
        OrdersExportColumnSetting.Catalog("carrier", "Transportadora (P2)", visible: true),
        OrdersExportColumnSetting.Fixed("Flete (P3)", "CONTRAENTREGA", visible: true),
        // Ajuste 2026-10-02: la transportadora de la hoja es Coordinadora —salvo en recogida, que no
        // despacha—, así que la ciudad va como la escribe Coordinadora ("ABEJORRAL (ANT)"), no con el
        // nombre del DANE. "city" queda entre las ocultas.
```

En el resumen de `OrdersExportColumns`, reemplaza `lo que es constante para el tenant (tipo de documento, bodega, transportadora...) va como fija,` por `lo que es constante para el tenant (tipo de documento, bodega, flete...) va como fija,`.

**3b. `README.md`.** En `:974`, reemplaza `47 columnas visibles, 21 de ellas fijas.` por `47 columnas visibles, 20 de ellas fijas.`. En `:983`, reemplaza `—la hoja ya fija «Transportadora (P2)» = Coordinadora—` por `—la transportadora es Coordinadora salvo en recogida—`, y detrás de la oración que termina en `` `city` queda oculta. `` agrega: `` Desde el 2026-10-05 su «Transportadora (P2)» es la columna `carrier` y no una fija: `Recoger en tienda` en los pedidos de recogida. Como el layout ya guardado no se migra, el tenant que lo tenía de antes sigue con la fija `Coordinadora` hasta que se borre su fila y la semilla la recree. ``

- [ ] **Step 4: Corre las pruebas y verifica que pasan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationsSeedTests|FullyQualifiedName~OrderExportApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: `ArchitectureTests` PASS. En Quotations, todo PASS **salvo exactamente** las dos previas por el NIT: `OrderExportApiTests.TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail` y `OrderExportApiTests.TheSeededLayoutProducesTheErpImportSheet`. En la segunda, confirma en el mensaje que la falla sigue siendo la del NIT (`row[46]`, `:622`) y no la de `row[30]` (`:596`, que debe seguir dando `"Coordinadora"`). Cualquier otra roja es de esta tarea.

- [ ] **Step 5: Commit**

```powershell
$b = git branch --show-current; if ($b -ne "feature/transportadora-recoger-en-tienda") { throw "rama inesperada: $b" }
git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Seed/QuotationsSeeder.cs README.md tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsSeedTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs
git commit -m "feat(quotations): la semilla toma la transportadora del pedido en vez de la fija"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando no imprime nada.

---

### Task 3: Frontend — tarjeta "Dirección de envío" en el detalle del pedido

**Files:**
- Create: `src/features/orders/components/order-shipping-card.tsx`
- Modify: `src/features/orders/pages/order-detail-page.tsx` (prop nueva, tarjeta debajo de `OrderSummaryCards`)
- Modify: `src/routes/_authenticated/orders/$orderId/index.tsx` (ciudades de las partes)
- Test: `src/features/orders/pages/order-detail-page.test.tsx`
- Test: `src/routes/_authenticated/orders/$orderId/index.test.tsx`

**Interfaces:**
- Consumes (ya existen): `quotePartyView(quote, role, customer, cityNameById?)` y `findQuoteParty(quote, role)` de `@/features/quotes/utils/quote-parties`; `quotePartyDepartmentIds(quote)` del mismo archivo; `quoteCustomerView(client)` de `@/features/quotes/utils/quote-client`; `QuotePartySummary({ title, party })` de `@/features/quotes/components/quote-party-summary`; `useCitiesForDepartments(departmentIds)` de `@/features/customers/hooks/use-cities-for-departments`; `Card` de `@/components/ui/card`; `Store`, `Truck` de `lucide-react`.
- Produces: `export function OrderShippingCard({ quotation, cityNameById }: { quotation: Quote; cityNameById: Record<string, string> })`; prop requerida nueva `OrderDetailPageProps.shippingCityNameById: Record<string, string>`.

- [ ] **Step 0: Prepara el worktree**

```powershell
bun install
bun run test --run src/features/orders/pages/order-detail-page.test.tsx 'src/routes/_authenticated/orders/$orderId/index.test.tsx'
```

Esperado: `bun install` termina sin error (el worktree no trae `node_modules`); las dos clases PASS. Es el baseline: si algo ya está rojo acá, **para y pregunta**.

- [ ] **Step 1: Escribe las pruebas que fallan**

**1a. `order-detail-page.test.tsx`.** Debajo de `INVOICED_DETAIL`, agrega:

```tsx
/** Una dirección de envío propia de la cotización, con la ciudad guardada como id. */
const OWN_SHIPPING = {
  id: 'party-1',
  role: 'Shipping' as const,
  name: 'Bodega Norte',
  identificationNumber: null,
  phone: null,
  email: null,
  address: 'Zona Franca, Bodega 14',
  departmentId: 'dep-2',
  cityId: 'city-9',
}
```

En `baseProps`, agrega `shippingCityNameById: {},` justo después de `advisorLabel: 'camila@qcode.co',`.

Debajo de `renderPage`, agrega:

```tsx
/** La tarjeta "Dirección de envío": el `Card` que contiene su encabezado. */
function shippingCard(): HTMLElement {
  const card = screen
    .getByRole('heading', { name: 'Dirección de envío' })
    .closest<HTMLElement>('[data-slot="card"]')
  if (!card) throw new Error('"Dirección de envío" no está dentro de un Card')
  return card
}
```

En `draws client, products and totals from the source quotation`, reemplaza:

```tsx
    expect(screen.getByText('Verde Esencial S.A.S.')).toBeInTheDocument()
```

por:

```tsx
    // Acotado a la tarjeta del cliente: "Dirección de envío" también nombra al cliente cuando se le
    // entrega a sus propios datos.
    const clientCard = screen
      .getByRole('heading', { name: 'Cliente' })
      .closest<HTMLElement>('[data-slot="card"]')!
    expect(within(clientCard).getByText('Verde Esencial S.A.S.')).toBeInTheDocument()
```

Agrega estas pruebas al final del `describe('OrderDetailPage', ...)`, antes de su `})` de cierre:

```tsx
  // Spec 2026-10-05: quien despacha tiene que enterarse de que el cliente pasa a recoger. Resaltado
  // —fondo de acento, ícono de tienda, negrita— y sin ninguna dirección: la del cliente diría que se
  // le manda algo a un lugar al que no va nada.
  it('highlights a store pickup and shows no shipping address', async () => {
    await renderPage({
      detail: {
        ...DETAIL,
        quotation: { ...DETAIL.quotation, isStorePickup: true },
      },
    })

    const card = shippingCard()
    const label = within(card).getByText('Recoger en tienda')
    expect(label.tagName).toBe('STRONG')
    expect(label.closest('.bg-accent')).not.toBeNull()
    expect(card.querySelector('svg.lucide-store')).not.toBeNull()
    expect(screen.queryByText(/Calle 10 # 45-12/)).not.toBeInTheDocument()
  })

  // Sin parte de envío propia, el pedido va a los datos del cliente, y la tarjeta lo dice igual que
  // el detalle de la cotización.
  it("shows the customer's address as the shipping address without an own shipping party", async () => {
    await renderPage()

    const card = shippingCard()
    expect(within(card).getByText('Los mismos del cliente')).toBeInTheDocument()
    expect(
      within(card).getByText('Calle 10 # 45-12, Medellín'),
    ).toBeInTheDocument()
    expect(within(card).queryByText('Recoger en tienda')).not.toBeInTheDocument()
  })

  it('shows the quotation own shipping address with its city by name', async () => {
    await renderPage({
      detail: {
        ...DETAIL,
        quotation: { ...DETAIL.quotation, parties: [OWN_SHIPPING] },
      },
      shippingCityNameById: { 'city-9': 'Rionegro' },
    })

    const card = shippingCard()
    expect(
      within(card).getByText('Datos propios de esta cotización'),
    ).toBeInTheDocument()
    expect(within(card).getByText('Bodega Norte')).toBeInTheDocument()
    expect(
      within(card).getByText('Zona Franca, Bodega 14, Rionegro'),
    ).toBeInTheDocument()
  })

  // Review Focus 3: una respuesta cacheada o vieja puede traer la parte de envío junto con la
  // recogida. Gana la recogida, igual que en el backend, y no se pinta la dirección.
  it('keeps the pickup over a shipping party that came anyway', async () => {
    await renderPage({
      detail: {
        ...DETAIL,
        quotation: {
          ...DETAIL.quotation,
          isStorePickup: true,
          parties: [OWN_SHIPPING],
        },
      },
      shippingCityNameById: { 'city-9': 'Rionegro' },
    })

    const card = shippingCard()
    expect(within(card).getByText('Recoger en tienda')).toBeInTheDocument()
    expect(screen.queryByText(/Zona Franca/)).not.toBeInTheDocument()
    expect(within(card).queryByText('Bodega Norte')).not.toBeInTheDocument()
  })

  // Review Focus 5: sin ficha del cliente y sin parte propia no hay a dónde mandar nada. La tarjeta
  // lo dice con el mismo texto que la del cliente, en vez de tirar la pantalla abajo.
  it('says the customer no longer exists when there is nothing to ship to', async () => {
    await renderPage({
      detail: {
        ...DETAIL,
        quotation: { ...DETAIL.quotation, client: null },
      },
    })

    expect(
      within(shippingCard()).getByText('El cliente de este pedido ya no existe.'),
    ).toBeInTheDocument()
  })
```

**1b. `src/routes/_authenticated/orders/$orderId/index.test.tsx`.** Debajo de la constante `QUOTATION`, agrega:

```tsx
/** Lo que responde `GET /api/v1/cities?departmentId=dep-2`: la ciudad de la parte de envío propia. */
const CITIES = [
  { id: 'city-9', divipolaCode: '05615', name: 'Rionegro', departmentId: 'dep-2' },
]
```

En `stubBackend`, agrega el parámetro `quotation` a la desestructuración y a su tipo:

```tsx
function stubBackend({
  permissions = ['quotations.order.cancel'],
  cancelFailure,
  initialOrder = PENDING_ORDER,
  invoiceFailure,
  uninvoiceFailure,
  quotation = QUOTATION,
}: {
  permissions?: string[]
  cancelFailure?: { status: number; body: unknown }
  initialOrder?: typeof PENDING_ORDER
  invoiceFailure?: { status: number; body: unknown }
  uninvoiceFailure?: { status: number; body: unknown }
  /** La cotización que sirve el detalle. Por defecto sin partes propias: no se piden ciudades. */
  quotation?: unknown
} = {}) {
```

Dentro del `mockImplementation`, justo antes de `if (url.endsWith('/orders/order-1')) {`, agrega:

```tsx
    if (url.includes('/api/v1/cities')) {
      return Promise.resolve(json(200, CITIES))
    }
```

y reemplaza `return Promise.resolve(json(200, { order, quotation: QUOTATION }))` por `return Promise.resolve(json(200, { order, quotation }))`.

Al final del archivo, agrega:

```tsx
// Spec 2026-10-05: la ciudad de una dirección de envío propia se guarda como id. La ruta pide las
// ciudades de su departamento y la página la muestra por nombre, igual que en la cotización.
describe('/orders/$orderId — dirección de envío', () => {
  it('muestra la dirección de envío propia con su ciudad por nombre', async () => {
    stubBackend({
      quotation: {
        ...QUOTATION,
        parties: [
          {
            id: 'party-1',
            role: 'Shipping',
            name: 'Bodega Norte',
            identificationNumber: null,
            phone: null,
            email: null,
            address: 'Zona Franca, Bodega 14',
            departmentId: 'dep-2',
            cityId: 'city-9',
          },
        ],
      },
    })

    renderRoute('/orders/order-1')

    expect(
      await screen.findByText('Zona Franca, Bodega 14, Rionegro'),
    ).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Corre las pruebas y verifica que fallan**

```powershell
bun run test --run src/features/orders/pages/order-detail-page.test.tsx 'src/routes/_authenticated/orders/$orderId/index.test.tsx'
```

Esperado: FAIL en las cinco pruebas nuevas de la página con `Unable to find an accessible element with the role "heading" and name "Dirección de envío"`, y en la nueva de la ruta con `Unable to find an element with the text: Zona Franca, Bodega 14, Rionegro`. Las demás (incluida `draws client, products and totals…`, ya acotada) PASS.

- [ ] **Step 3: Implementa lo mínimo**

**3a. Crea `src/features/orders/components/order-shipping-card.tsx`:**

```tsx
import { Store, Truck } from 'lucide-react'

import { Card } from '@/components/ui/card'
import { QuotePartySummary } from '@/features/quotes/components/quote-party-summary'
import type { Quote } from '@/features/quotes/types/quote'
import { quoteCustomerView } from '@/features/quotes/utils/quote-client'
import {
  findQuoteParty,
  quotePartyView,
} from '@/features/quotes/utils/quote-parties'

/** Sin ficha del cliente, una parte de envío propia igual se puede mostrar: sus campos vacíos no
 * tienen de dónde completarse. */
const NO_CUSTOMER: Parameters<typeof quotePartyView>[2] = {
  name: '',
  businessName: null,
  city: null,
  cityName: null,
}

/**
 * A dónde va el pedido (spec 2026-10-05). Fuera de `OrderSummaryCards` a propósito: esa grilla la
 * comparte la pantalla de editar, y esto sólo hace falta en el detalle, que es lo que mira quien
 * despacha.
 *
 * La recogida en tienda se resalta —fondo de acento, ícono de tienda y negrita— porque cambia lo que
 * hay que hacer con el pedido: no se despacha. Con envío, la dirección se resuelve con
 * `quotePartyView`, la misma que usa el detalle de la cotización, y se ve igual que allá.
 */
export function OrderShippingCard({
  quotation,
  cityNameById,
}: {
  quotation: Quote
  /** Nombre de la ciudad por id, para una dirección de envío propia. Lo pide la ruta. */
  cityNameById: Record<string, string>
}) {
  const customer = quoteCustomerView(quotation.client)
  // `?? false` para una respuesta cacheada que llega sin el campo, igual que `quotePartyView`.
  const isStorePickup = quotation.isStorePickup ?? false
  const hasOwnShipping = Boolean(findQuoteParty(quotation, 'Shipping'))

  return (
    <Card className="space-y-3 p-4">
      <div className="border-b border-border pb-3">
        <h2 className="flex items-center gap-2 text-[0.7rem] font-semibold tracking-wide text-muted-foreground uppercase">
          <Truck className="size-3.5" aria-hidden="true" />
          Dirección de envío
        </h2>
      </div>

      {isStorePickup ? (
        <div className="flex items-center gap-3 rounded-md bg-accent px-4 py-3 text-accent-foreground">
          <Store className="size-5 shrink-0" aria-hidden="true" />
          <div className="space-y-0.5">
            <strong className="block text-sm font-semibold">
              Recoger en tienda
            </strong>
            <p className="text-xs">
              El cliente pasa a recoger el pedido; no hay envío.
            </p>
          </div>
        </div>
      ) : customer || hasOwnShipping ? (
        <QuotePartySummary
          title="Entregar a"
          party={quotePartyView(
            quotation,
            'Shipping',
            customer ?? NO_CUSTOMER,
            cityNameById,
          )}
        />
      ) : (
        <p className="text-sm text-muted-foreground">
          El cliente de este pedido ya no existe.
        </p>
      )}
    </Card>
  )
}
```

**3b. `order-detail-page.tsx`.** Agrega el import, en orden alfabético con los de `@/features/orders/components/…` (después de `order-items-card`):

```tsx
import { OrderShippingCard } from '@/features/orders/components/order-shipping-card'
```

En `OrderDetailPageProps`, debajo de `advisorLabel: string`, agrega:

```tsx
  /** Nombre de la ciudad por id, para la dirección de envío propia de la cotización: la parte la
   * guarda como id. Lo resuelve la ruta, igual que los permisos. */
  shippingCityNameById: Record<string, string>
```

En la desestructuración de props, agrega `shippingCityNameById,` después de `advisorLabel,`. En el comentario de la función, reemplaza el último párrafo:

```tsx
 * `OrderSummaryCards` y `OrderItemsCard` son las mismas dos piezas que arma "Editar" — mismo
 * diseño y mismo contenido en las dos pantallas, sin sus acciones de mutar nada.
 */
```

por:

```tsx
 * `OrderSummaryCards` y `OrderItemsCard` son las mismas dos piezas que arma "Editar" — mismo
 * diseño y mismo contenido en las dos pantallas, sin sus acciones de mutar nada. Entre las dos va
 * `OrderShippingCard` (spec 2026-10-05), que sólo tiene el detalle: es lo que mira quien despacha.
 */
```

y entre `OrderSummaryCards` y `OrderItemsCard` del JSX:

```tsx
      <OrderShippingCard
        quotation={quotation}
        cityNameById={shippingCityNameById}
      />
```

**3c. `src/routes/_authenticated/orders/$orderId/index.tsx`.** Agrega los imports (con los demás de `@/features/…`):

```tsx
import { useCitiesForDepartments } from '@/features/customers/hooks/use-cities-for-departments'
import { quotePartyDepartmentIds } from '@/features/quotes/utils/quote-parties'
```

Justo después de `const { detail, isLoading, isError, error, refetch } = useOrderDetail(orderId)`, agrega:

```tsx
  // La ciudad de una dirección de envío propia se guarda como id: se piden las ciudades de los
  // departamentos que usan las partes (ninguno, uno o dos) para que la página la muestre por
  // nombre, igual que el detalle de la cotización. Sin partes propias no se pide nada.
  const { cities } = useCitiesForDepartments(
    detail ? quotePartyDepartmentIds(detail.quotation) : [],
  )
  const shippingCityNameById = Object.fromEntries(
    cities.map((city) => [city.id, city.name]),
  )
```

y en el JSX, después de `advisorLabel={detail?.quotation.advisorEmail ?? '—'}`, agrega:

```tsx
      shippingCityNameById={shippingCityNameById}
```

- [ ] **Step 4: Corre las pruebas y verifica que pasan**

```powershell
bun run test --run src/features/orders/pages/order-detail-page.test.tsx 'src/routes/_authenticated/orders/$orderId/index.test.tsx'
bunx prettier --write src/features/orders/components/order-shipping-card.tsx src/features/orders/pages/order-detail-page.tsx src/features/orders/pages/order-detail-page.test.tsx 'src/routes/_authenticated/orders/$orderId/index.tsx' 'src/routes/_authenticated/orders/$orderId/index.test.tsx'
bun run test --run src/features/orders/pages/order-detail-page.test.tsx 'src/routes/_authenticated/orders/$orderId/index.test.tsx'
bun run lint
bunx tsc -b
```

Esperado: las dos clases PASS (antes y después de prettier, que sólo formatea); `lint` sin errores; `tsc -b` sin salida. Si `tsc` marca `shippingCityNameById` faltante en algún otro render de `OrderDetailPage`, ése también lo tiene que pasar: hoy sólo lo renderizan la ruta y su prueba.

- [ ] **Step 5: Commit**

```powershell
$b = git branch --show-current; if ($b -ne "feature/direccion-envio-pedido") { throw "rama inesperada: $b" }
git add src/features/orders/components/order-shipping-card.tsx src/features/orders/pages/order-detail-page.tsx src/features/orders/pages/order-detail-page.test.tsx 'src/routes/_authenticated/orders/$orderId/index.tsx' 'src/routes/_authenticated/orders/$orderId/index.test.tsx'
git commit -m "feat(orders): tarjeta de dirección de envío con recoger en tienda en el detalle del pedido"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando no imprime nada.

---

### Task 4: Verificación completa y despliegue

Cada track corre su mitad cuando terminó sus tareas: 4A después de la Task 2, 4B después de la Task 3. Esta tarea no escribe código; si algo sale rojo, se arregla en la tarea dueña con su propio ciclo RED/GREEN y su commit.

**Files:** ninguno.

**Interfaces:** ninguna.

- [ ] **Step 4A.1: Suite completa del backend**

Con Docker corriendo, desde el worktree del backend:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*Api.dll*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet build Backend.slnx
dotnet test Backend.slnx
```

Esperado: build sin warnings ni errores; en la suite, las únicas rojas son, **por nombre**, `OrderExportApiTests.TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail` y `OrderExportApiTests.TheSeededLayoutProducesTheErpImportSheet` (NIT, previas). Si aparece otra, córrela por nombre en un worktree limpio de `develop` antes de atribuírsela a esta rama.

- [ ] **Step 4A.2: Pasos manuales de despliegue (para el handoff, no se ejecutan acá)**

Copia esto al handoff para el owner, tal cual:

1. Desplegar el backend (`develop` → `main` → imagen) con este cambio.
2. **Después** del despliegue, borrar la fila del tenant en `quotations.orders_export_layouts` y reiniciar el pod: la semilla la recrea con `carrier` en "Transportadora (P2)" y con la hoja `MIGRACION 1`. Si se borra **antes**, la semilla vieja la recrea con la fija `Coordinadora`.
3. Mientras la fila no se borre, ese tenant sigue exportando `Coordinadora` fijo (hallazgo 4): no hay error, sólo el dato viejo.

- [ ] **Step 4B.1: Suite completa y build del frontend**

Desde el worktree del frontend:

```powershell
bun run test --run
bun run lint
bun run format:check
bun run build
```

Esperado: `test` sin fallas; `lint` sin errores; `build` (`tsc -b && vite build`) termina sin error. El frontend no tiene CI de pruebas: si aparece una falla fuera de `src/features/orders/` o `src/routes/_authenticated/orders/`, córrela por nombre en un worktree limpio de `develop` antes de atribuírsela a esta rama. Si `format:check` marca archivos que esta rama **no** tocó, es deuda previa: no se formatean acá.

- [ ] **Step 4B.2: Ver la pantalla**

Con el backend local corriendo (`QEP_API_PROXY_TARGET=http://localhost:5000` en `.env.local`), `bun dev`, y abre el detalle de un pedido cuya cotización tenga "Recoger en tienda": la tarjeta "Dirección de envío" aparece debajo de las tres tarjetas de resumen, con el aviso resaltado y el ícono de tienda, en tema claro y oscuro. Abre otro pedido con envío normal y compara la dirección con la del detalle de su cotización: tiene que leerse igual.
