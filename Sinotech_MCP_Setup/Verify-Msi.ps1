param([ValidateSet('2024','2025','2026')][string[]]$RevitVersion=@('2024','2025','2026'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$version=(Get-Content (Join-Path $root 'server/package.json') -Raw | ConvertFrom-Json).version
$installer=New-Object -ComObject WindowsInstaller.Installer
foreach($year in $RevitVersion){
 $msi=Join-Path $PSScriptRoot "Release/Sinotech_MCP_Revit${year}_$version.msi"
 if(!(Test-Path -LiteralPath $msi)){throw "Missing MSI: $msi"}
 $db=$installer.OpenDatabase($msi,0) # Read-only: does not install/register anything.
 function Rows([string]$sql,[string[]]$columns){
  $view=$db.OpenView($sql);[void]$view.Execute()
  try {while($null -ne ($record=$view.Fetch())){
   $data=[ordered]@{};for($i=0;$i -lt $columns.Count;$i++){$data[$columns[$i]]=$record.StringData($i+1)}
   [pscustomobject]$data
  }}finally{[void]$view.Close()}
 }
 $properties=@{};foreach($r in (Rows 'SELECT `Property`,`Value` FROM `Property`' @('Name','Value'))){$properties[$r.Name]=$r.Value}
 if($properties.ProductVersion -ne $version){throw 'Wrong product version'}
 if($properties.ALLUSERS -eq '1'){throw 'MSI must install for the current user'}
 if($properties.ProductName -ne "Sinotech_MCP for Revit $year"){throw 'Incorrect Windows Apps display name'}
 if($properties.ARPSYSTEMCOMPONENT -or $properties.ARPNOREMOVE){throw 'MSI hides registration or disables uninstall'}
 if($properties.ARPNOMODIFY -ne '1' -or $properties.MSIINSTALLPERUSER -ne '1'){throw 'MSI uninstall/per-user settings are missing'}
 $sequence=@(Rows 'SELECT `Action`,`Condition`,`Sequence` FROM `InstallExecuteSequence`' @('Action','Condition','Sequence'))
 foreach($requiredAction in @('RegisterProduct','PublishProduct','PublishFeatures')){
  if(!($sequence | Where-Object {$_.Action -eq $requiredAction -and [int]$_.Sequence -gt 0 -and !$_.Condition})){throw "MSI registration action missing or conditional: $requiredAction"}
 }
 $actions=@(Rows 'SELECT `Source`,`Target` FROM `CustomAction`' @('Source','Target'))
 $payloadRoot="[AppDataFolder]Autodesk\Revit\Addins\Sinotech_MCP"
 $registrationRoot="[AppDataFolder]Autodesk\Revit\Addins\$year"
 if(!($actions | Where-Object {$_.Source -eq 'TARGETDIR' -and $_.Target -eq $payloadRoot})){throw 'Incorrect payload root'}
 if(!($actions | Where-Object {$_.Source -eq 'SINOTECHREGISTERDIR' -and $_.Target -eq $registrationRoot})){throw 'Incorrect registration root'}
 $directories=@{};foreach($r in (Rows 'SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`' @('Id','Parent','Name'))){$directories[$r.Id]=$r}
 foreach($requiredRoot in @('TARGETDIR','SINOTECHREGISTERDIR')){if(!$directories.ContainsKey($requiredRoot)){throw "Required MSI directory is missing: $requiredRoot"}}
 $components=@{};foreach($r in (Rows 'SELECT `Component`,`Directory_` FROM `Component`' @('Id','Directory'))){$components[$r.Id]=$r.Directory}
 function RelativeDirectory([string]$id){
  $parts=@();$seen=@{}
  while($id -ne 'TARGETDIR' -and $id -ne 'SINOTECHREGISTERDIR'){
   if($seen.ContainsKey($id) -or !$directories.ContainsKey($id)){throw "Invalid directory chain: $id"}
   $seen[$id]=$true;$d=$directories[$id];$name=($d.Name.Split(':')[0].Split('|') | Select-Object -Last 1)
   if($name -ne '.'){$parts=@($name)+$parts};$id=$d.Parent
  }
  return @{root=$id;relative=($parts -join '/')}
 }
 $files=@(Rows 'SELECT `File`,`Component_`,`FileName` FROM `File`' @('Id','Component','Name'))
 $index=Get-Content (Join-Path $PSScriptRoot "generated/$year/payload-index.json") -Raw | ConvertFrom-Json
 $actual=@{}
 foreach($file in $files){
  $name=($file.Name.Split('|') | Select-Object -Last 1)
  if($name -match '^(RevitAPI.*\.dll|commandRegistry.json|auth.json)$'){throw "Forbidden MSI file: $name"}
  $dir=RelativeDirectory $components[$file.Component]
  $relative=(@($dir.relative,$name) | Where-Object {$_}) -join '/'
  if($dir.root -eq 'SINOTECHREGISTERDIR'){
   if($relative -ne 'Sinotech_MCP.addin'){throw 'Unexpected registration file'}
  }else{$actual[$relative]=$file.Id}
 }
 if($files.Count -ne $index.files.Count+1){throw 'MSI file count differs from reviewed payload'}
 foreach($file in $index.files){if(!$actual.ContainsKey("$year/$($file.relative)")){throw "MSI missing payload file: $($file.relative)"}}
 foreach($required in @('RevitMCPPlugin.dll','RevitMCPSDK.dll','Newtonsoft.Json.dll','Commands/RevitMCPCommandSet/command.json',"Commands/RevitMCPCommandSet/$year/RevitMCPCommandSet.dll",'runtime/node_modules/better-sqlite3/package.json','runtime/node/node.exe','runtime/build/chat/index.js')){
  if(!$actual.ContainsKey("$year/$required")){throw "Missing runtime dependency: $required"}
 }
 $media=@(Rows 'SELECT `Cabinet` FROM `Media`' @('Cabinet'))
 if($media | Where-Object { !$_.Cabinet.StartsWith('#') }){throw 'MSI requires external cabinet files'}
 # Verify the bytes actually inside the MSI, rather than just its File table
 # or the staging folder. This does not install/register the product.
 $qa=Join-Path $PSScriptRoot ("generated/qa/msi-content-$year-"+[Guid]::NewGuid().ToString('N'))
 $expanded=Join-Path $qa 'cabinet'
 $verified=Join-Path $qa 'verified-dlls'
 New-Item -ItemType Directory -Path $expanded,$verified -Force | Out-Null
 $cabNumber=0
 foreach($cab in $media){
  $cabPath=Join-Path $qa ((++$cabNumber).ToString()+'.cab')
  & (Join-Path $PSScriptRoot 'Export-MsiCabinet.ps1') -Msi $msi -Stream $cab.Cabinet.Substring(1) -Destination $cabPath
  & "$env:SystemRoot/System32/expand.exe" '-F:*' $cabPath $expanded *> (Join-Path $qa "expand-$cabNumber.log")
  if($LASTEXITCODE -ne 0){throw 'Cannot extract embedded MSI cabinet'}
 }
 foreach($file in $index.files){
  $stored=Join-Path $expanded $actual["$year/$($file.relative)"]
  if(!(Test-Path -LiteralPath $stored) -or (Get-FileHash -LiteralPath $stored -Algorithm SHA256).Hash -ne $file.sha256){throw "MSI content mismatch: $($file.relative)"}
  if($file.relative -match '^[^/]+\.dll$'){Copy-Item -LiteralPath $stored -Destination (Join-Path $verified $file.relative)}
 }
 [xml]$manifest=Get-Content (Join-Path $PSScriptRoot "registration/$year/Sinotech_MCP.addin") -Raw
 $manifestFile=$files | Where-Object {($_.Name.Split('|') | Select-Object -Last 1) -eq 'Sinotech_MCP.addin'}
 if(@($manifestFile).Count -ne 1){throw 'Expected one add-in manifest'}
 $manifestSource=Join-Path $PSScriptRoot "registration/$year/Sinotech_MCP.addin"
 if((Get-FileHash -LiteralPath (Join-Path $expanded $manifestFile.Id)).Hash -ne (Get-FileHash -LiteralPath $manifestSource).Hash){throw 'Embedded add-in manifest differs from reviewed registration'}
 if($manifest.RevitAddIns.AddIn.Assembly -ne "..\Sinotech_MCP\$year\RevitMCPPlugin.dll"){throw 'Incorrect relative Assembly path'}
 if($properties.UpgradeCode -eq $lastUpgrade){throw 'Revit versions must have different upgrade identities'}
 $lastUpgrade=$properties.UpgradeCode
 [pscustomobject]@{RevitVersion=$year;Msi=$msi;FileCount=$files.Count;Version=$properties.ProductVersion;DisplayName=$properties.ProductName;UninstallEnabled=$true;PerUser=$true;EmbeddedCabinet=$true;PathsVerified=$true;NoSharedToken=$true} | ConvertTo-Json
}

