<a href="https://www.pulumi.com" title="Pulumi - Modern Infrastructure as Code - AWS Azure Kubernetes Containers Serverless">
    <img src="https://www.pulumi.com/images/logo/logo.svg" width="350">
</a>

This repository contains the scripts required to update the Pulumi package on [Windows Package Manager](https://github.com/microsoft/winget-cli).

Written with F# as a dotnet console application which runs in CI. 

### Running locally:
```bash
dotnet run --project ./src -- generate msi
```
This generates a MSI file (windows installer) and a winget manifest file

> NOTE: To actually get an MSI, you need candle.exe/light.exe from WiX tools in your path. These are available in the CI

### Versioning

The version inside the MSI is not the Pulumi version. Windows Installer stores `ProductVersion` as
a packed DWORD (8 bits of major, 8 bits of minor, 16 bits of build), so the highest version it can
express is `255.255.65535` — and Pulumi outgrew that with v3.256.0.

Windows Installer only uses `ProductVersion` to order upgrades, so we map the Pulumi version onto a
synthetic one that keeps increasing. The Pulumi major version gets the MSI major field (offset by
one, so the first mapped version outranks the literal `3.255.0` we shipped before this), and the
minor/patch pair is packed into the remaining two fields in base 100:

| Pulumi  | MSI        |
| ------- | ---------- |
| 3.255.0 | 4.0.25500  |
| 3.256.0 | 4.0.25600  |
| 3.257.1 | 4.0.25701  |
| 4.0.0   | 5.0.0      |

To check the mapping for a given version:
```bash
dotnet run --project ./src -- msi-version 3.256.0
```

Everything else keeps using the real Pulumi version: the release tag, the MSI file name,
`version.txt` and the winget manifest's `PackageVersion`. Because Windows shows the synthetic
version in Add/Remove Programs, the winget manifest also records it as `DisplayVersion` (via
`wingetcreate --display-version`) so that winget can still tell which version is installed.