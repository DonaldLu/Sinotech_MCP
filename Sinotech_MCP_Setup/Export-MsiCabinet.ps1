param([Parameter(Mandatory)][string]$Msi,[Parameter(Mandatory)][string]$Stream,[Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
if(!('SinotechMsiCabinet' -as [type])){
 Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class SinotechMsiCabinet {
 [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiOpenDatabase(string path, IntPtr mode, out uint db);
 [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiDatabaseOpenView(uint db, string sql, out uint view);
 [DllImport("msi.dll")] static extern uint MsiViewExecute(uint view, uint record);
 [DllImport("msi.dll")] static extern uint MsiViewFetch(uint view, out uint record);
 [DllImport("msi.dll")] static extern uint MsiRecordReadStream(uint record, uint field, byte[] buffer, ref uint size);
 [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
 static void Check(uint code) { if(code != 0) throw new IOException("MSI stream error " + code); }
 public static void Export(string msi, string name, string destination) {
  uint db=0, view=0, record=0;
  try {
   Check(MsiOpenDatabase(msi, IntPtr.Zero, out db));
   Check(MsiDatabaseOpenView(db, "SELECT `Data` FROM `_Streams` WHERE `Name`='" + name.Replace("'", "''") + "'", out view));
   Check(MsiViewExecute(view, 0)); Check(MsiViewFetch(view, out record));
   using(var file=new FileStream(destination, FileMode.CreateNew)) {
    var buffer=new byte[65536];
    while(true) { uint size=(uint)buffer.Length; Check(MsiRecordReadStream(record,1,buffer,ref size)); if(size==0) break; file.Write(buffer,0,(int)size); }
   }
  } finally { if(record!=0) MsiCloseHandle(record); if(view!=0) MsiCloseHandle(view); if(db!=0) MsiCloseHandle(db); }
 }
}
'@
}
[SinotechMsiCabinet]::Export([IO.Path]::GetFullPath($Msi),$Stream,[IO.Path]::GetFullPath($Destination))
