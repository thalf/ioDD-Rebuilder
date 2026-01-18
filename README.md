# iODD Rebuilder (v1.0.0)

Windows tool to rebuild an iODD drive in a single safe flow:

**Confirm → Format → Sequential copy**

Built for large ISO/IMG/VHD/VHDX workflows where copying big files first can improve iODD performance.

---

## Features

- No admin by default (UAC only during formatting)
- Sequential copy with real **bytes-written** progress
- File-based (%) and data-based (bytes) progress
- Prioritizes large disk images (`.iso/.img/.vhd/.vhdx`)
- Log file saved to `%TEMP%`

---

## Requirements

- Windows 10/11  
- .NET Desktop Runtime (matching build)

---

## Usage

1. Select **Source** folder  
2. Select **Destination** drive  
3. Choose filesystem (**exFAT / NTFS / FAT32**)  
4. **START** → confirm → **FORMAT DISK**

> ⚠️ Formatting permanently deletes all data on the destination drive.

---

## Building

Build with Visual Studio (Windows Forms).

```xml
<PropertyGroup>
  <ApplicationIcon>ioDD_Rebuilder.ico</ApplicationIcon>
</PropertyGroup>
