# Builds TouchpadLock.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName
$ico = Join-Path $root 'src\app.ico'
$common = @('/nologo', '/codepage:65001', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
if (-not (Test-Path $ico)) {
    $tool = Join-Path $env:TEMP 'TouchpadLock-icontool.exe'
    & $csc @common /target:exe "/out:$tool" $src
    if ($LASTEXITCODE) { throw 'Icon tool build failed' }
    & $tool --export-icon $ico
    Remove-Item $tool
}
& $csc @common /target:winexe /optimize+ "/out:$root\TouchpadLock.exe" "/win32icon:$ico" "/win32manifest:$root\src\app.manifest" $src
if ($LASTEXITCODE) { throw 'Build failed' }
Write-Host "Built $root\TouchpadLock.exe"
