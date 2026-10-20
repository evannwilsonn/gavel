/// A small CSV reader for the Kaggle files (RFC 4180 quoting, comma separated).
module Gavel.Csv

open System.Collections.Generic
open System.IO
open System.Text

/// A CSV file held as one string array per column. Repeated values share one string.
type Table =
    { header: string[]
      columns: string[][] }

    member t.rows = if t.columns.Length = 0 then 0 else t.columns.[0].Length
    member t.index(name: string) =
        match Array.tryFindIndex ((=) name) t.header with
        | Some i -> i
        | None -> failwithf "no column %s" name
    member t.column(name: string) = t.columns.[t.index name]

/// Splits one line into fields. Quoted fields may contain commas and doubled quotes.
let splitLine (line: string) : string[] =
    let fields = ResizeArray<string>()
    let sb = StringBuilder()
    let mutable inQuotes = false
    let mutable i = 0
    while i < line.Length do
        let c = line.[i]
        if inQuotes then
            if c = '"' then
                if i + 1 < line.Length && line.[i + 1] = '"' then
                    sb.Append('"') |> ignore
                    i <- i + 1
                else inQuotes <- false
            else sb.Append(c) |> ignore
        elif c = '"' then inQuotes <- true
        elif c = ',' then
            fields.Add(sb.ToString())
            sb.Clear() |> ignore
        else sb.Append(c) |> ignore
        i <- i + 1
    fields.Add(sb.ToString())
    fields.ToArray()

let read (path: string) : Table =
    use reader = new StreamReader(path)
    let header = splitLine (reader.ReadLine())
    let n = header.Length
    let cols = Array.init n (fun _ -> ResizeArray<string>(420_000))
    let pools = Array.init n (fun _ -> Dictionary<string, string>())
    let mutable line = reader.ReadLine()
    while not (isNull line) do
        let fields = splitLine line
        if fields.Length <> n then failwithf "%s: expected %d fields, got %d" path n fields.Length
        for j in 0 .. n - 1 do
            let s = fields.[j]
            let pooled =
                match pools.[j].TryGetValue s with
                | true, p -> p
                | _ ->
                    pools.[j].[s] <- s
                    s
            cols.[j].Add pooled
        line <- reader.ReadLine()
    { header = header
      columns = cols |> Array.map (fun c -> c.ToArray()) }
