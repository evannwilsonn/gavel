/// Scores. Predictions and targets are on the log(1 + price) scale unless named otherwise.
module Gavel.Metrics

/// Root mean squared log error: RMSE between log(1 + predicted) and log(1 + actual).
let rmsle (predLog: float[]) (targetLog: float[]) =
    let mutable s = 0.0
    for i in 0 .. predLog.Length - 1 do
        let d = max 0.0 predLog.[i] - targetLog.[i]
        s <- s + d * d
    sqrt (s / float predLog.Length)

/// Mean absolute error in dollars.
let mae (predLog: float[]) (price: float[]) =
    Array.map2 (fun p y -> abs (exp p - 1.0 - y)) predLog price |> Array.average

/// R^2 on dollars (what scikit-learn's model.score reports for the course's models).
let r2 (predLog: float[]) (price: float[]) =
    let mean = Array.average price
    let ssRes = Array.map2 (fun p y -> let d = exp p - 1.0 - y in d * d) predLog price |> Array.sum
    let ssTot = price |> Array.sumBy (fun y -> (y - mean) * (y - mean))
    1.0 - ssRes / ssTot

/// Median absolute percentage error, in percent.
let medianApe (predLog: float[]) (price: float[]) =
    let a = Array.map2 (fun p y -> abs (exp p - 1.0 - y) / y) predLog price |> Array.sort
    100.0 * a.[a.Length / 2]

type Score =
    { rmsle: float
      maeDollars: float
      r2: float
      medianAbsPctError: float
      n: int }

let score (predLog: float[]) (price: float[]) =
    let t = price |> Array.map (fun p -> log (1.0 + p))
    { rmsle = Json.round 4 (rmsle predLog t)
      maeDollars = Json.round 0 (mae predLog price)
      r2 = Json.round 4 (r2 predLog price)
      medianAbsPctError = Json.round 1 (medianApe predLog price)
      n = price.Length }

/// Quantile of a sample (linear interpolation).
let quantile (q: float) (xs: float[]) =
    let a = Array.sort xs
    let h = q * float (a.Length - 1)
    let lo = int (floor h)
    let hi = min (a.Length - 1) (lo + 1)
    a.[lo] + (h - float lo) * (a.[hi] - a.[lo])
