# Сборка надстройки GroupSpecB встроенным компилятором .NET Framework 4.8 (Visual Studio не нужна).
# Использование:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Install] [-InventorVersion 2026]
param([switch]$Install, [string]$InventorVersion = "2026", [string]$OutDir = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$interop = "C:\Program Files\Autodesk\Inventor $InventorVersion\Bin\Public Assemblies\Autodesk.Inventor.Interop.dll"
if (-not (Test-Path $interop)) { throw "Не найден $interop" }
$out = if ($OutDir) { $OutDir } else { Join-Path $root "bin" }   # загруженную в Inventor DLL перезаписать нельзя — для тестов собирайте в другую папку
New-Item -ItemType Directory -Force $out | Out-Null

$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
$refs = @("/link:$interop", "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll",
          "/r:System.Windows.Forms.dll", "/r:System.Xml.dll", "/r:Microsoft.VisualBasic.dll", "/r:Microsoft.CSharp.dll")

& $csc /nologo /target:library /platform:x64 /optimize+ /utf8output /codepage:65001 `
    "/out:$out\GroupSpecB.dll" @refs @src
if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции надстройки" }

& $csc /nologo /target:exe /platform:x64 /utf8output /codepage:65001 `
    "/out:$out\TestRunner.exe" "/r:$out\GroupSpecB.dll" @refs "$root\test\TestRunner.cs"
if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции TestRunner" }

# .addin с абсолютным путём к DLL
$addin = Get-Content "$root\GroupSpecB.addin" -Raw -Encoding UTF8
$addin = $addin -replace '<Assembly>.*</Assembly>', "<Assembly>$out\GroupSpecB.dll</Assembly>"
[IO.File]::WriteAllText("$out\GroupSpecB.addin", $addin, (New-Object Text.UTF8Encoding $true))
Write-Host "Собрано: $out\GroupSpecB.dll"

if ($Install) {
    $dst = "$env:APPDATA\Autodesk\Inventor $InventorVersion\Addins"
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item "$out\GroupSpecB.addin" $dst -Force
    Write-Host "Установлено: $dst\GroupSpecB.addin (перезапустите Inventor)"
}
