# Builds dist\AutoHdr.exe and the WPF native libraries next to it. Requires the .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'AutoHdr.exe'
$installed = Join-Path $env:ProgramFiles 'Auto HDR\AutoHdr.exe'

# A running copy keeps the exe open: ask it to close. Any copy can send the signal
if (Get-Process AutoHdr -ErrorAction SilentlyContinue) {
    $any = @($exe, $installed) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($any) { Start-Process $any -ArgumentList '--exit' -Wait }
    Start-Sleep -Seconds 3
}

# Clean folder: stray files next to the exe do not survive a rebuild. rd does not follow links to other folders
if (Test-Path $dist) {
    & cmd.exe /c rd /s /q $dist
    if (Test-Path $dist) { throw "Could not clean $dist" }
}

# Self-contained: the user's PC does not need .NET. The WPF native libraries (*_cor3.dll) stay next to the exe:
# otherwise they would be extracted to %TEMP%, which any user process can write to
dotnet publish (Join-Path $root 'AutoHdr\AutoHdr.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o $dist -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with code $LASTEXITCODE" }

# Autostart runs the copy in Program Files: update it and the task (UAC prompt)
if (Get-ScheduledTask -TaskName 'AutoHDR Enabler' -ErrorAction SilentlyContinue) {
    $install = Start-Process $exe -ArgumentList '--install-task' -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Could not update the autostart copy, code $($install.ExitCode)" }
    Write-Output "Updated: $installed"
}
Write-Output "Done: $exe"

