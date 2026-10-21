# Variantes de producto — prompt para la revisión independiente (Fable)

Pega el bloque de abajo, tal cual, en una sesión nueva de Claude Code con el modelo **Fable**, abierta en
`qep-backend`. Está escrito para esa sesión, que no sabe nada de cómo se construyó la rama.

---

```text
Revisa la implementación del proyecto «variantes de producto» en qep-backend. Es una revisión independiente: otra
sesión lo construyó con subagentes (un implementador y un revisor por tarea, más una revisión final) y quiero una
segunda opinión de alguien que no participó. Escribe en español colombiano, tuteando.

## Antes de empezar
1. Lee CLAUDE.md completo. Sus reglas duras aplican (secretos, no mergear a main, commits sin atribución de IA,
   PowerShell para todo comando que me entregues).
2. Comprueba dónde estás: `git branch --show-current` y `git log --oneline -1 develop`. Otras sesiones comparten este
   checkout y pueden cambiarle la rama: si vas a tocar algo, hazlo en un worktree nuevo desde develop. Para revisar
   basta con leer.
3. La implementación está en develop local (sin push): va desde `95dbdd0c` hasta el commit
   «docs: estado de variantes y prompt de revisión independiente» (la rama `feature/variantes-producto` se mergeó con
   ff-only; búscalo con `git log --oneline --grep "prompt de revisión independiente" develop`). `2ee12ca9` es un merge de develop hacia la rama: su contenido
   de develop no es parte de la revisión; mira su resolución con `git show --remerge-diff 2ee12ca9`.

## Documentos (léelos en este orden)
1. docs/superpowers/specs/2026-10-20-variantes-de-producto-design.md — el spec aprobado. Es la autoridad.
2. docs/superpowers/plans/2026-10-20-variantes-de-producto-backend.md — el plan (15 tareas).
3. docs/superpowers/plans/2026-10-20-variantes-estado.md — estado y decisiones del owner.
4. docs/superpowers/plans/2026-10-20-variantes-handoff.md — el contrato que se le entrega al frontend.
5. docs/superpowers/plans/2026-10-20-variantes-revision-fable.md, sección «Decisiones tomadas sin el owner» — cada una
   con lo que cuesta si es incorrecta. Revísalas una por una.

## Lo que debes saber
- Las pruebas de las tareas 4 a 15 se ESCRIBIERON pero NUNCA se EJECUTARON (pedido explícito del owner para no cargar
  el equipo). Sólo se corrió `dotnet build Backend.slnx` y `dotnet format`. Las de las tareas 1 a 3 sí pasaron.
  Una revisión anterior comprobó con `ToQueryString` que 18 consultas LINQ de riesgo traducen a SQL.
- No corras la suite completa. Las pruebas de integración levantan contenedores de Postgres (Testcontainers) y el
  equipo se pone lento: pregúntame antes de correr cualquier proyecto de IntegrationTests, y córrelos de a uno.
  Las UnitTests sí puedes correrlas por proyecto sin preguntar.
- La variante por defecto de cada producto migrado lleva el mismo id que el producto; los rellenos de `variant_id` en
  Quotations y Pos dependen de eso.

## Qué revisar (prioridad)
1. Correctitud que una ejecución destaparía y la lectura puede no ver: migraciones (AddProductVariants,
   AddOptionLibraryProvisions, AddVariantToLines, AddVariantToSaleLines) y sus Down; SQL crudo
   (CatalogVariantUsageChecker, ExportLoadSeeder, ops/export-load-cleanup.sql); pruebas que afirman algo distinto de
   lo que el código hace; pruebas que no pueden fallar.
2. Que el spec se cumpla, incluidos los anti-objetivos del §1: un producto simple no se vuelve más difícil de crear;
   ninguna línea histórica cambia de precio, código ni nombre; el escáner no deja de encontrar lo que hoy encuentra.
3. Los cinco «Enfoque de revisión» del plan: que su prueba exista y pruebe de verdad el caso.
4. Aislamiento de tenant (403, nunca 404, entre tenants; todo método de repositorio con tenantId), capas
   (ArchitectureTests; Catalog no referencia Quotations ni Pos) y traducción de errores de base por nombre de índice.
5. Que el traspaso al frontend coincida con el código: rutas, nombres JSON, códigos de error y status, claves del mapa
   errors por ruta.
6. Las decisiones tomadas sin el owner: di cuáles cambiarías y por qué.

## Entregable
Un informe con: veredicto (¿se puede llevar a main después de correr las pruebas, sí o no?), hallazgos Críticos,
Importantes y Menores con archivo:línea, cómo reproducirlos y cómo arreglarlos, y tu opinión sobre cada decisión
tomada sin el owner. No corrijas nada sin preguntarme. Si al final te pido arreglos, se hacen en un worktree con una
rama desde develop, se mergean a develop con ff-only y no se hace push.
```

---

## Decisiones tomadas sin el owner

Las tomó la sesión que implementó (controlador y subagentes) cuando el plan o el spec no lo decían, o se contradecían.
Cada una dice lo que cuesta si es incorrecta.

### Proceso
- **T9–T12 en un solo commit** (`34944393`): se trabajó con commits temporales y se juntaron al final. Costo: el
  historial no separa adaptadores, cotizaciones, Shopify y POS.
- **Pruebas sin ejecutar desde la T4**, por pedido del owner. Costo: los fallos aparecen en la primera corrida.
- **Integrar develop en la rama antes del ff-only** (`2ee12ca9`): develop había avanzado y dos de sus pruebas usaban
  las firmas viejas. Costo: un commit de merge en la historia.

### Catalog
- **Códigos de error extra** (T1, T2, T4, T5): `catalog.option.name_too_long`, `value_too_long`, `value_not_found`,
  `name_taken`, `not_found`, `in_use`, `inactive`, `value_inactive`; `catalog.variant.barcode_too_long`,
  `image_not_in_gallery`, `required`, `not_found`, `duplicated_in_request`, `active_required`;
  `catalog.product.option_duplicate`, `gallery_duplicate`. Costo: más códigos que el frontend tiene que conocer.
- **Combinación única sólo entre variantes activas** (T2, sigue el spec §3.3.1); sin índice único en la base, la
  unicidad la impone el agregado bajo `Version` (T3). Costo: una carrera extrema la resuelve la versión, no la base.
- **`ProductPriceChange.VariantId` NOT NULL desde la T3**, antes de la T7. Costo: ninguno visible.
- **Ruta de la biblioteca de opciones** `/api/v1/tenants/{tenantId}/catalog/options`; el plan decía
  `/api/tenants/{id}/options`, que no sigue el prefijo real. Costo: ninguno; el frontend usa la del traspaso.
- **Opción en uso**: `DELETE` da 409 `catalog.option.in_use` y se desactiva por el `isActive` del `PUT`; no hay
  endpoint aparte. La siembra de «Talla» y «Color» se marca en la tabla nueva `option_library_provisions`. Costo: si
  la marca falla, la siembra reaparece.
- **Concurrencia con 412 y `expectedVersion`** (el plan decía 409): es lo que usa el repo en todo `PUT` con versión.
- **Borrar una variante es explícito** con `deletedVariantIds` en el `PUT`; omitirla da 422
  `catalog.variant.missing_in_request`. El plan T6 decía que omitirla la borraba; ganó el spec §6, porque un formulario
  que pagina hasta 2.048 filas borraría en silencio. Costo: el frontend manda una lista más.
- **Índices de `variant_id` en las líneas** (`IX_quotation_items_variant`, `IX_order_items_variant`,
  `IX_sale_lines_variant`): el chequeo de uso corre en cada GET o PUT de producto. Costo: ninguno.
- **Carrera de borrado aceptada**: con READ COMMITTED, una línea que se guarda justo mientras Catalog borra la
  variante puede quedar con `variant_id` huérfano. Cerrarla exige `FOR UPDATE` en el PUT y `FOR KEY SHARE` en quien
  escribe, cruzando módulos. Costo: una línea huérfana en una carrera rara.
- **Excel de productos**: exporta las variantes inactivas; la columna «Variante» va vacía en la variante por defecto;
  los encabezados llevan tilde y «Nombre» pasó a «Producto»; el tope de 20.000 cuenta filas (variantes). Costo: cambia
  el archivo para quien lo lea con macros.
- **Selector de productos** (T14b, tarea agregada): el spec §6 lo pedía y el plan no lo asignaba.
  `GET /catalog/products?includeVariants=true` agrega las variantes vendibles, con página de 50 como máximo (200 sin
  ellas). Costo: el frontend de develop todavía lee `code` y `priceScales` del producto, que ya no existen.
- **La búsqueda del listado también encuentra por código de barras**, en todos sus usos (POS, catálogo, selector).

### Cotizaciones, pedidos, Shopify y POS
- **Nombre visible de más de 200 caracteres**: se recorta a 200 con «…» (regla compartida en
  `BuildingBlocks.Domain/SnapshotText`); no se ensanchó la columna. Costo: un nombre largo sale cortado en el documento
  enviado; el borrador lo muestra completo.
- **Shopify**: una línea sin variante emparejada deja el pedido en espera con `product_unmatched`, como hoy. El plan
  decía que entraba sin producto, pero describía mal el comportamiento actual.
- **Escáner del POS**: el código de barras gana aunque su variante esté inactiva. Costo: si el tenant le pone a A un
  código de barras igual al `Code` de B y desactiva A, la caja devuelve A, marcada como inactiva.
- **Contrato del POS sin compatibilidad**: `variantId` en ventas y vista previa, `GET /pos/products` con variantes,
  `by-code` admite hasta 64 caracteres (antes 60). El carrito del frontend agrupa por `variantId`.
- **Reporte de cambios de precio**: el desglose por variante trae todas las variantes del producto, también en cero;
  `ProductCode` es el código de la variante y es `null` en el ranking cuando el producto tiene varias.
- **Excel de pedidos**: la columna «Variante» nace oculta y, en la variante por defecto, lleva el nombre del producto,
  porque la línea no puede distinguirla sin consultar Catalog. Costo: no coincide con el Excel de productos.
- **Siembra de carga**: `ExportLoadSeeder` no inserta en `product_variants`, porque el catálogo lo crea el dominio con
  su variante por defecto.

### Diferido a propósito
- `catalog.product.code_taken` sale sin campo marcado en un caso de borde: una variante pedida en `deletedVariantIds`
  sigue en uso y una nueva reusa su código.
- Borrar una variante borra su historial de precios (CASCADE). El `Down` de Catalog no lo borra: deja las filas sin
  variante.
- `AddProductVariants.Down` puede devolver como variante única una que esté inactiva.
