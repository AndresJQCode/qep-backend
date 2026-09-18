# Comprobantes de pago públicos para el Excel de pedidos — diseño

**Fecha:** 2026-09-15 (diseño acordado con el owner el 2026-09-14)
**Rama:** `feature/comprobantes-publicos` (backend). No hay cambio en `qep-frontend`: la API no cambia de forma.

## Contexto

Quien revisa pedidos pendientes de aprobar exporta el listado a Excel y necesita abrir cada
comprobante de pago **desde el archivo**, con un clic.

Hoy eso no se puede:

- **Los comprobantes son privados.** Cada uno es un `FileResource` del bucket privado de R2, y el
  pedido guarda sólo su `FileId` (`OrderPaymentProof.FileId`). Para verlo hay que pedir una URL
  firmada de 5 minutos.
- **El Excel no los trae.** `OrdersExportProcessor.Columns` son las ocho columnas de la tabla de
  pedidos, sin comprobantes, y `OrderRepository.Filtered` los excluye a propósito para no caer en
  N+1.
- **El writer no sabe hacer enlaces.** `ExportCell` es texto o número.
- **Storage sólo publica imágenes.** `FileResource.Publish` rechaza cualquier otro tipo con
  `storage.file.public_image_required` (`FileResource.cs:251`), y un comprobante puede ser PDF, JPG
  o PNG (`OrderPaymentProofResolver`).

### Alternativas descartadas

| Alternativa | Por qué no |
| --- | --- |
| URL firmada larga en la celda | SigV4 no firma más de 7 días: el enlace muere aunque el archivo se siga usando. El owner quiere enlaces que no venzan. |
| Enlace a una ruta de la SPA, que con la sesión pide la URL firmada | Es la opción más segura (privado, revocable, sin vencimiento), pero exige un slice en `qep-frontend`. Apuntar directo a la API no sirve: Office pide el enlace por su cuenta, sin la cookie de sesión, y recibe un 401. |
| Publicar al exportar, con la clave reusada | Cubría también los comprobantes viejos y exponía sólo los exportados, pero el owner eligió publicar al adjuntar. |

### Riesgo aceptado por el owner (2026-09-14)

Un comprobante de pago suele traer nombre, cédula, banco y número de cuenta. Con la opción
encendida, **cualquiera que tenga la URL lo abre, sin sesión y sin revisar el tenant**, y no se
puede revocar si el Excel se reenvía. La clave aleatoria impide adivinar la URL; no controla quién
la tiene.

## Decisiones

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | Clave nueva `Quotations:PaymentProofs:PublicLinks` (bool). `appsettings.json` la trae en `false`; producción la enciende en `k8s/prod-configMap.yaml`. | Por ambiente, no por tenant: es un cambio sólo de backend. En `false` todo sigue como hoy. |
| P2 | Si está en `true` y falta `Storage:R2:PublicBucket` o `Storage:R2:PublicBaseUrl`, el arranque falla (`ValidateOnStart`). El validador vive en el Bootstrapper. | Sin eso, la opción quedaría encendida sin que se publique un solo comprobante y sin ningún error. El Bootstrapper es el único proyecto que ve `QuotationsOptions` y `StorageOptions` a la vez. |
| P3 | Puerto `IPaymentProofPublisher` en `Modules.Quotations.Application`. DI registra la implementación que publica o la que no hace nada, según la clave. | Mismo patrón que `ZenviaWhatsAppSender` / `LogWhatsAppSender`: los handlers no leen configuración. |
| P4 | `ConvertQuotationToOrderHandler` y `AddOrderPaymentProofsHandler` publican **sólo los comprobantes nuevos**, antes del `SaveChanges`. | Corregir un monto (`UpdatedProofs`) no cambia el archivo. |
| P5 | La copia pública usa la clave aleatoria `payment-proofs/{guid v7}.{ext}` y se guarda en la columna nueva y nullable `quotations.order_payment_proofs.public_storage_key`. Se guarda la clave, no la URL. | Precedente: `QuotationPdfStorage.PublishAsync`. La clave no se deduce de ningún id que viaje en el navegador. La URL se arma al exportar con `PublicBaseUrl`, así que cambiar el dominio no rompe nada. |
| P6 | `FileResource.Publish` no se toca. | Su regla («sólo imágenes») protege el endpoint de publicación de Storage. Relajarla dejaría a cualquiera con `FilePublish` publicar cualquier PDF desde la API. |
| P7 | Si una copia falla, falla el request y no se guarda nada. Si falla el `SaveChanges` después de copiar, se borran las copias hechas (best-effort). | Un pedido guardado sin su copia pública saldría en el Excel sin enlace y nadie se enteraría. El borrado sigue el rollback de `PublishFileHandler` (`SetFilePublication.cs:52-59`). |
| P8 | Sin backfill: los comprobantes anteriores, y los que se adjunten con la opción apagada, quedan privados. | El owner pidió publicar al adjuntar. Un backfill es un slice aparte. |
| E1 | La hoja «Pedidos» suma seis columnas al final, después de Total: «Comprobantes» (cantidad, número) y «Comprobante 1» a «Comprobante 5». **Enmendado el 2026-09-17: eran tres columnas de comprobante.** | Las ocho actuales copian el orden de `order-table.tsx` y no se mueven. Cinco cubre lo que un cliente paga en la práctica; la cantidad hace visible el que se pase de ahí y no tiene columna. El tope vive en `OrdersExportProcessor.ProofColumns`. |
| E2 | Cada celda de comprobante trae el enlace «Ver» si tiene copia pública, «Sin enlace» si es privado, y queda vacía si el pedido no tiene ese comprobante. | Una celda vacía no puede significar dos cosas. |
| E3 | El enlace es la fórmula `HYPERLINK("url","Ver")`, con el valor ya calculado y estilo de enlace (azul, subrayado). | El writer escribe en streaming y el zip admite una sola entrada abierta a la vez. Un hipervínculo de relación (`<hyperlinks>` más `sheet1.xml.rels`) obliga a guardar en memoria todos los enlaces hasta el final. |
| E4 | Si la URL o el texto pasan de 255 caracteres, la celda lleva la URL como texto plano. El tope se mide sobre la cadena tal como entra en la fórmula, con las comillas ya duplicadas. | Es el tope de Excel para una cadena dentro de una fórmula; medido ya escapado, toda fórmula que se escribe es válida. Una URL pública mide unos 100 caracteres; sólo lo rompe un `PublicBaseUrl` mal configurado, y así la URL se ve en vez de perderse. |
| E5 | `ExportCell` gana un tercer tipo: `OfLink(url, text)`. | El Excel de cotizaciones no cambia. |
| E6 | Los comprobantes se leen con una consulta por lote de 1000 pedidos, ordenados por `UploadedAt` y después por `Id`. | Evita el N+1. «Comprobante 1» es el primero que se subió. |
| E7 | Las cuatro columnas salen siempre, aunque la opción esté apagada. | La forma del archivo no depende del ambiente. |

## Sección 1: configuración y publicación

### Configuración

`QuotationsOptions` gana una subsección:

```csharp
public PaymentProofsOptions PaymentProofs { get; init; } = new();

public sealed class PaymentProofsOptions
{
    public bool PublicLinks { get; init; }
}
```

- `src/Api/appsettings.json` y `appsettings.example.json`: `"PaymentProofs": { "PublicLinks": false }`.
- `k8s/prod-configMap.yaml`: `Quotations__PaymentProofs__PublicLinks: "true"`. Es un valor
  literal, no un token `#{...}#`: no es un secreto ni cambia por despliegue.
- **Validador del Bootstrapper** (`IValidateOptions<QuotationsOptions>`, recibe
  `IOptions<StorageOptions>`): con `PublicLinks = true` exige `Storage:R2:PublicBucket` y
  `Storage:R2:PublicBaseUrl`, en **cualquier ambiente**. El mensaje dice qué falta y por qué
  (sin eso no se publica ningún comprobante).

### El puerto

En `Modules.Quotations.Application`:

```csharp
public interface IPaymentProofPublisher
{
    /// Copia el archivo al bucket público y devuelve la clave pública, o null si la opción está apagada.
    Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// Borra una copia pública. Sólo para el rollback de P7.
    Task DeleteAsync(string publicKey, CancellationToken cancellationToken);

    /// La URL pública de una clave, o null si la opción está apagada.
    string? UrlFor(string publicKey);
}
```

Dos implementaciones, en el Bootstrapper porque son el único lugar que ve Quotations y Storage:

- **`PublicPaymentProofPublisher`** (`IFileResourceRepository` + `IPublicObjectStorage`):
  1. Carga el `FileResource`. Si no existe, no es del tenant o no está `Available`, lanza el mismo
     `order.payment_proof.file_not_found` / `file_not_available` que el resolver. En la práctica
     no pasa, porque el resolver ya lo validó, pero la frontera de tenant se revisa igual.
  2. Arma `payment-proofs/{Guid.CreateVersion7():N}{ext}`. La extensión sale del `MimeType`
     (`application/pdf` → `.pdf`, `image/jpeg` → `.jpg`, `image/png` → `.png`), no del nombre, que
     puede traer `.jpeg` o mayúsculas.
  3. `CopyFromPrivateAsync(resource.StorageKey, publicKey)` y devuelve la clave.
- **`DisabledPaymentProofPublisher`**: `PublishAsync` devuelve `null`, `DeleteAsync` no hace nada y
  `UrlFor` devuelve `null`.

**Apagar la opción** deja de publicar y de mostrar enlaces en el Excel, pero **no despublica** lo
que ya se copió. Despublicar es trabajo aparte.

### Dominio y persistencia

- `OrderPaymentProofInput(Guid FileId, decimal Amount, string? PublicStorageKey = null)`: el
  parámetro nuevo va al final para no romper a quien lo construye posicionalmente.
- `OrderPaymentProof.PublicStorageKey` (`string?`), fijado en `Create` e inmutable después, igual
  que `FileId`.
- Migración `AddOrderPaymentProofPublicStorageKey`: columna `public_storage_key`, `varchar(200)`,
  nullable, sin índice (nadie busca por ella).

### Los handlers

En `ConvertQuotationToOrderHandler` y `AddOrderPaymentProofsHandler`, después de resolver los
comprobantes y antes de tocar el dominio:

1. Por cada comprobante **nuevo**, `publisher.PublishAsync`, juntando las claves.
2. Se construyen los `OrderPaymentProofInput` con su clave.
3. Dominio, auditoría y `SaveChanges`.
4. Si falla cualquier paso después de la primera copia, se borran las claves ya copiadas
   (`DeleteAsync`, cada una en su propio try/catch, con `CancellationToken.None`) y se relanza la
   excepción original. Una copia que falla sube como 5xx por `ApiExceptionHandler`.

Ese «cualquier paso» incluye un **rechazo del dominio**, que llega después de copiar: por ejemplo,
`order.order.not_pending` al sumar comprobantes a un pedido que otra persona acaba de aprobar. Las
claves tienen que existir antes de llamar al dominio, porque `OrderPaymentProof` las recibe en
`Create`. Por eso el rollback cubre también ese caso, en vez de duplicar en el handler las reglas
del dominio.

### Contras aceptados

- Si alguien borra el archivo en Storage (`SoftDeleteFileHandler`), la copia pública queda ahí:
  Storage no sabe que existe. Se deja comentado en `PublicPaymentProofPublisher`.
- **No debe haber una regla de lifecycle sobre `payment-proofs/`** en el bucket público. El prefijo
  de `QuotationPdfStorage` sí puede tener una, y confundirlos rompería los enlaces.
- `CopyObject` copia el `Content-Type` del original (S3 usa `MetadataDirective = COPY` por defecto),
  así que un PDF se abre en el navegador en vez de descargarse. Esto se verifica a mano contra R2,
  porque las pruebas usan un doble.

## Sección 2: el Excel de pedidos

### Columnas

`OrdersExportProcessor.Columns` suma, después de `Total`:

| Encabezado | Ancho | Contenido |
| --- | --- | --- |
| `Comprobantes` | 14 | Cantidad total de comprobantes del pedido, como número. |
| `Comprobante 1` | 16 | Enlace «Ver», «Sin enlace» o vacía (E2). |
| `Comprobante 2` a `Comprobante 5` | 16 | Ídem. |

Los encabezados van sin tildes, como el resto (`ExportColumn`).

### Lectura

`IOrderRepository.ListPaymentProofsForExportAsync(Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken)`
devuelve, por pedido, sus comprobantes ordenados por `UploadedAt` y después `Id`, con lo mínimo:
`Id`, `PublicStorageKey`, `UploadedAt`. Es `AsNoTracking` y una sola consulta por lote. El
procesador la llama dentro de la proyección del lote de `ExportBatchLoop`, con los ids del lote.

`order_payment_proofs` no tiene columna de tenant: el filtro por `tenantId` va con un join contra
`orders`. Así se cumple la regla de que todo método de repositorio recibe y aplica el tenant.

### Celdas

`ExportCell` pasa a `ExportCell(string? Text, decimal? Number, string? Url)`, con
`OfLink(string url, string text)`. `OfText` y `OfNumber` no cambian.

`OpenXmlExportWorkbook.ToCell` escribe un enlace así:

```xml
<c r="J2" t="str" s="2"><f>HYPERLINK("https://…/payment-proofs/….pdf","Ver")</f><v>Ver</v></c>
```

- En el XML la fórmula separa argumentos con coma. Excel la muestra con el separador de la
  configuración regional de quien abre el archivo.
- Las comillas dobles se escapan duplicándolas (`"` → `""`), en la URL y en el texto.
- `<v>` lleva el valor ya calculado: el archivo se ve bien antes de que Excel recalcule, y en
  visores que no calculan.
- Si la URL o el texto pasan de 255 caracteres tal como entran en la fórmula, con las comillas ya
  duplicadas, la celda sale como texto plano con la URL (E4).
- El estilo de índice 2 es una fuente azul (`FF0563C1`) y subrayada. `BuildStylesheet` pasa a tres
  fuentes y tres formatos de celda.

El procesador arma cada fila así:

- `Comprobantes` = `OfNumber(proofs.Count)`.
- Para `i` en 0..2: si no hay comprobante `i`, `OfText("")`. Si lo hay y
  `publisher.UrlFor(key)` devuelve una URL, `OfLink(url, "Ver")`. Si no, `OfText("Sin enlace")`.

### Lo que el usuario tiene que saber

Un Excel bajado de internet abre en **Vista protegida**, y ahí ningún enlace responde hasta que se
toca «Habilitar edición». Es comportamiento de Office, igual para cualquier tipo de enlace.

## Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias** (`Modules.Quotations.UnitTests` y las del Bootstrapper):

- Writer: una celda `OfLink` escribe la fórmula, el valor calculado y el estilo 2; una URL de más de
  255 caracteres cae a texto plano; las comillas se escapan. Se verifica abriendo el `.xlsx`
  generado.
- `OrdersExportProcessor`: filas con 0, 1, 3 y 4 comprobantes; uno privado da «Sin enlace»; la
  cantidad cuenta los cuatro aunque sólo haya tres columnas.
- `OrderPaymentProof`: guarda `PublicStorageKey` y lo acepta null.
- Validador: `PublicLinks = true` sin bucket público falla; con bucket, o con la opción en `false`,
  pasa.
- `PublicPaymentProofPublisher`: extensión según el `MimeType`, prefijo `payment-proofs/`, archivo
  de otro tenant rechazado.

**De integración** (`Modules.Quotations.IntegrationTests`, con un doble de `IPublicObjectStorage`):

- Convertir y sumar comprobantes con la opción encendida: la copia se hace y la clave queda
  guardada. Con la opción apagada no se copia nada y la clave queda null.
- Una copia que falla: el request falla, el pedido no se crea (o los comprobantes no se suman) y las
  copias hechas se borran.
- Corregir sólo montos no copia nada.
- Job de exportación completo: el `.xlsx` trae las columnas nuevas con los enlaces.
- Toda factoría de integración fija `Quotations:PaymentProofs:PublicLinks` de forma explícita, mismo
  criterio que `Notifications:EmailProvider`.

**Documentación:** la clave en el README (sección de configuración) y las columnas nuevas en
`docs/integracion-cotizaciones-y-pedidos.md`, si esa guía describe el Excel.

## Fuera de alcance

- Backfill de los comprobantes existentes.
- Configuración por tenant.
- Despublicar al apagar la opción o al borrar el archivo.
- Exponer la URL pública en la respuesta de la API (`OrderPaymentProofResponse` no cambia).
- Un tope de comprobantes por pedido.
