module Wix

open System
open System.Collections.Generic
open System.Xml
open System.Xml.Linq
 
type XAttribute with

    static member create (name: string, value: obj) =
        XAttribute(XName.Get(name), value) :> obj

type XElement with

    static member create (name: string, content: obj) =
        XElement(XName.Get(name), content) :> obj

    static member create (name: string, [<ParamArray>] content) =
        XElement(XName.Get(name), content) :> obj
    static member create (name: XName, [<ParamArray>] content) =
        XElement(name, content) :> obj
    static member createWix (name: XNamespace, content: obj) =
        XElement(name.GetName("Wix"), content) :> obj
    static member createProduct (name: XNamespace, content: obj) =
        XElement(name.GetName("Product"), content) :> obj



let wixNamespace = "http://schemas.microsoft.com/wix/2006/wi"
let ns = XNamespace.op_Implicit(wixNamespace)

let root (elements: obj seq) = 
    XElement.createWix(ns, seq {
        yield  XAttribute.create("xmlns", wixNamespace)
        for element in elements do 
            yield element
    })

let product (version: string) (elements: obj seq) = 
    XElement.create(ns + "Product", seq {
        XAttribute.create("Id", "*") 
        XAttribute.create("UpgradeCode", Guid.NewGuid().ToString())
        XAttribute.create("Version", version)
        XAttribute.create("Name", "Pulumi")
        XAttribute.create("Manufacturer", "Pulumi")
        XAttribute.create("Language", "1033")
        for element in elements do 
            element
    })

let directory (id: string) (name: string) (elements: obj seq) = 
    XElement.create(ns + "Directory", seq {
        yield XAttribute.create("Id", id)
        yield XAttribute.create("Name", name)
        for element in elements do
            yield element
    })

let directoryId (id: string) (elements: obj seq) = 
    XElement.create(ns + "Directory", seq {
        yield XAttribute.create("Id", id) |> box
        for element in elements do
            yield element |> box
    })

let directoryRef (id: string) (elements: obj seq) = 
    XElement.create(ns + "DirectoryRef", seq {
        yield XAttribute.create("Id", id) |> box
        for element in elements do
            yield element |> box
    })

let fragment (elements: obj seq) = 
    XElement.create(ns + "Fragment", seq {
        for element in elements do
            yield element |> box
    })

let attr key (value: string) = XAttribute.create(key, value)

let package (elements: obj seq) = 
    XElement.create(ns + "Package", seq {
        for element in elements do
            yield element |> box
    })

let createFolder() = XElement.create(ns + "CreateFolder")

let media (elements: obj seq) = 
    XElement.create(ns + "Media", seq {
        for element in elements do
            yield element |> box
    })

let mediaTemplate (elements: obj seq) = 
    XElement.create(ns + "MediaTemplate", seq {
        for element in elements do
            yield element |> box
    })

let component' (id: string) (elements: obj seq) = 
    XElement.create(ns + "Component", seq {
        yield XAttribute.create("Id", id) |> box
        yield XAttribute.create("Guid", Guid.NewGuid().ToString()) |> box
        for element in elements do
            yield element |> box
    })

let file (id: string) (source: string) = 
    XElement.create(ns + "File", seq {
        XAttribute.create("Id", id)
        XAttribute.create("Source", source)
        XAttribute.create("Compressed", "yes")
        XAttribute.create("KeyPath", "yes")
        if (source.EndsWith ".exe") then
            XAttribute.create("Checksum", "yes")
    })

let componentRef (id: string) = XElement.create(ns + "ComponentRef", XAttribute.create("Id", id))

let feature (id: string) (title: string) (elements: obj seq) = 
    XElement.create(ns + "Feature", seq {
        yield XAttribute.create("Id", id)
        yield XAttribute.create("Title", title)
        yield XAttribute.create("Level", "1")
        for element in elements do
            yield element
    })

// https://stackoverflow.com/questions/11400748/unable-to-update-path-environment-variable-using-wix
let updateEnvironmentPath (directoryRef: string) = 
    XElement.create(ns + "Environment", 
        XAttribute.create("Id", "PATH"),
        XAttribute.create("Name", "PATH"),
        XAttribute.create("Value", $"[{directoryRef}]"),
        XAttribute.create("Permanent", "yes"),
        XAttribute.create("Part", "last"),
        XAttribute.create("Action", "set"),
        XAttribute.create("System", "yes"))