# bug-0666 DIAG: запуск долгоживущего процесса ВНЕ job-объекта shell-вызова. PS 5.1.
# Проверено 25.11 17:03 на хосте: обычный Process.Start-потомок, оставшийся сиротой, снят
# вместе с деревом вызова через ~2 с после конца вызова (битые 12-секундные heartbeat'ы).
# Тот же процесс, созданный Win32_Process.Create (его поднимает WmiPrvSE, вне дерева вызова),
# дожил до своего конца. Поэтому: любой прогон, который должен пережить конец tool-вызова,
# запускать ТОЛЬКО через этот скрипт, иначе «тихая смерть» будет самоубийством харнесса.
param(
    [Parameter(Mandatory = $true)][string]$Command,
    [string]$WorkDir = ''
)

$args = @{ CommandLine = $Command }
if ($WorkDir) { $args['CurrentDirectory'] = $WorkDir }

$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments $args
if ($created.ReturnValue -ne 0) {
    Write-Host ("Win32_Process.Create failed ReturnValue=" + $created.ReturnValue)
    exit 1
}
Write-Host ("detached pid=" + $created.ProcessId + " cmd=" + $Command)
