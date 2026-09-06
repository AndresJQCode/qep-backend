<#
.SYNOPSIS
    Carga la semilla de productos del catálogo en un tenant, por la API.

.DESCRIPTION
    Lee ops/seed/catalog-products.json y hace POST a /api/v1/tenants/{tenantId}/catalog. No
    escribe en la base directamente a propósito: pasando por la API, la semilla no puede crear
    datos que los validadores, la autorización por tenant o los invariantes del dominio
    rechazarían, y cada alta queda auditada como cualquier otra.

    Es idempotente por código de producto: lista lo que ya existe y saltea esos SKU, igual que
    GeographySeeder upsertea por DivipolaCode. Correrlo dos veces no duplica nada.

    Los precios COP y USD viajan CON IVA INCLUIDO. QuotationItem extrae el impuesto de adentro
    del precio —total × tasa / (100 + tasa)— en vez de sumarlo encima, así que el precio base de
    un producto gravado ya lo contiene. Ver Modules.Quotations.Domain/QuotationItem.cs.

    Los 19 quedan con la tasa de `taxRate` del JSON. La lista de origen en USD marcaba 14 de
    ellos como exentos, pero Product.TaxRateId es un solo campo para las dos monedas: "gravado en
    COP y exento en USD" no se puede modelar. Decisión del owner el 2026-09-05: gana la lista COP.
    Consecuencia asumida: una cotización en USD de esos 14 extrae un IVA que su precio en dólares
    no contiene. Se corrige con precios USD que lo traigan adentro, o con un campo por moneda en
    el dominio — ninguna de las dos es tarea de este script.

.PARAMETER TenantId
    Tenant destino. Los productos son por tenant y no hay un default seguro: hay que pasarlo.

.PARAMETER SubjectId
    Sujeto que figura como autor en la auditoría de cada alta. Sólo lo lee el stub de desarrollo.

.PARAMETER BaseUrl
    Raíz de la API. El default es el del perfil "http" de launchSettings.json.

.PARAMETER DryRun
    Valida el archivo e imprime los cuerpos que se mandarían, sin tocar la API.

.EXAMPLE
    # 1. Arrancar la API con el stub de auth: los dos perfiles de launchSettings lo fijan en
    #    false, así que un `dotnet run` normal corre con auth real y estos headers no sirven.
    $env:Authentication__UseDevelopmentStub = "true"
    dotnet run --project src/Api

    # 2. En otra consola, ver qué se mandaría:
    .\ops\seed\Seed-CatalogProducts.ps1 -TenantId <guid> -DryRun

    # 3. Cargar:
    .\ops\seed\Seed-CatalogProducts.ps1 -TenantId <guid>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [guid] $TenantId,

    # El sujeto de desarrollo que documenta README § Identidad local.
    [guid] $SubjectId = [guid]'01900000-0000-7000-8000-000000000002',

    [string] $BaseUrl = 'http://localhost:5000',

    [string] $DataPath = (Join-Path $PSScriptRoot 'catalog-products.json'),

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

# MaxPageSize del listado de productos (ListProducts.cs). Pedir más no falla: el backend lo
# recorta en silencio, y entonces la paginación de acá contaría mal.
$script:MaxPageSize = 200

$script:Root = $BaseUrl.TrimEnd('/')

# El stub concede sólo los permisos de tenancy cuando X-Permissions no viene, así que hay que
# pedir los cuatro de catálogo a mano. Sin esto el 403 vendría del permiso faltante.
$script:Headers = @(
    "X-Subject-Id: $SubjectId",
    "X-Tenant-Id: $TenantId",
    'X-Permissions: catalog.product.read,catalog.product.manage,catalog.tax_rate.read,catalog.tax_rate.manage'
)

function Write-JsonBody {
    param(
        [Parameter(Mandatory = $true)] $Body,
        [Parameter(Mandatory = $true)] [string] $Path
    )

    # UTF-8 SIN BOM. Ocho de los diecinueve nombres llevan tilde, y un BOM al principio del
    # cuerpo hace que el parser de JSON de ASP.NET falle antes de ver el primer campo.
    $json = $Body | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
    return $json
}

function Invoke-QepApi {
    param(
        [Parameter(Mandatory = $true)] [string] $Method,
        [Parameter(Mandatory = $true)] [string] $Path,
        [string] $BodyFile
    )

    $outFile = [System.IO.Path]::GetTempFileName()
    try {
        $curlArgs = @('-s', '-S', '-o', $outFile, '-w', '%{http_code}', '-X', $Method, "$script:Root$Path")
        foreach ($header in $script:Headers) {
            $curlArgs += @('-H', $header)
        }
        if ($BodyFile) {
            # --data-binary y no -d: -d descarta saltos de línea y CR del archivo. Con JSON no
            # cambia el significado, pero tampoco hay razón para dejar que toque los bytes.
            $curlArgs += @('-H', 'Content-Type: application/json', '--data-binary', "@$BodyFile")
        }

        $status = & $script:Curl @curlArgs
        if ($LASTEXITCODE -ne 0) {
            throw "curl falló con código $LASTEXITCODE contra $script:Root$Path. ¿Está la API arriba?"
        }

        $raw = [System.IO.File]::ReadAllText($outFile, [System.Text.Encoding]::UTF8)
        $parsed = $null
        if ($raw) {
            try { $parsed = $raw | ConvertFrom-Json } catch { $parsed = $null }
        }

        return [pscustomobject]@{
            Status = [int] $status
            Raw    = $raw
            Json   = $parsed
        }
    }
    finally {
        Remove-Item $outFile -Force -ErrorAction SilentlyContinue
    }
}

function Format-ApiError {
    param([Parameter(Mandatory = $true)] $Response)

    if ($null -eq $Response.Json) {
        return "HTTP $($Response.Status): $($Response.Raw)"
    }

    $message = "HTTP $($Response.Status) $($Response.Json.code)"
    if ($Response.Json.detail) {
        $message += " - $($Response.Json.detail)"
    }
    # El mapa `errors` sólo viene en el 422 de validación, y es el que dice qué campo falló.
    if ($Response.Json.errors) {
        foreach ($field in $Response.Json.errors.PSObject.Properties) {
            $message += " [$($field.Name): $($field.Value -join '; ')]"
        }
    }
    return $message
}

# --- Cargar y validar el archivo -------------------------------------------------------------
# Todo se comprueba ANTES del primer POST: un archivo con un precio nulo en la fila 12 no debe
# dejar once productos creados y ocho sin crear.

if (-not (Test-Path -LiteralPath $DataPath)) {
    throw "No se encontró el archivo de semilla en '$DataPath'."
}

$data = [System.IO.File]::ReadAllText($DataPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json

if ($null -eq $data.taxRate -or [string]::IsNullOrWhiteSpace($data.taxRate.name)) {
    throw "El archivo de semilla no declara 'taxRate.name'."
}
# Explícito contra null: $null -lt 0 es $false, así que sin esta línea una tasa sin porcentaje
# atravesaría el rango y se crearía al 0%.
if ($null -eq $data.taxRate.percentage) {
    throw "El archivo de semilla no declara 'taxRate.percentage'."
}
if ($data.taxRate.percentage -lt 0 -or $data.taxRate.percentage -gt 100) {
    throw "taxRate.percentage debe estar entre 0 y 100 (TaxRate.cs); vino '$($data.taxRate.percentage)'."
}

$products = @($data.products)
if ($products.Count -eq 0) {
    throw "El archivo de semilla no trae productos."
}

$problems = @()
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($product in $products) {
    $sku = $product.sku
    if ([string]::IsNullOrWhiteSpace($sku)) {
        $problems += "un producto no tiene 'sku'"
        continue
    }
    if (-not $seen.Add($sku)) {
        $problems += "sku duplicado: $sku"
    }
    if ([string]::IsNullOrWhiteSpace($product.name)) {
        $problems += "$sku no tiene 'name'"
    }
    # Los anchos son los del dominio (Product.NameMaxLength / CodeMaxLength). Comprobarlos acá
    # convierte un 422 por producto en un error único antes de mandar nada.
    elseif ($product.name.Length -gt 200) {
        $problems += "$sku tiene un nombre de $($product.name.Length) caracteres (máximo 200)"
    }
    if ($sku.Length -gt 60) {
        $problems += "$sku excede los 60 caracteres de código"
    }

    $cop = $product.priceCop
    $usd = $product.priceUsd
    if ($null -eq $cop -and $null -eq $usd) {
        # Product.ApplyPricing lo exige incondicionalmente.
        $problems += "$sku no tiene precio en ninguna moneda"
    }
    if ($null -ne $cop -and $cop -lt 0) { $problems += "$sku tiene priceCop negativo" }
    if ($null -ne $usd -and $usd -lt 0) { $problems += "$sku tiene priceUsd negativo" }
}

if ($problems.Count -gt 0) {
    # Entre paréntesis: `throw "a" + $b` sin ellos es frágil de parsear.
    throw ("El archivo de semilla tiene $($problems.Count) problema(s):`n  - " + ($problems -join "`n  - "))
}

Write-Host "Semilla: $($products.Count) productos, tasa '$($data.taxRate.name)' al $($data.taxRate.percentage)%." -ForegroundColor Cyan

function New-ProductBody {
    param(
        [Parameter(Mandatory = $true)] $Product,
        $TaxRateId
    )

    # decimal y no double: ConvertTo-Json sobre el double que ConvertFrom-Json devolvió puede
    # imprimir 9.9700000000000006. El dominio también usa decimal.
    $baseUsd = $null
    if ($null -ne $Product.priceUsd) { $baseUsd = [decimal] $Product.priceUsd }
    $baseCop = $null
    if ($null -ne $Product.priceCop) { $baseCop = [decimal] $Product.priceCop }

    return [ordered]@{
        name        = $Product.name
        code        = $Product.sku
        description = $null
        # La imagen del JSON de origen es una ruta; ImageFileId es un Guid de un archivo de
        # Storage. No se puede mapear, así que va en null y se sube después.
        imageFileId = $null
        taxRateId   = $TaxRateId
        pricing     = [ordered]@{
            baseUsd = $baseUsd
            baseCop = $baseCop
            scales  = @()
        }
    }
}

# --- Ensayo -----------------------------------------------------------------------------------

if ($DryRun) {
    Write-Host "`n-- DryRun: no se llama a la API --`n" -ForegroundColor Yellow
    foreach ($product in $products) {
        $body = New-ProductBody -Product $product -TaxRateId '<taxRateId resuelto en la corrida real>'
        Write-Host "POST /api/v1/tenants/$TenantId/catalog/products"
        Write-Host ($body | ConvertTo-Json -Depth 10)
        Write-Host ''
    }
    Write-Host "Archivo válido: $($products.Count) productos listos para cargar." -ForegroundColor Green
    exit 0
}

# -CommandType Application es lo que importa acá, no el nombre: descarta alias y funciones, así
# que 'curl' NO puede resolver al alias de Invoke-WebRequest de Windows PowerShell aunque exista.
# Se prueba 'curl.exe' primero para que en Windows gane el binario real, y 'curl' después para
# que el script también corra en macOS y Linux, donde el .exe no existe.
$script:Curl = $null
foreach ($candidate in @('curl.exe', 'curl')) {
    $found = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) {
        $script:Curl = $found.Source
        break
    }
}
if (-not $script:Curl) {
    throw "No se encontró el ejecutable curl (curl.exe en Windows). Ojo: 'curl' a secas puede ser un alias de Invoke-WebRequest, que no sirve acá."
}

# --- Resolver la tasa de impuesto -------------------------------------------------------------

$taxRatesResponse = Invoke-QepApi -Method 'GET' -Path "/api/v1/tenants/$TenantId/catalog/tax-rates"
if ($taxRatesResponse.Status -ne 200) {
    throw "No se pudo listar las tasas de impuesto. $(Format-ApiError $taxRatesResponse)"
}

$taxRate = @($taxRatesResponse.Json.items) | Where-Object { $_.name -eq $data.taxRate.name } | Select-Object -First 1

if ($null -eq $taxRate) {
    $bodyFile = [System.IO.Path]::GetTempFileName()
    try {
        [void] (Write-JsonBody -Body ([ordered]@{
            name       = $data.taxRate.name
            percentage = [int] $data.taxRate.percentage
        }) -Path $bodyFile)
        $created = Invoke-QepApi -Method 'POST' -Path "/api/v1/tenants/$TenantId/catalog/tax-rates" -BodyFile $bodyFile
    }
    finally {
        Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue
    }

    if ($created.Status -ne 201) {
        throw "No se pudo crear la tasa '$($data.taxRate.name)'. $(Format-ApiError $created)"
    }
    $taxRate = $created.Json
    Write-Host "Tasa '$($taxRate.name)' creada: $($taxRate.id)" -ForegroundColor Green
}
else {
    # Reusar una tasa con otro porcentaje mueve los totales de toda cotización que la use, y el
    # síntoma aparece meses después en un PDF. Se aborta en vez de adivinar.
    if ([int] $taxRate.percentage -ne [int] $data.taxRate.percentage) {
        throw ("El tenant ya tiene una tasa '$($taxRate.name)' al $($taxRate.percentage)%, " +
               "pero la semilla espera $($data.taxRate.percentage)%. Corregila en el catálogo o " +
               "pasá -DataPath con otro nombre de tasa; este script no la modifica.")
    }
    # Una tasa inactiva se acepta igual: ProductTaxRateResolver sólo rechaza la que no existe o
    # es de otro tenant.
    Write-Host "Tasa '$($taxRate.name)' ya existía: $($taxRate.id)" -ForegroundColor DarkGray
}

# --- Listar lo que ya está --------------------------------------------------------------------

$existingCodes = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$page = 1
$fetched = 0
$total = 0
do {
    $path = "/api/v1/tenants/$TenantId/catalog/products?page=$page&pageSize=$script:MaxPageSize"
    $listed = Invoke-QepApi -Method 'GET' -Path $path
    if ($listed.Status -ne 200) {
        throw "No se pudo listar los productos existentes. $(Format-ApiError $listed)"
    }

    $items = @($listed.Json.items)
    foreach ($item in $items) {
        [void] $existingCodes.Add($item.code)
    }
    $fetched += $items.Count
    $total = [int] $listed.Json.total
    $page++
} while ($items.Count -gt 0 -and $fetched -lt $total)

Write-Host "El tenant ya tiene $total producto(s)." -ForegroundColor DarkGray

# --- Crear --------------------------------------------------------------------------------------

$createdCount = 0
$skippedCount = 0
$failures = @()

foreach ($product in $products) {
    if ($existingCodes.Contains($product.sku)) {
        Write-Host "  = $($product.sku) $($product.name) (ya existe)" -ForegroundColor DarkGray
        $skippedCount++
        continue
    }

    $bodyFile = [System.IO.Path]::GetTempFileName()
    try {
        [void] (Write-JsonBody -Body (New-ProductBody -Product $product -TaxRateId $taxRate.id) -Path $bodyFile)
        $response = Invoke-QepApi -Method 'POST' -Path "/api/v1/tenants/$TenantId/catalog/products" -BodyFile $bodyFile
    }
    finally {
        Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue
    }

    if ($response.Status -eq 201) {
        Write-Host "  + $($product.sku) $($product.name)" -ForegroundColor Green
        $createdCount++
    }
    else {
        $reason = Format-ApiError $response
        Write-Host "  x $($product.sku) $($product.name) -> $reason" -ForegroundColor Red
        $failures += "$($product.sku): $reason"
    }
}

# --- Resumen ------------------------------------------------------------------------------------

Write-Host ''
Write-Host "Creados: $createdCount   Saltados: $skippedCount   Fallidos: $($failures.Count)" -ForegroundColor Cyan

if ($failures.Count -gt 0) {
    Write-Host "`nFallos:" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    exit 1
}

exit 0
