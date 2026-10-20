/// The browser model, the form's catalogue, the example sales, and the page build.
module Gavel.Demo

open System
open System.Collections.Generic
open System.IO
open Gavel.Data
open Gavel.Learners
open Gavel.Experiments

/// Settings for the model that runs in the page: fewer, smaller trees than the full model, and
/// only inputs a visitor can describe.
let demoGbm =
    { gbmDefaults with
        trees = 400
        learningRate = 0.1
        leaves = 48
        minLeaf = 30
        patience = 0 }

/// Interval level for the estimate's range.
let level = 0.8

type Coverage = { key: string; n: int; share: float }

type DemoReport =
    { features: string[]
      settings: string
      trees: int
      nodes: int
      level: float
      bands: Band[]
      /// scored once on the held-out 2012 sales, with docs/model.json read back exactly as the page reads it
      heldOut: Metrics.Score
      /// share of held-out sales whose price fell inside the range
      coverage: float
      coverageByGroup: Coverage[]
      tuningRmsle: float
      modelBytes: int64 }

let private roundTree (t: Tree) =
    { t with
        t = t.t |> Array.map (Json.round 4)
        v = t.v |> Array.map (Json.round 5) }

let private bandsFrom (groupsOf: string[]) (resid: float[]) =
    let q (xs: float[]) = Json.round 4 (Metrics.quantile ((1.0 - level) / 2.0) xs), Json.round 4 (Metrics.quantile (1.0 - (1.0 - level) / 2.0) xs)
    [| let lo, hi = q resid
       yield { key = "all"; lo = lo; hi = hi }
       for g, rows in Array.zip groupsOf resid |> Array.groupBy fst do
           let lo, hi = q (rows |> Array.map snd)
           yield { key = g; lo = lo; hi = hi } |]

// ---------------------------------------------------------------------------------------------
// Catalogue for the form

type Level = { value: string; n: int }

type ModelEntry =
    { name: string
      n: int
      series: string[]
      sizeClass: string
      year: int }

type OptionField =
    { column: string
      label: string
      values: Level[] }

type GroupEntry =
    { code: string
      name: string
      n: int
      sizeClasses: Level[]
      models: ModelEntry[]
      cabs: Level[]
      options: OptionField[] }

type Catalog =
    { groups: GroupEntry[]
      states: string[]
      firstSale: string
      lastSale: string }

let private optionColumns =
    dict
        [ "TTT", [ "Blade_Type", "Blade"; "Ripper", "Ripper" ]
          "MG", [ "Ripper", "Ripper" ]
          "TEX", [ "Thumb", "Thumb" ]
          "BL", [ "Stick", "Stick" ]
          "WL", [ "Ride_Control", "Ride control" ]
          "SSL", [ "Hydraulics_Flow", "Hydraulic flow" ] ]

let catalog (train: Sales) =
    let col name = train.table.column name
    let group = col "ProductGroup"
    let counts (rows: int[]) (c: string[]) (minN: int) =
        rows
        |> Array.map (fun i -> c.[i])
        |> Array.filter (fun v -> v <> "")
        |> Array.countBy id
        |> Array.filter (fun (_, n) -> n >= minN)
        |> Array.sortByDescending snd
        |> Array.map (fun (v, n) -> { value = v; n = n })
    let groups =
        Array.init train.count id
        |> Array.groupBy (fun i -> group.[i])
        |> Array.sortByDescending (fun (_, rows) -> rows.Length)
        |> Array.map (fun (g, rows) ->
            let baseModel = col "fiBaseModel"
            let models =
                rows
                |> Array.groupBy (fun i -> baseModel.[i])
                |> Array.filter (fun (_, r) -> r.Length >= 40)
                |> Array.sortByDescending (fun (_, r) -> r.Length)
                |> Array.truncate 150
                |> Array.map (fun (m, r) ->
                    let years =
                        r
                        |> Array.map (fun i -> Features.yearMade (col "YearMade").[i])
                        |> Array.filter (fun v -> not (Double.IsNaN v))
                        |> Array.sort
                    { name = m
                      n = r.Length
                      series = counts r (col "fiSecondaryDesc") 8 |> Array.map (fun l -> l.value)
                      sizeClass = (counts r (col "fiProductClassDesc") 1).[0].value
                      year = if years.Length = 0 then 2000 else int years.[years.Length / 2] })
            { code = g
              name = (col "ProductGroupDesc").[rows.[0]]
              n = rows.Length
              sizeClasses = counts rows (col "fiProductClassDesc") 50
              models = models
              cabs = counts rows (col "Enclosure") 50
              options =
                (match optionColumns.TryGetValue g with
                 | true, cols -> cols
                 | _ -> [])
                |> List.map (fun (c, label) ->
                    { column = c
                      label = label
                      values = counts rows (col c) 30 })
                |> List.toArray })
    let dates = train.days |> Array.sort
    let fmt (d: int) =
        DateTime(1970, 1, 1).AddDays(float d).ToString("yyyy-MM-dd")
    { groups = groups
      states = col "state" |> Array.distinct |> Array.filter (fun s -> s <> "Unspecified") |> Array.sort
      firstSale = fmt dates.[0]
      lastSale = fmt dates.[dates.Length - 1] }

// ---------------------------------------------------------------------------------------------
// Example sales from the held-out set, for "Pull a real 2012 sale"

type Example =
    { salesId: string
      fields: Dictionary<string, string>
      price: float }

let rawColumns =
    [| "ProductGroup"; "fiBaseModel"; "fiSecondaryDesc"; "fiModelDesc"; "fiProductClassDesc"; "YearMade"
       "MachineHoursCurrentMeter"; "state"; "saledate"; "Enclosure"; "Blade_Type"; "Ripper"; "Thumb"; "Stick"
       "Ride_Control"; "Hydraulics_Flow" |]

let examples (valid: Sales) (cat: Catalog) (perGroup: int) =
    let rng = Random(2012)
    let col name = valid.table.column name
    let known = HashSet<string>(cat.groups |> Seq.collect (fun g -> g.models |> Seq.map (fun m -> g.code + "|" + m.name)))
    Array.init valid.count id
    // machines the form can describe: a listed model, year and hours known
    |> Array.filter (fun i ->
        known.Contains((col "ProductGroup").[i] + "|" + (col "fiBaseModel").[i])
        && not (Double.IsNaN(Features.yearMade (col "YearMade").[i]))
        && not (Double.IsNaN(Features.hours (col "MachineHoursCurrentMeter").[i])))
    |> Array.groupBy (fun i -> (col "ProductGroup").[i])
    |> Array.sortBy fst
    |> Array.collect (fun (_, rows) -> rows |> Array.sortBy (fun _ -> rng.Next()) |> Array.truncate perGroup)
    |> Array.map (fun i ->
        let d = Dictionary<string, string>()
        for c in rawColumns do
            d.[c] <- (col c).[i]
        { salesId = (col "SalesID").[i]
          fields = d
          price = valid.price.[i] })

/// Hand-written machines for the JavaScript parity check, including blanks and unknown values.
let parityMachines () =
    let m (pairs: (string * string) list) =
        let d = Dictionary<string, string>()
        for k, v in pairs do
            d.[k] <- v
        d
    [| m [ "ProductGroup", "TTT"; "fiBaseModel", "D6"; "fiSecondaryDesc", "R"; "YearMade", "1998"; "MachineHoursCurrentMeter", "8000"; "state", "Texas"; "saledate", "3/15/2012 0:00"; "Enclosure", "EROPS w AC" ]
       m [ "ProductGroup", "TEX"; "fiBaseModel", "320"; "YearMade", "2006"; "saledate", "1/20/2012 0:00"; "state", "Florida" ]
       m [ "ProductGroup", "SSL"; "fiBaseModel", "S185"; "YearMade", "1000"; "MachineHoursCurrentMeter", "0"; "saledate", "4/2/2012 0:00" ]
       m [ "ProductGroup", "WL"; "fiBaseModel", "NOT-A-MODEL"; "YearMade", "2010"; "saledate", "12/31/2015 0:00"; "state", "Nowhere" ]
       m [ "ProductGroup", "MG"; "fiBaseModel", "140"; "fiSecondaryDesc", "H"; "YearMade", "1985"; "MachineHoursCurrentMeter", "12000"; "saledate", "2/29/2012 0:00"; "Ripper", "Yes" ]
       m [ "ProductGroup", "BL"; "fiBaseModel", "580"; "fiSecondaryDesc", "K"; "YearMade", "1992"; "MachineHoursCurrentMeter", "4500"; "saledate", "1/1/1990 0:00"; "Stick", "Extended" ]
       m [] |]

type Parity =
    { note: string
      machines: Dictionary<string, string>[]
      logPrices: float[] }

// ---------------------------------------------------------------------------------------------

/// `order` is normally read from results/tuning.json; pass it to preview the page before tuning has run.
let build (dataDir: string) (resultsDir: string) (docsDir: string) (order: Order option) =
    let order =
        match order with
        | Some o -> o
        | None -> orderOf (Json.read<Tuning> (Path.Combine(resultsDir, "tuning.json"))).chosen.order
    let train = loadTrain dataDir
    let valid = loadValid dataDir
    let fit, check = splitTuning train

    // 1. Tree count and error bands from the tuning window.
    let pt = prepare (demoFeatures order fit) fit check
    let sw = timed ()
    let tuned, trees, _ = gbmBest demoGbm pt
    let checkPred = predictAll tuned pt.testX
    let tuningEntry = entry "Demo model, tuning window" (gbmSettings demoGbm) trees checkPred check.price sw
    let resid = Array.map2 (fun y p -> y - p) pt.testY checkPred
    let bands = bandsFrom (check.table.column "ProductGroup") resid

    // 2. Refit on all of Train.csv and export.
    let features = demoFeatures order train
    let p = prepare features train valid
    let g = { demoGbm with trees = trees }
    let model, _ = fitGbm g p.fitBins p.fitY None
    let deployed =
        { features = features
          model =
            { model with
                bias = Json.round 6 model.bias
                trees = model.trees |> Array.map roundTree }
          level = level
          bands = bands }
    let modelPath = Path.Combine(docsDir, "model.json")
    Json.writeCompact modelPath deployed

    // 3. Score it on the held-out sales exactly as the page will: read the file back, use Core.
    let ready = Estimate.ready (Json.read<Deployed> modelPath)
    let estimates = Array.init valid.count (fun i -> Estimate.estimate ready (valid.get i))
    let predLog = estimates |> Array.map (fun e -> Math.Log(1.0 + e.price))
    let inside =
        Array.init valid.count (fun i -> valid.price.[i] >= estimates.[i].low - 0.5 && valid.price.[i] <= estimates.[i].high + 0.5)
    let groupCol = valid.table.column "ProductGroup"
    let coverageByGroup =
        Array.init valid.count id
        |> Array.groupBy (fun i -> groupCol.[i])
        |> Array.sortBy fst
        |> Array.map (fun (k, rows) ->
            { key = k
              n = rows.Length
              share = Json.round 4 (float (rows |> Array.filter (fun i -> inside.[i]) |> Array.length) / float rows.Length) })
    let report =
        { features = features |> Array.map (fun f -> f.name)
          settings = gbmSettings g
          trees = trees
          nodes = Ensemble.nodeCount deployed.model
          level = level
          bands = bands
          heldOut = Metrics.score predLog valid.price
          coverage = Json.round 4 (float (inside |> Array.filter id |> Array.length) / float valid.count)
          coverageByGroup = coverageByGroup
          tuningRmsle = tuningEntry.score.rmsle
          modelBytes = (FileInfo modelPath).Length }
    log (sprintf "demo model held-out RMSLE %.4f, %.0f%% inside the range" report.heldOut.rmsle (100.0 * report.coverage))
    Json.write (Path.Combine(resultsDir, "demo.json")) report

    // 4. Catalogue, examples, parity answers.
    let cat = catalog train
    Json.writeCompact (Path.Combine(docsDir, "catalog.json")) cat
    let ex = examples valid cat 6
    Json.writeCompact (Path.Combine(docsDir, "examples.json")) ex
    let machines = Array.append (parityMachines ()) (ex |> Array.map (fun e -> e.fields))
    let logs =
        machines
        |> Array.map (fun m -> Estimate.predictLog ready (fun c -> match m.TryGetValue c with | true, v -> v | _ -> null))
    Json.write
        (Path.Combine(resultsDir, "parity.json"))
        { note = "Log-price predictions from the .NET build of Gavel.Core; scripts/check-js.mjs checks the Fable build gives the same."
          machines = machines
          logPrices = logs }
    report
