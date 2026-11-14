param([Parameter(Mandatory=$true)][string[]]$Executable)
$ErrorActionPreference = 'Stop'
# Inspect native PE icon resources directly. No shell cache and no disk operations.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class IconResources {
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
 [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
 [DllImport("kernel32.dll")] public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
 [DllImport("kernel32.dll")] public static extern IntPtr LockResource(IntPtr resource);
 [DllImport("kernel32.dll")] public static extern uint SizeofResource(IntPtr module, IntPtr resource);
 [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module);
 public static byte[] Read(IntPtr module, int id, int type) {
  var resource=FindResource(module,(IntPtr)id,(IntPtr)type);
  if(resource==IntPtr.Zero) throw new Exception("Missing icon resource");
  var bytes=new byte[SizeofResource(module,resource)];
  Marshal.Copy(LockResource(LoadResource(module,resource)),bytes,0,bytes.Length); return bytes;
 }
}
"@
$ico = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../ioDD_REBUILDER_256x256.ico'))
if ([BitConverter]::ToUInt16($ico,0) -ne 0 -or [BitConverter]::ToUInt16($ico,2) -ne 1) { throw 'Invalid ICO header' }
$count = [BitConverter]::ToUInt16($ico,4)
if ($count -ne 6) { throw 'Expected six icon sizes' }
foreach ($path in $Executable) {
 $module = [IconResources]::LoadLibraryEx((Resolve-Path $path).Path,[IntPtr]::Zero,2)
 if ($module -eq [IntPtr]::Zero) { throw "Cannot inspect $path" }
 try {
  $group = [IconResources]::Read($module,32512,14)
  if ([BitConverter]::ToUInt16($group,4) -ne $count) { throw 'Icon count mismatch' }
  for ($i=0; $i -lt $count; $i++) {
   $offset=6+16*$i; $entry=6+14*$i
   $length=[BitConverter]::ToInt32($ico,$offset+8); $start=[BitConverter]::ToInt32($ico,$offset+12)
   if ($start -lt 0 -or $length -lt 1 -or $start+$length -gt $ico.Length) { throw 'Invalid ICO bounds' }
   if ($ico[$offset] -ne $group[$entry] -or $ico[$offset+1] -ne $group[$entry+1]) { throw 'Icon dimensions mismatch' }
   $id=[BitConverter]::ToUInt16($group,$entry+12)
   $actual=[IconResources]::Read($module,$id,3)
   $expected=$ico[$start..($start+$length-1)]
   if ([Convert]::ToBase64String($actual) -ne [Convert]::ToBase64String($expected)) { throw "Icon pixels differ in $path" }
  }
  Write-Host "PASS: all six native icon resources match the original ICO: $path"
 } finally { [void][IconResources]::FreeLibrary($module) }
}
