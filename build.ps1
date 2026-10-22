# Собирает dist\AutoHdr.exe и родные библиотеки WPF рядом с ним. Нужен .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'AutoHdr.exe'
$installed = Join-Path $env:ProgramFiles 'Auto HDR\AutoHdr.exe'

# Работающая копия держит exe открытым: просим её закрыться. Сигнал может подать любая копия
if (Get-Process AutoHdr -ErrorAction SilentlyContinue) {
    $any = @($exe, $installed) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($any) { Start-Process $any -ArgumentList '--exit' -Wait }
    Start-Sleep -Seconds 3
}

# Чистая папка: посторонние файлы рядом с exe не переживают пересборку. rd не заходит внутрь ссылок на другие папки
if (Test-Path $dist) {
    & cmd.exe /c rd /s /q $dist
    if (Test-Path $dist) { throw "Не удалось очистить $dist" }
}

# Self-contained: на компьютере пользователя не нужен .NET. Родные библиотеки WPF (*_cor3.dll) остаются рядом с exe:
# иначе их пришлось бы распаковывать в %TEMP%, куда пишет любой процесс пользователя
dotnet publish (Join-Path $root 'AutoHdr\AutoHdr.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o $dist -nologo
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с ошибкой $LASTEXITCODE" }

# Автозапуск работает с копией в Program Files: обновляем её и задачу (окно UAC)
if (Get-ScheduledTask -TaskName 'AutoHDR Enabler' -ErrorAction SilentlyContinue) {
    $install = Start-Process $exe -ArgumentList '--install-task' -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Не удалось обновить копию для автозапуска, код $($install.ExitCode)" }
    Write-Output "Обновлено: $installed"
}
Write-Output "Готово: $exe"

