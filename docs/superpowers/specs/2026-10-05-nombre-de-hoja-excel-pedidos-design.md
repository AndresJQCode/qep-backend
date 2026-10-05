# Nombre de la hoja del Excel de pedidos

**Fecha:** 2026-10-05
**Módulos:** Quotations (backend) — tenant-settings (frontend)
**Estado:** aprobado en brainstorming el 2026-10-05
**Origen:** extiende `2026-09-24-homologacion-columnas-excel-pedidos-design.md`

## Problema

El Excel de pedidos sale con una sola hoja llamada `Pedidos`, fija en el código
(`OrdersExportProcessor.SheetName`). El importador del tenant (`ritual-botanico-backend`,
`AddShippingsCommandHandler`) busca la hoja por nombre con `GetSheet("MIGRACION 1")` y rechaza el
archivo si no la encuentra ("El nombre de la hoja de excel debe ser MIGRACION 1"). Hoy el tenant
renombra la hoja a mano en cada exportación, que es justo el retoque que la homologación de
columnas quería eliminar.

## Decisiones

### D1 — El nombre de la hoja es parte del layout

`OrdersExportLayout` gana `SheetName`. Es la misma configuración (cómo lee el ERP el archivo), con
los mismos permisos (`SettingsRead` / `SettingsUpdate`), la misma pantalla y la misma `Version`.
Un agregado o endpoint aparte sería una segunda versión que la pantalla tendría que cuadrar con la
primera sin ganar nada.

### D2 — Columna propia, no dentro del jsonb

`quotations.orders_export_layouts.sheet_name varchar(31) not null`. El jsonb es la lista de
columnas; el nombre de la hoja no es una columna.

### D3 — Default `Pedidos`; las filas existentes no cambian

`OrdersExportLayout.DefaultSheetName = "Pedidos"`. La migración agrega la columna con
`defaultValue: "Pedidos"`, así que todo layout ya guardado sigue exportando `Pedidos`. Sin fila
guardada, `Effective(null)` también da `Pedidos`. Ningún Excel que ya existe cambia de nombre por
desplegar.

### D4 — Reglas de Excel en el dominio

Excel no abre un libro cuya hoja viole sus reglas, y `OpenXmlExportWorkbookWriter` hoy no valida
nada. El nombre se recorta (`Trim`) y es válido si:

- tiene entre 1 y 31 caracteres;
- no contiene ninguno de `[ ] : * ? / \`;
- no empieza ni termina con `'`;
- no es `History` (comparación sin distinguir mayúsculas; Excel lo reserva).

Si no, `QuotationsDomainException` con `quotations.orders_export_layout.sheet_name_invalid`
(422). El validador de FluentValidation aplica las mismas reglas sobre el campo `sheetName`, para
que el 422 traiga el mapa `errors` y la pantalla marque el input (regla del repo: texto libre lleva
validador aunque el dominio valide).

Las tildes **sí** son válidas: Excel las acepta. Que el importador del tenant pida `MIGRACION 1` sin
tilde es asunto de ese importador, no una regla de este dominio.

### D5 — Guardar sólo el nombre es un cambio

`Replace(columns, sheetName, now)`: el chequeo de no-op compara columnas **y** nombre. Hoy compara
sólo columnas, así que cambiar sólo el nombre no subiría la versión, no auditaría y no se guardaría.
El evento de auditoría es el mismo `quotations.orders_export_layout.updated`.

### D6 — Contrato HTTP

- `GET` devuelve `sheetName` (el efectivo) y `defaultSheetName` (`"Pedidos"`). El default viaja por
  la misma razón que `DefaultHeader` por columna (regla BFF): "Restaurar todo" no tiene que saberlo
  de memoria.
- `PUT` acepta `sheetName` **opcional**. `null` o ausente conserva el nombre actual (o el default si
  no hay fila). Así un frontend desplegado antes que este cambio, que no manda el campo, no le borra
  el nombre al tenant al guardar columnas. Una cadena vacía o sólo espacios **no** es "ausente": es
  inválida (D4).
- `If-Match` / `ETag` sin cambios.

### D7 — El processor usa el nombre del layout

`OrdersExportProcessor` deja de usar la constante: toma `SheetName` del layout efectivo y se lo pasa
a `IExportWorkbookWriter.Create`. `QuotationsExportProcessor` (`Cotizaciones`) no se toca.

### D8 — La semilla trae `MIGRACION 1`

`SeedOrdersExportLayoutAsync` ya arma el layout homologado al ERP de Origen Botánico, así que el
layout sembrado sale con `MIGRACION 1`. La semilla sólo crea: el tenant de producción ya tiene
layout guardado y su nombre se cambia **a mano desde la pantalla** (mismo patrón que "Ciudad
Coordinadora" y "Forma de pago N").

### D9 — Frontend

En la pantalla de columnas del Excel de pedidos (`features/tenant-settings`):

- Campo de texto "Nombre de la hoja" arriba de la lista de columnas, con ayuda corta: "Así se llama
  la hoja dentro del archivo. Algunos programas la buscan por nombre."
- Validación en cliente con las reglas de D4 y mensaje en tuteo. El de `sheet_name_invalid`:
  "El nombre de la hoja debe tener entre 1 y 31 caracteres, sin [ ] : * ? / \ ni comillas al inicio
  o al final."
- Cambiar el nombre cuenta como cambio sin guardar y entra en la detección de conflicto de versión.
- "Restaurar todo por defecto" restaura también el nombre a `defaultSheetName`.
- El `PUT` manda siempre `sheetName`.
- La vista previa, si muestra el archivo, muestra el nombre de la hoja.

## Fuera de alcance

- Nombre de hoja configurable para el Excel de cotizaciones.
- Nombre del archivo descargado.
- Cambiar `ritual-botanico-backend` para que acepte otro nombre de hoja.

## Despliegue

Backend primero (la migración agrega la columna con default; el frontend viejo sigue funcionando por
D6). Después el frontend. Luego, en producción, el admin del tenant pone `MIGRACION 1` en la
pantalla.

## Pruebas que cambian

Las que hoy asertan el literal `Pedidos` siguen pasando sin tocarse, porque es el default:
`OrdersExportProcessorTests:47`, `OrderExportApiTests:168`,
`OpenXmlExportWorkbookWriterTests:240-310`. Las de semilla (`QuotationsSeedTests`) sí cambian:
el layout sembrado sale con `MIGRACION 1`.
