param([ValidateSet('2024','2025','2026')][string[]]$RevitVersion=@('2024','2025','2026'),[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$vswhere='C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -property installationPath
$devenv=Join-Path $vs 'Common7/IDE/devenv.com'
$fix=Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/VSI/DisableOutOfProcBuild/DisableOutOfProcBuild.exe'
if(!(Test-Path -LiteralPath $fix)){throw 'Install Microsoft Visual Studio Installer Projects extension first.'}
Push-Location $vs
try { & $fix } finally { Pop-Location }
if($LASTEXITCODE -ne 0){throw 'Failed to enable command-line setup builds.'}
Push-Location $root
try {
 if(!$SkipBuild){
  Push-Location server
  try{& npm.cmd run build;if($LASTEXITCODE -ne 0){throw 'Node build failed'}}finally{Pop-Location}
 }
 foreach($year in $RevitVersion){
  if(!$SkipBuild){
   $cfg="Release R$($year.Substring(2))"
   # The command set deploys into the add-in output, so build the add-in first.
   foreach($project in 'src/Sinotech.Revit.Addin/RevitMCPPlugin.csproj','src/Sinotech.Revit.Commands/RevitMCPCommandSet.csproj','src/Sinotech.Revit.Extensions/Sinotech.Revit.Extensions.csproj'){
    & dotnet build $project -c $cfg
    if($LASTEXITCODE -ne 0){throw "Revit $year compilation failed: $project"}
   }
  }
  & (Join-Path $PSScriptRoot 'Prepare-Payload.ps1') -RevitVersion $year
  $solution=Join-Path $PSScriptRoot "generated/$year/Sinotech_MCP_$year.sln"
  $log=Join-Path $PSScriptRoot "generated/$year/build.log"
  & $devenv $solution /Rebuild Release /Out $log > (Join-Path $PSScriptRoot "generated/$year/console.log") 2>&1
  if($LASTEXITCODE -ne 0){throw "MSI build failed: $log"}
  $version=(Get-Content server/package.json -Raw | ConvertFrom-Json).version
  if(!(Test-Path -LiteralPath (Join-Path $PSScriptRoot "Release/Sinotech_MCP_Revit${year}_$version.msi"))){throw "Visual Studio skipped the setup project. Inspect $log"}
  # Isolate MSI writer COM handles before opening the same package for review.
  & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'Set-MsiRegistration.ps1') -RevitVersion $year
  if($LASTEXITCODE -ne 0){throw "MSI registration settings failed for $year"}
  & (Join-Path $PSScriptRoot 'Verify-Msi.ps1') -RevitVersion $year
 }
}finally{Pop-Location}
