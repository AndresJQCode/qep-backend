<#
.SYNOPSIS
  Nivel rápido: compila y corre sólo las pruebas de lo que cambió la rama.

.PARAMETER Base
  Rama contra la que se compara (por defecto develop).

.PARAMETER DryRun
  Muestra el plan sin compilar ni probar.

.PARAMETER NoBuild
  Omite la compilación.

.OUTPUTS
  TestResults/feature/report.md y un .trx por paso. Sale con 1 si algún paso no queda en verde.
#>
param(
    [string]$Base = "develop",
    [switch]$DryRun,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
Import-Module (Join-Path $PSScriptRoot "TestSelection.psm1") -Force

function ConvertTo-RepoPath([string]$FullName) { $FullName.Substring($root.Length + 1) -replace '\\', '/' }

$mergeBase = git merge-base $Base HEAD
if ($LASTEXITCODE -ne 0) { throw "No existe la base '$Base'." }
$changed = @(git -c core.quotepath=false diff --name-only --no-renames $mergeBase)
if ($LASTEXITCODE -ne 0) { throw "git diff falló; no se puede saber qué cambió." }
$changed += @(git -c core.quotepath=false ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw "git ls-files falló; no se puede saber qué cambió." }

$projects = @(Get-ChildItem tests -Recurse -Filter *.csproj | ForEach-Object { ConvertTo-RepoPath $_.FullName })
$sources = @{}
foreach ($project in $projects | Where-Object { $_ -like "*.IntegrationTests.csproj" }) {
    $dir = $project.Substring(0, $project.LastIndexOf('/'))
    if (-not ($changed | Where-Object { $_.StartsWith("$dir/") })) { continue }
    Get-ChildItem -LiteralPath $dir -Recurse -Filter *.cs |
        Where-Object { ($_.FullName -replace '\\', '/') -notmatch '/(bin|obj)/' } |
        ForEach-Object { $sources[(ConvertTo-RepoPath $_.FullName)] = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }
}

# El formato se fija aquí: con diff.noprefix, diff.mnemonicprefix, color forzado o un diff externo en la configuración
# de git, las referencias nuevas pasarían sin aviso.
$projectDiff = @(git -c core.quotepath=false diff --no-renames --no-color --no-ext-diff --no-textconv `
    --src-prefix=a/ --dst-prefix=b/ --unified=0 $mergeBase -- "*.csproj")
if ($LASTEXITCODE -ne 0) { throw "git diff falló; no se puede saber qué referencias cambiaron." }
# Un proyecto sin agregar a git no sale en el diff: todas sus líneas son nuevas.
foreach ($file in git -c core.quotepath=false ls-files --others --exclude-standard -- "*.csproj") {
    $projectDiff += "diff --git a/$file b/$file"
    $projectDiff += @(Get-Content -LiteralPath $file -Encoding UTF8 | ForEach-Object { "+$_" })
}

$plan = Get-TestPlan -ChangedFiles $changed -Projects $projects -TestSources $sources -ProjectDiff $projectDiff

Write-Host "Cambios contra ${Base}: $($changed.Count) archivos. Pasos:" -ForegroundColor Cyan
foreach ($step in $plan.Steps) {
    Write-Host "  $([IO.Path]::GetFileNameWithoutExtension($step.Project))"
    if ($step.Filter) { Write-Host "    --filter $($step.Filter)" }
}
foreach ($w in $plan.Warnings) { Write-Host "AVISO: $w" -ForegroundColor Yellow }
if ($DryRun) { exit 0 }

if (Get-Process testhost -ErrorAction SilentlyContinue) {
    Write-Host "AVISO: hay un testhost corriendo (otra sesión). La corrida rápida sigue; no lo detengas." -ForegroundColor Yellow
}
if ($plan.Steps | Where-Object { $_.Project -match 'IntegrationTests|Bootstrapper' }) {
    # Con Stop, el stderr redirigido de un ejecutable lanza en PowerShell 5.1 aunque salga en 0.
    $ErrorActionPreference = "Continue"
    docker info *> $null
    $dockerExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($dockerExit -ne 0) { throw "Docker no responde. Abre Docker Desktop y espera a que el daemon esté listo." }
}

$stamp = Get-GitStamp
$total = [Diagnostics.Stopwatch]::StartNew()
if (-not $NoBuild) {
    if (Get-Process Api -ErrorAction SilentlyContinue) {
        throw "Api.exe está corriendo y bloquea la compilación. Detenlo tú y vuelve a correr."
    }
    dotnet build --no-restore -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "La compilación falló; no hay pruebas que correr." }
}

$out = Join-Path $root "TestResults\feature"
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory $out -Force | Out-Null

$rows = @(); $failures = @(); $red = 0
foreach ($step in $plan.Steps) {
    $name = [IO.Path]::GetFileNameWithoutExtension($step.Project)
    $dir = Join-Path $out $name
    New-Item -ItemType Directory $dir -Force | Out-Null
    Write-Host ">> $name" -ForegroundColor Cyan
    $arguments = @("test", $step.Project, "--no-build", "-nologo", "--logger", "trx;LogFileName=$name.trx", "--results-directory", $dir)
    if ($step.Filter) { $arguments += @("--filter", $step.Filter) }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @arguments | Out-File -LiteralPath (Join-Path $dir "output.log") -Encoding utf8
    $exit = $LASTEXITCODE
    $sw.Stop()

    $result = Read-TrxResult -Directory $dir
    $passed = Test-StepPassed -Result $result -ExitCode $exit
    if (-not $passed) { $red++ }
    $status = if ($passed) { "verde" } else { "ROJO" }
    if ($result) { $counts = "$($result.Total) | $($result.Passed) | $($result.Failed)" } else { $counts = "- | - | sin trx" }
    $rows += "| $name | $status | $counts | $([int]$sw.Elapsed.TotalSeconds) |"
    foreach ($f in $result.Failures) { $failures += "- ``$($f.Test)``: $($f.Message)" }
    $color = if ($passed) { "Green" } else { "Red" }
    Write-Host "   $status (total, pasan, fallan: $counts)" -ForegroundColor $color
}

$lines = @("# Pruebas de la rama, $(Get-Date -Format 'yyyy-MM-dd HH:mm')", "", $stamp, "", "Base: $Base. Duración: $([int]$total.Elapsed.TotalSeconds) s.", "",
    "| Proyecto | Estado | Total | Pasan | Fallan | Seg |", "| --- | --- | ---: | ---: | ---: | ---: |") + $rows
if ($failures) { $lines += @("", "## Fallas", "") + $failures }
if ($plan.Warnings) { $lines += @("", "## Avisos", "") + @($plan.Warnings | ForEach-Object { "- $_" }) }
$report = Join-Path $out "report.md"
$lines | Set-Content -LiteralPath $report -Encoding utf8

foreach ($w in $plan.Warnings) { Write-Host "AVISO: $w" -ForegroundColor Yellow }
Write-Host "Reporte: $report ($([int]$total.Elapsed.TotalSeconds) s)"
if ($red) { Write-Host "$red paso(s) en rojo. Revisa el reporte y output.log." -ForegroundColor Red; exit 1 }
Write-Host "Nivel rápido en verde." -ForegroundColor Green
exit 0
