# Plan maestro — `qep-backend`

Ledger de continuidad **de este repositorio**. Una sesión de backend empieza aquí, no en el
historial de chat.

> **Última actualización:** 2026-08-15
> **Alcance:** sólo slices que se ejecutan en `qep-backend`. El frontend lleva el suyo en
> `qep-frontend/sdd/02-plan/plan-maestro.md`. Ninguna fila aparece en los dos
> (`SDD-ADR-08`).

## Retomar el trabajo

1. Leer `qep-backend/sdd/README.md` y `qep-frontend/sdd/AGENTS.md`.
2. Revisar **Estado global** y el **Ledger de slices** de abajo.
3. Verificar `git status` en **este** repo. La carpeta que contiene los dos checkouts **no es
   un repositorio** desde el 2026-08-09: `git rev-parse` ahí falla, y eso es lo correcto.
4. Buscar el primer slice no `Complete` cuyas dependencias estén `Complete`, confirmar que
   sus bloqueos están cerrados, marcarlo `In Progress` y ejecutar el ciclo de
   `qep-frontend/sdd/00-metodo/ciclo-de-trabajo.md`.
5. **Un slice a la vez en este repositorio.** El frontend corre en paralelo con el suyo.
6. Cerrar con una entrada de **Handoff**, incluso si quedó bloqueado o sin commit.

## Estado global

| Campo | Valor |
| --- | --- |
| Fase activa | Fase 2 — módulos de producto. `catalog` es el primero con gate cerrado |
| Módulo activo | `catalog` — `En curso`, gate `CAT-00` cerrado el 2026-08-10 |
| Slice activo | **Ninguno.** `CAT-04` cerró el 2026-08-15 y no se abrió nada nuevo: **un slice a la vez en este repositorio**. El candidato es `CAT-05`, descrito en «Próxima acción ejecutable» |
| Último slice cerrado | **`CAT-04` — propiedades nuevas de producto**, `Complete` el 2026-08-15, abierto el 2026-08-13. `descripción`, `imagen`, `precio`, `moneda` y FK a `TaxRate`. **Commiteado el 2026-08-15 en `85b87c8`.** Runtime **11 de 11** y revisión con **4 lentes ciegos**, con el lente de **riesgo limpio**. **Los 5 hallazgos propios cerrados el 2026-08-15 en `a9575a5`**, con RED y GREEN literales en el tramo 6 del spec. **`Complete` el 2026-08-15:** el gate `CAT-00` se corrigió en `qep-frontend` (`38e5abe`) — declaraba `Product` con "Ningún campo más" y ahora lista los cinco campos con su origen. El gate **no se reabrió** (`SDD-ADR-01`) |
| Slice anterior | **`CAT-03` — API de tasas de impuesto**, `In Progress` desde el 2026-08-12. Spec en [`03-modulos/catalog/slices/CAT-03-api-de-tasas-de-impuesto.md`](../03-modulos/catalog/slices/CAT-03-api-de-tasas-de-impuesto.md), con partición `CAT-03a`/`CAT-03b` **declarada antes de escribir código** —al revés que `CAT-02`, que se midió en 1043 líneas recién al querer commitear— y ejecutada en secuencia el 2026-08-13. **Código completo y probado**: dominio, persistencia, migración `AddTaxRates`, permisos con sus dos mitades y los 5 endpoints. Unitarias `31/31`, arquitectura `16/16`, **integración `37/37`**, regresión de toda la solución con **233 en verde** y sólo los 5 fallos de `SDD-CT-14` verificados por nombre. **runtime 11 de 11** con la auditoría probada en base y la atomicidad por lo que **no** escribió. Revisión hecha: un bloqueante propio —`Version` sin prueba que lo ejercitara— corregido y verificado **saboteando el mecanismo**. **No cierra por decisiones, no por técnica:** faltan `DECISIÓN-PENDIENTE-CAT-05` y los hallazgos `B` y `C`, todos de producto. Deuda de método declarada: la revisión fue **autorrevisión**, no cuatro lentes ciegos |
| Último slice completado | **`CAT-02` (`a` y `b`), el 2026-08-11** — el primero cerrado en este ledger. La historia previa de backend —`AUTH-04`, `AUTH-05`, `AUTH-11`— vive en el ledger del frontend: eran slices de dos repos con un solo spec, y `SDD-ADR-08` decidió **no partirlos retroactivamente** porque están `Complete` y renumerar borra trazabilidad |
| Último commit verificado | Sesión del 2026-08-11, en tres: `ec5540e` (`fix(config)`: quitar la cadena de conexión de `appsettings.json`), `55f36e6` (`docs(readme)`) y el que cierra esta entrada del ledger. Antes: `ccd2eca` (`chore(i18n)` — **contiene además un cambio funcional en `Program.cs` que su mensaje no declara**; ver Handoff), `797c099` (`docs(CAT-02b)`), `3c2c9ec` (`feat(CAT-02b)`), `968c4a8` (`feat(CAT-02)`), `594ee11` (`docs(SDD-ADR-08,CAT-02)`). Rama **`feature/catalog-api`**, sin publicar; se creó rama en vez de commitear sobre `main`, que es la rama por defecto de este repo. Se suma **`84ebc5c`** (`fix(config)`: credenciales de k8s a un Secret propio), del handoff del 2026-08-11. Incluye `CLAUDE.md`, que **nunca estuvo versionado**: entra acá por decisión explícita del owner, y con eso queda cerrada la decisión que este ledger venía registrando como no tomada |
| Decisiones abiertas | **`DECISIÓN-PENDIENTE-INFRA-01` cerrada el 2026-08-11 por el owner: infraestructura y despliegue quedan explícitamente fuera del alcance del método.** No se abre módulo de plataforma ni se reserva prefijo. El corte es **por efecto, no por carpeta** —ver «Alcance del método» abajo—, y la obligación que **no** desaparece es la entrada de handoff en este ledger. Pendiente que deja: es una decisión estructural, así que `convenciones-de-id.md` pide que produzca un `SDD-ADR-*`. **`SDD-ADR-09` cerrado el 2026-08-11 en `qep-frontend`, commit `35e6f93` sobre `develop`.** Revisado contra sus fuentes antes de entrar: los cuatro commits citados, los cuatro archivos, el conteo de prefijos y la lista blanca de manifiestos de la plantilla de deploy. El mismo commit agrega al índice la fila de `SDD-ADR-08`, que estaba en el cuerpo del documento desde `3aca4a9` y no en la tabla. **`DECISIÓN-PENDIENTE-CAT-04` cerrada el 2026-08-10 por el owner:** el `code` de producto es único por tenant, con `IX_products_tenant_code` y traducción a `422 catalog.product.code_taken` en Infrastructure. **Tres decisiones del owner del 2026-08-12, sobre el modelo de `Product`:** `descripción`, `imagen`, `precio` y `moneda` entran a `catalog` (`CAT-04`); la **escala de precios** queda en `pricing`, como el gate ya declaraba; y **`stock` queda fuera del alcance del proyecto** — no tenía `RF` que lo sustentara ni módulo en el mapa, y como campo suelto era el candidato a corromperse por escrituras concurrentes. **Las tres corren el alcance del gate `CAT-00`**, que cerró con "Ningún campo más", así que hay que escribirlas ahí. **Dos abiertas nuevas: `DECISIÓN-PENDIENTE-CAT-05`** —¿el `name` de una tasa es único por tenant? Recomendado que sí; bloquea la migración `AddTaxRates`— y **`DECISIÓN-PENDIENTE-CAT-06`** —cuando exista `pricing`, ¿gana la lista o `Product.Price`? Default asumido y declarado: gana `pricing`, y `Product.Price` es precio base de fallback; bloquea `CAT-04`, no `CAT-03` |
| Contradicciones abiertas | `SDD-CT-14` — parcialmente cerrada: siguen fallando 5 pruebas de `RealAuthenticationApiTests` con `Expected: Created / Actual: Unauthorized`. `SDD-CT-07` — un registro de tenant fallido deja un usuario huérfano en `identity.users`; no bloquea, pide slice de mantenimiento. `SDD-CT-08` — `500` intermitente en `POST /auth/register-tenant`, no reproducida. Las tres se registran en el ledger del frontend, que sigue siendo el registro de contradicciones del producto |

### Próxima acción ejecutable

**`CAT-04` está `Complete`. No hay slice activo en este repositorio.**

**El candidato es `CAT-05` — el pegamento entre `Storage` y `Product.ImageFileId`.** El owner
pidió el 2026-08-15 *"API para subir imágenes y asignarlas a productos"*, y contrastado contra el
código la capacidad **ya existe en sus dos mitades**: el flujo de sesión de carga de `Storage`
(`POST /files` → URL prefirmada, `POST /files/{id}/complete`, variantes con ImageSharp,
publicación) y `Product.ImageFileId`, que `CAT-04` expone en el `POST`, el `PUT` y el `GET` de
productos. Lo que **no** existe es el pegamento:

| Hueco | Qué pasa hoy | Gravedad |
| --- | --- | --- |
| **`ImageFileId` entra sin ninguna validación** | Se guarda sin verificar que el archivo exista, esté `Available`, sea imagen ni **que sea del mismo tenant**. Un producto del tenant A puede apuntar al archivo del tenant B y la respuesta es un `201` normal | **Es la misma fuga que `ProductTaxRateResolver` cierra para la tasa**, con su `CA-CAT-04-07` y su revisión de riesgo. Para la imagen no hay nada |
| **`FileOwnerType` no tiene `Product`** | Sólo `User`, `Entity`, `System`. Y `StorageEndpoints.cs:84-86` hace **fallback silencioso a `User`** cuando el string no parsea: mandar `"Product"` guarda `User` y devuelve `201` | Un `OwnerType` inválido no falla, se convierte en otro |
| **El producto no expone URL de imagen** | Sólo el `Guid`. Pintar una grilla de 20 productos son 20 llamadas a `POST /files/{id}/download-url` | N+1 contra el frontend |

El primero es de riesgo y **debería llevar revisión de lentes ciegos**, por el mismo argumento
que la llevó `CAT-04`: es frontera de aislamiento entre tenants, que es donde una autorrevisión
menos vale.

**Y hay trabajo de frontend que este ledger no lleva.** Los cinco campos de `CAT-04` no están en
`qep-frontend`: `types/product.ts` y `catalog.api.ts` siguen con `id`, `name`, `code`, `isActive`,
y `catalog.api.ts` además apunta a `/api/v1/catalog/*` **sin tenant**, que el gate `CAT-00` ya
declaraba como deuda de `CAT-01`. Eso es fila del **ledger del frontend**, no de éste
(`SDD-ADR-08`): una fila de slice vive en exactamente un ledger.

**Dos seguimientos que no pertenecen a ningún slice de `catalog`:**

- **`NU1903`** — `dotnet restore` falla con `"SSH.NET" 2025.1.0 tiene una vulnerabilidad de
  gravedad alta`, tratada como error. Entra por `Testcontainers.PostgreSql` y **frena todos los
  proyectos de integración**. En local se esquiva con `-p:NuGetAudit=false`, que es lo que se
  usó el 2026-08-15 **sin tocar configuración del repo**. Va a frenar CI.
- **Hallazgo `C` de la revisión de `CAT-04`** — `ApiExceptionHandler` devuelve
  `exception.Message` sin distinguir entorno, así que un error no traducido filtra nombres de
  constraint y de tabla al llamador, también en producción. Es de `src/Api` y afecta a todos los
  módulos.

Para levantar la API local: **arrancar el contenedor `postgres18`** (`docker start postgres18`),
no `docker compose up` — ver el handoff del runtime de `CAT-03`.

---

**`CAT-03` está terminado y `Complete`.**
Unitarias `31/31`, arquitectura `16/16`, integración `37/37`, regresión con 233 en verde y sólo
los 5 fallos de `SDD-CT-14` por nombre, **runtime 11 de 11** con la auditoría probada en base, y
revisión hecha con su bloqueante corregido.

Tres decisiones antes de poder marcarlo `Complete`:

1. **`DECISIÓN-PENDIENTE-CAT-05`** — ¿el `name` de una tasa es único por tenant? Se implementó con
   el default asumido. **Hay 4 tasas de prueba cargadas en `dev_lulo_crm_v2`, pero ningún dato
   real: revertirla todavía cuesta una migración vacía.**
2. **Hallazgo `B`** — ¿desactivar una tasa libera su nombre? Hoy no: el índice único no filtra por
   `is_active`, así que recrearla devuelve `422 name_taken` con la lista de activas vacía.
3. **Hallazgo `C`** — ¿`GET T/tax-rates` debe filtrar las inactivas? Hoy las devuelve, y quien
   arme una cotización puede elegir una tasa desactivada.

Y una deuda de método que no bloquea pero está declarada: la revisión del tramo 5 fue
**autorrevisión**, no cuatro lentes ciegos entre sí.

Para levantar la API con el stub —cuesta una vuelta descubrirlo—:
`dotnet run --project src/Api --no-launch-profile`, con `ASPNETCORE_ENVIRONMENT`,
`ASPNETCORE_URLS` y `Authentication__UseDevelopmentStub=true` en el entorno. Sin
`--no-launch-profile` los dos perfiles fijan el stub en `false` y todo devuelve `401`.

**Y queda una ratificación pendiente que hoy es barata y mañana no:**
`DECISIÓN-PENDIENTE-CAT-05` se implementó con el **default asumido** —`name` único por tenant—
porque se preguntó dos veces sin respuesta. **Mientras no haya datos cargados, revertirla cuesta
una migración vacía.** Después de la primera carga real, es migración sobre datos sucios.

Antes de arrancar, tres seguimientos abiertos por la revisión de `CAT-02` (tramo 6 del spec):
el mensaje veneno de `AuditProjectionWorker`, que merece `SDD-CT` y no lo introdujo `CAT-02`;
el `ETag`/`If-Match` de productos, que es trabajo cruzado con `CAT-01`; y los **22 archivos**
donde `dotnet format` falla en todo el repositorio. Ninguno bloquea `CAT-03`.

#### Historia de `CAT-02`, cerrado el 2026-08-11

Orden de trabajo que se ejecutó, con RED antes que GREEN en cada tramo:

1. ~~**Andamiaje**: cuatro `.csproj` de `Modules.Catalog.*` en `Backend.slnx`, más
   `CatalogLayerTests.cs`.~~ **Hecho el 2026-08-10.** RED literal `error CS0234: ... 'Catalog'
   no existe en el espacio de nombres 'Modules'`; GREEN `Superado: 16, Total: 16` (eran 12);
   `dotnet build Backend.slnx` → `Compilación correcta. 0 Advertencia(s), 0 Errores`.
   Evidencia completa en el spec. Commiteado en `968c4a8`.
2. ~~**Dominio**: `Product` con sus invariantes, en `Modules.Catalog.UnitTests`.~~ **Hecho el
   2026-08-10.** RED literal `error CS0103: El nombre 'ProductId' no existe en el contexto
   actual` y `CS0246` sobre `CatalogDomainException`; GREEN `Superado: 13, Total: 13`; build
   limpio y `ArchitectureTests` sin regresión en `16/16`. Ocho invariantes con prueba, cada
   una con su código de dominio. Commiteado en `968c4a8`.
3. ~~**Persistencia y migración**: `CatalogDbContext`, `InitialCatalog` con
   `IX_products_tenant_code`.~~ **Hecho el 2026-08-10**, junto con el primer endpoint
   (`GET /products`), porque una prueba de integración sin ruta que ejercer no verifica nada.
   GREEN `Superado: 3, Total: 3` contra PostgreSQL real. Sin regresión: `Tenancy` sigue con
   los mismos 5 fallos de `SDD-CT-14`, verificados por nombre. Commiteado en `968c4a8`.
4. ~~**Casos de uso y endpoints**, uno a uno, con integración contra PostgreSQL real.~~
   **Hecho el 2026-08-10** como `CAT-02b`. RED de runtime `Expected: Created / Actual:
   MethodNotAllowed`; GREEN `Superado: 15, Total: 15`. **Los 12 criterios de aceptación
   cubiertos por prueba.** Sin regresión.

5. ~~**Runtime contra la API local.**~~ **Hecho el 2026-08-11**, los 12 criterios endpoint por
   endpoint, con la auditoría verificada en base y no por status HTTP. Tramo 5 del spec.
6. ~~**Revisión adversarial de 4 lentes y su transacción de corrección.**~~ **Hecha el
   2026-08-11.** Seis hallazgos, cuatro corregidos: token de concurrencia —al que llegaron dos
   lentes por separado—, permisos de `tax_rate` retirados, comodines de `LIKE` escapados y
   autorización antes que validación. Regresión de **203 pruebas** con los 5 fallos de
   `SDD-CT-14` verificados por nombre. Tramo 6 del spec.

**`CAT-02` cumple el DoD y está `Complete`.** Los ítems de UI van `N/A` con razón: es un slice
de backend, y su interfaz es `CAT-01`, en el otro repositorio.

**Antes de cualquier `dotnet build`, `dotnet test` o comando `ef`: verificar que `Api.exe` no
esté corriendo.** Si lo está, los tres fallan por archivo bloqueado. El nombre del proceso es
`Api.exe` porque `src/Api/Api.csproj` no declara `AssemblyName`.

**La factory de integración debe fijar `Notifications:EmailProvider`** (`SDD-CT-17`): sin eso
las pruebas mueren al arrancar con `OptionsValidationException`, heredando el `infobip` con
credenciales vacías del `appsettings.json`.

## Alcance del método

Cerrado por el owner el 2026-08-11 (`DECISIÓN-PENDIENTE-INFRA-01`). **Infraestructura y
despliegue quedan fuera del alcance del método.** No requieren slice, spec, gate ni prefijo.

**El corte es por efecto, no por carpeta.** Definirlo por ruta sería un agujero: un cambio en
`src/Api/appsettings.json` puede tumbar el arranque, y una migración vive en `src/` pero se
parece a infraestructura. La pregunta que decide es una sola: **¿cambia el comportamiento
observable de la API?**

| Fuera del método — sin slice | Dentro — exige slice con ID |
| --- | --- |
| Manifiestos de `k8s/`, `azure-pipelines.yml`, `Dockerfile`, `compose.yaml` | Contratos HTTP: rutas, cuerpos, códigos de estado, códigos de error |
| Dónde vive un valor de configuración: `appsettings.json` ↔ ConfigMap ↔ Secret ↔ user-secrets | Dominio, invariantes, permisos, políticas de autorización |
| Secretos, observabilidad de plataforma, recursos y probes del pod | Migraciones de base y forma del esquema |
| Comentarios, README, y este ledger | Cualquier cambio de configuración que **altere** comportamiento, no sólo lo mueva |

La última fila es la que hay que mirar con cuidado. Mover `ConnectionStrings__QepDatabase` del
ConfigMap a un Secret está fuera. Cambiar `Registration__PublicTenantSignupEnabled` de `false`
a `true` **no**: eso es una decisión de producto que se ejecuta por configuración.

**Lo que la exención NO exime:** la entrada de handoff en este ledger, con evidencia literal y
comandos con su resultado real. Sigue siendo obligatoria. El precedente es la sesión del
2026-08-11: sin ese handoff se habría perdido que la plantilla de deploy aplica una lista
blanca y no el directorio, que es lo que evitó una caída de producción.

## Ledger de slices — Módulo `catalog`

Ficha y gate: `qep-frontend/sdd/03-modulos/catalog/` · Estado del módulo: `En curso` ·
Owner: Andres Jaramillo

| ID | Resultado revisable | Depende de | Estado | Evidencia / commit / PR |
| --- | --- | --- | --- | --- |
| `CAT-02` | **API de productos** — fila padre, ya no es unidad ejecutable | `CAT-00` (gate cerrado) | Partido el 2026-08-10 | Se partió al medir **1043 líneas autoradas** con un endpoint de cinco, contra el umbral de ~400 de `convenciones-de-id.md`. No se renumera nada y los commits que citan `feat(CAT-02)` siguen válidos: el ID padre no se toca. Spec único en [`03-modulos/catalog/slices/CAT-02-api-de-productos.md`](../03-modulos/catalog/slices/CAT-02-api-de-productos.md) |
| `CAT-02a` | Andamiaje del módulo, dominio `Product`, persistencia con `InitialCatalog` y `GET /products` | `CAT-00` | **Complete** | Tres tramos con RED y GREEN literales en el spec. Commits `968c4a8` y `594ee11`. Runtime en el tramo 5 y revisión de 4 lentes en el tramo 6, ambos el 2026-08-11 |
| `CAT-02b` | Escrituras: `GET` por id, `POST`, `PUT`, `deactivate`, validadores, traducción del índice único y pruebas de auditoría y outbox | `CAT-02a` | **Complete** | Código en `3c2c9ec`. Cubre `CA-CAT-02-03` a `-09`, `-11`, `-12` y las mitades pendientes de `-01` y `-10`. **Runtime del 2026-08-11: los 12 criterios verificados contra la API local**, con `422 catalog.product.code_taken` confirmado en vivo —el `500` de `SDD-CT-06` que este slice existía para cerrar— y la atomicidad del outbox probada por lo que **no** dejó rastro: el `403` y los tres `422` no escribieron fila. **Revisión de 4 lentes y su corrección** en el tramo 6: token de concurrencia, permisos de `tax_rate` retirados, comodines de `LIKE` escapados y autorización antes que validación. Regresión de 203 pruebas, con los 5 fallos de `SDD-CT-14` verificados **por nombre** |
| `CAT-03` | API de tasas de impuesto | `CAT-02` | **Complete** | Cerrado el 2026-08-13. Spec con evidencia en [`CAT-03-api-de-tasas-de-impuesto.md`](../03-modulos/catalog/slices/CAT-03-api-de-tasas-de-impuesto.md): 11 criterios, todos con prueba. Unitarias `31/31`, arquitectura `16/16`, integración `37/37`, regresión con 233 en verde y los 5 de `SDD-CT-14` por nombre, **runtime 11 de 11** con la auditoría verificada en base. Partición `CAT-03a`/`CAT-03b` declarada **antes** de escribir código. Devolvió `catalog.tax_rate.read`/`.manage` con sus tres mitades —constante, definición y **`AddPolicy`**—, que la revisión de `CAT-02` había retirado. `DECISIÓN-PENDIENTE-CAT-05` y los hallazgos `B` y `C` cerrados por el owner el 2026-08-13, los tres ratificando lo implementado. **Deuda declarada:** la revisión fue autorrevisión, no 4 lentes ciegos |
| `CAT-04` | **`Product` enriquecido:** `descripción`, `imagen`, `precio`, `moneda` y FK a `TaxRate` | `CAT-03` | **Complete** | Abierto el 2026-08-13, spec en [`CAT-04-propiedades-de-producto.md`](../03-modulos/catalog/slices/CAT-04-propiedades-de-producto.md) con 11 criterios. Commiteado el 2026-08-15 en `85b87c8`. `ProductDetails` como value object, migración `AddProductDetails` con 5 columnas nullable y FK `RESTRICT`. Unitarias `44/44`, integración `50/50`, arquitectura `16/16`, regresión sin cambios con los 5 de `SDD-CT-14` por nombre. **Runtime 11 de 11**, con la auditoría probada por lo que **no** escribió y `CA-CAT-04-11` verificado sobre datos reales del 2026-08-11. **`CA-CAT-04-07` verificado contra el mecanismo ausente** y confirmado en runtime: no persiste. **Revisión con 4 lentes ciegos —salda la deuda de método de `CAT-03`— con el lente de riesgo limpio.** **Los 5 hallazgos propios cerrados el 2026-08-15 en `a9575a5`** (tramo 6): `A` y `F` con reglas de validador que faltaban, `D` con `ProductWriteRules` incluido por los dos validadores, `E` con `ProductDetails` no posicional, y `B` **decidido: se traduce** la violación de FK. Integración `55/55`, regresión con **265 en verde** y los 5 de `SDD-CT-14` por nombre. **Cerrado el 2026-08-15:** el gate `CAT-00` se corrigió en `qep-frontend` (`38e5abe`) y con eso el slice cumple el DoD. `C` es de `src/Api` y no pertenece a este slice. `stock` **fuera del alcance del proyecto**; `escala de precios` es de `pricing` |

## Handoff

### 2026-08-15 (cont.) — `CAT-04` `Complete`: se corrigió el gate `CAT-00`

**El último bloqueo de `CAT-04` no era técnico: era el gate**, que declaraba el modelo de
`Product` con **"Ningún campo más"** mientras el código tenía cinco campos más. `SDD-ADR-01`
manda que gane el código y se corrija el documento, y eso se hizo: `qep-frontend`, commit
`38e5abe`.

**El gate no se reabrió.** Su cierre del 2026-08-10 sigue válido; lo que cambió es el alcance que
declara. Ahora lista los cinco campos con su origen y su razón —incluida la referencia blanda a
`Storage` sin FK y la moneda por producto, que se implementó **contra la recomendación del
spec**— y escribe los dos límites que estaban decididos desde el 2026-08-12 y sin documentar:
`stock` **fuera del alcance del proyecto** y la escala de precios como propiedad de `pricing`.

**Dos cuidados al escribir en el otro repositorio, y los dos importan:**

1. **Se verificó la rama antes de leer y de escribir:** `git -C ../qep-frontend branch
   --show-current` → `develop`. Es la lección del 2026-08-11, cuando se leyó ese repo estando en
   `feature/catalog` —nueve commits atrás— y se reportaron como faltantes un ADR y una sección de
   `AGENTS.md` que sí existían.
2. **El commit se acotó al archivo del gate**, con `git commit -- <path>`. Ese repositorio tenía
   seis archivos de su developer **staged y sin commitear** (`use-active-tenant`,
   `_authenticated.tsx`, `.oxlintrc.json`, `bun.lock` y su propio ledger): un `git add -A` los
   habría arrastrado a un commit ajeno con un mensaje que no los describe. Se verificó después
   que el índice quedó intacto.

También se corrigió la fila de `catalog` en `01-contexto/registro-de-modulos.md`, que seguía
diciendo "el slice de backend todavía no tiene spec" cuando ya hay tres `Complete`. El **estado**
del módulo no cambió: sigue `En curso`, porque el frontend no está.

**`CAT-04` cumple el DoD y pasa a `Complete`.** No queda slice activo en este repositorio.

**Lo que este cierre deja sobre la mesa, en orden de riesgo:**

1. **`ImageFileId` entra sin ninguna validación** — ni existencia, ni estado, ni tipo, ni
   **tenant**. Es la misma fuga que `ProductTaxRateResolver` cierra para la tasa; para la imagen
   no hay nada. Candidato a `CAT-05`, con revisión de lentes ciegos por ser frontera de
   aislamiento.
2. **Los cinco campos no están en el frontend**, y `catalog.api.ts` sigue apuntando a
   `/api/v1/catalog/*` sin tenant. Es fila del **ledger del frontend** (`CAT-01`), no de éste.
3. `NU1903` sobre `SSH.NET` y el hallazgo `C` de `ApiExceptionHandler`, ninguno de `catalog`.

### 2026-08-15 (cont.) — Los 5 hallazgos propios de `CAT-04`, cerrados

**Punto de partida:** el owner pidió *"API para subir imágenes y asignarlas a productos"*. Antes
de escribir nada se contrastó contra el código, y la capacidad **ya existía en sus dos mitades**:
el flujo de sesión de carga de `Storage` y `Product.ImageFileId`, que `CAT-04` había agregado. Lo
que falta es el pegamento, y quedó anotado arriba como candidato a `CAT-05`. No se abrió: **un
slice a la vez**, y `CAT-04` estaba abierto. El owner mandó cerrarlo primero.

**Lo que se hizo:** una transacción de corrección, tramo 6 del spec, commit `a9575a5`.

| Hallazgo | Qué era | Cómo se cerró |
| --- | --- | --- |
| `A` | El `422` de precio-sin-moneda salía con código de dominio y **sin el mapa `errors`**: ningún validador lo atajaba | Regla en los dos sentidos, cada uno apuntando al campo que hay que corregir |
| `F` | La regla de `Currency` comprobaba sólo el largo; el dominio además exige letras, así que `"123"` la atravesaba | `Matches("^[A-Za-z]{3}$")` además de `Length(3)` |
| `D` | Reglas duplicadas textualmente entre los dos validadores: corregir una dejaba `POST` y `PUT` validando distinto | `ProductWriteRules`, incluido por los dos con `Include()`. Los comandos implementan `IProductWriteCommand` |
| `E` | `ProductDetails` era posicional con los dos `string?` en posiciones no adyacentes: intercambiarlos **compilaba** | Propiedades `init`; sólo se construye por nombre |
| `B` | La violación de la FK con `RESTRICT` no estaba traducida: salía `500` | **Se traduce**, por nombre de constraint (`SDD-CT-06`) |

**Decisión sobre `B`, que el tramo 5 dejó abierta: se traduce, no se declara deuda.** El
argumento para postergarla —«hoy no hay endpoint que borre una tasa»— es justamente la condición
que puede cambiar sin que nadie se acuerde de la rama que falta. Cuesta diez líneas.

**Cambio de contrato, declarado y no escondido.** `CA-CAT-04-06` pedía `422` con el código
`catalog.product.price_currency_mismatch`; con validador ese caso pasa a `422 validation.failed`
**con el mapa `errors`**. **El criterio y la tabla de «Riesgos» del propio spec se contradecían**
—la tabla pedía «invariante de dominio **y** validador»— y es exactamente lo que el hallazgo `A`
señaló. Gana la tabla: es lo que ya hacían los otros dos invariantes de `CAT-04`. El código de
dominio sigue vivo como red de abajo, cubierto por las unitarias.

**Evidencia.** RED de las 5 pruebas nuevas por el motivo correcto —`Expected: "validation.failed"`
en cuatro, `DbUpdateException` sin traducir en la quinta—; GREEN con unitarias `44/44`,
arquitectura `16/16`, integración de `catalog` `55/55`. **Regresión de la solución: 265 en
verde**, con los 5 fallos de `SDD-CT-14` verificados **por nombre**. `dotnet format
--verify-no-changes` no reporta ninguno de los seis archivos tocados.

**Trampa de entorno nueva:** `dotnet restore` falla con **`NU1903`** sobre `SSH.NET 2025.1.0`,
vulnerabilidad alta tratada como error, que entra por `Testcontainers.PostgreSql` y frena **todos**
los proyectos de integración. Se esquivó en local con `-p:NuGetAudit=false`, **sin tocar
configuración del repo**. Es preexistente y va a frenar CI.

**Qué queda.** Un solo bloqueo, y no es técnico: **el gate `CAT-00`**, en `qep-frontend`. Se
verificó que ese checkout está en `develop` —la rama correcta— antes de citarlo, que es la
lección del 2026-08-11. **No se tocó el otro repo.**

### 2026-08-15 — `CAT-03` y `CAT-04` commiteados; runtime y revisión de 4 lentes de `CAT-04`

**Estado: `CAT-04` sigue `In Progress`.** El runtime salió **11 de 11** y la revisión con
lentes ciegos dejó **6 hallazgos**, ninguno en el lente de riesgo. No cierra por eso y por el
gate.

**El árbol de trabajo se partió en tres commits**, sobre `feature/catalog-api`:

| Commit | Qué | Tamaño |
|---|---|---|
| `e1630a3` | `feat(CAT-03)` — tasas de impuesto completas, con su spec | +2404 / −11 |
| `85b87c8` | `feat(CAT-04)` — `ProductDetails`, FK a `TaxRate`, migración y pruebas | +1818 / −49 |
| `21fcf35` | `docs(CAT-03,CAT-04)` — este ledger | +460 / −10 |

Tres archivos estaban compartidos entre los dos slices —`CatalogDtos.cs`, `CatalogDbContext.cs`
y el `ModelSnapshot`— y se cortaron por hunks para que cada commit fuera un slice entero.
`e1630a3` se verificó compilando **en un worktree aparte**: `Modules.Catalog.UnitTests` en
verde, 0 errores `CS`.

**Se midió antes de commitear, como manda `convenciones-de-id.md`, y los dos slices se pasan
del umbral:** `CAT-03` ~1480 líneas autoradas y `CAT-04` ~1115, contra los ~400 de referencia.
`CAT-03` había previsto 500-650 y declarado partición `a`/`b`; **la previsión quedó corta por
casi tres veces.** Se decidió no partir los commits retroactivamente porque hacerlo obligaba a
cortar por hunks también `TaxRateApiTests.cs` (579 líneas) y el registro de handlers. **Lección
para el próximo slice: la estimación de tamaño no está midiendo las pruebas de integración**,
que en los dos casos fueron más de la mitad del volumen.

**Regresión completa, con `Api.exe` detenido:**

```txt
Modules.Catalog.UnitTests         44/44
Modules.Catalog.IntegrationTests  50/50
ArchitectureTests                 16/16
Modules.Tenancy.IntegrationTests  52/57  ← los 5 de SDD-CT-14, por nombre
```

Los 5 en rojo son `LogoutRevokesTheSessionCookie`,
`RoleDowngradeRemovesPermissionsOnTheNextRequest`,
`SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken`,
`MutatingRequestWithoutCsrfHeaderIsRejected` y
`SuspendingMembershipRevokesTheMembersActiveSession` — todos de `RealAuthenticationApiTests`.
Cero regresión.

**Runtime de `CAT-04`: 11 de 11.** Evidencia completa en el **tramo 4** del spec. Lo que más
valía verificar en vivo:

- **`CA-CAT-04-10`, la auditoría, se probó por lo que NO escribió.** El outbox quedó con 5
  entradas —4 `created` y **1** `updated`— y los **7 pedidos rechazados no dejaron ninguna**.
  El `occurred_at` del `updated` coincide al microsegundo con el `updatedAt` que devolvió el
  `PUT`: misma transacción.
- **`CA-CAT-04-11` salió con datos reales.** Los 4 productos que dejó el runtime de `CAT-02`
  el 2026-08-11 son anteriores a la migración y se leen con las cinco columnas en `NULL`.
- **`CA-CAT-04-07` no persistió**, verificado por `count(*)` en base y no por el status.

**Revisión con 4 lentes ciegos — salda la deuda de método de `CAT-03`.** El lente de **riesgo
salió limpio**, que es el resultado que importa: la frontera de aislamiento entre tenants era
la razón declarada para exigir esta revisión. Los 6 hallazgos, con su detalle en el tramo 5 del
spec:

| # | Lente | Qué | De quién es |
|---|---|---|---|
| `A` | Fiabilidad | El `422` de `CA-CAT-04-06` no lleva el mapa `errors`: falta la regla que empareje `Price` y `Currency` en los dos validadores | `CAT-04` |
| `B` | Resiliencia | `23503` sobre la FK nueva no está traducido en `CatalogUnitOfWork` | `CAT-04`, no alcanzable por HTTP hoy |
| `C` | Resiliencia | `ApiExceptionHandler` devuelve `exception.Message` sin distinguir entorno | **No es de `catalog`** |
| `D` | Legibilidad | Las tres reglas nuevas están duplicadas entre los dos validadores | `CAT-04` |
| `E` | Legibilidad | `ProductDetails` es posicional con dos `string?` no adyacentes: intercambiarlos compila | `CAT-04` |
| `F` | Legibilidad | La regla de `Currency` sólo mira el largo; el dominio además exige letras | `CAT-04` |

**`A` es el que más pesa, y lo encontraron dos lentes por separado.** Contradice la tabla de
«Riesgos» del propio spec de `CAT-04`, que pide «invariante de dominio **y** validador». El
runtime lo confirmó en vivo, y **la prueba de integración no lo detecta** porque afirma sobre
el status y el código, nunca sobre `errors`.

**Dos hallazgos de entorno, verificados:**

- **`launchSettings.json` pisa las variables de entorno.** Sus dos perfiles fijan
  `Authentication__UseDevelopmentStub=false` y `applicationUrl=http://localhost:5000`, así que
  exportar la variable antes de `dotnet run` **no alcanza**: hay que pedir
  `--no-launch-profile`. El `CLAUDE.md` decía que se pide con la variable; con eso solo, no.
- **`NU1903`: `SSH.NET 2025.1.0` tiene una vulnerabilidad de gravedad alta**
  (`GHSA-q939-rpr3-3284`), entra como transitiva y con `TreatWarningsAsErrors` **rompe el
  build** de `Modules.Catalog.IntegrationTests` y `ArchitectureTests`. Verificado como
  preexistente en `e7060f0`, anterior a este trabajo. Se sorteó con
  `-p:WarningsNotAsErrors=NU1903` **sólo en línea de comandos**, sin tocar el repo. **Va a
  frenar CI**, y no es de ningún slice: es dependencia.

**Handoff.** Lo que sigue, en orden: decidir sobre los hallazgos `A`, `D`, `E` y `F`, que son
de `CAT-04`; decidir si `B` se corrige o se declara deuda; abrir `DECISIÓN-PENDIENTE` por `C` y
otra por `NU1903`; y **acordar con `qep-frontend` el alcance del gate `CAT-00`**, que declara
`Product` con «Ningún campo más» y es el bloqueo formal que impide cerrar el slice.

### 2026-08-13 (cont.) — Runtime de `CAT-03`: 11 de 11, con la auditoría probada en base

Los 11 criterios verificados endpoint por endpoint contra la API local con PostgreSQL real.
Evidencia completa —comandos, códigos de estado y cuerpos— en el **tramo 6** del spec.

**Lo que más valía verificar en vivo, y salió bien:** `CA-CAT-03-11` devuelve **`422
catalog.tax_rate.name_taken`, no 500**. Es la forma exacta de `SDD-CT-06`, y confirma que la
discriminación **por nombre de índice** distingue `IX_tax_rates_tenant_name` de
`IX_products_tenant_code` con los dos índices vivos en el mismo esquema.

**La atomicidad se probó por lo que NO escribió**, que es la única forma de probarla:

```txt
 catalog.tax_rate.created     | 4      <- los 4 201, ni una mas
 catalog.tax_rate.deactivated | 1
 catalog.tax_rate.updated     | 1
```

El `403`, los **seis** `422` y los tres `404` no dejaron una sola fila de outbox.

**Dos hallazgos del runtime, ninguno bloqueante:**

1. **El mapa `errors` sale en el idioma del SO del servidor** (`"'Name' no debería estar
   vacío."`). **Es el mismo hallazgo 2 del runtime de `CAT-02`, sin corregir.** Que reaparezca en
   un recurso nuevo confirma que es sistémico, no del slice. Importa para el frontend.
2. **El `compose.yaml` de este repo no sirve para el entorno local tal como está configurado.**
   La cadena de user-secrets apunta a `localhost:5433` —el contenedor `postgres18` del
   developer— y el compose crea `qep` en **5432**. Con Docker abajo, la API muere al arrancar en
   `TenancyDatabaseInitializer` con `Failed to connect to 127.0.0.1:5433`. **Para levantar la API
   hay que arrancar `postgres18`, no `docker compose up`.** No está en el README.

**Datos de prueba que quedaron:** cuatro tasas en `dev_lulo_crm_v2`, tenants `…000c31` y
`…000c32`, con sus 6 filas de outbox. No se borraron, mismo criterio que con los cuatro productos
de `CAT-02`.

### 2026-08-13 (cont.) — Revisión de `CAT-03`: un bloqueante propio, corregido

**Fue una autorrevisión, y se declara como deuda de método.** El método pide cuatro lentes
**ciegos entre sí**; acá los aplicó quien escribió el código. Vale menos que la de `CAT-02`,
donde dos lentes independientes convergieron en el token de concurrencia y esa convergencia fue
lo que lo movió a bloqueante.

**Hallazgo `A`, bloqueante, corregido: `Version` existía y nada lo ejercitaba.** El spec lo
vendía como mejora sobre `Product` —"nace con el agregado"— pero lo único que lo respaldaba era
una unitaria que assertaba `Assert.Equal(2, taxRate.Version)`: **un contador en memoria**. No
probaba que `IsConcurrencyToken()` estuviera mapeado, ni que el `UPDATE` llevara la versión en su
`WHERE`, ni que `DbUpdateConcurrencyException` se tradujera. `Product` sí tenía esa prueba;
`CAT-03` copió la columna y no la prueba.

**Es la misma familia que el peor hallazgo de `CAT-02`:** una afirmación con una prueba verde
encima que no prueba lo afirmado.

**Y la corrección se verificó saboteando el mecanismo**, que es lo que `CAT-02` enseñó a exigir.
Quitando `.IsConcurrencyToken()` del `CatalogDbContext`, la prueba nueva se pone roja con el
motivo correcto:

```txt
Assert.Throws() Failure: No exception was thrown
Expected: typeof(BuildingBlocks.Application.RequestConcurrencyException)
```

Restaurado: `Correctas! - Con error: 0, Superado: 37, Total: 37`.

**Seguimiento, no corregido — `B` y `C` son decisión de producto y no las toma quien implementa:**

- **`B`** — desactivar una tasa **no libera su nombre**: el índice único no tiene filtro parcial
  por `is_active`, así que recrearla devuelve `422 name_taken` mientras la lista de activas está
  vacía. `Product.code` tiene lo mismo; este slice lo duplicó.
- **`C`** — `GET T/tax-rates` devuelve también las inactivas. Quien arme una cotización recibe una
  tasa desactivada, y elegirla mueve los totales.
- **`D`** — el reparto por rol no tiene prueba: mover `TaxRateManage` a `tenancy.member` pasa las
  37 pruebas, aunque el gate lo ratificó como `high`.
- **`E`** — el mensaje veneno de `AuditProjectionWorker` suma un tercer productor a la cola
  compartida. Sigue mereciendo `SDD-CT`.

### 2026-08-13 (cont.) — Integración en verde y el defecto que sólo aparecía en runtime

Se levantó Docker Desktop y corrieron las de integración. **Primera corrida: 18 de 18 en rojo**,
todas `InternalServerError`, mientras las 18 de `Product` pasaban.

**El gotcha que hay que llevarse de acá, y vale para todo caso de uso nuevo del backend: los
handlers se registran UNO POR UNO a mano en `QepServiceCollectionExtensions`, no por escaneo de
ensamblado.** Los cinco de tasas faltaban. El caso de uso compila, el endpoint mapea, la política
resuelve — y el `IRequestDispatcher` no encuentra a quién despachar, así que **500**. Es
exactamente el mismo modo de falla que un permiso sin su `AddPolicy`, y el síntoma no se parece a
la causa en ninguno de los dos. **Un caso de uso nuevo tiene dos mitades, igual que un permiso.**

**Y una lección de método sobre el helper de prueba.** El fallo llegaba como
`ArgumentNullException : Value cannot be null. (Parameter 'collection')`, que no dice nada. La
causa era que `ListAsync` deserializaba el cuerpo **sin assertar el status**: un `500` deserializa
igual a `TaxRatesResponse` con `Items` en `null`. Corregido el helper para assertar `OK` antes de
leer el cuerpo, el fallo pasó a decir `Expected: OK / Actual: InternalServerError`. **Un helper
que no assertea el status convierte cualquier error de servidor en un `NullReference` a diez
líneas de distancia.**

**Evidencia final:**

```txt
dotnet test Backend.slnx
Con error! - Con error: 5, Superado: 52, Total: 57 - Modules.Tenancy.IntegrationTests.dll
Correctas!  - Con error: 0, Superado: 36, Total: 36 - Modules.Catalog.IntegrationTests.dll
Correctas!  - Con error: 0, Superado: 31, Total: 31 - Modules.Catalog.UnitTests.dll
Correctas!  - Con error: 0, Superado: 16, Total: 16 - ArchitectureTests.dll
(resto de los ensamblados, todos en verde)
```

**233 en verde, 5 en rojo.** Los 5 son los de `SDD-CT-14`, verificados **por nombre**:
`LogoutRevokesTheSessionCookie`, `RoleDowngradeRemovesPermissionsOnTheNextRequest`,
`SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken`,
`MutatingRequestWithoutCsrfHeaderIsRejected`,
`SuspendingMembershipRevokesTheMembersActiveSession`. **Cero regresión.** `dotnet format` sin
hallazgos en las rutas del slice.

**`CAT-03` sigue `In Progress`.** Faltan el runtime contra la API local y la **revisión
adversarial de 4 lentes**, que es obligatoria porque el slice toca permisos.

### 2026-08-13 — `CAT-03` implementado de punta a punta, **sin ejecutar las de integración**

**Estado: `CAT-03` sigue `In Progress`.** El código está completo —dominio, persistencia,
migración, permisos y los cinco endpoints— pero **no cumple el DoD** y no se puede cerrar.

**Lo verificado, con evidencia literal:**

| Qué | Resultado |
|---|---|
| RED del dominio | `error CS0103: El nombre 'TaxRate' no existe en el contexto actual` |
| GREEN del dominio | `Superado: 31, Total: 31` — eran 13, o sea 18 nuevos |
| Arquitectura | `Superado: 16, Total: 16`, sin regresión |
| Build | `Compilación correcta. 0 Advertencia(s), 0 Errores` |
| `dotnet format` | Sin hallazgos en las rutas del slice |
| Migración | `20260813135959_AddTaxRates` — `catalog.tax_rates`, 8 columnas, 2 índices; `Down()` borra sólo esa tabla |

**Lo que bloquea el cierre, y es una sola cosa: Docker Desktop no está corriendo.**
`docker info` responde `failed to connect to the docker API at
npipe:////./pipe/dockerDesktopLinuxEngine`. `TaxRateApiTests.cs` está escrito con los 11
criterios cubiertos, pero **no se ejecutó nunca**. Una prueba escrita y no corrida no es
evidencia. Falta también el runtime, la regresión completa —los 5 fallos de `SDD-CT-14` se
verifican **por nombre**— y la revisión de 4 lentes.

**Para retomar:** levantar Docker Desktop y correr
`dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests`.

**`DECISIÓN-PENDIENTE-CAT-05` se resolvió por defecto asumido, no por ratificación.** Se preguntó
dos veces sin respuesta, así que se implementó la recomendación —`name` único por tenant, con
`IX_tax_rates_tenant_name` y `422 catalog.tax_rate.name_taken`—. **Todavía no hay datos**, así que
cambiarla cuesta una migración vacía; después de la primera carga, no.

**Una prueba de `CAT-02` se borró a propósito.**
`ProductWriteApiTests.TaxRatePermissionsAreNotPublishedBeforeTheirSliceExists` afirmaba que
`catalog.tax_rate` **no** aparece en `/authorization/catalog`. Su propio comentario decía que se
borraba cuando `CAT-03` trajera los permisos con su implementación. Su reemplazo, `CA-CAT-03-10`,
afirma lo contrario y además verifica que **la política resuelva** — la mitad que, si falta, da
500 en vez de 403.

**Un cambio de comportamiento fuera del recurso, declarado:** el mensaje de
`concurrency.conflict` en `CatalogUnitOfWork` pasó de *"The product changed…"* a *"The catalog
record changed…"*. El `catch` lo comparten los dos agregados. El `code`, que es el contrato, no
cambió, y ninguna prueba assertaba sobre ese texto.

**La partición `CAT-03a`/`CAT-03b` se ejecutó como estaba declarada**, en secuencia y en la misma
sesión. No hizo falta partir el commit.

**Sin commit.**

### 2026-08-12 — Apertura de `CAT-03`: spec escrito, sin código, con dos decisiones abiertas

**Estado:** `CAT-03` pasa a `In Progress`. Se escribió
[`03-modulos/catalog/slices/CAT-03-api-de-tasas-de-impuesto.md`](../03-modulos/catalog/slices/CAT-03-api-de-tasas-de-impuesto.md)
con 11 criterios de aceptación. **No se escribió una sola línea de código** — el spec es el
primer artefacto, y el tramo 1 arranca por el RED del dominio `TaxRate`.

**La partición se declaró antes, no después.** `CAT-03a` (dominio, persistencia, migración,
permisos y los dos `GET`) y `CAT-03b` (escrituras y validadores), con ~250 y ~300 líneas
estimadas. `CAT-02` se midió en 1043 líneas recién al querer commitear y hubo que partirlo
retroactivamente; acá la previsión está escrita desde el arranque, con la salvedad explícita de
que si `CAT-03a` mide menos de 400 no se parte.

**De dónde salió el trabajo.** El owner pidió enriquecer `Product` con nombre, descripción,
código, imagen, estado, precio, impuesto, moneda, stock y escala de precios. Contrastado contra
la descomposición de módulos y contra el código, esa lista cruzaba tres fronteras:

| Campo | Resolución del owner, 2026-08-12 |
|---|---|
| `nombre`, `código`, `estado` | Ya existen en `Product` |
| `descripción`, `imagen`, `precio`, `moneda` | Entran a `catalog` — son `CAT-04` |
| `impuesto` | FK a `TaxRate`, que no existe: por eso `CAT-03` va primero |
| `escala de precios` | Queda en `pricing`, como el gate y `RN-030`..`RN-037` ya declaraban |
| **`stock`** | **Fuera del alcance del proyecto** |

**Por qué `stock` salió, que es la decisión que más ahorra:** no tenía `RF` que lo sustentara
—regla dura, "no inventar"— ni módulo en el mapa de seis. Y como `int` en `Product`, movido
desde `orders`, era el mismo *lost update* que la revisión de 4 lentes encontró en `CAT-02`,
pero sin historial para auditarlo. Sacarlo ahora costó una línea.

**Lo que queda pendiente y hay que resolver, en este orden:**

1. **`DECISIÓN-PENDIENTE-CAT-05` — ¿el `name` de una tasa es único por tenant?** Bloquea la
   migración `AddTaxRates`, **no** el tramo 1. Recomendación registrada en el spec: sí, con
   `IX_tax_rates_tenant_name` y `422 catalog.tax_rate.name_taken`. Agregar el índice después es
   migración sobre datos ya sucios.
2. **`DECISIÓN-PENDIENTE-CAT-06` — precedencia de precio contra `pricing`.** Bloquea `CAT-04`.
   Se preguntó y quedó sin respuesta explícita, así que **se asumió un default y se dejó
   escrito** en el spec: gana `pricing`, `Product.Price` es precio base de fallback. Si el owner
   quiere lo contrario, se cambia una línea del spec y no hay código que rehacer.
3. **Escribir en el gate `CAT-00` las tres decisiones de la tabla de arriba.** El gate cerró
   declarando el modelo de `Product` con **"Ningún campo más"**, y vive en
   `qep-frontend/sdd/03-modulos/catalog/gate.md`, que es autoridad del otro repo. **No se tocó
   desde este lado.** Sin eso, quien lea el gate dentro de dos semanas ve la lista vieja y tiene
   razón.

**Verificado contra el código antes de escribir el spec, no citado de memoria:** `Product` hoy
tiene `Id`, `TenantId`, `Name`, `Code`, `IsActive`, `Version`, `CreatedAt`, `UpdatedAt`
(`Product.cs:40-66`); `CatalogPermissions` tiene sólo los dos de producto, y su comentario
(`CatalogPermissions.cs:6-12`) declara que los de `tax_rate` "vuelven con `CAT-03`, junto a su
implementación"; y las políticas se registran a mano en
`QepServiceCollectionExtensions.cs:359-364`. El checkout de `qep-frontend` estaba en `develop`
al leer el gate — comprobado, porque el 2026-08-11 leerlo desde una rama vieja produjo dos
hallazgos falsos.

**Sin commit.** El trabajo de esta sesión son dos archivos de `sdd/`: el spec nuevo y este
ledger.

### 2026-08-11 — Remediación del RBAC: paso 1 **aplicado**, corte detenido antes del paso 2

**Los manifiestos NO viven en este repositorio.** Se escribieron primero en `ops/` y se
movieron el mismo día, borrados de acá sin haber llegado a commitearse. El motivo no es de
prolijidad: el `ClusterRole` y su `ClusterRoleBinding` están declarados en
`migracion-k8s/k8s/platform/azure-devops-agent/rbac.yaml`, que es un recurso de **kustomize**.
El paso 3 borra el `ClusterRoleBinding`, y el próximo `kubectl apply -k` de ese directorio
**lo vuelve a crear, en silencio**. Remediar desde `qep-backend` estaba garantizado a
revertirse en el siguiente apply de plataforma.

La regla que queda: el corte no es "¿es RBAC?", es **"¿de quién es el radio?"**. Lo que sólo
afecta a `prod-qep-backend` puede vivir acá —`ops/rbac-deployer-secrets.yaml` lo hace—; lo que
cruza namespaces es de plataforma.

| Archivo, en `migracion-k8s` | Paso | Estado |
| --- | --- | --- |
| `k8s/platform/azure-devops-agent/rolebindings.yaml` | 1 — 25 RoleBindings | **aplicado** el 2026-08-11 |
| `k8s/platform/azure-devops-agent/rbac.yaml` | 2 — partir el ClusterRole | declarado, **sin aplicar** |
| `kustomization.yaml`, `README.md`, `docs/04-migracion-azure-devops.md` | contexto | actualizados |

**Los 3 dudosos se resolvieron, y ninguno lleva `RoleBinding`:** `sonarqube` va por
`kubectl apply -k` sobre `cp-01`, `ingress-smoke` por `scripts/deploy-ingress-smoke-test.sh`
y `cnpg-system`/`barman-cloud` por el playbook `84-cnpg-backups.yml`. La lista de 25 derivada
del `regcred` era exacta.

**Aplicado:** `kubectl apply -f ops/rbac-deployer-rolebindings.yaml` → 25 `created`.
Verificado: 25 `RoleBinding` con `roleRef=azure-devops-deployer`, el `ClusterRoleBinding`
sigue vivo, y `auth can-i create deployments` sigue dando `yes` en `prod-qep-backend`,
`prod-lulo-erp-backend`, `sonarqube` y `cnpg-system`. **Cero cambio observable**, que es lo
esperado: RBAC es unión y mientras el `ClusterRoleBinding` exista los permisos se suman.

**El rollback ya no necesita un archivo aparte:** el `ClusterRoleBinding` que el paso 3
destruye está versionado en el git de `migracion-k8s`, así que deshacerlo es `git revert` del
commit que lo quitó, más el apply. Eso hizo innecesario el `ops/rbac-deployer-rollback.yaml`
que se había escrito acá.

**Detenido antes del paso 2, por decisión del owner el 2026-08-11.** Los pasos 2 y 3 son el
corte: afectan a las 25 aplicaciones a la vez y el `ClusterRole` es infraestructura
compartida de devops. El paso 1 es el único que se podía dar sin certeza, porque una lista
incompleta no hace daño mientras el `ClusterRoleBinding` cubra a todos.

**Ya no falta ningún dato:** el repo de plataforma respondió las dos preguntas que se le
iban a hacer a devops. Lo que queda es la decisión de correr el corte.

**Trampa armada, a sabiendas:** `rbac.yaml` ya declara el estado post-migración, así que **el
próximo `kubectl apply -k k8s/platform/azure-devops-agent` ejecuta el paso 2**, incluso si se
corre por otra razón —rotar el PAT, por ejemplo—. Está anunciado en un banner al tope del
archivo. Para el corte conviene `apply -f rbac.yaml -f rolebindings.yaml`, no `-k`.

**Falsa alarma descartada:** el `--dry-run=server` reportaba `secret/azure-devops-agent-pat
configured`, que parecía que un apply iba a pisar el PAT vivo. No: comparados por SHA-256 el
local y el del clúster son idénticos (84 bytes, mismo hash). El `configured` lo produce
`stringData`, que es campo de sólo escritura y el servidor nunca devuelve, así que el parche
de tres vías nunca sale vacío. Vale para cualquier Secret aplicado con `stringData`.

**Bloqueante concreto del paso 3:** `cnpg-system`, `ingress-smoke` y `sonarqube` no tienen
`RoleBinding`. Si el pipeline los despliega, el paso 3 les corta el deploy. No se puede
resolver desde el clúster —`kubectl-client-side-apply` no prueba el actor—; lo resuelven las
definiciones de pipeline en Azure DevOps.

**Trampa de YAML que costó dos intentos:** los anchors (`&labels` / `*labels`) **no cruzan
documentos**. El `---` los invalida y `kubectl` corta con `unknown anchor`. En un archivo
multi-documento los bloques van repetidos. Y `Get-Content` en PowerShell 5.1 lee UTF-8 como
ANSI: reconstruir un archivo con acentos por ese camino lo deja en mojibake.

### 2026-08-11 — Limpieza de Secrets huérfanos en `prod-qep-backend`: uno borrado, uno retenido

Fuera del alcance del método (`DECISIÓN-PENDIENTE-INFRA-01`). Salió al auditar el radio de
exposición del hallazgo de RBAC: de los 6 Secrets del namespace, 3 no los referencia nadie.

**Borrado: `api-qep-qcode-co-tls`** (TLS, cert-manager, creado el 2026-08-05). Huérfano
probado, no inferido: **ningún objeto `Certificate` lo posee** —el único que existe es
`qep-api-qcode-co-tls`, así que cert-manager no lo iba a renovar ni recrear—, ningún Pod,
ServiceAccount ni Ingress lo referencia, y `k8s/prod-wildcard-certificate.yaml` apunta a
`lulocrm-wildcard-tls`, otro nombre. Sobró de un rename de host: sus `alt-names` son
`api-qep.qcode.co` y el Ingress vivo sirve `qep-api.qcode.co`. Post-borrado el `Certificate`
en uso sigue `Ready=True`. Reversible: cert-manager lo reemite si alguna vez hiciera falta.

**Retenido por decisión del owner: `prod-qep-backend`** (Opaque, 2026-08-02). Nada lo
referencia —el Deployment consume `qep-backend-secret` por `envFrom.secretRef` y nada más—,
así que borrarlo no rompería lo que corre. **Pero no es config vieja de la app: son
credenciales de base**, con el juego de claves de CloudNativePG (`database`, `host`,
`password`, `port`, `uri`, `username`). Tiene las **mismas claves y los mismos tamaños byte a
byte** que `qep-postgres`, lo que lo hacía parecer un duplicado inocuo.

**No lo es.** Comparados por `SHA-256` de `.data` —sin imprimir ningún valor— dan distinto:
`3554e7fe…0e01de2` contra `af0edbbe…6c6aab`. Mismas claves, mismos tamaños, contenido
diferente. Borrarlo destruye valores únicos y es irreversible, así que se frenó y el owner
decidió conservarlo.

**Queda abierto:** cuál de los dos passwords es el vigente. Los dos son del 2026-08-02 y
`qep-backend-secret` es del 2026-08-11, posterior a la rotación del incidente de ese día, lo
que sugiere que ambos quedaron pre-rotación — pero eso es lectura de fechas, no verificación.
Se resuelve contra CNPG en el namespace `database`, no leyendo los valores. `qep-postgres`
está en la misma situación y tampoco se tocó.

**Técnica reusable:** comparar dos Secrets por hash de `.data` prueba o descarta que sean
idénticos sin exponerlos. El valor entra a una variable y sólo sale el hash. Es la forma de
decidir un borrado sin romper la regla de no imprimir secretos.

### 2026-08-11 — Plan de remediación del RBAC del deployer (propuesto, no aplicado)

Fuera del alcance del método por `DECISIÓN-PENDIENTE-INFRA-01`: no lleva slice ni spec. Entra
acá porque la exención no alcanza al handoff.

**Se re-verificó el hallazgo anterior contra `contabo-prod` con comandos read-only** (`get`,
`auth can-i`). El núcleo se sostiene íntegro: el `ClusterRole` `azure-devops-deployer` tiene
exactamente las cuatro reglas documentadas, lo ata **un solo** `ClusterRoleBinding` sin ningún
`RoleBinding`, no hay `pod-security.kubernetes.io/enforce` en ningún namespace, no hay
webhook de admisión que restrinja pods y no hay `ValidatingAdmissionPolicy` alguna. La SA
sigue sin poder crear `roles`, `rolebindings`, `serviceaccounts`, `pods`, `daemonsets` ni
`statefulsets`.

**Cuatro correcciones a lo que decía el handoff anterior:**

1. **Son 38 namespaces, no 15.** El radio de acción es 2,5× lo documentado.
2. **`monitoring` tiene 34 Secrets** —el mayor depósito del clúster, casi 5× `database`— y el
   handoff no lo mencionaba. `database` tiene 7 y `prod-qep-backend` 6.
3. **La SA tiene `patch` y `update` sobre `namespaces` en todo el clúster.** PSA se configura
   con etiquetas **en el objeto Namespace**, así que activar Pod Security Admission sin
   quitarle antes ese permiso es decorativo: el agente comprometido borra la etiqueta. **El
   orden de la remediación no es negociable por esto.**
4. **La SA no tiene `delete` en ningún recurso.** Un `RoleBinding` faltante se manifiesta como
   `Forbidden` en el `apply` y rompe el pipeline en rojo — falla ruidosa, no silenciosa. Eso
   es lo que hace tolerable la migración.

**Y una distinción que el handoff anterior mezclaba:** montar un Secret en un Pod es legal
bajo `baseline` **y** bajo `restricted`. PSA **no** cierra el robo de Secrets; cierra el salto
de Pod privilegiado a nodo. Lo que cierra el robo de Secrets es acotar el RBAC. Son dos
controles para dos amenazas distintas, no uno redundante.

**Inventario de consumidores.** Un único sujeto usa el `ClusterRole`: la SA
`azure-devops-agent`. Los namespaces con cargas aplicadas por `kubectl-client-side-apply`
—firma del pipeline compartido `devops/pipeline-templates` `k8s/deploy.yml`— son **27**, más
`prod-carnivore-bot`, que tiene Service e Ingress sin Deployment. `n8n` está **vacío**: sólo un
PVC de 10 GiB, sin carga. Todo lo de `helm` (monitoring, cert-manager, ingress-nginx,
cilium, metrics-server) y lo del operador CNPG (`database`) **no** lo despliega el agente.
Salvedad: `kubectl-client-side-apply` no prueba el actor —un admin con kubectl deja la misma
firma—, así que la lista autoritativa son las definiciones de pipeline en Azure DevOps y la
confirma devops, no este inventario.

**Estado PSA de las cargas actuales, calculado desde los specs de los 93 pods** (sin
`--dry-run`, que es escritura): incumplen `baseline` sólo `kube-system` (14 de 17),
`monitoring` (6 de 15), `ingress-nginx` (2 de 2) y `sonarqube` (1). **Los 28 namespaces de
aplicación, `azure-devops-agent`, `database`, `cert-manager` y `cnpg-system` cumplen `baseline`
hoy, sin tocar un manifiesto.** Más: `database`, `cert-manager` y `cnpg-system` cumplen
`restricted` completo. `kube-system` **no puede** llevar `baseline` —son los componentes del
plano de control— así que PSA no lo protege; lo protege el paso 1.

**Plan propuesto a devops, en este orden:**

1. Crear los `RoleBinding` por namespace contra el mismo `ClusterRole` (un `RoleBinding` a un
   `ClusterRole` sólo concede en su namespace: no hacen falta 28 `Role`). Aditivo, riesgo cero,
   sin ventana.
2. Quitar `namespaces` de las reglas del `ClusterRole` y moverlo a uno residual con
   `get`/`list`/`watch` únicamente. Cierra el vector del punto 3.

   **Esto no cuesta nada, y se puede probar.** El único reparo era que el pipeline aplica
   `k8s/<env>-namespace.yaml` como primer manifiesto, y `namespaces` es cluster-scoped: un
   `RoleBinding` no lo puede conceder. Pero el namespace **ya está creado antes** de que el
   pipeline corra. La cadena: los 25 namespaces que despliega el agente tienen un Secret
   `regcred` de tipo `kubernetes.io/dockerconfigjson` —sin él la imagen de Docker Hub no baja
   y los pods quedan en `ImagePullBackOff`—; los 25 los gestiona `kubectl-client-side-apply`;
   y la SA tiene `create secrets = no` en **todos** ellos salvo `prod-qep-backend`, donde el
   `Role` acotado se creó el 2026-08-11, mucho después. Un Secret no puede existir en un
   namespace inexistente. Por lo tanto **una persona con kubectl ya crea el namespace y su
   `regcred` en cada onboarding**, y el `apply` del manifiesto de namespace es un no-op sobre
   un objeto que siempre preexiste.

   Consecuencia operativa, que es el mejor argumento a favor de todo el plan: **la migración
   no inventa un proceso manual nuevo, extiende uno que ya existe.** El `RoleBinding` se suma
   al runbook de alta que hoy ya tiene "crear namespace + crear regcred". Un renglón más.
3. Borrar el `ClusterRoleBinding`. **Único paso destructivo, un solo objeto, rollback =
   volver a aplicarlo.**
4. PSA por olas: `database` a `restricted`; los 28 de aplicación a `baseline`;
   `kube-system`, `ingress-nginx`, `monitoring` y `sonarqube` a `privileged` explícito con
   `audit=baseline`.

**Riesgo que queda aceptado:** el agente conserva `create`/`patch` sobre cargas en los ~28
namespaces que despliega, y por lo tanto puede leer los Secrets de esos namespaces —incluidos
los 6 de `prod-qep-backend`, con la cadena de conexión. Es inherente a ser el deployer y no lo
arregla el RBAC. Lo que la migración compra es sacar de ese radio a `kube-system`, `database`,
`monitoring`, `cert-manager`, `cnpg-system` e `ingress-nginx`.

**No se aplicó nada y no se ejecutó ninguna prueba de explotación.** Todo sale de `get` y de
`auth can-i`, que es evaluación. Ningún valor de Secret se leyó: los conteos salen de
`get secrets` sin `-o`, que no imprime `.data`.

### 2026-08-11 — Auditoría de RBAC: el `Role` acotado no es una frontera real

**Hallazgo preexistente, no lo introdujo este trabajo.** Se encontró al auditar el `Role` que
se creó para que el pipeline aplicara `prod-secret.yaml`.

El `Role` quedó bien acotado —`get`/`patch`/`update` sólo sobre `qep-backend-secret`, y ni
siquiera `list`— pero **eso no impide que el agente lea cualquier Secret del clúster.** El
`ClusterRole` `azure-devops-deployer`, atado por `ClusterRoleBinding` y por lo tanto vigente en
**todos** los namespaces, concede:

```
apiGroups=[""]                resources=[namespaces configmaps services]  verbs=[get list watch create update patch]
apiGroups=[apps]              resources=[deployments]                     verbs=[get list watch create update patch]
apiGroups=[networking.k8s.io] resources=[ingresses]                       verbs=[get list watch create update patch]
apiGroups=[batch]             resources=[cronjobs jobs]                   verbs=[get list watch create update patch]
```

**Por qué eso alcanza para leer secretos sin permiso de leer secretos:** montar un Secret en un
Pod **no** requiere permiso RBAC sobre ese Secret. El kubelet lo monta con sus propias
credenciales. Con `create` sobre `jobs` o `deployments` en un namespace, se crea una carga que
monta cualquier Secret de ese namespace y lo vuelca. Verificado con `auth can-i` que la SA
puede crear Jobs y Deployments en `default`, `database`, `azure-devops-agent` y **`kube-system`**.

**Y no hay contención:** ningún namespace tiene `pod-security.kubernetes.io/enforce` —los 15
salen vacíos— ni hay webhook de admisión que restrinja pods; los cuatro presentes son de
cert-manager, CNPG, ingress-nginx y Prometheus. Sin eso, un Pod privilegiado o con `hostPath`
en `kube-system` llega al nodo. `database` tiene 7 Secrets, y ahí vive el PostgreSQL del que
sale la cadena de conexión de producción.

**Lo que sí está cerrado:** la SA **no** puede crear `roles`, `rolebindings` ni
`serviceaccounts`. No hay escalación directa por RBAC.

**No se ejecutó ninguna prueba de explotación.** Todo lo anterior sale de leer objetos de RBAC
y las etiquetas de los namespaces con `auth can-i`, que es evaluación, no acción.

**Remediación propuesta, no aplicada — es de devops, no de este repositorio:**

1. Reemplazar el `ClusterRoleBinding` por `RoleBinding`s namespace por namespace, sólo en los
   que el pipeline despliega. Mata el acceso cruzado de una. **Ojo:** ese `ClusterRole` lo usan
   otros pipelines —hay namespaces de otras aplicaciones en el mismo clúster—, así que
   acotarlo sin crear su `RoleBinding` a cada uno les rompe el deploy.
2. Activar Pod Security Admission en `baseline` o `restricted`, al menos en `kube-system` y
   `database`. Cierra el salto de Pod privilegiado a nodo.

Mientras 1 y 2 no estén, el `Role` acotado documenta la intención pero **no** es la frontera
que aparenta ser.

### 2026-08-11 — El deploy corta al aplicar el Secret: falta RBAC, no es el manifiesto

Con el build ya verde tras fijar el SDK, el `Deploy` falla en el paso del `Secret`:

```
Error from server (Forbidden): error when retrieving current configuration of:
Resource: "/v1, Resource=secrets", Kind: "Secret"
Name: "qep-backend-secret", Namespace: "prod-qep-backend"
secrets "qep-backend-secret" is forbidden: User
"system:serviceaccount:azure-devops-agent:azure-devops-agent" cannot get resource
"secrets" in API group "" in the namespace "prod-qep-backend"
```

**El manifiesto está bien.** `kubectl apply --dry-run=client -f k8s/prod-secret.yaml` responde
`secret/qep-backend-secret created (dry run)`. Lo que falta es permiso: la service account del
agente puede ConfigMaps y Deployments —se aplican hoy— pero no Secrets.

**Detalle que importa para el arreglo:** falla en el **`get`**, no en el `create`. `kubectl
apply` primero recupera la configuración actual para decidir entre crear y parchear, así que
dar sólo `create` no destraba nada.

**Corrección propuesta, sin aplicar:** `ops/rbac-deployer-secrets.yaml`, un `Role` +
`RoleBinding` en `prod-qep-backend`. Dos reglas, no una: `create` va ancho porque RBAC no
admite `resourceNames` en ese verbo —la autorización ocurre antes de que el objeto exista—, y
`get`/`patch`/`update` van acotados a `qep-backend-secret`. Eso evita que un agente de CI
comprometido pueda leer cualquier otro Secret del namespace.

**Vive en `ops/` y no en `k8s/` a propósito:** la misma service account tampoco puede crear
Roles, así que esto no se puede aplicar desde el pipeline. Lo aplica alguien con admin del
clúster, una vez. `ops/` además queda fuera del alcance de la plantilla, que sólo lee
`manifestDirectory: k8s`.

**Antes de aplicarlo hay que preguntar a devops si ya existe un Role** que le da a esa SA
acceso a configmaps y deployments en este namespace — tiene que existir. Si existe, lo correcto
es agregarle las dos reglas y **no** crear un Role paralelo: los permisos de dos Roles sobre el
mismo sujeto se suman, así que restringir uno después no restringe nada.

### 2026-08-11 — El build de Docker rompió solo: tag flotante contra `global.json` fijo

**El primer deploy tras publicar `main` falló en el `Build`, no en el despliegue.** Literal:

```
Requested SDK version: 10.0.301
global.json file: /src/global.json
Installed SDKs:
10.0.400 [/usr/share/dotnet/sdk]
Install the [10.0.301] .NET SDK or update [/src/global.json] to match an installed SDK.
exit code: 155
```

**Causa.** `mcr.microsoft.com/dotnet/sdk:10.0` es un tag flotante y se movió a `10.0.400`.
`global.json` pide `10.0.301` con `rollForward: latestPatch`, que rueda **dentro de la misma
banda de feature**: de `10.0.3xx` a `10.0.3xx` sí, de `3xx` a `4xx` no. Nadie tocó el
repositorio: `global.json` no cambia desde `0d3e5e9`, el commit inicial, y el `Dockerfile`
desde `414afd1`. **Rompió el paso del tiempo.**

**Corrección.** `Dockerfile:8` fija `mcr.microsoft.com/dotnet/sdk:10.0.301`. Se fija la imagen
en lugar de aflojar `global.json` a propósito: `global.json` existe para que todos compilen
con el mismo SDK, y la máquina del developer tiene `10.0.301` — aflojarlo haría que CI y
developer dejaran de coincidir, que es justo lo que ese archivo evita. Al subir de banda se
cambian los dos, juntos.

**Evidencia:** reproducido local con `docker build`, mismo `exit code: 155` y mismo texto que
CI. Con el tag fijo, `docker build` termina en `naming to docker.io/library/...` sin error.

**Trampa que casi desvía el diagnóstico:** `docker image ls` mostraba `sdk:10.0` en `10.0.302`
—que sí resuelve— y `docker run` sobre esa imagen cacheada confirmaba `10.0.302`. Pero
buildkit resuelve el tag **contra el registry**, no contra la caché local, y ahí ya era
`10.0.400`. Para saber con qué SDK construye de verdad hay que mirar el log del build, no la
imagen local.

**Queda como riesgo aceptado, no corregido:** `mcr.microsoft.com/dotnet/aspnet:10.0` de la
etapa de runtime sigue flotante. No rompe el build, pero puede cambiar el runtime de
producción sin que nadie lo pida. Fijarlo es decisión aparte.

### 2026-08-11 — El checkout de `qep-frontend` está en una rama vieja, y eso invalidó dos hallazgos

**Regla que sale de acá, y es la parte que importa: antes de leer cualquier cosa de
`qep-frontend/sdd/`, verificar en qué rama está ese checkout.** No alcanza con que el
directorio exista.

Al cerrar `DECISIÓN-PENDIENTE-INFRA-01` se buscó dónde escribir su `SDD-ADR`, y el registro
`qep-frontend/sdd/01-contexto/decisiones-de-arquitectura.md` parecía terminar en
`SDD-ADR-07`. De ahí salieron dos hallazgos que se registraron en este ledger y **los dos son
falsos**:

- «`SDD-ADR-08` se cita en tres archivos de este repo y no tiene entrada». **Falso.** Existe
  desde `3aca4a9`, titulado «Cada repo tiene su carpeta `sdd/`, con un solo ledger».
- «`AGENTS.md` §0c contradice a `SDD-ADR-08`». **Falso.** §0c ya está actualizado, con la
  tabla del reparto y la mención explícita a `SDD-ADR-08`.

La causa es una sola: **ese checkout está en `feature/catalog`, nueve commits detrás de
`develop`**, que es donde vive todo eso. `SDD-CT-18` también está en `develop`, y `SDD-CT-19`
no está en ningún lado porque es un ID reservado a futuro, tal como lo declara el handoff del
`fix(config)` más abajo — no es deuda.

Cómo se detectó, y es la comprobación que hay que correr: `git -C qep-frontend log -S
"<término>" --all` encontró el commit; `git branch -a --contains` mostró que vive en
`develop` y no en la rama activa.

Lo escrito sobre el working tree del frontend se revirtió: `git status` de ese repo está
limpio salvo un `.claude/` sin trackear, que ya estaba. `SDD-ADR-09` queda **sin redactar**,
y va sobre `develop`.

**Único hallazgo real que sobrevive, y hay que verificarlo antes de actuar:** el `SDD-ADR-08`
de `develop` se declara `Estado: decidida, **sin ejecutar** — la migración de archivos no se
hizo todavía`. Ya no es cierto: `594ee11` la ejecutó en este repositorio el 2026-08-11. Es
corrección de una línea, en el otro repo.

**Lección de método, la misma que `SDD-CT-16`:** un mecanismo inferido de leer archivos se
contradijo al contrastarlo contra la historia de git. La diferencia es qué se contrastó: acá
la primera lectura fue del árbol de trabajo, que es un estado, no la autoridad.

### 2026-08-11 — Forma de la configuración en k8s: híbrida, con Secret propio

**Sin slice.** Ver `DECISIÓN-PENDIENTE-INFRA-01` en Estado global. Se registra acá porque el
cierre de sesión es obligatorio aunque el trabajo no tenga ID, no para darle uno por la puerta
de atrás.

**La pregunta que lo originó:** reemplazar el `appsettings.json` entero desde un ConfigMap
—como en otro proyecto del owner— contra inyectar variables de entorno. Gana la segunda, por
precedencia: `appsettings.json` < `appsettings.{Env}.json` < variables de entorno. El archivo
reemplazado es todo-o-nada y duplica el contrato de configuración fuera del repositorio; una
clave nueva en código queda borrada en silencio por un ConfigMap viejo. Las variables se
superponen clave por clave y dejan la imagen como dueña de los valores por defecto.

**El hallazgo que lo volvió urgente.** `azure-pipelines.yml` pasaba
`removeAppsettingsPath: $(Build.SourcesDirectory)/Api/appsettings.json` — vestigio del enfoque
de reemplazo, **con la ruta mal**: el archivo real es `src/Api/appsettings.json`, así que era un
no-op silencioso. El híbrido ya funcionaba por accidente, no por decisión.

**Qué cambió:**

- `k8s/prod-secret.yaml` **nuevo**, con las cuatro credenciales que estaban en texto plano en
  el ConfigMap: `ConnectionStrings__QepDatabase` (lleva la contraseña),
  `Notifications__Infobip__ApiKey`, `Storage__R2__AccessKeyId` y `__SecretAccessKey`.
- `k8s/prod-deployment.yaml` suma un `secretRef` **después** del `configMapRef`: ante colisión
  de claves gana la fuente posterior.
- `k8s/prod-configMap.yaml` pierde esas cuatro y además las cinco que sólo repetían el valor
  por defecto de la imagen (`Notifications__EmailProvider`, los dos `Audit__*`,
  `Storage__PresignedUrlMinutes`). `Registration__PublicTenantSignupEnabled` se conserva
  duplicada **a propósito**: que un anónimo pueda crear un tenant es decisión de seguridad y
  producción la declara explícita.
- `src/Api/appsettings.json` pierde 18 líneas: la sección `Authentication` completa, el bloque
  `R2` entero, los dos `""` de Infobip y **`DataProtection`, que era configuración muerta** —
  `rg DataProtection` sobre todo el repositorio devolvía esa única línea del propio archivo.
- `README.md` y `CLAUDE.md` decían que la cadena de conexión la fija `prod-configMap.yaml`.
  Corregidos: gana el código (`SDD-ADR-01`).

**El defecto que casi llega a producción.** La plantilla `k8s/deploy.yml@k8sTemplates` **no
aplica el directorio**: `manifestDirectory` sólo dice dónde buscar. Recorre una lista blanca
—`namespace`, `configMap`, `service`, `deployment`, `ingress`— y aplica
`<dir>/<env>-<entrada>.yaml` uno por uno. `secret` no está en ella. Sin declararla explícita,
`prod-secret.yaml` **nunca se habría aplicado**, y con la cadena de conexión ya fuera del
ConfigMap los pods quedaban en `CreateContainerConfigError` de forma permanente. Corregido
declarando `manifests` en `azure-pipelines.yml`, con `secret` antes que `deployment`.

**Evidencia:**

```
dotnet build Backend.slnx -c Release
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

```
dotnet test Backend.slnx -c Release --no-build
Con error!  - Con error: 5, Superado: 52, Total: 57 - Modules.Tenancy.IntegrationTests.dll
Correctas!  - Con error: 0, Superado: 19, Total: 19 - Modules.Catalog.IntegrationTests.dll
(resto de los ensamblados, todos en verde)
```

Los 5 fallos son los de `SDD-CT-14`, verificados **por nombre** y no por conteo:
`RoleDowngradeRemovesPermissionsOnTheNextRequest`, `MutatingRequestWithoutCsrfHeaderIsRejected`,
`SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken`,
`SuspendingMembershipRevokesTheMembersActiveSession`, `LogoutRevokesTheSessionCookie`.
Cero regresión.

Comportamiento idéntico al quitar los `""`, confirmado en código y no por suposición:
`Coalesce` (`QepServiceCollectionExtensions.cs:316`) filtra por `IsNullOrWhiteSpace`, y
`StorageOptions.R2` y `NotificationsOptions.Infobip` tienen `= new()` con `string.Empty` en
cada campo. Ausente y `""` se comportan igual.

**Lo que NO se verificó: el pipeline nunca corrió.** Todo lo de arriba es lectura de la
plantilla más build y pruebas locales. El primer deploy es la prueba real.

**Tres seguimientos abiertos.** `prod-wildcard-certificate.yaml` **no** es uno: quedó fuera de
la lista de `manifests` a propósito, resuelto por el owner el 2026-08-11 — el certificado se
usa más adelante y se agrega a la lista cuando llegue ese momento.

- **El `sed` del tag corre sobre credenciales ya sustituidas.** En `deploy.yml` el reemplazo de
  tokens es el paso previo al `find prod-*.yaml -exec sed -i "s|TAG|<buildId>|g"`. Una
  credencial que contenga la cadena literal `TAG` en mayúsculas se corrompe en silencio, y el
  síntoma es "credencial inválida" sin ninguna pista. Ya pasaba con el ConfigMap.
- **El enmascarado en logs no vive en este repositorio.** `replacetokens@5` está sin `logLevel`
  explícito; depende de que las variables del grupo `Backend-prod` estén marcadas como *secret*
  en Azure DevOps. Verificar antes del primer deploy.
- **Data Protection sin persistir.** Al borrar la sección muerta quedó a la vista que no hay
  `PersistKeysToFileSystem` en ningún lado: las claves son efímeras por pod, así que cada deploy
  desloguea a todo el mundo y con más de una réplica se rompe. `replicas: 1` lo tapa hoy. Es
  trabajo propio, no forma de la configuración.

**Estado al cerrar:** commiteado en **`84ebc5c`**, sobre `feature/catalog-api`, sin publicar.
Siete archivos: `azure-pipelines.yml`, `k8s/prod-configMap.yaml`, `k8s/prod-deployment.yaml`,
`k8s/prod-secret.yaml` (nuevo), `src/Api/appsettings.json`, `README.md` y `CLAUDE.md`. Este
último **nunca estuvo versionado** y entra por decisión explícita del owner tomada en esta
sesión, con lo que se cierra el pendiente que el ledger arrastraba desde el 2026-08-11.

### 2026-08-11 — Revisión de 4 lentes de `CAT-02` y su corrección

Corrieron los cuatro lentes **en paralelo y ciegos entre sí** sobre
`git diff 4677d59..3c2c9ec -- src/ tests/` (1870 líneas autoradas, 41 archivos). Obligatorios
por dos razones independientes: superan las 400 líneas y tocan permisos. Evidencia literal en
el **tramo 6** del spec.

Seis hallazgos. **Dos lentes independientes convergieron en el mismo** —`Product` sin token de
concurrencia—, uno describiéndolo como *lost update* silencioso y el otro como violación de la
invariante `EnsureActive()`. Esa convergencia es lo que lo movió a bloqueante.

**Corregidos en la transacción única:** el token de concurrencia (`A`), los permisos de
`tax_rate` publicados sin implementación (`B`), los comodines de `LIKE` sin escapar (`C`) y el
orden validador/autorización (`D`).

**`A` entró por decisión explícita del owner.** La revisión lo planteó como candidato a slice
propio —columna, migración, traducción y prueba son más que una corrección— y el owner resolvió
incluirlo. Se registra porque corrió el límite de la transacción, no por trámite.

**Seguimiento, no corregido acá:**

- **`E` — mensaje veneno en `AuditProjectionWorker`.** Aborta el lote entero ante el primer
  mensaje que no parsea, y como ese mensaje sigue siendo el primero por `OccurredAt` en cada
  tick, reprime la cola para siempre. El comentario del propio archivo afirma lo contrario.
  **No lo introdujo `CAT-02`**: vive en el módulo `Audit` y es anterior. Lo que `CAT-02` hizo
  fue ampliar el radio, porque agregó un segundo productor a una cola compartida que antes sólo
  alimentaba `Storage`. Merece `SDD-CT`.
- **`F` — `GetProduct` lee con tracking** sin mutar nada. Menor.
- **`ETag`/`If-Match` en productos.** La corrección puso el token **interno**, que cierra el
  *lost update*; exponer el contrato como en `Tenant` y `Membership` obliga al frontend a mandar
  `If-Match` y agrega un `412` al contrato que el spec declaró. Es trabajo cruzado con `CAT-01`.
- **`dotnet format` falla en 22 archivos del repositorio**, casi todos ajenos a `CAT-02`
  (`SessionService.cs`, `TenancyUnitOfWork.cs`, `StorageEndpoints.cs`…). Es sistémico y
  anterior: el comando nunca corrió en CI. Se formatearon **sólo los de `CAT-02`**, que es lo
  que pide el DoD.

**Una lección que vale más que los hallazgos:** la primera prueba RED de `D` **pasó en verde
sin tocar el código**. El lente de riesgo acertó el defecto y erró el escenario — describió un
llamador sin permiso, a quien frena la política del endpoint antes de que el handler exista.
El escenario real era el cruce de tenants. Sin exigir que el RED falle **por el motivo
correcto**, la corrección se habría escrito contra un caso que ya funcionaba, y el defecto
seguiría ahí con una prueba en verde encima.

### 2026-08-11 — Runtime de `CAT-02` y hallazgos

**Qué se hizo:** el runtime que faltaba para el DoD. Los 12 criterios de aceptación
verificados endpoint por endpoint contra la API local con PostgreSQL real, más la consulta
directa a `platform.outbox_messages` y `audit.entries`. Evidencia literal —comandos, códigos
de estado y cuerpos— en el **tramo 5** del spec de `CAT-02`.

**Lo que queda para cerrar `CAT-02a` y `CAT-02b`:** la revisión de riesgo de 4 lentes. Nada
más.

**Cómo levantar la API con el stub, porque cuesta una vuelta descubrirlo:**
`dotnet run --project src/Api --no-launch-profile`, con `ASPNETCORE_ENVIRONMENT`,
`ASPNETCORE_URLS` y `Authentication__UseDevelopmentStub=true` en el entorno. Sin
`--no-launch-profile` los dos perfiles de `launchSettings.json` fijan el stub en `false` y
ganan sobre el shell: la API arranca con auth real y todo devuelve `401`.

#### Tres hallazgos, ninguno bloqueante de `CAT-02`

1. ~~**`appsettings.json` declara una base que no existe.**~~ **Corregido el 2026-08-11 por
   indicación del owner.** Decía `Database=dev_lulo_crm2`; la real es `dev_lulo_crm_v2` y
   llega por user-secrets. Al mirarlo de cerca resultó peor de lo reportado: el valor
   **commiteado** era `Database=dev_lulo_crm`, que tampoco coincide con `compose.yaml` —el
   compose de este repo crea `qep` en `5432`—, así que estaba roto contra la propia
   infraestructura local del repositorio, no sólo contra la máquina del developer.

   **Qué se hizo:** se quitó el bloque `ConnectionStrings` de `src/Api/appsettings.json`. La
   clave pasa a ser requerida y se provee por user-secrets en local y por
   `ConnectionStrings__QepDatabase` en k8s. Ausente, los seis `AddXInfrastructure` fallan al
   arrancar con `InvalidOperationException: Connection string 'QepDatabase' is required.`

   **Por qué se borró en vez de vaciarse:** el guard es `?? throw`, sólo contra `null`. Con
   `""` la cadena vacía lo atraviesa y el error lo termina tirando Npgsql, sin decir qué
   falta.

   Se actualizaron también `README.md` (paso de `user-secrets` en Ejecución local y la tabla
   de configuración) y `CLAUDE.md`, que describían el estado viejo.

   **Verificado con arranque real, las dos mitades:**

   - *Con el secreto presente* (`Development`): `dotnet build Backend.slnx` →
     `Compilación correcta. 0 Advertencia(s), 0 Errores`. La API arranca, los **seis** módulos
     registran `No migrations were applied. The database is already up to date.` —lo que exige
     conexión real, no sólo configuración—, `Now listening on: http://localhost:5000`,
     `/health/live` → `200`, y `GET T/products?search=cat02b` → `200` con el producto de la
     corrida anterior.
   - *Sin el secreto* (`ASPNETCORE_ENVIRONMENT=Staging`, donde los user-secrets no se cargan):

     ```txt
     Unhandled exception. System.InvalidOperationException: Connection string 'QepDatabase' is required.
     ```

     Es el mensaje exacto que el `README` promete. Falla en `AddAuditInfrastructure`
     (`QepServiceCollectionExtensions.cs:102`), que corre **antes** de `AddAuthentication`
     (línea 110): por eso el error que ve el developer es el de la cadena de conexión y no el
     de la audiencia JWT.

   **Deuda de método, declarada:** este arreglo **no pertenece a ningún slice**. Se hizo por
   pedido directo del owner mientras `CAT-02b` seguía activo. Queda como fila a reconocer
   —junto con el `SDD-CT-19` que este hallazgo merece— cuando se abra el slice de
   configuración tipada, que ya lo tenía en su alcance.
2. **El mapa `errors` sale en el idioma del sistema operativo del servidor.** Observado:
   `"'Name' no debería estar vacío."`. El `code` es contrato estable; el texto no. Importa
   para el frontend, que hoy podría estar mostrándolo.
3. **El `search` de productos ignora mayúsculas pero no acentos.** `valvula` no encuentra
   `Válvula`. `CA-CAT-02-10` sólo exige lo primero, así que el criterio pasa. Observación de
   producto, no defecto del slice.

#### Lo que se commiteó aparte, y una advertencia sobre ese commit

Los cambios de traducción que estaban sin commitear se cerraron en **`ccd2eca`**
(`chore(i18n): traducir al español los comentarios del código`). La edición local de la
cadena de conexión (`5433`/`dev_lulo_crm2`) se descartó, no se commiteó.

**Advertencia para quien lea el historial:** ese commit no es sólo i18n. Arrastra un cambio
funcional —la eliminación del bloque `AddUserSecrets` / `IsEnvironment("Local")` de
`Program.cs`, 43 líneas— que su mensaje no menciona. Buscar por qué desapareció ese bloque
mirando mensajes de commit no lo va a encontrar. Queda anotado acá porque el historial es la
única otra forma de saberlo.

#### Datos de prueba que quedaron

Cuatro productos en `catalog.products` de `dev_lulo_crm_v2` (códigos `VS-001` y `CAT02B-*`,
en los tenants `…00a1` y `…00b1`), con sus 6 filas de outbox ya proyectadas. No se borraron:
no hay endpoint de baja y un `DELETE` por SQL sobre la base del developer no es de esta
sesión.

### 2026-08-10 — Apertura de este ledger

Creado al ejecutar `SDD-ADR-08`, que reparte `sdd/` por repositorio porque **backend y
frontend los llevan developers distintos, cada uno a su ritmo**. Antes, un slice de backend
tenía que commitear su spec en `qep-frontend` y su código acá.

Lo que entró en este repo: `sdd/README.md`, este ledger y el spec de `CAT-02`, movido desde
`qep-frontend/sdd/03-modulos/catalog/slices/`.

Lo que **no** se movió, a propósito: método, requisitos, registro de módulos, ADRs y las
fichas y gates de módulo. Siguen siendo autoridad única en `qep-frontend/sdd/`.

Commiteado en `feature/catalog-api`: `968c4a8` el código, `898cc10` este ledger y el spec.
En `qep-frontend` no se tocó nada desde este lado; ahí sigue abierto el merge de
`origin/feature/catalog`, que es del dev de frontend.
