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