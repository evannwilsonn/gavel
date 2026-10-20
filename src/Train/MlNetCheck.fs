/// The same data through ML.NET's tree learners, to check the from-scratch ones against an
/// established library.
module Gavel.MlNetCheck

open Microsoft.ML
open Microsoft.ML.Data

[<CLIMutable>]
type Row = { Features: float32[]; Label: float32 }

[<CLIMutable>]
type Prediction = { Score: float32 }

type Learner =
    | FastForest of trees: int * leaves: int * minLeaf: int
    | FastTree of trees: int * leaves: int * minLeaf: int * learningRate: float
    | LightGbm of trees: int * leaves: int * minLeaf: int * learningRate: float

let name =
    function
    | FastForest(t, l, m) -> sprintf "ML.NET FastForest (%d trees, %d leaves, min leaf %d)" t l m
    | FastTree(t, l, m, r) -> sprintf "ML.NET FastTree (%d trees, %d leaves, min leaf %d, rate %g)" t l m r
    | LightGbm(t, l, m, r) -> sprintf "ML.NET LightGBM (%d trees, %d leaves, min leaf %d, rate %g)" t l m r

let private view (ml: MLContext) (x: float[][]) (y: float[]) =
    let nf = x.[0].Length
    let rows =
        Array.init x.Length (fun i ->
            // FastTree drops any row with a NaN, so missing values become -1, below every
            // real value (all codes and measurements are >= 0): the same as missing-goes-left.
            { Features = x.[i] |> Array.map (fun v -> if System.Double.IsNaN v then -1.0f else float32 v)
              Label = float32 y.[i] })
    let schema = SchemaDefinition.Create(typeof<Row>)
    schema.["Features"].ColumnType <- VectorDataViewType(NumberDataViewType.Single, nf)
    ml.Data.LoadFromEnumerable(rows, schema)

/// Fits on (x, y) and returns predictions (log price) for each test matrix.
let fitPredict (learner: Learner) (x: float[][]) (y: float[]) (tests: float[][] list) : float[] list =
    let ml = MLContext(seed = 2026)
    let train = view ml x y
    let model: ITransformer =
        match learner with
        | FastForest(t, l, m) ->
            ml.Regression.Trainers.FastForest(
                numberOfTrees = t,
                numberOfLeaves = l,
                minimumExampleCountPerLeaf = m
            ).Fit(train)
        | FastTree(t, l, m, r) ->
            ml.Regression.Trainers.FastTree(
                numberOfTrees = t,
                numberOfLeaves = l,
                minimumExampleCountPerLeaf = m,
                learningRate = r
            ).Fit(train)
        | LightGbm(t, l, m, r) ->
            ml.Regression.Trainers.LightGbm(
                numberOfIterations = t,
                numberOfLeaves = l,
                minimumExampleCountPerLeaf = m,
                learningRate = r
            ).Fit(train)
    tests
    |> List.map (fun tx ->
        let dummy = Array.zeroCreate<float> tx.Length
        let scored = model.Transform(view ml tx dummy)
        ml.Data.CreateEnumerable<Prediction>(scored, false)
        |> Seq.map (fun p -> float p.Score)
        |> Seq.toArray)
