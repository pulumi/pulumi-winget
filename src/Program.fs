open Octokit
open System
open System.IO
open System.Net.Http
open System.Threading.Tasks

let inline await (task: Task<'t>) = 
    task
    |> Async.AwaitTask
    |> Async.RunSynchronously

let httpClient = new HttpClient()
httpClient.DefaultRequestHeaders.UserAgent.Add(Headers.ProductInfoHeaderValue("PulumiBot", "1.0"))

let github = new GitHubClient(ProductHeaderValue "PulumiBot")

let version (release: Release) = release.Name.Substring(1, release.Name.Length - 1)

type InstallerAsset = { DownloadUrl: string; Sha256: string }

let findWindowsInstaller (release: Release) : Result<InstallerAsset, string> = 
    let currentVersion = version release
    let checksums = 
        release.Assets
        |> Seq.tryFind (fun asset -> asset.Name = $"pulumi-{currentVersion}-checksums.txt")

    let windowsBuild = 
        release.Assets
        |> Seq.tryFind (fun asset -> asset.Name = $"pulumi-v{currentVersion}-windows-x64.zip")

    if checksums.IsNone then 
        Error $"Checksums file pulumi-{currentVersion}-checksums.txt was not found"
    elif windowsBuild.IsNone then
        Error $"Windows build pulumi-v{currentVersion}-windows-x64.zip was not found"
    else 
        let contents = await (httpClient.GetStringAsync checksums.Value.BrowserDownloadUrl)
        contents.Split "\n"
        |> Array.tryFind (fun line -> line.EndsWith $"pulumi-v{currentVersion}-windows-x64.zip")
        |> function 
            | None ->
                Error "Could not find the installer SHA256 for the windows build"
            | Some line -> 
                let parts = line.Split "  "
                let sha265 = parts[0]
                Ok {
                    DownloadUrl = windowsBuild.Value.BrowserDownloadUrl
                    Sha256 = sha265
                }

let createManifest (release: Release) (installer: InstallerAsset) = [|
    $"PackageIdentifier: Pulumi.Pulumi"
    $"PackageName: Pulumi"
    $"PackageVersion: {version release}"
    $"License: Apache License 2.0"
    $"LicenseUrl: https://github.com/pulumi/pulumi/blob/master/LICENSE"
    $"ShortDescription: Pulumi CLI for managing modern infrastructure as code"
    $"PackageUrl: https://www.pulumi.com"
    $"Installers:"
    $"- Architecture: x64"
    $"  InstallerUrl: {installer.DownloadUrl}"
    $"  InstallerSha256: {installer.Sha256}"
    $"  InstallerType: zip"
    "PackageLocale: en-US"
    "ManifestType: singleton"
    "ManifestVersion: 1.0.0"
|]

[<EntryPoint>]
let main (args: string[]) = 
    try
        let latestRelease = await (github.Repository.Release.GetLatest("pulumi", "pulumi"))
        match findWindowsInstaller latestRelease with 
        | Error errorMessage -> 
            printfn "Error occured while creating the manifest file for pulumi CLI:"
            printfn "%s" errorMessage
            1
        
        | Ok windowsInstaller -> 
            let manifest = createManifest latestRelease windowsInstaller
            let output = Path.Combine(__SOURCE_DIRECTORY__, "manifest.yaml")
            File.WriteAllLines(path=output, contents=manifest)
            printfn "Created Pulumi manifest file:"
            manifest |> Seq.iter Console.WriteLine 
            0
    with 
    | error -> 
        printfn "Error occured while creating the manifest file for pulumi CLI:"
        printfn "%s" error.Message
        1