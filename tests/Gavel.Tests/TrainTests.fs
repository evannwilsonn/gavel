module Gavel.Tests.TrainTests

open System
open Xunit
open Gavel
open Gavel.Binning

[<Fact>]
let ``csv fields keep quoted commas and quotes`` () =
    Assert.Equal<string[]>([| "1"; "Hydraulic Excavator, Track - 12.0 to 14.0"; ""; "say \"hi\"" |], Csv.splitLine "1,\"Hydraulic Excavator, Track - 12.0 to 14.0\",,\"say \"\"hi\"\"\"")

[<Fact>]
let ``bins and raw thresholds agree`` () =
    let rng = Random(5)
    for many in [ false; true ] do
        let values = Array.init 5000 (fun _ -> if rng.NextDouble() < 0.1 then nan elif many then rng.NextDouble() * 100.0 else float (rng.Next 12))
        let cuts = fitCuts 64 values
        Assert.True(cuts.Length <= 63)
        for v in values do
            let b = int (binOf cuts v)
            for t in 0 .. cuts.Length do
                let rawLeft = Double.IsNaN v || v <= threshold cuts t
                Assert.Equal(b <= t, rawLeft)

[<Fact>]
let ``few distinct values get a bin each`` () =
    let cuts = fitCuts 255 [ 1.0; 2.0; 2.0; 5.0 ]
    Assert.Equal<float[]>([| 1.5; 3.5; Double.PositiveInfinity |], cuts)
    Assert.Equal(0us, binOf cuts nan)
    Assert.Equal(1us, binOf cuts 1.0)
    Assert.Equal(3us, binOf cuts 5.0)

let private synthetic n seed =
    let rng = Random(seed)
    let x = Array.init n (fun _ -> [| rng.NextDouble() * 10.0; float (rng.Next 5); (if rng.NextDouble() < 0.2 then nan else rng.NextDouble()) |])
    let y = x |> Array.map (fun r -> (if r.[0] > 5.0 then 3.0 else 1.0) + 0.5 * r.[1] + (if Double.IsNaN r.[2] then -1.0 else 0.0) + 0.05 * (rng.NextDouble() - 0.5))
    x, y

let private binned (x: float[][]) = apply (fit 255 255 [| false; true; false |] x) x

[<Fact>]
let ``one tree finds the step, the category and the missing values`` () =
    let x, y = synthetic 4000 7
    let data = binned x
    let p: TreeBuilder.Params = { maxLeaves = 64; maxDepth = 10; minLeaf = 5; lambda = 0.0; featureFraction = 1.0; shrink = 1.0 }
    let b = TreeBuilder.build p data [| 0; 1; 2 |] y (Array.init x.Length id) (Random 1)
    let e = { bias = 0.0; scale = 1.0; trees = [| b.tree |] }
    let pred = x |> Array.map (Ensemble.predict e)
    Assert.True(Metrics.rmsle pred y < 0.08)
    // binned and raw routing give identical answers
    for i in 0 .. x.Length - 1 do
        Assert.Equal(pred.[i], TreeBuilder.predictBinned b data i)

[<Fact>]
let ``boosting keeps lowering training error and the watch curve is recorded`` () =
    let x, y = synthetic 3000 11
    let data = binned x
    let g = { Learners.gbmDefaults with trees = 60; learningRate = 0.2; leaves = 8; minLeaf = 10; patience = 0 }
    let model, curve = Learners.fitGbm g data y (Some(data, y))
    Assert.Equal(60, curve.Length)
    Assert.True(curve.[59] < curve.[0])
    Assert.True(curve.[59] < 0.08)
    // the curve is plain RMSE on the watch set, recomputed here from the returned model
    let pred = Learners.predictAll model x
    Assert.Equal(curve.[59], sqrt (Array.map2 (fun p t -> (p - t) * (p - t)) pred y |> Array.average), 9)

[<Fact>]
let ``forest averages bootstrapped trees and is reproducible`` () =
    let x, y = synthetic 2000 13
    let data = binned x
    let f = { Learners.forestDefaults with trees = 8; minLeaf = 3 }
    let a = Learners.fitForest f data y
    let b = Learners.fitForest f data y
    let pa = Learners.predictAll a x
    Assert.Equal<float[]>(pa, Learners.predictAll b x)
    Assert.True(Metrics.rmsle pa y < 0.1)
    Assert.Equal(1.0 / 8.0, a.scale)

[<Fact>]
let ``rmsle clips negative predictions and quantile interpolates`` () =
    Assert.Equal(1.0, Metrics.rmsle [| -5.0 |] [| 1.0 |], 12)
    Assert.Equal(2.5, Metrics.quantile 0.5 [| 4.0; 1.0; 2.0; 3.0 |], 12)

[<Fact>]
let ``deployed model survives a JSON round trip`` () =
    let d =
        { features = [| { name = "g"; kind = "cat"; column = "ProductGroup"; levels = [| "BL" |]; group = "Product group" } |]
          model = { bias = 1.0; scale = 1.0; trees = [| { f = [| -1 |]; t = [| 0.0 |]; l = [| -1 |]; r = [| -1 |]; v = [| 0.5 |] } |] }
          level = 0.8
          bands = [| { key = "all"; lo = -0.2; hi = 0.2 } |] }
    let path = IO.Path.GetTempFileName()
    Json.writeCompact path d
    let back = Json.read<Deployed> path
    Assert.Equal(d.features.[0].levels.[0], back.features.[0].levels.[0])
    Assert.Equal(1.5, Ensemble.predict back.model [| 0.0 |])
    Assert.Equal(0.2, back.bands.[0].hi)
