# 用法: .\scripts\build.ps1 -Revit 2026 [-Install]
# 建置 plugin + commandset，輸出至 dist\<版本>\，加上 -Install 時複製到 %AppData%\Autodesk\Revit\Addins\<版本>\
param(
    [ValidateSet('2020','2021','2022','2023','2024','2025','2026')][string]$Revit = '2026',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cfg = "Release R$($Revit.Substring(2))"

dotnet build "$root\plugin\RevitMCPPlugin.csproj" -c $cfg
if ($LASTEXITCODE) { throw 'plugin 建置失敗' }
dotnet build "$root\commandset\RevitMCPCommandSet.csproj" -c $cfg
if ($LASTEXITCODE) { throw 'commandset 建置失敗' }

$src = "$root\plugin\bin\AddIn $Revit $cfg"
$dist = "$root\dist\$Revit"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null
Copy-Item "$src\*" $dist -Recurse
Write-Host "輸出: $dist"

if ($Install) {
    $dest = "$env:AppData\Autodesk\Revit\Addins\$Revit"
    Copy-Item "$dist\*" $dest -Recurse -Force
    Write-Host "已安裝至: $dest"
}
