open Octokit
open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Threading.Tasks
open System.Xml.Linq
open Fake.Core
open System.Security.Cryptography

let inline await (task: Task<'t>) = 
    task
    |> Async.AwaitTask
    |> Async.RunSynchronously

let httpClient = new HttpClient()
httpClient.DefaultRequestHeaders.UserAgent.Add(Headers.ProductInfoHeaderValue("PulumiBot", "1.0"))

let github = new GitHubClient(ProductHeaderValue "PulumiBot")
github.Credentials <- Credentials(Environment.GetEnvironmentVariable "GITHUB_TOKEN")

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

let createManifest (version: string) (installer: InstallerAsset) = [|
    $"PackageIdentifier: Pulumi.Pulumi"
    $"PackageName: Pulumi"
    $"PackageVersion: {version}"
    $"License: Apache License 2.0"
    $"LicenseUrl: https://github.com/pulumi/pulumi/blob/master/LICENSE"
    $"ShortDescription: Pulumi CLI for managing modern infrastructure as code"
    $"PackageUrl: https://www.pulumi.com"
    $"Installers:"
    $"- Architecture: x64"
    $"  InstallerUrl: {installer.DownloadUrl}"
    $"  InstallerSha256: {installer.Sha256}"
    $"  InstallerType: msi"
    "PackageLocale: en-US"
    "ManifestType: singleton"
    "ManifestVersion: 1.0.0"
|]

let cwd = __SOURCE_DIRECTORY__

let resolvePath (relativePaths: string list) =  Path.Combine [| 
    yield cwd
    yield! relativePaths
|]

type Shell with 
    static member exec(cmd: string, args: string) = 
        let exitCode = Shell.Exec(cmd, args, cwd)
        if exitCode <> 0
        then failwithf "Failed to execute '%s %s'" cmd args

let latestMsiRelease() = 
    let releases = await (github.Repository.Release.GetAll("pulumi", "pulumi-winget"))
    if releases.Count = 0 then 
        None
    else 
        releases
        |> Seq.maxBy (fun release -> release.CreatedAt)
        |> Some


let generateMsi() = 
    let latestRelease = await (github.Repository.Release.GetLatest("pulumi", "pulumi"))
    match findWindowsInstaller latestRelease with 
    | Error errorMessage -> 
        printfn "Error occured while creating the manifest file for pulumi CLI:"
        printfn "%s" errorMessage
        1

    | Ok windowsInstaller -> 
        // Download ZIP file
        let pulumiZip = await (httpClient.GetByteArrayAsync(windowsInstaller.DownloadUrl))
        let pulumiZipOutput = resolvePath [ "pulumi.zip" ]
        File.WriteAllBytes(pulumiZipOutput, pulumiZip)
        // Unzip into ./pulumi
        let pulumiUnzipped = resolvePath [ "pulumi" ]
        ZipFile.ExtractToDirectory(pulumiZipOutput, pulumiUnzipped)
        
        let filesFromUnzippedArchive = Directory.EnumerateFiles (resolvePath [ "pulumi"; "pulumi"; "bin" ])

        let fileId (filePath: string) = 
            let fileName = Path.GetFileName filePath
            // dashes are illegal in WiX files
            // Identifiers may contain ASCII characters A-Z, a-z, digits, underscores (_), or periods (.).  Every identifier must begin with either a letter or an underscore.
            fileName.Replace("-", "_")

        // Schema of allowed elements
        // https://wixtoolset.org/documentation/manual/v3/xsd/wix/wix.html
        let wixDefinition = XDocument [
            Wix.root [
                Wix.product (version latestRelease) [
                    
                    Wix.package [ 
                        Wix.attr "Platform" "x64"
                        Wix.attr "Description" "Pulumi CLI for managing cloud infrastructure"
                        Wix.attr "InstallerVersion" "200"
                        Wix.attr "Compressed" "yes"
                    ]

                    Wix.mediaTemplate [ Wix.attr "EmbedCab" "yes" ]

                    Wix.directory "TARGETDIR" "SourceDir" [
                        Wix.directoryId "ProgramFilesFolder" [
                            Wix.directory "PULUMIDIR" "Pulumi" []
                        ]
                    ]

                    Wix.directoryRef "PULUMIDIR" [
                        for file in filesFromUnzippedArchive do
                            Wix.component' $"component_{fileId file}" [
                                Wix.file (fileId file) file
                            ]

                        Wix.component' "SetEnvironment" [
                            Wix.createFolder()
                            // Add install folder to PATH
                            Wix.updateEnvironmentPath "PULUMIDIR"
                        ]
                    ]

                    Wix.feature "MainInstaller" "Installer" [
                        for file in filesFromUnzippedArchive do
                        Wix.componentRef $"component_{fileId file}"
                    ]

                    Wix.feature "UpdatePath" "Update PATH" [
                        Wix.componentRef "SetEnvironment"
                    ]
                ]
            ]
        ]

        let wixOutput = resolvePath [ "PulumiInstaller.wxs" ]

        wixDefinition.Save wixOutput

        printfn "Written WixInstaller definition:"

        System.Console.WriteLine(File.ReadAllText wixOutput)

        Shell.exec("candle.exe", "PulumiInstaller.wxs")
        Shell.exec("light.exe", $"PulumiInstaller.wixobj -o pulumi-{version latestRelease}-windows-x64.msi")

        let msi = resolvePath [ $"pulumi-{version latestRelease}-windows-x64.msi" ]

        let info = FileInfo msi

        printfn "Succesfully created MSI at '%s' (%d bytes)" msi info.Length

        match latestMsiRelease() with 
        | Some msiRelease when version msiRelease = version latestRelease -> 
            printfn "Version v%s of Pulumi MSI is already published, skipping..." (version msiRelease)
            1

        | _ ->
            printfn "Publishing asset to github..."
            let sha256Algo = HashAlgorithm.Create("SHA256")
            let sha256 = sha256Algo.ComputeHash(new MemoryStream(File.ReadAllBytes msi))
            let releaseInfo = NewRelease($"v{version latestRelease}")
            let msiRelease = await (github.Repository.Release.Create("pulumi", "pulumi-winget", releaseInfo))
            let releaseAsset = ReleaseAssetUpload()
            releaseAsset.FileName <- Path.GetFileName msi
            releaseAsset.ContentType <- "application/msi"
            releaseAsset.RawData <- File.OpenRead(msi)

            let uploadResult = await (github.Repository.Release.UploadAsset(msiRelease, releaseAsset))
            printfn $"Released {version latestRelease}: {uploadResult.BrowserDownloadUrl}"
            let installerAsset = {
                DownloadUrl = uploadResult.BrowserDownloadUrl
                Sha256 = BitConverter.ToString(sha256).Replace("-", "")
            }

            let manifest = createManifest (version latestRelease) installerAsset
            let manifestOutput = resolvePath [ "manifest.yaml" ]
            File.WriteAllLines(path=manifestOutput, contents=manifest)
            printfn "Created Pulumi/Winget manifest file:"
            manifest |> Seq.iter Console.WriteLine 
            0


[<EntryPoint>]
let main (args: string[]) = 
    try
        match args with 
        | [| "generate"; "msi" |] -> generateMsi()
        | otherwise -> 
            printfn "Unknown arguments provided: %A" otherwise
            0
    with 
    | :? AggregateException as aggregateError when aggregateError.InnerExceptions.Count = 1 -> 
        match aggregateError.InnerExceptions[0] with 
        | :? Octokit.ApiException as githubError -> 
            printfn "Error occured executing github operation:"
            printfn "%s" githubError.ApiError.Message
            for error in githubError.ApiError.Errors do
                printfn "(%s) [%s]: %s" error.Code error.Field error.Message
            1
        
        | error -> 
            printfn "Error occured while creating the manifest file for pulumi CLI:"
            printfn "%s" error.Message
            printfn "%s" error.StackTrace
            1
    | :? AggregateException as aggregateError -> 
        printfn "Errors occured while creating the manifest file for pulumi CLI:"
        for error in aggregateError.InnerExceptions do 
            printfn "%s" error.Message
        
        printfn "%s" aggregateError.StackTrace
        1
    | error -> 
        printfn "Error occured while creating the manifest file for pulumi CLI:"
        printfn "%s" error.Message
        printfn "%s" error.StackTrace
        1