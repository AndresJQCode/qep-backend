# Excel de pedidos: indicativo del teléfono — diseño (backend)

Fecha: 2026-11-25 · Repo: `qep-backend` · Rama: `feature/telefono-pais` · Estado: **aprobado** (owner, 2026-11-25). Está
implementado y mergeado en `develop` el 2026-11-25 (merge `da98b8e20`), pendiente de despliegue; las pruebas quedaron escritas,
pero no se han corrido.

## Decisión

Cada tenant elige si el Excel de pedidos exporta los teléfonos con el indicativo del país. Es un interruptor del layout de
exportación, junto a `SheetName`: propiedad de dominio `IncludePhoneDialCode`, **encendido por defecto**. Un tenant sin layout
guardado y toda fila que ya existía siguen exportando como antes.

## Contrato

- `GET /api/v1/tenants/{tenantId}/orders-export-layout` devuelve `includePhoneDialCode` (bool), `true` sin nada guardado.
- `PUT` acepta `includePhoneDialCode` como bool opcional: ausente o `null` conserva el actual, así que un frontend anterior a
  este campo no lo cambia.
- Permisos, versión/`If-Match`, auditoría (`quotations.orders_export_layout.updated`) y no-op: los mismos de `SheetName`.
  Guardar sólo el interruptor es un cambio.

## Persistencia

Columna `quotations.orders_export_layouts.include_phone_dial_code`, `boolean NOT NULL DEFAULT true` (migración
`AddOrdersExportLayoutPhoneDialCode`). Es aditiva: un pod viejo que inserte durante el rollout recibe el default. EF la escribe
siempre (`ValueGeneratedNever`); sin eso omitiría un `false` al insertar y la base guardaría `true`.

## Comportamiento de la exportación

Aplica sólo a las columnas del catálogo `phone`, `customer_phone`, `billing_phone` y `shipping_phone`. Las columnas fijas no se
tocan, y lo guardado en el pedido y en el cliente no cambia.

| Interruptor | Valor guardado                               | Celda                                        |
| ----------- | -------------------------------------------- | -------------------------------------------- |
| Encendido   | cualquiera                                   | el valor guardado                            |
| Apagado     | empieza por `+` y se puede partir            | número nacional (`+573001234567` → `3001234567`, `+34612345678` → `612345678`) |
| Apagado     | empieza por `+` y no se puede partir         | el valor guardado                            |
| Apagado     | sin `+`                                      | el valor guardado                            |
| Apagado     | vacío                                        | vacío                                        |

La celda sigue siendo de texto en todos los casos.

Con el interruptor apagado nunca se pierden dígitos. «No se puede partir», y por tanto sale el valor guardado, cuando:

- `libphonenumber` no lo reconoce como un número válido;
- es un número no geográfico, de la región `001` (`+80012345678`);
- los dígitos del valor guardado no son exactamente el indicativo seguido del número nacional (`+576015550000 ext 123`,
  donde la extensión se descarta al partir).

El cero inicial de Italia hace parte del número nacional, así que `+390612345678` sí se parte (`0612345678`).

## Piezas

El partido usa `libphonenumber` a través de `IPhoneNumberSplitter` (Customers.Application), sin región de respaldo. Quotations no
referencia a Customers: declara el puerto `IQuotationPhoneNumbers` y Bootstrapper lo implementa con `QuotationPhoneNumbers`. Los
teléfonos no se registran en logs.
