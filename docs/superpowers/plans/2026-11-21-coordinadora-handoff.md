# Integración con Coordinadora — traspaso

Fecha: 2026-11-21 · Rama: `claude/sweet-dijkstra-vo3em9` (desde `develop` en `143cca5`) · Spec:
[`2026-11-21-integracion-coordinadora-design.md`](../specs/2026-11-21-integracion-coordinadora-design.md)

Este documento dice **qué quedó hecho, qué se verificó de verdad, qué no, y por dónde seguir**. La autoridad es el código; si algo
de aquí no coincide con él, gana el código y se corrige este archivo.

## Estado en una línea

Backend construido y revisado (build y pruebas unitarias en verde), **sin una sola llamada real a Coordinadora, sin migraciones
aplicadas a una base, sin arrancar el host y sin pruebas de integración**. Nada está mergeado ni desplegado.

## Qué hay en la rama

Cinco commits sobre `develop`:

| Commit | Qué |
| --- | --- |
| `ce977f5` | Spec, clave de módulo `coordinadora` (Tenancy), proveedor `coordinadora` (Integrations) con su probador, y las dos migraciones de `CHECK` |
| `b141184` | Módulo `Coordinadora` (4 capas), puerto `IExternalOrderShipping` en Quotations, adaptadores de Bootstrapper, cableado, permisos y pruebas |
| `7e7ef73` | README, `.claude/rules/coordinadora.md` y su fila en `CLAUDE.md` |
| `8f33da8` | El spec deja de decir que había pruebas de integración escritas: no las hay |
| `0b28cf7` | Cuatro correcciones de la revisión independiente (ver «Decisiones de seguridad») |

Piezas, por si vas a navegar:

- **Dominio:** `CoordinadoraSettings` (configuración por conexión, versión para `If-Match`) y `CoordinadoraShipment` (una fila por
  intento de guía). Estados: `Pending`, `Created`, `Rejected`, `Unknown`, `Failed`, `Cancelled`.
- **Armador:** `CoordinadoraGuideBuilder` junta todos los faltantes (`422 coordinadora.shipment.not_ready`) y arma el `p` de la guía y
  el de la cotización. Es puro y es lo más probado.
- **Cliente:** `CoordinadoraApiClient` (AGS por JSON-RPC, guías por SOAP 1.2) y `CoordinadoraSoap` (sobre y lectura de respuesta).
- **Rutas:** `/api/v1/tenants/{tenantId}/coordinadora/connections/{connectionId}/settings` (GET y PUT con `If-Match`) y
  `/api/v1/tenants/{tenantId}/orders/{orderId}/coordinadora-shipment` (`GET /`, `/preview`, `/label`; `POST /`, `/quote`, `/track`,
  `/cancel`, `/resolve`). Tabla completa en el README § Integración con Coordinadora.
- **Datos:** esquema `coordinadora` (`settings`, `shipments`), migración `InitialCoordinadora`. Índice único parcial
  `IX_shipments_tenant_order_live` (una guía viva por pedido).

## Qué corrió y qué no

**Corrió**, con SDK 10.0.115 en el contenedor (no el 10.0.4xx del `global.json`):

- `dotnet build` del host `Api`, sin errores ni avisos.
- Pruebas unitarias: Coordinadora (77), Integrations (799), Tenancy (475), `ArchitectureTests` (306).
- Sólo las **pruebas nuevas** de Quotations (`ExternalOrderShippingServiceTests`, 4) y de Bootstrapper (`CoordinadoraOrderSourceTests`, 2
  más las de `SiigoOrderSource`). **La suite completa de Quotations unitaria no se corrió.**
- `dotnet format --verify-no-changes` sobre los archivos tocados.

**No corrió:**

1. **Ninguna llamada a Coordinadora**, ni sandbox ni producción. El portal `developers.coordinadora.com` no resolvía desde el
   contenedor y sus esquemas REST no están publicados; el contrato sale del cliente PHP público `saulmoralespa/coordinadora-webservice-php`
   y del README del repo (ver spec §3). Las cinco dudas V-1 a V-5 siguen abiertas.
2. **Migraciones contra Postgres.** Las tres (`AddCoordinadoraModuleKey`, `AddCoordinadoraProvider`, `InitialCoordinadora`) se generaron
   con `dotnet-ef` 10.0.15 y el modelo no tiene cambios pendientes, pero nunca se aplicaron.
3. **El arranque del host** (`ValidateOnStart` de `Coordinadora`, `MigrateAsync` dentro del candado de arranque).
4. **Toda prueba de integración** (no hay Docker en el contenedor). Y **no se escribieron las del módulo.**
5. Las suites de Tenancy e Integrations **de integración**: sus listas de módulos y proveedores se actualizaron a mano y no corrieron.
6. En `Bootstrapper.UnitTests` fallan, con `Failed to connect to Docker endpoint`, `CatalogVariantUsageCheckerTests` y
   `CustomerMergeReferencesTests`. **No son de esta rama:** necesitan Docker.

## Lo primero que haría en tu máquina

```powershell
git fetch origin claude/sweet-dijkstra-vo3em9
git switch claude/sweet-dijkstra-vo3em9
dotnet restore --force-evaluate
git status --short
```

1. **Lock files.** Los de los proyectos nuevos y los de `Api`, `Bootstrapper`, `ArchitectureTests`, `Bootstrapper.UnitTests` se generaron
   con SDK 10.0.115. El `Dockerfile` restaura con `--locked-mode` y falla con `NU1004` si no coinciden. Si el `restore --force-evaluate`
   los cambia, commitéalos aparte (`chore(coordinadora): regenerate lock files`).
2. **Migraciones contra una base real.** Con la base del `compose.yaml` y `$env:Seed__Enabled = "false"`, arranca el host y comprueba
   que las tres migraciones aplican y que el pod no falla por `Coordinadora:*`. Revisa también el `Down` de las dos que ensanchan un
   `CHECK` (borran las filas del módulo y del proveedor, a propósito).
3. **Suites completas por módulo tocado**, no la solución entera: Quotations, Tenancy, Integrations, Bootstrapper y `ArchitectureTests`,
   incluidas las de integración. Comandos en [README § Verificación](../../../README.md#verificación).
4. **Escribir `Modules.Coordinadora.IntegrationTests`** (hoy no existe el proyecto; el spec §11 lo lista como pendiente): harness
   calcado de `SiigoApiHarness`, un `FakeCoordinadoraApi` como handler primario del cliente nombrado `coordinadora`, y como mínimo:
   - 403 (no 404) con tenant ajeno o sin permiso, y `tenancy.module_not_enabled` con el módulo apagado;
   - `PUT settings` con `If-Match` (428 sin él, 412 con versión vieja, ETag en la respuesta);
   - generar una guía: fila `Pending` commiteada antes de la llamada, `Created` con número, y un segundo `POST` que **no** llama de nuevo;
   - timeout o 5xx → `Unknown`, y `resolve` con `exists` y `missing`;
   - dos `POST` simultáneos → una sola guía (el candado y `IX_shipments_tenant_order_live`);
   - un pedido que no está listo → `422 not_ready` con `details.missing` y **cero llamadas** al doble.
   Añádelo a `Backend.slnx` y a `ArchitectureTests.csproj` si hace falta.

## Decisiones mías que el owner debe confirmar (spec §9)

Asumí lo siguiente para poder construir; ninguna fue aprobada:

| | Decisión | Si se revierte |
| --- | --- | --- |
| P-1 | `coordinadora` fuera de `DefaultForNewTenants` (se prende por tenant) | Una línea en `TenantModuleKey` y su prueba |
| P-2 | La guía **no** se escribe en el pedido ni sale en el Excel de pedidos | Columna nueva en Quotations con su spec y migración |
| P-3 | El flete cotizado sólo se muestra; no reemplaza el flete manual del pedido | Un botón que llene `FreightAmount`; va en Quotations |
| P-4 | `advisor` genera guías de fábrica (como prepara pedidos); configurar es sólo de `admin` | Quitar `ShipmentManage` del rol y su prueba en `CompositionRootTests` |
| P-5 | El recaudo contraentrega nace apagado (`collect_on_delivery = false`) | Cambiar el default de la columna y el agregado |

Otras que tomé y que conviene mirar: **una conexión de Coordinadora por tenant** (`maxConnections: 1`, para no pedir al pedido que
elija cuál); el **remitente vive en la configuración** y no en la empresa de QEP; el **probador de la conexión sólo verifica alcance**
(`Cotizador.departamentos`, sin credenciales) porque ninguna lectura barata las valida.

## Lo que se cierra con la primera guía real (V-1 a V-5)

Cada duda vive en un solo lugar del cliente, así que corregirla no toca los handlers:

| | Qué | Dónde |
| --- | --- | --- |
| V-1 | El `xmlns` del cuerpo SOAP de guías y que el servidor acepte SOAP 1.2 | `Coordinadora:GuidesNamespace`, `CoordinadoraSoap.Build` |
| V-2 | El campo del PDF en `Guias_generarGuia` e `imprimirRotulos` | `CoordinadoraSoap.Reply.Pdf` (hoy toma cualquier texto que empiece por `JVBERi0`) |
| V-3 | El tipo de `recaudos` (hoy va como texto) | `CoordinadoraGuideBuilder.GuideBody` |
| V-4 | Que el AGS acepte `Cotizador.cotizar` por JSON-RPC con `params: {"p": …}` y la forma de su `result` | `CoordinadoraApiClient.QuoteAsync`, `FreightTotalOf` |
| V-5 | Los campos de `Guias_rastreoSimple` | `CoordinadoraApiClient.TrackAsync` |

Orden recomendado con credenciales de sandbox (cambia `Coordinadora:AgsUrl` y `Coordinadora:GuidesUrl` en el ambiente): primero
`POST …/quote` (confirma la apikey y la clave del AGS y V-4), después `POST …/` (usuario y clave de guías, V-1, V-2, V-3), después
`label`, `track` y `cancel`. Con la **producción** de Coordinadora, cada guía es un servicio que se cobra y el recaudo es una
obligación financiera: no la uses para probar.

## Decisiones de seguridad que no se deben «simplificar»

Están en `.claude/rules/coordinadora.md`; las repito porque son las que más se rompen sin que nada se ponga rojo:

- **Una guía nunca se reenvía.** Coordinadora no tiene clave de idempotencia. Un timeout, un 5xx o un Fault de servidor
  (`Receiver`/`Server`) deja la fila `Unknown` y la resuelve una persona. **Sólo un Fault `Sender`/`Client` es un rechazo** que libera el
  pedido (`CoordinadoraSoap.IsClientFault`).
- La llamada a Coordinadora y el guardado de su resultado corren con `CancellationToken.None` después del commit de la fila `Pending`:
  si la persona cierra la pestaña, el número de guía no se pierde.
- `request_json`, `error_detail` y `label_pdf` llevan datos del destinatario y no salen en ningún `GET` salvo `…/preview` y `…/label`.
  El rastreo aplanado (V-5) deja sin valor los elementos que nombren al destinatario.
- Los dos clientes HTTP (`coordinadora` e `integrations.coordinadora`) no tienen loggers ni siguen redirecciones; lo amarra
  `CompositionRootTests.TheCoordinadoraHttpClientsHaveNoLoggersAndNoRedirects`.
- Los dos índices de `shipments` sobre `(tenant_id, order_id)` se declaran **con nombre**: `CoordinadoraUnitOfWork` traduce el índice
  vivo por nombre a `409 coordinadora.shipment.already_exists`.

## Pendiente fuera de esta rama

- **Frontend (`qep-frontend`)**: spec propio (pantalla de configuración de la conexión, tarjeta «Envío con Coordinadora» en el detalle del
  pedido, tarjeta del proveedor en Integraciones). Antes de eso, revisa en qué rama está el frontend (`git -C ../qep-frontend
  branch --show-current`). El proveedor nuevo usa `onboarding.kind = "Form"` y la categoría `Shipping`: confirma que el catálogo del
  frontend sabe pintarla.
- **`docs/estado.md`**: no se tocó, a propósito (las ramas de feature no lo editan). Al mergear a `develop` hay que agregar la fila de
  Coordinadora y el Registro, con evidencia en el código.
- **Merge:** a `develop`, nunca a `main`, y sólo después de V-1 a V-5 o con el módulo apagado para todos los tenants (está apagado de
  fábrica: nace sin fila en `tenant_modules`).
- Recogidas, despachos agrupados, conciliación del recaudo, peso y medidas por producto en Catalog y seguimiento automático quedan
  fuera por diseño (spec §2).

## Cosas del entorno de esta sesión que no debes arrastrar

- No se commiteó ningún cambio de `global.json`. En el contenedor se probó con 10.0.115 cambiándolo temporalmente y se restauró con
  `git checkout global.json`; si ves un diff en ese archivo, no es de la rama.
- Los commits no llevan atribución de IA, como pide `CLAUDE.md`.
