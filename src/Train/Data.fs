/// Loading the sales, the time-based splits, and the feature lists.
module Gavel.Data

open System
open System.Collections.Generic
open System.IO
open Gavel.Csv

/// A set of sales: the raw table, plus price and sale date pulled out.
type Sales =
    { table: Table
      price: float[] // SalePrice in dollars
      days: int[] } // sale date, days since 1 Jan 1970

    member s.count = s.price.Length
    /// log(1 + price): the competition scores RMSE on this scale (RMSLE), so the models fit it.
    member s.target = s.price |> Array.map (fun p -> log (1.0 + p))
    member s.get (row: int) =
        let t = s.table
        fun (column: string) ->
            match Array.tryFindIndex ((=) column) t.header with
            | Some j -> t.columns.[j].[row]
            | None -> null

let private daysOf (table: Table) =
    table.column "saledate"
    |> Array.map (fun s ->
        match Features.parseSaleDate s with
        | Some d -> d.days
        | None -> failwithf "bad saledate %s" s)

let private ofTable (table: Table) (price: float[]) =
    { table = table
      price = price
      days = daysOf table }

/// Keeps the given rows, in order.
let subset (s: Sales) (rows: int[]) =
    { table =
        { header = s.table.header
          columns = s.table.columns |> Array.map (fun c -> rows |> Array.map (fun r -> c.[r])) }
      price = rows |> Array.map (fun r -> s.price.[r])
      days = rows |> Array.map (fun r -> s.days.[r]) }

let where (pred: int -> bool) (s: Sales) =
    subset s (Array.init s.count id |> Array.filter pred)

let day (y: int) (m: int) (d: int) = Features.daysFromCivil y m d

/// Train.csv: every sale up to the end of 2011, with prices.
let loadTrain (dataDir: string) =
    let t = read (Path.Combine(dataDir, "Train.csv"))
    let price = t.column "SalePrice" |> Array.map float
    ofTable t price

/// Valid.csv joined to ValidSolution.csv: the Jan-Apr 2012 sales and their prices.
let loadValid (dataDir: string) =
    let t = read (Path.Combine(dataDir, "Valid.csv"))
    let sol = read (Path.Combine(dataDir, "ValidSolution.csv"))
    let byId = Dictionary<string, float>()
    for i in 0 .. sol.rows - 1 do
        byId.[(sol.column "SalesID").[i]] <- float (sol.column "SalePrice").[i]
    let price = t.column "SalesID" |> Array.map (fun id -> byId.[id])
    ofTable t price

// ---------------------------------------------------------------------------------------------
// Feature lists

/// Raw columns used as categories by the full model. ProductGroupDesc is left out because it
/// repeats ProductGroup. SalesID and MachineID are identifiers and are left out by default.
let categoryColumns =
    [| "ModelID"; "datasource"; "auctioneerID"; "UsageBand"; "fiModelDesc"; "fiBaseModel"
       "fiSecondaryDesc"; "fiModelSeries"; "fiModelDescriptor"; "ProductSize"; "fiProductClassDesc"
       "state"; "ProductGroup"; "Drive_System"; "Enclosure"; "Forks"; "Pad_Type"; "Ride_Control"
       "Stick"; "Transmission"; "Turbocharged"; "Blade_Extension"; "Blade_Width"; "Enclosure_Type"
       "Engine_Horsepower"; "Hydraulics"; "Pushblock"; "Ripper"; "Scarifier"; "Tip_Control"
       "Tire_Size"; "Coupler"; "Coupler_System"; "Grouser_Tracks"; "Hydraulics_Flow"; "Track_Type"
       "Undercarriage_Pad_Width"; "Stick_Length"; "Thumb"; "Pattern_Changer"; "Grouser_Type"
       "Backhoe_Mounting"; "Blade_Type"; "Travel_Controls"; "Differential_Type"; "Steering_Controls" |]

/// How category values are turned into numbers.
///   Alphabetical  codes in sorted order, like the course's OrdinalEncoder
///   ByPrice       codes in order of the category's average log price in the fitting data
///                 (shrunk towards the overall average for rare values), so a tree split on the
///                 code separates cheaper values from dearer ones
type Order =
    | Alphabetical
    | ByPrice

let orderName =
    function
    | Alphabetical -> "alphabetical"
    | ByPrice -> "by price"

/// Category levels of a column, in the chosen order, learned from `fit` only.
let levels (order: Order) (fit: Sales) (column: string) : string[] =
    let values = fit.table.column column
    let y = fit.target
    let sums = Dictionary<string, float * int>()
    for i in 0 .. values.Length - 1 do
        let v = values.[i]
        if v <> "" then
            let s, n =
                match sums.TryGetValue v with
                | true, x -> x
                | _ -> 0.0, 0
            sums.[v] <- (s + y.[i], n + 1)
    let keys = sums.Keys |> Seq.toArray
    match order with
    | Alphabetical -> keys |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    | ByPrice ->
        let mean = Array.average y
        let m = 20.0
        keys
        |> Array.sortBy (fun k ->
            let s, n = sums.[k]
            (s + m * mean) / (float n + m), k)

let private feature name kind column group levels =
    { name = name
      kind = kind
      column = column
      levels = levels
      group = group }

let private dateFeatures =
    [| feature "saleYear" "saleYear" "saledate" "Sale date" [||]
       feature "saleMonth" "saleMonth" "saledate" "Sale date" [||]
       feature "saleDay" "saleDay" "saledate" "Sale date" [||]
       feature "saleDayofweek" "saleDow" "saledate" "Sale date" [||]
       feature "saleDayofyear" "saleDoy" "saledate" "Sale date" [||] |]

type FeatureSet =
    { /// the course's columns plus date parts
      course: bool
      /// add age at sale and a continuous time trend
      engineered: bool
      /// add SalesID and MachineID, as the course does
      identifiers: bool }

let standard =
    { course = true
      engineered = true
      identifiers = false }

/// The full model's features: every useful column of the sale record.
let fullFeatures (set: FeatureSet) (order: Order) (fit: Sales) : Feature[] =
    [| yield feature "YearMade" "yearMade" "YearMade" "Year made" [||]
       yield feature "MachineHoursCurrentMeter" "hours" "MachineHoursCurrentMeter" "Hours" [||]
       yield! dateFeatures
       if set.engineered then
           yield feature "ageAtSale" "age" "YearMade" "Year made" [||]
           yield feature "saleElapsed" "saleElapsed" "saledate" "Sale date" [||]
       if set.identifiers then
           yield feature "SalesID" "num" "SalesID" "Identifiers" [||]
           yield feature "MachineID" "num" "MachineID" "Identifiers" [||]
       for c in categoryColumns do
           yield feature c "cat" c c (levels order fit c) |]

/// The demo model's features: only what a visitor can describe, grouped into the page's fields.
let demoColumns =
    [| "ProductGroup", "Product group"
       "fiBaseModel", "Model"
       "fiSecondaryDesc", "Model"
       "fiProductClassDesc", "Size class"
       "state", "State"
       "Enclosure", "Cab"
       "Blade_Type", "Options"
       "Ripper", "Options"
       "Thumb", "Options"
       "Stick", "Options"
       "Ride_Control", "Options"
       "Hydraulics_Flow", "Options" |]

let demoFeatures (order: Order) (fit: Sales) : Feature[] =
    [| yield feature "YearMade" "yearMade" "YearMade" "Year made" [||]
       yield feature "ageAtSale" "age" "YearMade" "Year made" [||]
       yield feature "MachineHoursCurrentMeter" "hours" "MachineHoursCurrentMeter" "Hours" [||]
       yield feature "saleElapsed" "saleElapsed" "saledate" "Sale date" [||]
       yield feature "saleMonth" "saleMonth" "saledate" "Sale date" [||]
       for c, group in demoColumns do
           yield feature c "cat" c group (levels order fit c) |]

/// Encodes every sale into a row-major feature matrix (NaN = missing).
let encode (features: Feature[]) (s: Sales) : float[][] =
    let enc = Features.encoder features
    let t = s.table
    let index = Dictionary<string, int>()
    t.header |> Array.iteri (fun i h -> index.[h] <- i)
    Array.Parallel.init s.count (fun r ->
        let get (column: string) =
            match index.TryGetValue column with
            | true, j -> t.columns.[j].[r]
            | _ -> null
        Features.encode enc get)
