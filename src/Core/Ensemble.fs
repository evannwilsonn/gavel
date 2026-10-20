namespace Gavel

/// One regression tree in flat arrays, the form it is saved in (docs/model.json) and read back from.
/// Node 0 is the root. At node i:
///   f.[i]  the feature it splits on, or -1 for a leaf
///   t.[i]  go left when x <= t.[i]; a missing value (NaN) always goes left
///   l.[i], r.[i]  child node indices
///   v.[i]  what the tree would predict if it stopped at this node (for leaves, the prediction).
///          The inner-node values are only used to explain predictions.
/// Field names are short and lower case because the same record is read from JSON in the browser.
type Tree =
    { f: int[]
      t: float[]
      l: int[]
      r: int[]
      v: float[] }

/// A sum (boosting) or average (random forest) of trees, predicting log(1 + price).
///   prediction = bias + scale * sum of tree outputs
type Ensemble =
    { bias: float
      scale: float
      trees: Tree[] }

module Ensemble =

    let inline private goesLeft (x: float) (threshold: float) = System.Double.IsNaN x || x <= threshold

    /// Index of the leaf that x lands in.
    let leaf (tree: Tree) (x: float[]) =
        let mutable i = 0
        while tree.f.[i] >= 0 do
            i <- if goesLeft x.[tree.f.[i]] tree.t.[i] then tree.l.[i] else tree.r.[i]
        i

    let predict (e: Ensemble) (x: float[]) =
        let mutable s = 0.0
        for tree in e.trees do
            s <- s + tree.v.[leaf tree x]
        e.bias + e.scale * s

    /// Splits a prediction into a starting value plus one contribution per feature
    /// (Saabas path attribution): every time a path moves from a node to a child, the change in
    /// node value is credited to the feature the node split on. The parts add up exactly:
    ///   start + sum contributions = predict e x
    let explain (e: Ensemble) (nFeatures: int) (x: float[]) : float * float[] =
        let contributions = Array.zeroCreate<float> nFeatures
        let mutable start = e.bias
        for tree in e.trees do
            start <- start + e.scale * tree.v.[0]
            let mutable i = 0
            while tree.f.[i] >= 0 do
                let feature = tree.f.[i]
                let next = if goesLeft x.[feature] tree.t.[i] then tree.l.[i] else tree.r.[i]
                contributions.[feature] <- contributions.[feature] + e.scale * (tree.v.[next] - tree.v.[i])
                i <- next
        start, contributions

    /// Keeps only the first n trees (boosting) - used to export a smaller model.
    let truncate (n: int) (e: Ensemble) =
        { e with trees = Array.truncate n e.trees }

    let nodeCount (e: Ensemble) = e.trees |> Array.sumBy (fun t -> t.f.Length)
