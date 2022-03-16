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
    static member createWix (name: XNamespace, content: obj) =
        XElement(name.GetName("Wix"), content) :> obj



let root (elements: obj seq) = 
    let ns = XNamespace.op_Implicit("http://schemas.microsoft.com/wix/2006/wi")
    XElement.createWix(ns, seq {
        yield  XAttribute.create("xmlns", "http://schemas.microsoft.com/wix/2006/wi")
        for element in elements do 
            yield element
    })

let product (version: string) (elements: obj seq) = 
    XElement.create("Product", [|
        XAttribute.create("Id", "*") 
        XAttribute.create("UpgradeCode", Guid.NewGuid().ToString())
        XAttribute.create("Version", version)
        XAttribute.create("Name", "Pulumi")
        XAttribute.create("Manufacturer", "Pulumi")
        for element in elements do 
            element
    |])

let directory (id: string) (name: string) (elements: obj seq) = 
    XElement.create("Directory", seq {
        yield XAttribute.create("Id", id)
        yield XAttribute.create("Name", name)
        for element in elements do
            yield element
    })

let directoryId (id: string) (elements: obj seq) = 
    XElement.create("Directory", seq {
        yield XAttribute.create("Id", id) |> box
        for element in elements do
            yield element |> box
    })

let directoryRef (id: string) (elements: obj seq) = 
    XElement.create("DirectoryRef", seq {
        yield XAttribute.create("Id", id) |> box
        for element in elements do
            yield element |> box
    })

let component' (id: string) (elements: obj seq) = 
    XElement.create("Component", seq {
        yield XAttribute.create("Id", id) |> box
        yield XAttribute.create("Guid", Guid.NewGuid().ToString()) |> box
        for element in elements do
            yield element |> box
    })

let file (id: string) (source: string) = 
    XElement.create("File", seq {
        XAttribute.create("Id", id)
        XAttribute.create("Source", source)
        XAttribute.create("KeyPath", "yes")
        if (source.EndsWith ".exe") then
            XAttribute.create("Checksum", "yes")
    })

let componentRef (id: string) = XElement.create("ComponentRef", XAttribute.create("Id", id))

let feature (id: string) (title: string) (elements: obj seq) = 
    XElement.create("Feature", seq {
        yield XAttribute.create("Id", id)
        yield XAttribute.create("Title", title)
        yield XAttribute.create("Level", "1")
        for element in elements do
            yield element
    })
