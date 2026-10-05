open Octokit
open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Threading.Tasks
open System.Xml.Linq
open Fake.Core
open System.Security.Cryptography
open System.Text

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

// Windows Installer stores ProductVersion as a packed DWORD (8 bits of major, 8 bits of minor and
// 16 bits of build) so the highest version it can express is 255.255.65535. Pulumi outgrew that
// with v3.256.0, which WiX rejects at compile time with CNDL0242.
//
// The only thing Windows Installer uses ProductVersion for is ordering upgrades, so we map the
// Pulumi version onto a synthetic MSI version that is guaranteed to keep increasing. The Pulumi
// major version gets the MSI major field (offset by one, so the first mapped version outranks the
// literal 3.255.0 we already shipped) and the minor/patch pair is packed into the two remaining
// fields in base 100:
//
//     3.255.0 -> 4.0.25500        3.257.1 -> 4.0.25701
//     3.256.0 -> 4.0.25600        4.0.0   -> 5.0.0
//
// Giving the Pulumi major version a field of its own is what keeps a future Pulumi 4.0.0 ordered
// above an installed 3.x. Every other surface (the release tag, the MSI file name, version.txt and
// the winget manifest) keeps using the real Pulumi version.
//
// Each MSI minor version holds a "generation" of 650 Pulumi minor versions: 3.649.99 maps to
// 4.0.64999 and the next release, 3.650.0, rolls over to 4.1.0 and starts the build field again.
// So there is no Pulumi version this cannot express -- 650 is just the largest round generation
// that fits, since 649 * 100 + 99 = 64999 stays inside the 65535 build limit. The mapping only runs
// out of room if a patch version reaches 100 (the highest Pulumi has ever shipped is 4) or the
// minor version reaches 166,400, both of which fail the build rather than mis-order an upgrade.
let minorVersionsPerGeneration = 650

let msiProductVersion (pulumiVersion: string) =
    let parseField (name: string) (value: string) =
        match Int32.TryParse value with
        | true, parsed when parsed >= 0 -> parsed
        | _ -> failwithf "Cannot derive an MSI product version from '%s': the %s version '%s' is not a number" pulumiVersion name value

    match pulumiVersion.Split "." with
    | [| major; minor; patch |] ->
        let major = parseField "major" major
        let minor = parseField "minor" minor
        let patch = parseField "patch" patch

        if patch > 99 then
            failwithf "Cannot derive an MSI product version from '%s': a patch version above 99 would collide with the next minor version" pulumiVersion

        let msiMajor = major + 1
        let msiMinor = minor / minorVersionsPerGeneration
        let msiBuild = (minor % minorVersionsPerGeneration) * 100 + patch

        if msiMajor > 255 || msiMinor > 255 then
            failwithf "Cannot derive an MSI product version from '%s': it no longer fits the MSI limit of 255.255.65535" pulumiVersion

        $"{msiMajor}.{msiMinor}.{msiBuild}"

    | _ ->
        failwithf "Cannot derive an MSI product version from '%s': expected a major.minor.patch version" pulumiVersion

type Architecture = { Name: string; InstallerVersion: string }

let x64 = { Name = "x64"; InstallerVersion = "200" }
// Windows Installer only understands the Arm64 platform from schema 500 onwards
let arm64 = { Name = "arm64"; InstallerVersion = "500" }
let architectures = [ x64; arm64 ]

let msiFileName (pulumiVersion: string) (arch: Architecture) = $"pulumi-{pulumiVersion}-windows-{arch.Name}.msi"

// checksum-256.txt predates the arm64 installer, so it keeps holding the x64 checksum
let checksumFileName (arch: Architecture) =
    if arch = x64 then "checksum-256.txt" else $"checksum-256-{arch.Name}.txt"

type InstallerAsset = { DownloadUrl: string; Sha512: string }

let findWindowsBinaries (release: Release) (arch: Architecture) : Result<InstallerAsset, string> = 
    let currentVersion = version release
    let archiveName = $"pulumi-v{currentVersion}-windows-{arch.Name}.zip"
    let checksums = 
        release.Assets
        |> Seq.tryFind (fun asset -> asset.Name = $"SHA512SUMS")

    let windowsBuild = 
        release.Assets
        |> Seq.tryFind (fun asset -> asset.Name = archiveName)

    if checksums.IsNone then 
        Error $"Checksums file SHA512SUMS was not found"
    elif windowsBuild.IsNone then
        Error $"Windows build {archiveName} was not found"
    else 
        let contents = await (httpClient.GetStringAsync checksums.Value.BrowserDownloadUrl)
        contents.Split "\n"
        |> Array.tryFind (fun line -> line.EndsWith archiveName)
        |> function 
            | None ->
                Error $"Could not find the installer SHA512 for the windows {arch.Name} build"

            | Some line -> 
                let parts = line.Split "  "
                let sha512 = parts[0]
                Ok {
                    DownloadUrl = windowsBuild.Value.BrowserDownloadUrl
                    Sha512 = sha512
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
    for arch in architectures do
        let unzippedPulumiPath = resolvePath [ $"pulumi-{arch.Name}" ]
        if Directory.Exists unzippedPulumiPath then 
            printfn "Deleting %s" unzippedPulumiPath
            Fake.IO.Shell.deleteDir unzippedPulumiPath
    
    let filesToDelete = [
        "download-url.txt"
        "version.txt"
        for arch in architectures do
            $"pulumi-{arch.Name}.zip"
            $"PulumiInstaller-{arch.Name}.wxs"
    ]
    for file in filesToDelete do
        let filePath = resolvePath [ file ]
        if File.Exists filePath then 
            printfn "Deleting %s" filePath
            File.Delete filePath

let computeSha256 (file: string) = 
    use fs = File.OpenRead(file)
    use sha256 = SHA256.Create()
    let hashBytes = sha256.ComputeHash(fs)
    Convert.ToHexString(hashBytes)

let buildMsi (pulumiVersion: string) (msiVersion: string) (arch: Architecture) (windowsBinaries: InstallerAsset) =
    // Download ZIP file
    printfn "Downloading Pulumi %s binaries from %s" arch.Name windowsBinaries.DownloadUrl
    let pulumiZip = await (httpClient.GetByteArrayAsync(windowsBinaries.DownloadUrl))
    let pulumiZipOutput = resolvePath [ $"pulumi-{arch.Name}.zip" ]
    File.WriteAllBytes(pulumiZipOutput, pulumiZip)
    // Unzip into ./pulumi-<arch>
    let pulumiUnzipped = resolvePath [ $"pulumi-{arch.Name}" ]
    ZipFile.ExtractToDirectory(pulumiZipOutput, pulumiUnzipped)
    
    let filesFromUnzippedArchive = Directory.EnumerateFiles(pulumiUnzipped, "*.*", SearchOption.AllDirectories)

    if Seq.isEmpty filesFromUnzippedArchive then
        failwith "Error occurred while getting files from unzipped Pulumi archive: 0 files found"

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
        Wix.product msiVersion (formatProductCode productCode) [
            Wix.package [ 
                Wix.attr "Platform" arch.Name
                Wix.attr "Description" "Pulumi CLI for managing cloud infrastructure"
                Wix.attr "InstallerVersion" arch.InstallerVersion
                Wix.attr "Compressed" "yes"
            ]

            // Tells the installer to embed all source files
            Wix.mediaTemplate [ Wix.attr "EmbedCab" "yes" ]

            // Installation of new version will uninstall old version (if found)
            Wix.majorUpgrade [ Wix.attr "DowngradeErrorMessage" "Can't downgrade." ]

            Wix.directory "TARGETDIR" "SourceDir" [
                Wix.directoryId "ProgramFilesFolder" [
                    Wix.directory "PULUMIDIR" "Pulumi" []
                ]

                // Show the real Pulumi version in Add/Remove Programs rather than the
                // synthetic MSI product version. This has to be a 64-bit component to write
                // into the product's own registration, which rules out PULUMIDIR: that lives
                // under the 32-bit ProgramFilesFolder, and ICE80 rejects the combination.
                Wix.component64 "SetArpDisplayVersion" [
                    Wix.arpDisplayVersion pulumiVersion
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

            Wix.writeRegistryValuesAfterRegisterProduct()

            Wix.feature "MainInstaller" "Installer" [
                for file in filesFromUnzippedArchive do
                    Wix.componentRef (componentId file)
                Wix.componentRef "SetArpDisplayVersion"
            ]

            Wix.feature "UpdatePath" "Update PATH" [
                Wix.componentRef "SetEnvironment"
            ]
        ]
    ]
    

    let wixName = $"PulumiInstaller-{arch.Name}"
    let wixOutput = resolvePath [ $"{wixName}.wxs" ]

    wixDefinition.Save wixOutput

    printfn "Written WixInstaller definition:"

    System.Console.WriteLine(File.ReadAllText wixOutput)

    // TODO: check candle/light already exist before executing them
    Shell.exec("candle.exe", $"{wixName}.wxs")
    Shell.exec("light.exe", $"{wixName}.wixobj -o {msiFileName pulumiVersion arch}")
    let msi = resolvePath [ msiFileName pulumiVersion arch ]

    let info = FileInfo msi
    printfn "Successfully created unsigned MSI at '%s' (%d bytes)" msi info.Length

let generateMsi () =
    let latestRelease = await (github.Repository.Release.GetLatest("pulumi", "pulumi"))
    let binaries = architectures |> List.map (fun arch -> arch, findWindowsBinaries latestRelease arch)
    let errors = binaries |> List.choose (function _, Error errorMessage -> Some errorMessage | _ -> None)
    if not errors.IsEmpty then
        printfn "Error occurred while creating the manifest file for pulumi CLI:"
        for errorMessage in errors do
            printfn "%s" errorMessage
        1
    else
        let pulumiVersion = version latestRelease
        // The MSI product version is synthetic, see msiProductVersion above
        let msiVersion = msiProductVersion pulumiVersion
        printfn "Pulumi %s maps onto MSI product version %s" pulumiVersion msiVersion

        for arch, windowsBinaries in binaries do
            match windowsBinaries with
            | Ok windowsBinaries -> buildMsi pulumiVersion msiVersion arch windowsBinaries
            | Error _ -> ()

        // Persist the target version so a subsequent `publish msi` invocation
        // (after the MSI has been signed by azure/artifact-signing-action) can
        // locate the artifact without re-querying the Pulumi release.
        let versionPath = resolvePath [ "version.txt" ]
        File.WriteAllText(versionPath, pulumiVersion)
        printfn $"Written the release version to file {versionPath}"
        0

let publishMsi () =
    let versionPath = resolvePath [ "version.txt" ]
    if not (File.Exists versionPath) then
        printfn "Expected version.txt at %s — run `generate msi` before `publish msi`." versionPath
        1
    else
        let targetVersion = (File.ReadAllText versionPath).Trim()
        let msis = architectures |> List.map (fun arch -> arch, resolvePath [ msiFileName targetVersion arch ])
        match msis |> List.tryFind (fun (_, msi) -> not (File.Exists msi)) with
        | Some (_, msi) ->
            printfn "Expected signed MSI at %s — run `generate msi` and the signing step before `publish msi`." msi
            1
        | None ->
            match latestMsiRelease() with
            | Some msiRelease when version msiRelease = targetVersion ->
                printfn "Version v%s of Pulumi MSI is already published, skipping..." targetVersion
                0
            | _ ->
                printfn "Publishing assets to GitHub..."

                let releaseInfo = NewRelease($"v{targetVersion}")
                let msiRelease = await (github.Repository.Release.Create("pulumi", "pulumi-winget", releaseInfo))

                let downloadUrls = [
                    for arch, msi in msis do
                        let msiChecksum256 = computeSha256 msi
                        let installerAsset = ReleaseAssetUpload()
                        installerAsset.FileName <- Path.GetFileName msi
                        installerAsset.ContentType <- "application/msi"
                        installerAsset.RawData <- File.OpenRead(msi)

                        let checksumAsset = ReleaseAssetUpload()
                        checksumAsset.FileName <- checksumFileName arch
                        checksumAsset.ContentType <- "text/plain"
                        checksumAsset.RawData <- new MemoryStream(Encoding.UTF8.GetBytes(msiChecksum256))

                        let uploadedInstaller = await (github.Repository.Release.UploadAsset(msiRelease, installerAsset))
                        let uploadedChecksumFile = await (github.Repository.Release.UploadAsset(msiRelease, checksumAsset))

                        printfn $"Released {targetVersion} ({arch.Name}): {uploadedInstaller.BrowserDownloadUrl}"
                        printfn $"Checksum: {uploadedChecksumFile.BrowserDownloadUrl}"

                        // wingetcreate takes the architecture override as <url>|<architecture>
                        $"{uploadedInstaller.BrowserDownloadUrl}|{arch.Name}"
                ]

                let downloadUrlPath = resolvePath [ "download-url.txt" ]
                File.WriteAllLines(downloadUrlPath, downloadUrls)
                printfn $"Written the release download URLs to file {downloadUrlPath}"
                0

[<EntryPoint>]
let main (args: string[]) = 
    try
        match args with
        | [| "generate"; "msi" |] ->
            clean()
            generateMsi ()
        | [| "publish"; "msi" |] ->
            publishMsi ()
        | [| "clean" |] ->
            clean()
            0
        | [| "msi-version"; pulumiVersion |] ->
            // Lets you check the version mapping without WiX, or Windows, in sight
            printfn "%s" (msiProductVersion pulumiVersion)
            0
        | otherwise -> 
            printfn "Unknown arguments provided: %A" otherwise
            0
    with 
    | :? AggregateException as aggregateError when aggregateError.InnerExceptions.Count = 1 -> 
        match aggregateError.InnerExceptions[0] with 
        | :? Octokit.ApiException as githubError -> 
            printfn "Error occurred executing github operation:"
            printfn "%s" githubError.ApiError.Message
            for error in githubError.ApiError.Errors do
                printfn "(%s) [%s]: %s" error.Code error.Field error.Message
            1
        
        | error -> 
            printfn "Error occurred while creating the manifest file for pulumi CLI:"
            printfn "%s" error.Message
            printfn "%s" error.StackTrace
            1
    | :? AggregateException as aggregateError -> 
        printfn "Errors occurred while creating the manifest file for pulumi CLI:"
        for error in aggregateError.InnerExceptions do 
            printfn "%s" error.Message
        
        printfn "%s" aggregateError.StackTrace
        1
    | error -> 
        printfn "Error occurred while creating the manifest file for pulumi CLI:"
        printfn "%s" error.Message
        printfn "%s" error.StackTrace
        1
