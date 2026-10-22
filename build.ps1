# Собирает dist\AutoHdr.exe. Нужен .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'dist\AutoHdr.exe'

# Работающая копия держит exe открытым: просим её закрыться
if (Get-Process AutoHdr -ErrorAction SilentlyContinue) {
    if (Test-Path $exe) { Start-Process $exe -ArgumentList '--exit' -Wait }
    Start-Sleep -Seconds 3
}

dotnet publish (Join-Path $root 'AutoHdr\AutoHdr.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=embedded -o (Join-Path $root 'dist') -nologo
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с ошибкой $LASTEXITCODE" }
Write-Output "Готово: $exe"

