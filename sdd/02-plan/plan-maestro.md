# Plan maestro — `qep-backend`

Ledger de continuidad **de este repositorio**. Una sesión de backend empieza aquí, no en el
historial de chat.

> **Última actualización:** 2026-08-10
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
| Slice activo | **`CAT-02` — API de productos**, `In Progress` desde el 2026-08-10. Único activo en este repo |
| Último slice completado | Ninguno en este ledger. La historia previa de backend —`AUTH-04`, `AUTH-05`, `AUTH-11`— vive en el ledger del frontend: eran slices de dos repos con un solo spec, y `SDD-ADR-08` decidió **no partirlos retroactivamente** porque están `Complete` y renumerar borra trazabilidad |
| Último commit verificado | `968c4a8` (`feat(CAT-02)`) y `898cc10` (`docs(SDD-ADR-08,CAT-02)`), rama **`feature/catalog-api`**, árbol limpio. Se creó rama en vez de commitear sobre `main`, que es la rama por defecto de este repo |
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
4. **Casos de uso y endpoints**, uno a uno, con integración contra PostgreSQL real.

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
| `CAT-02` | **API de productos**: módulo backend nuevo con sus cuatro capas, permisos propios, auditoría por outbox y aislamiento entre tenants | `CAT-00` (gate cerrado) | **In Progress** | Desbloqueado el 2026-08-10 al cerrar `DECISIÓN-PENDIENTE-CAT-04` (`code` único por tenant). Spec en [`03-modulos/catalog/slices/CAT-02-api-de-productos.md`](../03-modulos/catalog/slices/CAT-02-api-de-productos.md), creada el 2026-08-10. Contrato y permisos vienen ratificados del gate: rutas `/api/v1/tenants/{tenantId:guid}/catalog/products` y permisos `catalog.product.read` / `catalog.product.manage`. **Estimado ~500 líneas, por encima del umbral de 400**: se mide al implementar y, si se confirma, se parte en `CAT-02a` (andamiaje + `GET`) y `CAT-02b` (escrituras). **Tres tramos en verde el 2026-08-10** — andamiaje, dominio, y persistencia con el `GET` de productos—, cada uno con su RED y GREEN literales en el spec. Falta el resto de los endpoints. Commits `968c4a8` (código) y `898cc10` (spec y ledger) |
| `CAT-03` | API de tasas de impuesto | `CAT-02` | Pending | Sin spec todavía. Se separa de `CAT-02` porque es otro recurso, con otros permisos y otra migración, y juntos pasarían holgado el umbral de 400 líneas. El porcentaje es **entero de 0 decimales** (`P-008`, decidido por el owner el 2026-08-10): no admite retenciones con fracción, y eso está declarado como límite de alcance en el gate |

## Handoff

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
