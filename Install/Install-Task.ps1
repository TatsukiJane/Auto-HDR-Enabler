# Включает автозапуск Auto HDR при входе в Windows (задача планировщика с наивысшими правами).
$exe = Join-Path $PSScriptRoot '..\dist\AutoHdr.exe' | Resolve-Path
$p = Start-Process $exe -ArgumentList '--install-task' -Wait -PassThru
if ($p.ExitCode -eq 0) { Write-Output 'Автозапуск включён' } else { Write-Output "Не удалось включить автозапуск, код $($p.ExitCode)" }

