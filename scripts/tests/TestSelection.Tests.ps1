$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\TestSelection.psm1") -Force
$script:failed = 0
function Assert-Equal($name, $expected, $actual) {
    if ("$expected" -ne "$actual") { $script:failed++; Write-Host "FALLA $name`n  esperado: $expected`n  obtenido: $actual" -ForegroundColor Red }
}

$arch = "tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj"
$boot = "tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj"
$unit = "tests/Modules/Catalog/Modules.Catalog.UnitTests/Modules.Catalog.UnitTests.csproj"
$int = "tests/Modules/Catalog/Modules.Catalog.IntegrationTests/Modules.Catalog.IntegrationTests.csproj"
$projects = @($arch, $boot, $unit, $int)
$dir = "tests/Modules/Catalog/Modules.Catalog.IntegrationTests"
$sources = @{
    "$dir/ProductApiTests.cs"       = "public sealed partial class ProductApiTests`n{`n    [Fact]`n    public void A() { }`n}"
    "$dir/ProductApiTests.Extra.cs" = "public sealed partial class ProductApiTests`n{`n    private void Help() { }`n}"
    "$dir/OrderBase.cs"             = "public abstract class OrderBase`n{`n}"
    "$dir/OrderReadTests.cs"        = "public sealed class OrderReadTests(Host h)`n    : OrderBase`n{`n    [Theory]`n    public void B() { }`n}"
    "$dir/CatalogTestDatabase.cs"   = "internal static class CatalogTestDatabase`n{`n}"
}
function Plan([string[]]$files) { Get-TestPlan -ChangedFiles $files -Projects $projects -TestSources $sources }
function Names($p) { ($p.Steps | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Project) }) -join "," }
function FilterOf($p) { ($p.Steps | Where-Object Project -eq $int).Filter }

$p = Plan @()
Assert-Equal "vacio: solo arquitectura" "ArchitectureTests" (Names $p)
Assert-Equal "vacio: sin avisos" 0 @($p.Warnings).Count

$p = Plan @("src/Modules/Catalog/Modules.Catalog.Domain/Product.cs")
Assert-Equal "produccion: unitarias" "ArchitectureTests,Modules.Catalog.UnitTests" (Names $p)
Assert-Equal "produccion: aviso sin integracion" "Catalog: cambió producción y no corre ninguna prueba de integración" ($p.Warnings -join ";")

$p = Plan @("$dir/ProductApiTests.cs")
Assert-Equal "clase: filtro" "FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("$dir/ProductApiTests.Extra.cs")
Assert-Equal "partial sin Fact" "FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("$dir/OrderBase.cs")
Assert-Equal "base abstracta" "FullyQualifiedName~.OrderReadTests." (FilterOf $p)
Assert-Equal "base abstracta: sin aviso" 0 @($p.Warnings).Count

$p = Plan @("$dir/ProductApiTests.cs", "$dir/OrderReadTests.cs")
Assert-Equal "dos clases" "FullyQualifiedName~.OrderReadTests.|FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("$dir/CatalogTestDatabase.cs")
Assert-Equal "harness: sin paso" "" (FilterOf $p)
Assert-Equal "harness: aviso" $true (($p.Warnings -join ";") -like "*Apoyo de pruebas cambiado*CatalogTestDatabase.cs*")

$p = Plan @("$dir/BorradaTests.cs")
Assert-Equal "borrado: sin paso" "" (FilterOf $p)

$p = Plan @(($dir -replace '/', '\') + "\ProductApiTests.cs")
Assert-Equal "barra invertida" "FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("src/Bootstrapper/X.cs")
Assert-Equal "bootstrapper" "ArchitectureTests,Bootstrapper.UnitTests" (Names $p)
Assert-Equal "bootstrapper: transversal" "Cambio transversal: conviene la corrida completa antes de seguir" ($p.Warnings -join ";")

$p = Plan @("src/Modules/Catalog/I/Persistence/Migrations/a.cs", "src/Modules/Pos/I/Persistence/Migrations/b.cs")
Assert-Equal "migraciones en dos modulos" $true ($p.Warnings -contains "Cambio transversal: conviene la corrida completa antes de seguir")

$p = Plan @("src/Modules/Catalog/I/Persistence/Migrations/a.cs")
Assert-Equal "migraciones en un modulo" $false ($p.Warnings -contains "Cambio transversal: conviene la corrida completa antes de seguir")

if ($script:failed) { Write-Host "$($script:failed) fallas"; exit 1 }
Write-Host "OK" -ForegroundColor Green
