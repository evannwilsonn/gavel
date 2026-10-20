/// Gradient-boosted trees and a random forest, both built on TreeBuilder.
module Gavel.Learners

open System
open System.Threading.Tasks
open Gavel.Binning
open Gavel.TreeBuilder

type Gbm =
    { trees: int
      learningRate: float
      leaves: int
      maxDepth: int
      minLeaf: int
      lambda: float
      /// fraction of rows each tree sees (sampled without replacement)
      rowFraction: float
      /// fraction of features each tree may use
      colFraction: float
      /// stop when the watch score hasn't improved for this many trees (0 = never)
      patience: int
      seed: int }

let gbmDefaults =
    { trees = 1000
      learningRate = 0.05
      leaves = 63
      maxDepth = 20
      minLeaf = 20
      lambda = 1.0
      rowFraction = 0.7
      colFraction = 0.8
      patience = 250
      seed = 2026 }

/// Squared-error boosting on y (log price). If `watch` is given, returns its RMSE after every
/// tree, which is how the number of trees is chosen.
let fitGbm (p: Gbm) (data: Binned) (y: float[]) (watch: (Binned * float[]) option) : Ensemble * float[] =
    let n = data.rows
    let rng = Random(p.seed)
    let bias = Array.average y
    let pred = Array.create n bias
    let resid = Array.zeroCreate<float> n
    let allFeatures = Array.init data.bins.Length id
    let tp =
        { maxLeaves = p.leaves
          maxDepth = p.maxDepth
          minLeaf = p.minLeaf
          lambda = p.lambda
          featureFraction = 1.0
          shrink = p.learningRate }
    let watchPred = watch |> Option.map (fun (w, _) -> Array.create w.rows bias)
    let curve = ResizeArray<float>()
    let trees = ResizeArray<Tree>()
    let mutable bestScore = Double.MaxValue
    let mutable bestAt = 0
    let mutable m = 0
    while m < p.trees && (p.patience = 0 || watch.IsNone || m - bestAt < p.patience) do
        m <- m + 1
        for i in 0 .. n - 1 do
            resid.[i] <- y.[i] - pred.[i]
        let rows =
            if p.rowFraction >= 1.0 then Array.init n id
            else
                let keep = ResizeArray<int>(int (float n * p.rowFraction) + 16)
                for i in 0 .. n - 1 do
                    if rng.NextDouble() < p.rowFraction then keep.Add i
                keep.ToArray()
        let features =
            if p.colFraction >= 1.0 then allFeatures
            else
                let k = max 1 (int (Math.Round(p.colFraction * float allFeatures.Length)))
                let a = Array.copy allFeatures
                for i in 0 .. k - 1 do
                    let j = i + rng.Next(a.Length - i)
                    let t = a.[i]
                    a.[i] <- a.[j]
                    a.[j] <- t
                Array.sub a 0 k |> Array.sort
        let built = build tp data features resid rows (Random(rng.Next()))
        trees.Add built.tree
        Parallel.For(0, n, fun i -> pred.[i] <- pred.[i] + predictBinned built data i) |> ignore
        match watch, watchPred with
        | Some(w, wy), Some wp ->
            let mutable se = 0.0
            for i in 0 .. w.rows - 1 do
                wp.[i] <- wp.[i] + predictBinned built w i
                let d = wp.[i] - wy.[i]
                se <- se + d * d
            let score = sqrt (se / float w.rows)
            curve.Add score
            if score < bestScore then
                bestScore <- score
                bestAt <- m
        | _ -> ()
    { bias = bias
      scale = 1.0
      trees = trees.ToArray() },
    curve.ToArray()

type Forest =
    { trees: int
      /// rows drawn (with replacement) for each tree; 0 means as many as there are rows
      maxSamples: int
      /// fraction of features tried at each split (scikit-learn's max_features)
      featureFraction: float
      minLeaf: int
      maxLeaves: int
      seed: int }

let forestDefaults =
    { trees = 100
      maxSamples = 0
      featureFraction = 0.5
      minLeaf = 3
      maxLeaves = Int32.MaxValue
      seed = 2026 }

/// A random forest on y: bootstrap samples, a random subset of features at each split,
/// trees grown out to `minLeaf`, predictions averaged.
let fitForest (p: Forest) (data: Binned) (y: float[]) : Ensemble =
    let n = data.rows
    let m = if p.maxSamples <= 0 then n else min n p.maxSamples
    let features = Array.init data.bins.Length id
    let tp =
        { maxLeaves = p.maxLeaves
          maxDepth = 64
          minLeaf = p.minLeaf
          lambda = 0.0
          featureFraction = p.featureFraction
          shrink = 1.0 }
    let seeds =
        let r = Random(p.seed)
        Array.init p.trees (fun _ -> r.Next())
    let trees = Array.zeroCreate<Tree> p.trees
    Parallel.For(
        0,
        p.trees,
        ParallelOptions(MaxDegreeOfParallelism = Environment.ProcessorCount),
        fun (k: int) ->
            let rng = Random(seeds.[k])
            let rows = Array.init m (fun _ -> rng.Next n)
            trees.[k] <- (build tp data features y rows rng).tree
    )
    |> ignore
    { bias = 0.0
      scale = 1.0 / float p.trees
      trees = trees }

/// Predictions for every row of a raw (row-major) feature matrix.
let predictAll (e: Ensemble) (x: float[][]) = Array.Parallel.map (Ensemble.predict e) x
