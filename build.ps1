# Сборка надстройки ESKD_Specia_SchlosserV встроенным компилятором .NET Framework 4.8 (Visual Studio не нужна).
# Использование:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Install] [-Test] [-InventorVersion 2026]
#   -Test — собрать и запустить проверки логики без Inventor (test\UnitTests.cs)
param([switch]$Install, [switch]$Test, [string]$InventorVersion = "2026", [string]$OutDir = "")

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
    "/out:$out\ESKD_Specia_SchlosserV.dll" @refs @src
if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции надстройки" }

& $csc /nologo /target:exe /platform:x64 /utf8output /codepage:65001 `
    "/out:$out\TestRunner.exe" "/r:$out\ESKD_Specia_SchlosserV.dll" @refs "$root\test\TestRunner.cs"
if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции TestRunner" }

# проверки логики спецификации — без Inventor API
$logic = @("Model.cs", "SpecLogic.cs", "Layout.cs", "Log.cs", "MaterialLogic.cs") | ForEach-Object { Join-Path "$root\src" $_ }
& $csc /nologo /target:exe /platform:x64 /utf8output /codepage:65001 `
    "/out:$out\UnitTests.exe" /r:System.dll /r:System.Core.dll /r:System.Xml.dll @logic "$root\test\UnitTests.cs"
if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции UnitTests" }
if ($Test) {
    & "$out\UnitTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "Проверки логики не пройдены" }
}

# .addin с абсолютным путём к DLL (в исходном ESKD_Specia_SchlosserV.addin путь относительный)
$addin = Get-Content "$root\ESKD_Specia_SchlosserV.addin" -Raw -Encoding UTF8
$addin = $addin -replace '<Assembly>.*</Assembly>', "<Assembly>$out\ESKD_Specia_SchlosserV.dll</Assembly>"
[IO.File]::WriteAllText("$out\ESKD_Specia_SchlosserV.addin", $addin, (New-Object Text.UTF8Encoding $true))
Write-Host "Собрано: $out\ESKD_Specia_SchlosserV.dll"

if ($Install) {
    $dst = "$env:APPDATA\Autodesk\Inventor $InventorVersion\Addins"
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item "$out\ESKD_Specia_SchlosserV.addin" $dst -Force
    # прежнее имя надстройки (тот же ClassId) — иначе Inventor увидит её дважды
    Remove-Item "$dst\GroupSpecB.addin" -ErrorAction SilentlyContinue
    Write-Host "Установлено: $dst\ESKD_Specia_SchlosserV.addin (перезапустите Inventor)"
}
