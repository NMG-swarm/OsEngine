# run.ps1 — прогон bug-0578 харнесса (BUG-0577). Требует чистый worktree движка на проверяемом коммите.
# Примеры:
#   .\run.ps1 -EngineWorktree <путь-к-worktree-fix>                       # fix-режим, ждём exit 0
#   .\run.ps1 -EngineWorktree <путь-к-worktree-master> -Mode base          # base-режим (A/B), ждём exit 0
param(
  [Parameter(Mandatory = $true)][string]$EngineWorktree,
  [ValidateSet('fix', 'base')][string]$Mode = 'fix'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$eng = Join-Path $EngineWorktree 'project\OsEngine\OsEngine.csproj'
if (-not (Test-Path $eng)) { throw "engine csproj not found: $eng" }
$dirty = @(git -C $EngineWorktree status --porcelain | Where-Object { $_ -notmatch '^\?\?' }).Count
if ($dirty -gt 0) { throw "engine worktree dirty ($dirty) — pin uncommitted edits first" }
Write-Output ("ENGINE_HEAD=" + (git -C $EngineWorktree rev-parse HEAD).Trim())

# инкрементал врёт: чистим obj/bin харнесса и Release-obj движка
Remove-Item (Join-Path $here 'obj'), (Join-Path $here 'bin') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $EngineWorktree 'project\OsEngine\obj\Release') -Recurse -Force -ErrorAction SilentlyContinue
dotnet build (Join-Path $here 'bug-0578.Tests.csproj') -c Release -v q --nologo --no-incremental /nodeReuse:false -p:EngineProject=$eng
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$built = Join-Path $here 'bin\Release\net10.0-windows\OsEngine.dll'
Write-Output ('BUILT_ENGINE_SHA=' + (Get-FileHash -Algorithm SHA256 -Path $built).Hash.ToLower())

$exe = Join-Path $here 'bin\Release\net10.0-windows\bug0578-tests.exe'
if ($Mode -eq 'base') { & $exe base } else { & $exe }
Write-Output ('RUN_EXIT=' + $LASTEXITCODE)
