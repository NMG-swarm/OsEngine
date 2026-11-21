# bug-0537 — прогон юнит-харнесса маппинга ISS (ТЗ-ред.2 F1–F5)
# usage: .\run.ps1 [-Lib <путь к bin клона OsEngine>]
# До фикса ожидаем exit=1 (RED). После фикса (fix/bug-0530): пересобрать
# OsEngine в клоне и прогнать с -Lib на свежую сборку; гейды sha — в README.
param(
    [string]$Lib = 'C:\Users\Admin\Desktop\NMG\Agents\Deployments\FixPipeline\App\OsEngine\project\OsEngine\bin\Debug'
)
$ErrorActionPreference = 'Stop'
dotnet build "$PSScriptRoot\bug-0537.harness.csproj" -v q -nologo
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED'; exit 2 }
dotnet "$PSScriptRoot\bin\Debug\net10.0-windows\bug-0537.harness.dll" $Lib
$rc = $LASTEXITCODE
Write-Host "harness exit=$rc (0 = all GREEN, 1 = RED present, 2 = harness fatal)"
exit $rc
