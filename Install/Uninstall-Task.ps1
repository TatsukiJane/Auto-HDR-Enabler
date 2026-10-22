# Выключает автозапуск Auto HDR и закрывает работающую копию.
$exe = Join-Path $PSScriptRoot '..\dist\AutoHdr.exe' | Resolve-Path
Start-Process $exe -ArgumentList '--exit' -Wait
$p = Start-Process $exe -ArgumentList '--uninstall-task' -Wait -PassThru
if ($p.ExitCode -eq 0) { Write-Output 'Автозапуск выключен' } else { Write-Output "Не удалось выключить автозапуск, код $($p.ExitCode)" }

