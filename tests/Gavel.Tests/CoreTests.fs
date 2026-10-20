module Gavel.Tests.CoreTests

open System
open System.Collections.Generic
open Xunit
open Gavel

let private get (pairs: (string * string) list) =
    let d = dict pairs
    fun (c: string) -> match d.TryGetValue c with | true, v -> v | _ -> null

[<Fact>]
let ``days from civil matches known dates`` () =
    Assert.Equal(0, Features.daysFromCivil 1970 1 1)
    Assert.Equal(10957, Features.daysFromCivil 2000 1 1)
    Assert.Equal(-1, Features.daysFromCivil 1969 12 31)
    Assert.Equal((DateTime(2012, 2, 29) - DateTime(1970, 1, 1)).Days, Features.daysFromCivil 2012 2 29)

[<Fact>]
let ``sale date parts agree with System.DateTime`` () =
    let rng = Random(1)
    for _ in 1 .. 500 do
        let d = DateTime(1989, 1, 1).AddDays(float (rng.Next(9000)))
        let s = (Features.parseSaleDate (sprintf "%d/%d/%d 0:00" d.Month d.Day d.Year)).Value
        Assert.Equal(d.Year, s.year)
        Assert.Equal(d.DayOfYear, s.dayOfYear)
        // pandas: Monday = 0
        Assert.Equal((int d.DayOfWeek + 6) % 7, s.dayOfWeek)

[<Fact>]
let ``numbers parse strictly`` () =
    Assert.Equal(68.0, Features.parseNumber "68")
    Assert.Equal(23.5, Features.parseNumber "23.5")
    Assert.Equal(-3.0, Features.parseNumber " -3 ")
    for bad in [ ""; "-"; "."; "28 inch"; "1e5"; "None or Unspecified"; null ] do
        Assert.True(Double.IsNaN(Features.parseNumber bad), sprintf "%A" bad)

[<Fact>]
let ``placeholder year and zero hours are missing`` () =
    Assert.True(Double.IsNaN(Features.yearMade "1000"))
    Assert.Equal(1998.0, Features.yearMade "1998")
    Assert.True(Double.IsNaN(Features.hours "0"))
    Assert.True(Double.IsNaN(Features.hours ""))
    Assert.Equal(4640.0, Features.hours "4640")

let private features =
    [| { name = "g"; kind = "cat"; column = "ProductGroup"; levels = [| "BL"; "TTT" |]; group = "Product group" }
       { name = "y"; kind = "yearMade"; column = "YearMade"; levels = [||]; group = "Year made" }
       { name = "a"; kind = "age"; column = "YearMade"; levels = [||]; group = "Year made" }
       { name = "m"; kind = "saleMonth"; column = "saledate"; levels = [||]; group = "Sale date" }
       { name = "e"; kind = "saleElapsed"; column = "saledate"; levels = [||]; group = "Sale date" } |]

[<Fact>]
let ``encoding uses level positions and treats unseen values as missing`` () =
    let enc = Features.encoder features
    let x = Features.encode enc (get [ "ProductGroup", "TTT"; "YearMade", "1998"; "saledate", "3/15/2012 0:00" ])
    Assert.Equal(1.0, x.[0])
    Assert.Equal(1998.0, x.[1])
    Assert.Equal(14.0, x.[2])
    Assert.Equal(3.0, x.[3])
    Assert.InRange(x.[4], 23.19, 23.21)
    let y = Features.encode enc (get [ "ProductGroup", "XYZ"; "YearMade", "1000" ])
    Assert.True(Array.forall Double.IsNaN y)

/// root splits feature 0 at 0.5; left leaf 1.0; right splits feature 1 at 10 into 2.0 / 4.0
let private tree =
    { f = [| 0; -1; 1; -1; -1 |]
      t = [| 0.5; 0.0; 10.0; 0.0; 0.0 |]
      l = [| 1; -1; 3; -1; -1 |]
      r = [| 2; -1; 4; -1; -1 |]
      v = [| 2.0; 1.0; 3.0; 2.0; 4.0 |] }

[<Fact>]
let ``trees route values and send missing values left`` () =
    let e = { bias = 0.5; scale = 1.0; trees = [| tree |] }
    Assert.Equal(1.5, Ensemble.predict e [| 0.0; 99.0 |])
    Assert.Equal(2.5, Ensemble.predict e [| 1.0; 10.0 |])
    Assert.Equal(4.5, Ensemble.predict e [| 1.0; 11.0 |])
    Assert.Equal(1.5, Ensemble.predict e [| nan; 11.0 |])
    Assert.Equal(2.5, Ensemble.predict e [| 1.0; nan |])

[<Fact>]
let ``explanations add up to the prediction`` () =
    let e = { bias = 0.5; scale = 0.5; trees = [| tree; tree |] }
    let rng = Random(3)
    for _ in 1 .. 200 do
        let x = [| rng.NextDouble(); rng.NextDouble() * 20.0 |]
        let start, c = Ensemble.explain e 2 x
        Assert.Equal(Ensemble.predict e x, start + Array.sum c, 12)
    let start, c = Ensemble.explain e 2 [| 1.0; 11.0 |]
    Assert.Equal(2.5, start, 12)
    Assert.Equal(1.0, c.[0], 12)
    Assert.Equal(1.0, c.[1], 12)

[<Fact>]
let ``estimates group pushes by field and apply the product group's band`` () =
    let f = features
    let model = { bias = 0.0; scale = 1.0; trees = [| { f = [| 0; -1; -1 |]; t = [| 0.5; 0.; 0. |]; l = [| 1; -1; -1 |]; r = [| 2; -1; -1 |]; v = [| 10.0; 9.0; 11.0 |] } |] }
    let d =
        { features = f
          model = model
          level = 0.8
          bands = [| { key = "all"; lo = -0.3; hi = 0.3 }; { key = "TTT"; lo = -0.1; hi = 0.2 } |] }
    let r = Estimate.ready d
    let est = Estimate.estimate r (get [ "ProductGroup", "TTT" ])
    Assert.Equal(exp 11.0 - 1.0, est.price, 6)
    Assert.Equal(exp 10.9 - 1.0, est.low, 6)
    Assert.Equal(exp 11.2 - 1.0, est.high, 6)
    Assert.Equal("Product group", est.pushes.[0].field)
    Assert.Equal(1.0, est.pushes.[0].effect, 12)
    let other = Estimate.estimate r (get [ "ProductGroup", "BL" ])
    Assert.Equal(exp 9.3 - 1.0, other.high, 6)
