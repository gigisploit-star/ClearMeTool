# ClearMeTool

Windows desktop tool that does **Route A** on an Intel SPI dump:

- you open **your** full dump
- you open a **clean ME** (another full dump whose ME is already clean, or a raw ME region / RGN)
- it writes a new image where **only the ME region changed**
- the descriptor, GbE and **BIOS region stay byte-for-byte identical**

Serial, UUID and DMI that live in the BIOS region are kept. The optional setup-password checkbox invalidates only the AMI `AMITSESetup` variable (the usual supervisor/user password record) and zeroes its payload. Other NVRAM variables, including `SerialNUM`, are not written.

This is not Intel FIT. It does not ship Intel firmware. It will not invent a clean ME from nothing — you supply one. If `AMITSESetup` is not in the BIOS region, the password option changes nothing and says so.

## Build

Visual Studio 2022 (17.8 or newer) with the **.NET desktop development** workload.

1. Open `ClearMeTool.sln`.
2. Build **Release | Any CPU**.
3. The exe is `ClearMeTool\bin\Release\net8.0-windows\ClearMeTool.exe`.

That exe needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) on the PC you run it on. To make a single file that does not need a separate runtime, in a developer prompt:

```bat
dotnet publish ClearMeTool\ClearMeTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## What the two modes do

**Uninitialize ME in this dump** (default, no second file). Keeps the signed code partitions (`FTPR`, `FTUP`, `NFTP`, `DLMP`, `ROMB`, `PSVN`). Erases state partitions to `0xFF`: `MFS` / `MFSB` (the ME filesystem that holds the machine configuration), `FLOG`, `IVBP`, `UTOK`, and on older firmware `EFFS` / `AFSP`. On the next boot the ME rebuilds an empty filesystem. This is the same split your M710t dumps showed: the code was identical and only those state areas differed. It is not a byte-for-byte Intel FIT factory image. If the board boot-loops, write the original dump back.

**Replace the whole ME region** and **copy state from a clean file** still need a donor ME of the same platform (a full dump or an extracted region). Use those when you already have a known-good clean image.

The ME step is discarded if the descriptor, BIOS or GbE regions would change. The password option runs after that, and it is the only step allowed to write inside the BIOS region.

## Compare

**Compare loaded files** diffs the dump you opened against the clean file, as whole files.

**Compare two files** opens any two images. Accepted extensions are `.bin`, `.rom`, `.fd`, `.rgn` and `.bio` (any other file can be picked from All files). The report shows size, SHA-256, how many bytes differ, a per-region breakdown when both files have an Intel flash descriptor, and the first difference ranges.

## Before you flash

- Keep the original dump. Read the chip twice and compare before you write.
- ME version of the clean file should match yours. The log warns when it does not.
- Replacing ME drops firmware TPM (PTT) state. BitLocker will ask for the recovery key.
- Boot Guard can reject an ME body the OEM did not sign. State-only mode avoids that when the code partitions already match.
- Program the chip with an external programmer. Verify the read-back against the file you just wrote.

## Format notes

The descriptor must sit at the start of a full dump: signature `5A A5 F0 0F` at offset `0x10`. Region bases use the standard flash-region registers (`base = (FLREG & 0x7FFF) << 12`, limit inclusive). ME version is read from the `$MN2` manifest inside `FTPR` as four little-endian `uint16` values (for example `11.6.1.1142`).
