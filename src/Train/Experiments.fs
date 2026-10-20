/// The experiments behind every number in the README.
///
/// Splits (all by sale date, never at random, because the job is to price future sales):
///   fitting window   Train.csv sales before 1 Sep 2011
///   tuning window    Train.csv sales from 1 Sep to 31 Dec 2011 (four months, like the held-out set)
///   final fit        all of Train.csv (to 31 Dec 2011)
///   held-out         Valid.csv, Jan-Apr 2012, scored once at the end with settings fixed by `tune`
/// The Kaggle test period (May-Nov 2012) has no published prices, so it is not used.
module Gavel.Experiments

open System
open System.Diagnostics
open System.IO
open Gavel.Data
open Gavel.Learners

let log (s: string) =
    printfn "[%s] %s" (DateTime.Now.ToString "HH:mm:ss") s

/// Features encoded for a fitting set and a scoring set, with bins learned from the fitting set.
type Prepared =
    { features: Feature[]
      fitX: float[][]
      fitBins: Binning.Binned
      fitY: float[]
      testX: float[][]
      testBins: Binning.Binned
      testY: float[] }

let prepare (features: Feature[]) (fit: Sales) (test: Sales) =
    let fitX = encode features fit
    let testX = encode features test
    let isCat = features |> Array.map (fun f -> f.kind = "cat")
    let cuts = Binning.fit 255 4096 isCat fitX
    { features = features
      fitX = fitX
      fitBins = Binning.apply cuts fitX
      fitY = fit.target
      testX = testX
      testBins = Binning.apply cuts testX
      testY = test.target }

/// One row of a results table.
type Entry =
    { name: string
      settings: string
      /// for boosting: the number of trees with the best tuning-window score
      trees: int
      score: Metrics.Score
      seconds: float }

let entry name settings trees (pred: float[]) (price: float[]) (sw: Stopwatch) =
    let e =
        { name = name
          settings = settings
          trees = trees
          score = Metrics.score pred price
          seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) }
    log (sprintf "%-46s RMSLE %.4f  (%s, %.0fs)" name e.score.rmsle settings e.seconds)
    e

let gbmSettings (g: Gbm) =
    sprintf
        "rate %g, %d leaves, min leaf %d, lambda %g, rows %g, features %g"
        g.learningRate
        g.leaves
        g.minLeaf
        g.lambda
        g.rowFraction
        g.colFraction

let forestSettings (f: Forest) =
    sprintf
        "%d trees, %s rows per tree, %g of features per split, min leaf %d"
        f.trees
        (if f.maxSamples = 0 then "all" else string f.maxSamples)
        f.featureFraction
        f.minLeaf

/// Boosting with a watch set: returns the model cut at its best tree count, with that count.
let gbmBest (g: Gbm) (p: Prepared) =
    let model, curve = fitGbm g p.fitBins p.fitY (Some(p.testBins, p.testY))
    let best = (curve |> Array.mapi (fun i v -> i, v) |> Array.minBy snd |> fst) + 1
    Ensemble.truncate best model, best, curve

let timed () = Stopwatch.StartNew()

let splitTuning (train: Sales) =
    let cut = day 2011 9 1
    where (fun i -> train.days.[i] < cut) train, where (fun i -> train.days.[i] >= cut) train

// ---------------------------------------------------------------------------------------------

type Choice =
    { order: string
      features: FeatureSet
      gbm: Gbm
      gbmTrees: int
      forest: Forest
      /// weight of the boosted model in the final blend (1.0 = boosting alone)
      blendWeight: float }

type Tuning =
    { fitting: string
      tuning: string
      fittingSales: int
      tuningSales: int
      baselines: Entry[]
      encoding: Entry[]
      boosting: Entry[]
      forest: Entry[]
      blend: Entry[]
      mlnet: Entry[]
      /// RMSE on the tuning window after each tree, for the chosen boosting settings
      curve: float[]
      chosen: Choice }

let quick =
    { gbmDefaults with
        trees = 700
        learningRate = 0.1 }

let tune (dataDir: string) (resultsDir: string) =
    let train = loadTrain dataDir
    let fit, check = splitTuning train
    log (sprintf "fitting window %d sales, tuning window %d sales" fit.count check.count)
    let price = check.price
    let baselines =
        [| let sw = timed ()
           entry "Median price of all sales" "" 0 (Baselines.globalMedian fit check) price sw
           let sw = timed ()
           entry "Median by product group" "" 0 (Baselines.groupMedian fit check) price sw
           let sw = timed ()
           entry "Median by product group and year made" "at least 20 sales" 0 (Baselines.groupYearMedian fit check) price sw
           let sw = timed ()
           entry "Median by base model and year made" "at least 5 sales, else group and year" 0 (Baselines.modelYearMedian fit check) price sw |]

    // 1. How to encode categories, then which columns to use, with quick boosting settings.
    //    One question at a time: the column variants use the encoding that won.
    let quickRun (label: string) (set: FeatureSet) (order: Order) =
        let sw = timed ()
        let p = prepare (fullFeatures set order fit) fit check
        let model, trees, _ = gbmBest quick p
        entry ("Boosting, " + label) (gbmSettings quick) trees (predictAll model p.testX) price sw
    let orders =
        [| ByPrice, quickRun "categories by price" standard ByPrice
           Alphabetical, quickRun "categories alphabetical" standard Alphabetical |]
    let order, orderEntry = orders |> Array.minBy (fun (_, e) -> e.score.rmsle)
    log (sprintf "chosen encoding: %s" (orderName order))
    let sets =
        [| standard, orderEntry
           { standard with engineered = false }, quickRun (sprintf "categories %s, no age or time trend" (orderName order)) { standard with engineered = false } order
           { standard with identifiers = true }, quickRun (sprintf "categories %s, plus SalesID and MachineID" (orderName order)) { standard with identifiers = true } order |]
    let set, _ = sets |> Array.minBy (fun (_, e) -> e.score.rmsle)
    let encoding = Array.append (orders |> Array.map snd) (sets |> Array.skip 1 |> Array.map snd)
    log (sprintf "chosen columns: engineered %b, identifiers %b" set.engineered set.identifiers)
    let p = prepare (fullFeatures set order fit) fit check

    // 2. Boosting settings, at the slower learning rate.
    let grid =
        [| { gbmDefaults with trees = 2500; leaves = 63; minLeaf = 20 }
           { gbmDefaults with trees = 2500; leaves = 255; minLeaf = 20 }
           { gbmDefaults with trees = 2500; leaves = 127; minLeaf = 50; colFraction = 0.5 } |]
    let gbmRuns =
        grid
        |> Array.map (fun g ->
            let sw = timed ()
            let model, trees, curve = gbmBest g p
            let pred = predictAll model p.testX
            g, trees, curve, pred, entry "Boosting (from scratch)" (gbmSettings g) trees pred price sw)
    let bestG, bestTrees, bestCurve, gbmPred, _ = gbmRuns |> Array.minBy (fun (_, _, _, _, e) -> e.score.rmsle)

    // 3. Random forest, close to the course's final settings (90 trees, max_features 0.5,
    //    min_samples_split 5, all rows), on log price.
    let forests =
        [| { forestDefaults with trees = 90; minLeaf = 2 }
           { forestDefaults with trees = 90; minLeaf = 5; featureFraction = 0.33 } |]
    let forestRuns =
        forests
        |> Array.map (fun f ->
            let sw = timed ()
            let model = fitForest f p.fitBins p.fitY
            let pred = predictAll model p.testX
            f, pred, entry "Random forest (from scratch)" (forestSettings f) 0 pred price sw)
    let bestF, forestPred, _ = forestRuns |> Array.minBy (fun (_, _, e) -> e.score.rmsle)

    // 4. Averaging the two, in log space.
    let blends =
        [| 0.0; 0.25; 0.5; 0.75; 1.0 |]
        |> Array.map (fun w ->
            let pred = Array.map2 (fun g f -> w * g + (1.0 - w) * f) gbmPred forestPred
            w, entry (sprintf "Blend: %g boosting + %g forest" w (1.0 - w)) "" 0 pred price (timed ()))
    let bestW, _ = blends |> Array.minBy (fun (_, e) -> e.score.rmsle)

    // 5. ML.NET with matching settings, as a check on the from-scratch learners.
    let mlnet =
        [| MlNetCheck.LightGbm(bestTrees, bestG.leaves, bestG.minLeaf, bestG.learningRate)
           MlNetCheck.FastTree(bestTrees, bestG.leaves, bestG.minLeaf, bestG.learningRate)
           MlNetCheck.FastForest(90, 4096, bestF.minLeaf) |]
        |> Array.map (fun l ->
            let sw = timed ()
            let pred = (MlNetCheck.fitPredict l p.fitX p.fitY [ p.testX ]).Head
            entry (MlNetCheck.name l) "same features" 0 pred price sw)

    let result =
        { fitting = "Train.csv sales before 2011-09-01"
          tuning = "Train.csv sales 2011-09-01 to 2011-12-31"
          fittingSales = fit.count
          tuningSales = check.count
          baselines = baselines
          encoding = encoding
          boosting = gbmRuns |> Array.map (fun (_, _, _, _, e) -> e)
          forest = forestRuns |> Array.map (fun (_, _, e) -> e)
          blend = blends |> Array.map snd
          mlnet = mlnet
          curve = bestCurve |> Array.map (Json.round 5)
          chosen =
            { order = orderName order
              features = set
              gbm = bestG
              gbmTrees = bestTrees
              forest = bestF
              blendWeight = bestW } }
    Json.write (Path.Combine(resultsDir, "tuning.json")) result
    result

// ---------------------------------------------------------------------------------------------
// The held-out run: settings come from results/tuning.json, the 2012 prices are read once.

type Course =
    { notebook: string
      model: string
      validRmsle: float
      validMae: float
      validR2: float
      note: string }

/// From the course notebook's saved output (ideal_model, show_scores).
let course =
    { notebook = "section-3-structured-data-projects/end-to-end-bluebook-bulldozer-price-regression-v2.ipynb"
      model = "RandomForestRegressor(n_estimators=90, min_samples_split=5, max_features=0.5), on dollar prices"
      validRmsle = 0.24733
      validMae = 5979.0
      validR2 = 0.88105
      note =
        "Same Jan-Apr 2012 validation sales. The notebook chose its settings by scoring them on these sales, so its figure is not a held-out estimate." }

type Group =
    { key: string
      n: int
      rmsle: float
      /// mean of log(estimate) - log(price): below 0 means the model priced too low
      bias: float }

type Miss =
    { model: string
      group: string
      yearMade: string
      state: string
      saleDate: string
      price: float
      estimate: float }

type Importance = { feature: string; meanAbsEffect: float }

type Errors =
    { byProductGroup: Group[]
      byModelHistory: Group[]
      byYearMade: Group[]
      byHours: Group[]
      bySaleMonth: Group[]
      largestMisses: Miss[]
      importance: Importance[] }

type Holdout =
    { fit: string
      heldOut: string
      fitSales: int
      heldOutSales: int
      settings: Choice
      baselines: Entry[]
      models: Entry[]
      mlnet: Entry[]
      final: Entry
      trainingRmsle: float
      course: Course
      testPeriod: string }

let private groups (keyOf: int -> string) (order: string[]) (pred: float[]) (y: float[]) =
    let n = pred.Length
    Array.init n id
    |> Array.groupBy keyOf
    |> Array.map (fun (k, rows) ->
        let d = rows |> Array.map (fun i -> max 0.0 pred.[i] - y.[i])
        { key = k
          n = rows.Length
          rmsle = Json.round 4 (sqrt (d |> Array.averageBy (fun x -> x * x)))
          bias = Json.round 4 (Array.average d) })
    |> Array.sortBy (fun g ->
        match Array.tryFindIndex ((=) g.key) order with
        | Some i -> i
        | None -> 1000)

let errorAnalysis (final: float[]) (p: Prepared) (gbm: Ensemble) (train: Sales) (valid: Sales) =
    let y = valid.target
    let col name = valid.table.column name
    let trainModelCounts = train.table.column "ModelID" |> Array.countBy id |> dict
    let history i =
        match trainModelCounts.TryGetValue((col "ModelID").[i]) with
        | true, c when c >= 100 -> "100+ earlier sales of the model"
        | true, c when c >= 10 -> "10-99 earlier sales"
        | true, _ -> "1-9 earlier sales"
        | _ -> "model never sold before"
    let ym i =
        let v = Features.yearMade (col "YearMade").[i]
        if Double.IsNaN v then "year made unknown"
        elif v < 1990.0 then "before 1990"
        elif v < 2000.0 then "1990s"
        else "2000 or later"
    let hours i =
        if Double.IsNaN(Features.hours (col "MachineHoursCurrentMeter").[i]) then "hours not recorded" else "hours recorded"
    let month i =
        match Features.parseSaleDate (col "saledate").[i] with
        | Some d -> sprintf "2012-%02d" d.month
        | None -> "?"
    let misses =
        Array.init valid.count id
        |> Array.sortByDescending (fun i -> abs (final.[i] - y.[i]))
        |> Array.truncate 12
        |> Array.map (fun i ->
            { model = (col "fiModelDesc").[i]
              group = (col "ProductGroup").[i]
              yearMade = (col "YearMade").[i]
              state = (col "state").[i]
              saleDate = ((col "saledate").[i]).Split(' ').[0]
              price = valid.price.[i]
              estimate = Math.Round(exp final.[i] - 1.0, 0) })
    // Mean size of each feature's contribution in the boosted model, over the held-out sales.
    let nf = p.features.Length
    let totals = Array.zeroCreate<float> nf
    let parts = p.testX |> Array.Parallel.map (fun x -> snd (Ensemble.explain gbm nf x))
    for c in parts do
        for j in 0 .. nf - 1 do
            totals.[j] <- totals.[j] + abs c.[j]
    let importance =
        Array.init nf (fun j ->
            { feature = p.features.[j].name
              meanAbsEffect = Json.round 4 (totals.[j] / float parts.Length) })
        |> Array.sortByDescending (fun i -> i.meanAbsEffect)
        |> Array.truncate 15
    { byProductGroup = groups (fun i -> (col "ProductGroup").[i]) [| "BL"; "MG"; "SSL"; "TEX"; "TTT"; "WL" |] final y
      byModelHistory =
        groups history [| "model never sold before"; "1-9 earlier sales"; "10-99 earlier sales"; "100+ earlier sales of the model" |] final y
      byYearMade = groups ym [| "year made unknown"; "before 1990"; "1990s"; "2000 or later" |] final y
      byHours = groups hours [| "hours recorded"; "hours not recorded" |] final y
      bySaleMonth = groups month [| "2012-01"; "2012-02"; "2012-03"; "2012-04" |] final y
      largestMisses = misses
      importance = importance }

let orderOf (s: string) = if s = orderName Alphabetical then Alphabetical else ByPrice

let holdout (dataDir: string) (resultsDir: string) =
    let tuning: Tuning = Json.read (Path.Combine(resultsDir, "tuning.json"))
    let c = tuning.chosen
    let train = loadTrain dataDir
    let valid = loadValid dataDir
    log (sprintf "final fit %d sales, held-out %d sales" train.count valid.count)
    let price = valid.price
    let baselines =
        [| entry "Median price of all sales" "" 0 (Baselines.globalMedian train valid) price (timed ())
           entry "Median by product group" "" 0 (Baselines.groupMedian train valid) price (timed ())
           entry "Median by product group and year made" "at least 20 sales" 0 (Baselines.groupYearMedian train valid) price (timed ())
           entry "Median by base model and year made" "at least 5 sales, else group and year" 0 (Baselines.modelYearMedian train valid) price (timed ()) |]
    let p = prepare (fullFeatures c.features (orderOf c.order) train) train valid
    let sw = timed ()
    let g = { c.gbm with trees = c.gbmTrees; patience = 0 }
    let gbm, _ = fitGbm g p.fitBins p.fitY None
    let gbmPred = predictAll gbm p.testX
    let gbmEntry = entry "Boosting (from scratch)" (gbmSettings g) c.gbmTrees gbmPred price sw
    let sw = timed ()
    let forest = fitForest c.forest p.fitBins p.fitY
    let forestPred = predictAll forest p.testX
    let forestEntry = entry "Random forest (from scratch)" (forestSettings c.forest) 0 forestPred price sw
    let w = c.blendWeight
    let finalPred = Array.map2 (fun a b -> w * a + (1.0 - w) * b) gbmPred forestPred
    let finalEntry =
        entry (sprintf "Final: %g boosting + %g forest" w (1.0 - w)) "weights chosen on the tuning window" 0 finalPred price (timed ())
    let trainPred =
        Array.map2 (fun a b -> w * a + (1.0 - w) * b) (predictAll gbm p.fitX) (predictAll forest p.fitX)
    let mlnet =
        [| MlNetCheck.LightGbm(c.gbmTrees, c.gbm.leaves, c.gbm.minLeaf, c.gbm.learningRate)
           MlNetCheck.FastTree(c.gbmTrees, c.gbm.leaves, c.gbm.minLeaf, c.gbm.learningRate)
           MlNetCheck.FastForest(90, 4096, c.forest.minLeaf) |]
        |> Array.map (fun l ->
            let sw = timed ()
            let pred = (MlNetCheck.fitPredict l p.fitX p.fitY [ p.testX ]).Head
            entry (MlNetCheck.name l) "same features" 0 pred price sw)
    let result =
        { fit = "all of Train.csv (sales to 2011-12-31)"
          heldOut = "Valid.csv with ValidSolution.csv (sales 2012-01-01 to 2012-04-30)"
          fitSales = train.count
          heldOutSales = valid.count
          settings = c
          baselines = baselines
          models = [| gbmEntry; forestEntry |]
          mlnet = mlnet
          final = finalEntry
          trainingRmsle = Json.round 4 (Metrics.rmsle trainPred p.fitY)
          course = course
          testPeriod =
            "Kaggle's test sales (May-Nov 2012) come without prices in the course's copy of the data, so they are not scored." }
    Json.write (Path.Combine(resultsDir, "holdout.json")) result
    Json.write (Path.Combine(resultsDir, "errors.json")) (errorAnalysis finalPred p gbm train valid)
    result
