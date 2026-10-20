/// The functions the demo page calls. Only compiled by Fable: the page passes the parsed
/// docs/model.json and plain objects of raw fields ({ ProductGroup: "TTT", YearMade: "1998", ... }).
module Gavel.Exports

#if FABLE_COMPILER
open Fable.Core
open Fable.Core.JsInterop

let private getter (fields: obj) =
    fun (column: string) ->
        let v: obj = fields?(column)
        if isNull v then null else string v

/// Prepares a model read from docs/model.json (already parsed by JSON.parse).
let load (deployed: obj) : Estimate.Ready = Estimate.ready (unbox<Deployed> deployed)

/// Estimate, range and what moved it, for one machine.
let estimate (ready: Estimate.Ready) (fields: obj) : Estimate = Estimate.estimate ready (getter fields)

/// Log-price predictions for many records (used by scripts/check-js.mjs).
let predictLogs (ready: Estimate.Ready) (rows: obj[]) : float[] =
    rows |> Array.map (fun row -> Estimate.predictLog ready (getter row))
#endif
