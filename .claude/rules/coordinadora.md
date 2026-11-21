---
paths:
  - "src/Modules/Coordinadora/**"
  - "tests/Modules/Coordinadora/**"
  - "src/Bootstrapper/Coordinadora*.cs"
  - "src/Modules/Integrations/**/Coordinadora/**"
---

# Coordinadora (guías de un pedido)

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **El contrato de Coordinadora es el legacy (AGS por JSON-RPC y guías por SOAP 1.2), y cinco cosas de él están sin verificar contra
  una cuenta real** (spec 2026-11-21 §3 y §9, V-1 a V-5): el `xmlns` del cuerpo SOAP de guías (`Coordinadora:GuidesNamespace`; vacío =
  la URL de guías), el campo del PDF en la respuesta de `Guias_generarGuia` e `imprimirRotulos` (`CoordinadoraSoap.Reply.Pdf` toma
  cualquier texto que empiece por `JVBERi0`), el tipo de `recaudos` (va como texto), que el AGS acepte `Cotizador.cotizar` con
  `params: {"p": {…}}`, y los campos de `Guias_rastreoSimple`. Cada una vive en un solo lugar: si la primera guía real falla, se
  corrige ahí y se actualiza el spec, no se improvisa en el handler. El portal `developers.coordinadora.com` (REST, OAuth2) no se
  usó: no se pudo leer y sus esquemas no están publicados.
- **No hay reenvío de una guía.** Coordinadora no tiene clave de idempotencia: un timeout o un 5xx al crear deja la fila en `Unknown`
  y la resuelve una persona (`POST …/resolve`), nunca un reintento automático. Una `Pending` cuyo reclamo de 2 min venció pasa a
  `Unknown` al leerla (`ExpireIfStale`). «Reintentar la Pending» generaría dos guías que Coordinadora cobra. Por lo mismo, **sólo un
  SOAP Fault del lado del cliente (`Sender`/`Client`) es un rechazo** que libera el pedido; un `Receiver`/`Server` o un código propio pudo
  llegar después de crear la guía y queda `Unknown` (`CoordinadoraSoap.IsClientFault`). Y la llamada posterior al commit corre con
  `CancellationToken.None`: si el request se cancela mientras Coordinadora contesta, el número de guía se guarda igual.
- **Una cuenta de Coordinadora por tenant** (`maxConnections: 1` en el proveedor): el módulo toma «la conexión del tenant» sin que el
  pedido elija cuál. Si el proveedor pasa a admitir varias, `CoordinadoraShipmentSupport.LoadAsync` necesita un criterio para elegir.
- **El probador de la conexión no verifica credenciales:** llama `Cotizador.departamentos`, que no las lleva. Ninguna lectura barata
  del AGS ni de guías las valida sin cotizar o generar algo con una cuenta y un producto que viven en la configuración del módulo.
  Las credenciales se confirman con la primera cotización (apikey/clave) y la primera guía (usuario/clave de guías).
- **El recaudo contraentrega nace apagado** (`collect_on_delivery`): un recaudo es una obligación financiera y no hay cuenta de
  pruebas. Una prueba o un seed nunca lo prende. Con el interruptor prendido, `recaudos` = `NetTotal` sólo si el pedido es contraentrega.
- **`request_json`, `error_detail` y `label_pdf` llevan datos del destinatario** y sólo los leen los handlers del módulo: ningún `GET`
  devuelve la petición ni el rótulo salvo `…/label` (con `coordinadora.shipment.manage`). El cliente HTTP `coordinadora` y el del
  probador `integrations.coordinadora` no tienen loggers (`RemoveAllLoggers`) ni siguen redirecciones: las claves viajan en el cuerpo
  (`CompositionRootTests.TheCoordinadoraHttpClientsHaveNoLoggersAndNoRedirects`).
- **Los dos índices de `coordinadora.shipments` van sobre las mismas columnas y se declaran con nombre.** Un segundo `HasIndex` sin
  nombre sobre `(tenant_id, order_id)` es el mismo índice y lo renombra, y `CoordinadoraUnitOfWork` traduce
  `IX_shipments_tenant_order_live` por nombre a `409 coordinadora.shipment.already_exists`: renombrarlo compila y deja la carrera en 500.
- **La ciudad va como DIVIPOLA + `000`** (`CoordinadoraCityRef.CoordinadoraCode`) y sólo si `City.CoordinadoraName` no es nulo: una
  ciudad que Coordinadora no lista es `recipient_city_not_served`, no un código inventado. El snapshot que decide eso es
  `coordinadora-cities.json` (README § Nombres de ciudad de Coordinadora).
- **El pedido no cambia por tener guía** (spec §2 y §9, P-2): el puerto `IExternalOrderShipping` de Quotations es de sólo lectura y
  `Order` no tiene número de guía. Si el owner decide escribirlo en el pedido o sacarlo en el Excel, es una columna nueva de
  Quotations con su spec, no un atajo desde este módulo.
