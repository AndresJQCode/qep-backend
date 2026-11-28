# WMS: la zona y el muelle de recepción se protegen por identidad (G-11)

Fecha: 2026-11-27. Estado: **aprobado por el owner en conversación (2026-11-27).** Tarjeta: <https://trello.com/c/ck9h9QAI>
(brecha G-11 de la consola web).

## Por qué

`Warehouse.RemoveLocation` protege la zona de recepción y su muelle **por código** (`RECEIVING`, `REC-01`).
`UpdateLocation` deja cambiar ese código, y después de renombrarlas se pueden borrar: la bodega queda sin dónde recibir.
Una regla de negocio atada a un texto que la persona edita falla en cuanto alguien cambia ese texto.

## Decisión del owner

Proteger por identidad, no por código. Se descartó bloquear el renombre: tapa el síntoma y deja la regla atada al código.
El precedente es `Warehouse.GeneralLocationId`.

## Diseño

- **Dominio.** `Warehouse` gana `ReceivingZoneId` y `ReceivingDockId` (`LocationId?`), que `Warehouse.Create` llena con
  las dos ubicaciones que ya crea ahí.
- **Borrado.** `RemoveLocation` rechaza esas dos ubicaciones por id, con el mismo `wms.location.virtual_immutable` de
  hoy, y deja de mirar el código. Así una ubicación nueva que alguien llame `REC-01` ya no queda protegida por
  accidente.
- **Renombre.** Sigue permitido: `LocationTreeTests.TheReceivingDockCanBeUpdated` sigue siendo válida.
- **Persistencia.** Dos columnas `uuid` nulas en la tabla de bodegas, configuradas como `GeneralLocationId` en
  `WmsDbContext`.
  - Las columnas son nulas porque no hay datos en producción y no se rellenan filas viejas (decisión del owner).
  - Una bodega local creada antes de esta migración queda sin la protección; se resuelve recreándola.
- **Lo que no cambia:** el contrato HTTP (`WarehouseDto` no expone los ids nuevos), las rutas y los códigos de error.
- **Para después:** la recepción (T3) busca el muelle por `ReceivingDockId`, no por `REC-01`. Queda anotado en su tarjeta.

## Pruebas (TDD)

- `WarehouseTests`:
  - `Create` llena los dos ids con la zona y el muelle;
  - borrar cualquiera de los dos por id da `wms.location.virtual_immutable`, también después de renombrarlos;
  - una ubicación nueva con código `REC-01` en otra bodega o renombrada no queda protegida (con la regla de unicidad,
    eso se prueba renombrando el muelle original y creando otra ubicación con `REC-01`).
- Integración de la migración: la prueba existente de migraciones del módulo, si la hay, más el GET de la bodega sin
  cambios.
