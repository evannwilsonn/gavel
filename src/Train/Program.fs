module Gavel.Program

open System.IO

let usage =
    """usage: dotnet run --project src/Train -c Release -- <command>
  tune      choose encoding, features and model settings on the 2011 tuning window -> results/tuning.json
  mlnet     the same choices through ML.NET's FastTree, LightGBM and FastForest -> results/tuning.json
  holdout   fit on all of Train.csv, score the Jan-Apr 2012 sales once -> results/holdout.json, errors.json
  demo      train and export the browser model -> docs/model.json, catalog.json, examples.json, results/demo.json
  page      write docs/index.html from web/template.html and results/
  bench     time one small boosting run (development check)"""

[<EntryPoint>]
let main argv =
    let root = Directory.GetCurrentDirectory()
    let data = Path.Combine(root, "data")
    let results = Path.Combine(root, "results")
    let docs = Path.Combine(root, "docs")
    match argv with
    | [| "tune" |] -> Experiments.tune data results |> ignore; 0
    | [| "mlnet" |] -> Experiments.mlnetCheck data results; 0
    | [| "holdout" |] -> Experiments.holdout data results |> ignore; 0
    | [| "demo" |] -> Demo.build data results docs None |> ignore; 0
    | [| "demo-preview"; dir |] -> Demo.build data dir dir (Some Data.Alphabetical) |> ignore; 0
    | [| "page" |] -> Page.write root; 0
    | [| "mlnet-smoke" |] ->
        let r = System.Random(1)
        let x = Array.init 2000 (fun _ -> [| r.NextDouble(); r.NextDouble() |])
        let y = x |> Array.map (fun v -> 3.0 * v.[0] + v.[1])
        for l in [ MlNetCheck.LightGbm(50, 15, 10, 0.1); MlNetCheck.FastTree(50, 15, 10, 0.1); MlNetCheck.FastForest(10, 64, 2) ] do
            let p = (MlNetCheck.fitPredict l x y [ x ]).Head
            printfn "%s: RMSE %.4f" (MlNetCheck.name l) (sqrt (Array.map2 (fun a b -> (a - b) * (a - b)) p y |> Array.average))
        0
    | [| "bench" |] ->
        let train = Data.loadTrain data
        let fit, check = Experiments.splitTuning train
        let p = Experiments.prepare (Data.fullFeatures Data.standard Data.ByPrice fit) fit check
        let sw = Experiments.timed ()
        let g = { Learners.gbmDefaults with trees = 50; learningRate = 0.1 }
        let model, trees, curve = Experiments.gbmBest g p
        Experiments.log (sprintf "50 trees in %.1fs, RMSLE after 50: %.4f" sw.Elapsed.TotalSeconds curve.[curve.Length - 1])
        let sw = Experiments.timed ()
        let f = Learners.fitForest { Learners.forestDefaults with trees = 4; maxSamples = 0 } p.fitBins p.fitY
        let pred = Learners.predictAll f p.testX
        Experiments.log (sprintf "4 forest trees in %.1fs, RMSLE %.4f, nodes %d" sw.Elapsed.TotalSeconds (Metrics.rmsle pred p.testY) (Ensemble.nodeCount f))
        0
    | _ ->
        eprintfn "%s" usage
        1
