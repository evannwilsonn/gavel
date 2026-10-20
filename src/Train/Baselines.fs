/// Baselines that need no model: medians of the fitting sales' prices.
module Gavel.Baselines

open System.Collections.Generic
open Gavel.Data

let private median (xs: float seq) =
    let a = xs |> Seq.toArray |> Array.sort
    let n = a.Length
    if n % 2 = 1 then a.[n / 2] else (a.[n / 2 - 1] + a.[n / 2]) / 2.0

/// Every sale gets the median price of all fitting sales.
let globalMedian (fit: Sales) (target: Sales) =
    let m = log (1.0 + median fit.price)
    Array.create target.count m

/// Median price of sales sharing the key; falls back to `fallback` for keys seen fewer than
/// `minCount` times.
let private byKey (key: Sales -> int -> string) (minCount: int) (fit: Sales) (target: Sales) (fallback: float[]) =
    let groups = Dictionary<string, ResizeArray<float>>()
    for i in 0 .. fit.count - 1 do
        let k = key fit i
        match groups.TryGetValue k with
        | true, l -> l.Add fit.price.[i]
        | _ -> groups.[k] <- ResizeArray [ fit.price.[i] ]
    let medians = Dictionary<string, float>()
    for kv in groups do
        if kv.Value.Count >= minCount then medians.[kv.Key] <- log (1.0 + median kv.Value)
    Array.init target.count (fun i ->
        match medians.TryGetValue(key target i) with
        | true, m -> m
        | _ -> fallback.[i])

let private col (name: string) (s: Sales) =
    let c = s.table.column name
    fun i -> c.[i]

/// Median price of the product group (dozer, excavator, ...).
let groupMedian (fit: Sales) (target: Sales) =
    byKey (fun s i -> col "ProductGroup" s i) 1 fit target (globalMedian fit target)

/// Median price of the product group and year made (at least 20 sales), else the group's.
let groupYearMedian (fit: Sales) (target: Sales) =
    byKey
        (fun s i -> col "ProductGroup" s i + "|" + col "YearMade" s i)
        20
        fit
        target
        (groupMedian fit target)

/// Median of the same base model and year made (at least 5 sales), else group and year.
let modelYearMedian (fit: Sales) (target: Sales) =
    byKey
        (fun s i -> col "fiBaseModel" s i + "|" + col "YearMade" s i)
        5
        fit
        target
        (groupYearMedian fit target)
