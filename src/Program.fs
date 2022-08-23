﻿open Octokit
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
let githubToken = Environment.GetEnvironmentVariable "GITHUB_TOKEN"
// only assign github token to the client when it is available (usually in Github CI)
if not (isNull githubToken) then  github.Credentials <- Credentials(githubToken)

let version (release: Release) = 
    if not (String.IsNullOrWhiteSpace(release.Name)) then
        release.Name.Substring(1, release.Name.Length - 1)
    elif not (String.IsNullOrWhiteSpace(release.TagName)) then 
        release.TagName.Substring(1, release.TagName.Length - 1)
    else 
        ""

type InstallerAsset = { DownloadUrl: string; Sha256: string }

let findWindowsBinaries (release: Release) : Result<InstallerAsset, string> = 
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

let formatProductCode (code: Guid) = "{" + code.ToString().ToUpper() + "}" 

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

let clean() = 
    printfn "Cleaning up artifacts"
    let unzippedPulumiPath = resolvePath [ "pulumi"]
    if Directory.Exists unzippedPulumiPath then 
        printfn "Deleting %s" unzippedPulumiPath
        Fake.IO.Shell.deleteDir unzippedPulumiPath
    
    let filesToDelete = [
        "pulumi.zip"
        "manifest.yaml"
        "PulumiInstaller.wxs"
    ]
    for file in filesToDelete do
        let filePath = resolvePath [ file ]
        if File.Exists filePath then 
            printfn "Deleting %s" filePath
            File.Delete filePath

let generateMsi() = 
    let latestRelease = await (github.Repository.Release.GetLatest("pulumi", "pulumi"))
    match findWindowsBinaries latestRelease with 
    | Error errorMessage -> 
        printfn "Error occured while creating the manifest file for pulumi CLI:"
        printfn "%s" errorMessage
        1

    | Ok windowsBinaries -> 
        // Download ZIP file
        printfn "Downloading Pulumi binaries from %s" windowsBinaries.DownloadUrl
        let pulumiZip = await (httpClient.GetByteArrayAsync(windowsBinaries.DownloadUrl))
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

        let componentId (filePath) = $"comp_{fileId filePath}"

        // Random guid used for both the MSI and the manifest
        let productCode = Guid.NewGuid()

        // Create installer definition
        // see below for allowed elements
        // https://wixtoolset.org/documentation/manual/v3/xsd/wix/wix.html
        let wixDefinition = Wix.installer [
            Wix.product (version latestRelease) (formatProductCode productCode) [
                Wix.package [ 
                    Wix.attr "Platform" "x64"
                    Wix.attr "Description" "Pulumi CLI for managing cloud infrastructure"
                    Wix.attr "InstallerVersion" "200"
                    Wix.attr "Compressed" "yes"
                ]

                // Tells the installer to embed all source files
                Wix.mediaTemplate [ Wix.attr "EmbedCab" "yes" ]

                Wix.directory "TARGETDIR" "SourceDir" [
                    Wix.directoryId "ProgramFilesFolder" [
                        Wix.directory "PULUMIDIR" "Pulumi" []
                    ]
                ]

                Wix.directoryRef "PULUMIDIR" [
                    for file in filesFromUnzippedArchive do
                        Wix.component' (componentId file) [
                            Wix.file (fileId file) file
                        ]

                    Wix.component' "SetEnvironment" [
                        // Required dummy <CreateFolder /> element
                        Wix.createFolder()
                        // Add install folder to PATH
                        Wix.updateEnvironmentPath "PULUMIDIR"        
                    ]
                ]

                Wix.feature "MainInstaller" "Installer" [
                    for file in filesFromUnzippedArchive do
                    Wix.componentRef (componentId file)
                ]

                Wix.feature "UpdatePath" "Update PATH" [
                    Wix.componentRef "SetEnvironment"
                ]
            ]
        ]
        

        let wixOutput = resolvePath [ "PulumiInstaller.wxs" ]

        wixDefinition.Save wixOutput

        printfn "Written WixInstaller definition:"

        System.Console.WriteLine(File.ReadAllText wixOutput)

        match latestMsiRelease() with 
        | Some msiRelease when version msiRelease = version latestRelease  ->
            printfn "Version v%s of Pulumi MSI is already published, skipping..." (version msiRelease)
            0

        | _ ->
            // TODO: check candle/light already exist before executing them
            Shell.exec("candle.exe", "PulumiInstaller.wxs")
            Shell.exec("light.exe", $"PulumiInstaller.wixobj -o pulumi-{version latestRelease}-windows-x64.msi")
            let msi = resolvePath [ $"pulumi-{version latestRelease}-windows-x64.msi" ]
            let info = FileInfo msi
            printfn "Succesfully created MSI at '%s' (%d bytes)" msi info.Length
            printfn "Publishing asset to github..."
            
            let releaseInfo = NewRelease($"v{version latestRelease}")
            let msiRelease = await (github.Repository.Release.Create("pulumi", "pulumi-winget", releaseInfo))
            let releaseAsset = ReleaseAssetUpload()
            releaseAsset.FileName <- Path.GetFileName msi
            releaseAsset.ContentType <- "application/msi"
            releaseAsset.RawData <- File.OpenRead(msi)

            let uploadResult = await (github.Repository.Release.UploadAsset(msiRelease, releaseAsset))
            printfn $"Released {version latestRelease}: {uploadResult.BrowserDownloadUrl}"
            0

[<EntryPoint>]
let main (args: string[]) = 
    try
        match args with 
        | [| "generate"; "msi" |] -> 
            clean()
            generateMsi()
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