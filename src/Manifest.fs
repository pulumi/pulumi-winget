module Manifest

open System
open System.IO
open System.Text

type MonikerUpdate =
    | AlreadySet
    | Added
    | Replaced of previous: string

// The defaultLocale schema fields that come after Moniker, in schema order. The moniker goes in
// front of the first of these the manifest has, which matches the order wingetcreate writes.
let private fieldsAfterMoniker = [
    "Tags"
    "Agreements"
    "ReleaseNotes"
    "ReleaseNotesUrl"
    "PurchaseUrl"
    "InstallationNotes"
    "Documentations"
    "Icons"
    "ManifestType"
    "ManifestVersion"
]

// winget manifests are flat mappings, so a top-level field is any line that starts with `Key:`.
// Indented lines belong to a nested value, and `- ` lines are list items (wingetcreate writes
// those at column 0 too). Editing lines rather than round-tripping the YAML keeps wingetcreate's
// header comments and formatting exactly as it wrote them.
let private topLevelField (line: string) =
    let line = line.TrimEnd '\r'
    if line.Length = 0 || Char.IsWhiteSpace line[0] || line.StartsWith "#" || line.StartsWith "-" then
        None
    else
        match line.IndexOf ':' with
        | -1 -> None
        | index -> Some (line.Substring(0, index), line.Substring(index + 1).Trim().Trim('\'', '"'))

let private utf8Bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

let findDefaultLocaleManifests (manifestDir: string) =
    Directory.EnumerateFiles(manifestDir, "*.yaml")
    |> Seq.filter (fun file ->
        File.ReadLines file
        |> Seq.exists (fun line -> topLevelField line = Some ("ManifestType", "defaultLocale")))
    |> Seq.sort
    |> List.ofSeq

let ensureMoniker (moniker: string) (manifestFile: string) =
    let bytes = File.ReadAllBytes manifestFile
    let hasBom = bytes.Length >= utf8Bom.Length && bytes[0..utf8Bom.Length - 1] = utf8Bom
    let offset = if hasBom then utf8Bom.Length else 0
    let text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset)
    // Split on \n and leave any \r on its line, so every untouched line keeps its own ending
    let lines = text.Split '\n' |> List.ofArray

    // The moniker line takes the ending of the line it replaces or goes in front of
    let monikerLineLike (neighbour: string) =
        $"Moniker: {moniker}" + (if neighbour.EndsWith "\r" then "\r" else "")

    let field predicate =
        lines
        |> List.indexed
        |> List.tryPick (fun (index, line) ->
            topLevelField line
            |> Option.filter predicate
            |> Option.map (fun (_, value) -> index, value))

    let updatedLines, update =
        match field (fun (key, _) -> key = "Moniker") with
        | Some (_, current) when current = moniker -> lines, AlreadySet
        | Some (index, current) -> List.updateAt index (monikerLineLike lines[index]) lines, Replaced current
        | None ->
            match field (fun (key, _) -> List.contains key fieldsAfterMoniker) with
            | Some (index, _) -> List.insertAt index (monikerLineLike lines[index]) lines, Added
            | None -> failwithf "Cannot find where to add the moniker in %s: it has none of the fields %A" manifestFile fieldsAfterMoniker

    if update <> AlreadySet then
        // Keep the file's own encoding and line endings so the only change is the moniker line
        let content = Encoding.UTF8.GetBytes(String.Join("\n", updatedLines))
        File.WriteAllBytes(manifestFile, Array.append (if hasBom then utf8Bom else [||]) content)

    update
