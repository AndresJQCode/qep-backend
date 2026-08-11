# Plan maestro — `qep-backend`

Ledger de continuidad **de este repositorio**. Una sesión de backend empieza aquí, no en el
historial de chat.

> **Última actualización:** 2026-08-11
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
| Slice activo | **`CAT-02b` — escrituras de productos**, código y pruebas listos el 2026-08-10. **Runtime ejecutado el 2026-08-11: 12 de 12 criterios verificados contra la API local.** Falta **sólo la revisión de riesgo de 4 lentes**. `CAT-02` se partió al medir 1043 líneas autoradas; `a` y `b` ejecutan en secuencia, así que el repo sigue con un solo slice activo |
| Último slice completado | Ninguno en este ledger. La historia previa de backend —`AUTH-04`, `AUTH-05`, `AUTH-11`— vive en el ledger del frontend: eran slices de dos repos con un solo spec, y `SDD-ADR-08` decidió **no partirlos retroactivamente** porque están `Complete` y renumerar borra trazabilidad |
| Último commit verificado | `ccd2eca` (`chore(i18n): traducir al español los comentarios del código` — **contiene además un cambio funcional en `Program.cs` que su mensaje no declara**; ver Handoff). Antes: `797c099` (`docs(CAT-02b)`), `3c2c9ec` (`feat(CAT-02b)`), `968c4a8` (`feat(CAT-02)`), `594ee11` (`docs(SDD-ADR-08,CAT-02)`). Rama **`feature/catalog-api`**; se creó rama en vez de commitear sobre `main`, que es la rama por defecto de este repo. Sin commitear al cierre: la evidencia del runtime, el borrado de `ConnectionStrings` y el glosario del `README.md` |
| Decisiones abiertas | Ninguna. **`DECISIÓN-PENDIENTE-CAT-04` cerrada el 2026-08-10 por el owner:** el `code` de producto es único por tenant, con `IX_products_tenant_code` y traducción a `422 catalog.product.code_taken` en Infrastructure |
| Contradicciones abiertas | `SDD-CT-14` — parcialmente cerrada: siguen fallando 5 pruebas de `RealAuthenticationApiTests` con `Expected: Created / Actual: Unauthorized`. `SDD-CT-07` — un registro de tenant fallido deja un usuario huérfano en `identity.users`; no bloquea, pide slice de mantenimiento. `SDD-CT-08` — `500` intermitente en `POST /auth/register-tenant`, no reproducida. Las tres se registran en el ledger del frontend, que sigue siendo el registro de contradicciones del producto |

### Próxima acción ejecutable

**Ejecutar el ciclo TDD de `CAT-02`.** Sin decisiones abiertas: el gate está cerrado, el
contrato y los permisos están ratificados, y `code` es único por tenant.

Orden de trabajo, con RED antes que GREEN en cada tramo:

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

**Lo que queda de `CAT-02` para el DoD:** ~~runtime del owner contra la API local~~ **hecho el
2026-08-11, 12 de 12 criterios** (evidencia literal en el tramo 5 del spec) — queda **sólo la
revisión de riesgo de 4 lentes**. Sin ella, ni `CAT-02a` ni `CAT-02b` pasan a `Complete`.

**Antes de cualquier `dotnet build`, `dotnet test` o comando `ef`: verificar que `Api.exe` no
esté corriendo.** Si lo está, los tres fallan por archivo bloqueado. El nombre del proceso es
`Api.exe` porque `src/Api/Api.csproj` no declara `AssemblyName`.

**La factory de integración debe fijar `Notifications:EmailProvider`** (`SDD-CT-17`): sin eso
las pruebas mueren al arrancar con `OptionsValidationException`, heredando el `infobip` con
credenciales vacías del `appsettings.json`.

## Ledger de slices — Módulo `catalog`

Ficha y gate: `qep-frontend/sdd/03-modulos/catalog/` · Estado del módulo: `En curso` ·
Owner: Andres Jaramillo

| ID | Resultado revisable | Depende de | Estado | Evidencia / commit / PR |
| --- | --- | --- | --- | --- |
| `CAT-02` | **API de productos** — fila padre, ya no es unidad ejecutable | `CAT-00` (gate cerrado) | Partido el 2026-08-10 | Se partió al medir **1043 líneas autoradas** con un endpoint de cinco, contra el umbral de ~400 de `convenciones-de-id.md`. No se renumera nada y los commits que citan `feat(CAT-02)` siguen válidos: el ID padre no se toca. Spec único en [`03-modulos/catalog/slices/CAT-02-api-de-productos.md`](../03-modulos/catalog/slices/CAT-02-api-de-productos.md) |
| `CAT-02a` | Andamiaje del módulo, dominio `Product`, persistencia con `InitialCatalog` y `GET /products` | `CAT-00` | Código listo, runtime verificado | Tres tramos con RED y GREEN literales en el spec. `ArchitectureTests` 16/16, unitarias 13/13, integración 3/3 contra PostgreSQL real; `Tenancy` sin regresión, con los mismos 5 fallos de `SDD-CT-14` verificados por nombre. Commits `968c4a8` y `594ee11`. Runtime cubierto por el tramo 5 el 2026-08-11. **Falta la revisión de riesgo de 4 lentes**, así que no es `Complete` |
| `CAT-02b` | Escrituras: `GET` por id, `POST`, `PUT`, `deactivate`, validadores, traducción del índice único y pruebas de auditoría y outbox | `CAT-02a` | **In Progress** | Abierto el 2026-08-10, código en `3c2c9ec`. Cubre `CA-CAT-02-03` a `-09`, `-11`, `-12` y las mitades pendientes de `-01` y `-10`. **Runtime del 2026-08-11: los 12 criterios verificados contra la API local**, con `422 catalog.product.code_taken` confirmado en vivo —el `500` de `SDD-CT-06` que este slice existía para cerrar— y la atomicidad del outbox probada por lo que **no** dejó rastro: el `403` y los tres `422` no escribieron fila. **Falta la revisión de riesgo de 4 lentes** |
| `CAT-03` | API de tasas de impuesto | `CAT-02` | Pending | Sin spec todavía. Se separa de `CAT-02` porque es otro recurso, con otros permisos y otra migración, y juntos pasarían holgado el umbral de 400 líneas. El porcentaje es **entero de 0 decimales** (`P-008`, decidido por el owner el 2026-08-10): no admite retenciones con fracción, y eso está declarado como límite de alcance en el gate |

## Handoff

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
