/// JSON in and out (results/*.json, docs/model.json).
module Gavel.Json

open System.IO
open System.Text.Json
open System.Text.Encodings.Web

let private options =
    JsonSerializerOptions(
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    )

let private compact =
    JsonSerializerOptions(
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    )

let write (path: string) (value: 'T) =
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
    File.WriteAllText(path, JsonSerializer.Serialize(value, options) + "\n")
    printfn "  wrote %s" path

let writeCompact (path: string) (value: 'T) =
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
    File.WriteAllText(path, JsonSerializer.Serialize(value, compact))
    printfn "  wrote %s (%d KB)" path ((FileInfo path).Length / 1024L)

let read<'T> (path: string) : 'T =
    JsonSerializer.Deserialize<'T>(File.ReadAllText path, options)

/// Rounds to a number of significant decimals, to keep exported files small.
let round (digits: int) (x: float) = System.Math.Round(x, digits)
