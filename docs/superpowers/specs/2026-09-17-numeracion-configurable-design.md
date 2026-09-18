# Numeración de documentos configurable por tenant — diseño

**Fecha:** 2026-09-17 (diseño acordado con el owner el mismo día)
**Rama:** por crear desde `develop` (backend). No cambia la forma de la API: `qep-frontend` no se toca.
**Depende de:** [`2026-09-17-fechas-locales-del-tenant-design.md`](2026-09-17-fechas-locales-del-tenant-design.md).
Cuando el formato incluye el año, ese año sale de `calendar.Today.Year`, no de UTC.
**Datos existentes:** sin backfill. Lo ya emitido queda como está; el formato nuevo aplica a lo que se
emita de aquí en adelante.

## Contexto

El número de pedido y el de cotización están fijos en código:

- `OrderNumberFormatter.Format` arma `PED-{year}-{sequence:D4}` (`OrderNumberFormatter.cs:9-11`).
- `QuotationNumberFormatter.Format` arma `QUO-{year}-{sequence:D4}` (`QuotationNumberFormatter.cs:9-12`).
- El consecutivo lo emite un contador atómico por (tenant, año) con `UPDATE ... RETURNING`
  (`OrderNumberGenerator.cs`, `QuotationNumberGenerator.cs`), sobre
  `quotations.order_number_counters` y `quotations.quotation_number_counters`.
- El dominio solo exige que el número llegue y quepa: `Order.OrderNumberMaxLength` y
  `Quotation.QuotationNumberMaxLength` valen **20** (`Order.cs:18`, `Quotation.cs:25`), y
  `IX_orders_tenant_number` lo hace único por tenant.

Un cliente que viene de otro sistema necesita **su prefijo, sin año, continuando su propio
consecutivo**: `PW234235`. Hoy eso no se puede sin tocar código, y tocar código lo cambiaría para
todos los tenants.

## Decisiones

1. **El formato es un dato por tenant y por tipo de documento**, no una constante de código.
2. **Lo configuramos nosotros con SQL documentado, sin endpoint** (opción A del brainstorming). El
   permiso más cercano, `platform.*`, lo tiene el `admin` **del tenant**, o sea el admin del cliente:
   cualquier endpoint protegido con un permiso se lo expone. Emitir un token de soporte que no sea
   miembro del tenant no existe hoy y es otro proyecto.
3. **Sin fila, el comportamiento actual.** `PED-`/`QUO-`, con año, separador `-` y 4 dígitos. Ningún
   tenant existente cambia y no hay migración de datos.
4. **El consecutivo solo avanza.** Fijar el siguiente número usa `GREATEST`: nunca queda por debajo
   de lo ya emitido, porque un número repetido choca contra el índice único.
5. **Un formato sin año usa un contador que no se reinicia**, la fila `year = 0` de la tabla de
   contadores que ya existe. Con año se sigue usando la fila del año, como hoy.

### Alternativas descartadas

| Alternativa | Por qué no |
| --- | --- |
| Endpoint con permiso propio, configurable por el admin del tenant | El owner lo descartó: la numeración es decisión de negocio nuestra al montar al cliente, no del cliente. |
| Endpoint con un permiso que ningún rol trae | Hoy no hay forma de emitir un token de soporte ajeno al tenant; habría que inventarla. |
| Seeder al arrancar, leyendo ConfigMap | Un redeploy por cliente, y datos de un tenant viviendo en la configuración del despliegue. |
| Plantilla libre (`"PW{seq:6}"`) en vez de campos | Un parser propio y un lenguaje que nadie más habla, para cubrir los mismos tres grados de libertad. |

## Diseño

### Tabla `quotations.document_numbering_formats`

| Columna | Tipo | Notas |
| --- | --- | --- |
| `tenant_id` | `uuid` | PK junto a `document_type` |
| `document_type` | `text` | `order` o `quotation` |
| `prefix` | `text` | 0 a 10 caracteres, `[A-Za-z0-9-]` |
| `include_year` | `boolean` | |
| `year_separator` | `text` | `''`, `-` o `/`; solo se usa si `include_year` |
| `min_digits` | `int` | 1 a 10, relleno con ceros a la izquierda |

Los tres rangos van también como `CHECK` en la base: es configuración que se escribe a mano, y el
`CHECK` es la única red que no depende de quién corra el SQL.

Ejemplos:

| Caso | `prefix` | `include_year` | `year_separator` | `min_digits` | Resultado |
| --- | --- | --- | --- | --- | --- |
| Default actual (sin fila) | `PED-` | `true` | `-` | `4` | `PED-2026-0001` |
| Cliente PW | `PW` | `false` | `''` | `1` | `PW234235` |
| Con año y 6 dígitos | `PW-` | `true` | `-` | `6` | `PW-2026-000007` |

### Emisión

- **`DocumentNumberFormatter`** (Application, Quotations) reemplaza a `OrderNumberFormatter` y
  `QuotationNumberFormatter`. Es puro: recibe el formato, el año y el consecutivo, y devuelve el
  texto. Se prueba sin base.
- **`IDocumentNumberingFormatLookup`** (puerto en Application, adaptador en Infrastructure) devuelve
  la fila del tenant y tipo, o el default si no hay.
- **Contador:** se reutilizan `order_number_counters` y `quotation_number_counters`. El handler pide
  el consecutivo con `year` si el formato lleva año, y con **`0`** si no. No hacen falta tablas
  nuevas y la concurrencia sigue resuelta por el `UPDATE ... RETURNING` que ya existe.
- `CreateQuotationHandler` y `ConvertQuotationToOrderHandler` leen el formato una vez por request,
  piden el consecutivo y formatean. `IQuotationNumberGenerator` e `IOrderNumberGenerator` no cambian
  de firma.

### Configuración (runbook en el README)

Tres operaciones, con `tenant_id` y valores como parámetros:

```sql
-- 1. Formato del tenant
INSERT INTO quotations.document_numbering_formats
       (tenant_id, document_type, prefix, include_year, year_separator, min_digits)
VALUES (:tenant_id, 'order', 'PW', false, '', 1)
ON CONFLICT (tenant_id, document_type) DO UPDATE
SET prefix = EXCLUDED.prefix, include_year = EXCLUDED.include_year,
    year_separator = EXCLUDED.year_separator, min_digits = EXCLUDED.min_digits;

-- 2. Continuar el consecutivo del sistema viejo (solo avanza)
INSERT INTO quotations.order_number_counters (tenant_id, year, next_value)
VALUES (:tenant_id, 0, :siguiente_numero)
ON CONFLICT (tenant_id, year) DO UPDATE
SET next_value = GREATEST(quotations.order_number_counters.next_value, EXCLUDED.next_value);

-- 3. Verificar
SELECT * FROM quotations.document_numbering_formats WHERE tenant_id = :tenant_id;
SELECT * FROM quotations.order_number_counters WHERE tenant_id = :tenant_id;
```

`year = 0` es la fila del formato sin año. El `GREATEST` es la regla "solo avanza" en una línea:
correr el mismo SQL dos veces no retrocede el consecutivo.

**`next_value` es el número que se va a emitir, no el último emitido.** El generador hace
`UPDATE … SET next_value = next_value + 1 … RETURNING next_value - 1`
(`OrderNumberGenerator.cs:20-28`), o sea devuelve el valor que la fila tenía. Si el cliente venía
en `PW234234`, `:siguiente_numero` es **234235**.

### Errores

- **Formato inválido en la base:** los `CHECK` lo impiden al escribir. Si aun así llega algo
  inválido, el lector falla al emitir con código de dominio y `422`; no se emite un número a medias.
- **El número no cabe en 20 caracteres:** `422` con `order.order.number_too_long` /
  `quotation.quotation.number_too_long`, que ya existen. Ese consecutivo se pierde y queda un hueco
  en la serie, lo mismo que ya pasa con el CUC cuando el alta falla después de emitir.
- **Sin fila:** no es error, es el default.
- Ningún status HTTP se arma a mano: el mapeo sigue siendo central en `src/Api/ApiExceptionHandler.cs`.

### Cambio de formato a mitad de camino

El contador no depende del prefijo, así que cambiar `PED-` por `PW` no reinicia nada: la serie sigue
donde iba. Pasar de **con año a sin año** (o al revés) sí cambia de fila de contador, y por eso el
runbook incluye el paso 2: se fija el siguiente número de la fila nueva.

## Pruebas (TDD: RED con evidencia literal antes de GREEN)

- **`DocumentNumberFormatter`, unitarias:** default `PED-2026-0001`; `PW234235` (sin año ni relleno);
  `PW-2026-000007` (con año y 6 dígitos); y un caso que excede los 20 caracteres.
- **Integración:** un tenant con formato PW y `next_value = 234235` convierte una cotización y
  obtiene `PW234235`; la siguiente, `PW234236`.
- **Integración:** dos tenants con formatos distintos no se pisan.
- **Integración:** un tenant **sin fila** sigue emitiendo `PED-2026-…` y `QUO-2026-…` — la prueba de
  que nada cambia para los que ya están.
- **Integración:** el `GREATEST` del runbook no retrocede el contador si se corre dos veces.
- **Migración:** se genera con el factory de diseño, como manda el repo:
  `dotnet ef migrations add AddDocumentNumberingFormats --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`.
- **Arquitectura:** `Modules.Quotations.Application` no referencia EF Core ni Npgsql; el adaptador
  del lookup vive en Infrastructure. `QuotationsLayerTests` en verde sin cambiar reglas.

## Fuera de alcance

- **Pantalla o endpoint para configurar el formato** (decisión 2).
- **Backfill o renumeración de lo ya emitido** (`SDD` del repo: los IDs no se renumeran).
- **Numeración de la carga sintética de `ExportLoadSeeder`**, que sigue armando números por año UTC
  y es data de prueba.
- **El resto de los documentos** (comprobantes, exports): solo pedidos y cotizaciones tienen
  consecutivo propio hoy.
