param(
    [string]$PlatformToolsZip,
    [switch]$Test,
    [switch]$Package
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler is required.' }
$dist = Join-Path $root 'dist\SamsungUsbInstaller'
$buildWork = Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $dist,$buildWork | Out-Null
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.IO.Compression.dll') |
    ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') |
    ForEach-Object { '/reference:' + (Join-Path $framework ('WPF\' + $_)) }
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:' + (Join-Path $dist 'Main.exe')),
    ('/resource:' + (Join-Path $root 'src\MainWindow.xaml') + ',MainWindow.xaml'),
    ('/win32manifest:' + (Join-Path $root 'src\app.manifest'))) + $references + @(
    (Join-Path $root 'src\App.cs'), (Join-Path $root 'src\Core.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'src\App.config') -Destination (Join-Path $dist 'Main.exe.config')
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $dist
if ($PlatformToolsZip) {
    $zipPath = (Resolve-Path -LiteralPath $PlatformToolsZip).Path
    $expected = (Get-Content -LiteralPath (Join-Path $root 'platform-tools.sha256') -Raw).Trim()
    $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw 'Platform Tools checksum differs from the reviewed version. Verify its origin and update platform-tools.sha256 intentionally.' }
    $staging = Join-Path $buildWork 'platform-tools-staging'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $staging -Force
    # Skip identical files: an existing local ADB server can keep adb.exe open.
    Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($staging.Length + 1)
        $destination = Join-Path $dist $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        if (-not (Test-Path -LiteralPath $destination) -or
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
    }
}
if ($Test) {
    $coreRefs = @('/reference:' + (Join-Path $framework 'System.IO.Compression.dll'))
    & $compiler /nologo /target:exe /platform:x64 ('/out:' + (Join-Path $buildWork 'FakeAdb.exe')) (Join-Path $root 'tests\FakeAdb.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test process compilation failed.' }
    & $compiler /nologo /target:exe /platform:x64 ('/out:' + (Join-Path $buildWork 'Tests.exe')) @coreRefs (Join-Path $root 'src\Core.cs') (Join-Path $root 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Tests compilation failed.' }
    & (Join-Path $buildWork 'Tests.exe') | Tee-Object -FilePath (Join-Path $buildWork 'test-results.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $preview = Start-Process -FilePath (Join-Path $dist 'Main.exe') -ArgumentList @('--preview',('"' + (Join-Path $buildWork 'previews') + '"')) -PassThru -Wait -WindowStyle Hidden
    if ($preview.ExitCode -ne 0) { throw 'UI preview rendering failed; see dist/SamsungUsbInstaller/startup-error.txt.' }
}
if ($Package) {
    if (-not (Test-Path -LiteralPath (Join-Path $dist 'platform-tools\adb.exe'))) { throw 'Supply the verified official Windows Platform Tools ZIP to package a complete app.' }
    $zip = Join-Path $root 'dist\SamsungUsbInstaller-Windows-x64.zip'
    Compress-Archive -Path $dist -DestinationPath $zip -Force
    Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Format-List
}
Write-Output ('Built: ' + (Join-Path $dist 'Main.exe'))
