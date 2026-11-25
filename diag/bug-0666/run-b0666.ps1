# bug-0666 DIAG: обёртка запуска движка (замещает launch-engine.ps1). PS 5.1, без внешних модулей.
# Пишет CSV-сэмплер памяти + heartbeat по MCP-порту + строку DEATH с exit code, и забирает
# Engine\CrashStart.log (у гварда путь относительный - берём из CWD запуска, а не из «где-то на хосте»).
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$WorkDir = '',
    [string]$OutDir = 'C:\OsEngineDumps\b0666\run',
    [string]$Proxy = '',                 # например 127.0.0.1:8443 - cut-tunnel tr362; пусто = без прокси
    [string[]]$Arguments = @(),
    [int]$IntervalSec = 5,
    [int]$TimeoutMin = 0,                # 0 = ждать сколько потребуется
    [string]$MarkFile = ''               # если задан: новые строки в нём пишутся в CSV как MARK
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $OutDir "sampler-$stamp.csv"
$summary = Join-Path $OutDir "summary-$stamp.txt"

Add-Content -Path $csv -Value 'utc,event,pid,ws_mb,private_mb,committed_mb,commit_limit_mb,note'
function Add-Row([string]$event, [int]$procId, [string]$note) {
    $ws = ''; $pr = ''
    if ($procId -gt 0) {
        try { $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
              if ($p) { $ws = [math]::Round($p.WorkingSet64 / 1MB); $pr = [math]::Round($p.PrivateMemorySize64 / 1MB) } } catch {}
    }
    $com = ''; $lim = ''
    try {
        if (-not $script:MemApi) {
            if (-not ('B0666.OsMem' -as [type])) {
                Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class B0666 {
  [StructLayout(LayoutKind.Sequential)]
  public struct MEMSTAT { public uint dwLength; public uint dwMemoryLoad;
    public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile;
    public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual; }
  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool GlobalMemoryStatusEx(ref MEMSTAT lpStatus);
}
'@ -ErrorAction Stop
            }
            $ms = New-Object B0666+MEMSTAT; $ms.dwLength = [Runtime.InteropServices.Marshal]::SizeOf($ms)
            if ([B0666]::GlobalMemoryStatusEx([ref]$ms)) {
                $com = [math]::Round(($ms.ullTotalPageFile - $ms.ullAvailPageFile) / 1MB)   # commit charge
                $lim = [math]::Round($ms.ullTotalPageFile / 1MB)                            # commit limit
            }
        }
    } catch { }
    Add-Content -Path $csv -Value "$(Get-Date -Format 'o'),$event,$procId,$ws,$pr,$com,$lim,$note"
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
if ($Arguments.Count -gt 0) { $psi.Arguments = ($Arguments -join ' ') }
$psi.WorkingDirectory = if ($WorkDir) { $WorkDir } else { Split-Path -Parent $Exe }
$psi.UseShellExecute = $false
if ($Proxy) { $psi.EnvironmentVariables['HTTPS_PROXY'] = "http://$Proxy" }
$psi.EnvironmentVariables['OSENGINE_DIAG_FIRSTCHANCE'] = '1'

$proc = [System.Diagnostics.Process]::Start($psi)
$started = Get-Date
Add-Row 'START' $proc.Id "cwd=$($psi.WorkingDirectory)"
Write-Host "launched pid=$($proc.Id) cwd=$($psi.WorkingDirectory) proxy=$Proxy out=$csv"

$mcpPort = ''
$settings = Join-Path $psi.WorkingDirectory 'Engine\McpSettings.txt'
if (Test-Path $settings) {
    # Формат McpSettings.txt (McpSettings.Save): строка 1 = порт, строка 2 = API-ключ (не читаем, не логируем),
    # строка 3 = enabled. Отсюда берём ровно первую строку.
    $first = (Get-Content $settings -TotalCount 1)
    $portNum = 0
    if ([int]::TryParse(("$first" -replace '\D', ''), [ref]$portNum) -and $portNum -gt 0) { $mcpPort = "$portNum" }
}
Write-Host "mcp port from McpSettings.txt: $(if ($mcpPort) { $mcpPort } else { 'not found' })"

$misses = 0
$lastMarkLen = 0
while (-not $proc.HasExited) {
    $alive = 'na'
    if ($mcpPort) {
        $alive = 'fail'
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', [int]$mcpPort)
            if ($c.Connected) { $alive = 'ok'; $misses = 0 }
            $c.Close()
        } catch { $misses++ }
    }
    Add-Row 'SAMPLE' $proc.Id "mcp=$alive"
    if ($MarkFile -and (Test-Path $MarkFile)) {
        $marks = @(Get-Content $MarkFile -ErrorAction SilentlyContinue)
        if ($marks.Count -gt $lastMarkLen) {
            foreach ($m in $marks[$lastMarkLen..($marks.Count - 1)]) { Add-Row 'MARK' $proc.Id $m }
            $lastMarkLen = $marks.Count
        }
    }
    if ($misses -eq 3) { Add-Row 'MCP_MISS3' $proc.Id '3 connect fails to MCP port in a row' }
    if ($TimeoutMin -gt 0 -and ((Get-Date) - $started).TotalMinutes -gt $TimeoutMin) {
        Add-Row 'TIMEOUT_KILL' $proc.Id "wrapper timeout $TimeoutMin min"
        $proc.Kill(); break
    }
    Start-Sleep -Seconds $IntervalSec
    if ($alive -eq 'fail' -and -not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { break }
}

$proc.WaitForExit()
# ExitCode иногда недоступен для снятого извне процесса — тогда ориентируемся на «unknown» + свидетели №2/№4
$code = 'unknown'
try { $code = '0x{0:X8}' -f $proc.ExitCode } catch {
    try { $proc.Refresh(); $code = '0x{0:X8}' -f $proc.ExitCode } catch { }
}
$uptime = [math]::Round(((Get-Date) - $started).TotalSeconds)
Add-Row 'DEATH' $proc.Id ("exitcode=$code uptime_s=$uptime mcp_misses=$misses")

$collected = ''
# Свидетель №4: FailFast-семейство на этом хосте всегда оставляет WER 1000/1001 (P1: дампа нет, а события есть).
# Дампу не верим — под OOM он не материализуется.
try {
    $wer = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1001;
            StartTime = $started; EndTime = (Get-Date).AddSeconds(5) } -ErrorAction SilentlyContinue |
        Select-Object -First 4)
    if ($wer.Count -eq 0) { Add-Row 'WER_NONE' $proc.Id 'no Application Error/WER in window' }
    foreach ($e in $wer) {
        $txt = ($e.Message -replace '\r?\n', ' ')
        if ($txt.Length -gt 200) { $txt = $txt.Substring(0, 200) }
        Add-Row 'WER' $proc.Id "id=$($e.Id) $txt"
    }
} catch { Add-Row 'WER_QUERY_FAIL' 0 $_.Exception.Message }

foreach ($f in @('Engine\CrashStart.log', 'Engine\ErrorLog.txt')) {
    $src = Join-Path $psi.WorkingDirectory $f
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $OutDir ("{0}-{1}" -f $stamp, (Split-Path -Leaf $src))) -Force
        $collected += (Split-Path -Leaf $src) + ' '
        Add-Row 'COLLECTED' 0 $src
    }
}

@(
    "exitcode=$code",
    "pid=$($proc.Id)",
    "started=$($started.ToString('o'))",
    "ended=$((Get-Date).ToString('o'))",
    "uptime_s=$uptime",
    "cwd=$($psi.WorkingDirectory)",
    "collected=$collected"
) | Set-Content -Path $summary -Encoding UTF8

Write-Host "DEATH exitcode=$code uptime=${uptime}s -> $csv"
