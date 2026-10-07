<a href="https://www.pulumi.com" title="Pulumi - Modern Infrastructure as Code - AWS Azure Kubernetes Containers Serverless">
    <img src="https://www.pulumi.com/images/logo/logo.svg" width="350">
</a>

This repository contains the scripts required to update the Pulumi package on [Windows Package Manager](https://github.com/microsoft/winget-cli).

Written with F# as a dotnet console application which runs in CI. 

### Running locally:
```bash
dotnet run --project ./src -- generate msi
```
This generates an MSI file (Windows installer). The release workflow then publishes it and updates the winget
manifest with [wingetcreate](https://github.com/microsoft/winget-create).

> NOTE: To actually get an MSI, you need candle.exe/light.exe from WiX tools in your path. These are available in the CI

### Versioning

The version inside the MSI is not the Pulumi version. Windows Installer stores `ProductVersion` as
a packed DWORD (8 bits of major, 8 bits of minor, 16 bits of build), so the highest version it can
express is `255.255.65535` — and Pulumi outgrew that with v3.256.0.

Windows Installer only uses `ProductVersion` to order upgrades, so we map the Pulumi version onto a
synthetic one that keeps increasing. The Pulumi major version gets the MSI major field (offset by
one, so the first mapped version outranks the literal `3.255.0` we shipped before this), and the
minor/patch pair is packed into the remaining two fields in base 100:

| Pulumi   | MSI        |                                            |
| -------- | ---------- | ------------------------------------------ |
| 3.255.0  | 4.0.25500  |                                            |
| 3.256.0  | 4.0.25600  |                                            |
| 3.257.1  | 4.0.25701  |                                            |
| 3.649.99 | 4.0.64999  | last of a generation                       |
| 3.650.0  | 4.1.0      | rolls over into the next MSI minor version |
| 4.0.0    | 5.0.0      | a Pulumi major bump outranks every 3.x     |

To check the mapping for a given version:
```bash
dotnet run --project ./src -- msi-version 3.256.0
```

Everything else keeps using the real Pulumi version: the release tag, the MSI file name,
`version.txt` and the winget manifest's `PackageVersion`.

That includes the version shown in Add/Remove Programs. Windows normally derives it from
`ProductVersion`, and there is no property to override it (`ARPDISPLAYVERSION` does not exist), but
`RegisterProduct` only writes it into the product's uninstall key. The installer writes the real
Pulumi version over the top afterwards, by moving the standard `WriteRegistryValues` action from
sequence 5000 to 6150 so that it runs after `RegisterProduct` at 6100. Keep that in mind before
adding registry values to this package: they will all be written late.

### Moniker

The manifest gives the package the moniker `pulumi`, so `winget install pulumi` resolves straight to
`Pulumi.Pulumi` rather than relying on a name search. `wingetcreate update` copies the previous
version's metadata forward but has no way to set a moniker, so the release workflow saves the updated
manifest with `--out`, runs `ensure-moniker` against it, and then submits it. `ensure-moniker` adds the
moniker to the defaultLocale manifest if it's missing and leaves every other line of the file as is.

To try it against a manifest directory:
```bash
dotnet run --project ./src -- ensure-moniker <path to manifest directory>
```
