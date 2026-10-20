/// Turns feature values into small integer bins, so a tree can find its best split by scanning
/// a histogram instead of sorting (the trick LightGBM and XGBoost's "hist" method use).
module Gavel.Binning

open System

/// Bin 0 holds missing values. Bin k >= 1 holds values v with cuts.[k-2] < v <= cuts.[k-1].
/// The last cut is +infinity.
type Binned =
    { bins: uint16[][] // [feature][row]
      nBins: int[]
      cuts: float[][] }

    member b.rows = if b.bins.Length = 0 then 0 else b.bins.[0].Length

/// The raw-value threshold for "bin <= b goes left". b = 0 sends only missing values left.
let threshold (cuts: float[]) (b: int) = if b = 0 then -1e300 else cuts.[b - 1]

/// Cut points for one feature: one bin per distinct value when there are few, otherwise bins
/// holding roughly equal numbers of rows. Cuts sit halfway between neighbouring values.
let fitCuts (maxBins: int) (values: float seq) : float[] =
    let counts =
        values
        |> Seq.filter (fun v -> not (Double.IsNaN v))
        |> Seq.countBy id
        |> Seq.sortBy fst
        |> Seq.toArray
    let k = counts.Length
    if k = 0 then [| Double.PositiveInfinity |]
    elif k <= maxBins - 1 then
        Array.init k (fun i ->
            if i = k - 1 then Double.PositiveInfinity
            else (fst counts.[i] + fst counts.[i + 1]) / 2.0)
    else
        let cuts = ResizeArray<float>()
        let mutable remaining = counts |> Array.sumBy (fun (_, c) -> float c)
        let mutable binsLeft = maxBins - 1
        let mutable acc = 0.0
        for i in 0 .. k - 2 do
            let c = float (snd counts.[i])
            acc <- acc + c
            let target = remaining / float binsLeft
            if acc >= target && binsLeft > 1 then
                cuts.Add((fst counts.[i] + fst counts.[i + 1]) / 2.0)
                remaining <- remaining - acc
                binsLeft <- binsLeft - 1
                acc <- 0.0
        cuts.Add Double.PositiveInfinity
        cuts.ToArray()

let binOf (cuts: float[]) (v: float) : uint16 =
    if Double.IsNaN v then 0us
    else
        // first index with v <= cuts.[i]
        let mutable lo = 0
        let mutable hi = cuts.Length - 1
        while lo < hi do
            let mid = (lo + hi) / 2
            if v <= cuts.[mid] then hi <- mid else lo <- mid + 1
        uint16 (lo + 1)

/// Learns cuts from `x` (row-major). `isCategory.[f]` features may use up to `maxCatBins`.
let fit (maxBins: int) (maxCatBins: int) (isCategory: bool[]) (x: float[][]) : float[][] =
    let nf = if x.Length = 0 then 0 else x.[0].Length
    Array.Parallel.init nf (fun f ->
        fitCuts (if isCategory.[f] then maxCatBins else maxBins) (x |> Seq.map (fun row -> row.[f])))

let apply (cuts: float[][]) (x: float[][]) : Binned =
    let nf = cuts.Length
    { bins = Array.Parallel.init nf (fun f -> x |> Array.map (fun row -> binOf cuts.[f] row.[f]))
      nBins = cuts |> Array.map (fun c -> c.Length + 1)
      cuts = cuts }
