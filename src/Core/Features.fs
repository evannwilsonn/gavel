namespace Gavel

open System.Collections.Generic

/// How one model input is computed from a raw sale record. Saved with the model, so training and
/// the browser compute features with the same code.
///   kind    "cat"         a category column; its code is the index of the value in `levels`
///                         (unseen or blank values are missing)
///           "num"         a number column (blank is missing)
///           "yearMade"    YearMade, with the dataset's placeholder 1000 treated as missing
///           "hours"       MachineHoursCurrentMeter, with 0 and blank treated as missing
///           "saleYear" "saleMonth" "saleDay" "saleDow" "saleDoy"   parts of saledate
///           "saleElapsed" years since 1 Jan 1989, with fractions (a time trend)
///           "age"         sale year minus YearMade
///   column  the raw column it reads
///   group   the visitor-facing field it belongs to, used to sum up explanations
type Feature =
    { name: string
      kind: string
      column: string
      levels: string[]
      group: string }

/// Calendar parts of a sale date.
type SaleDate =
    { year: int
      month: int
      day: int
      dayOfWeek: int // Monday = 0, as in pandas
      dayOfYear: int
      days: int } // days since 1 Jan 1970

module Features =

    /// Days since 1 Jan 1970 for a proleptic Gregorian date (Howard Hinnant's days_from_civil).
    let daysFromCivil (y: int) (m: int) (d: int) =
        let y = if m <= 2 then y - 1 else y
        let era = (if y >= 0 then y else y - 399) / 400
        let yoe = y - era * 400
        let mp = (m + 9) % 12
        let doy = (153 * mp + 2) / 5 + d - 1
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        era * 146097 + doe - 719468

    let saleDate (y: int) (m: int) (d: int) =
        let days = daysFromCivil y m d
        { year = y
          month = m
          day = d
          dayOfWeek = ((days + 3) % 7 + 7) % 7
          dayOfYear = days - daysFromCivil y 1 1 + 1
          days = days }

    /// Parses a plain decimal number ("68", "-3", "23.5"). Anything else gives NaN.
    let parseNumber (s: string) : float =
        if isNull s then nan
        else
            let s = s.Trim()
            let n = s.Length
            let mutable i = 0
            let mutable neg = false
            if n > 0 && s.[0] = '-' then
                neg <- true
                i <- 1
            let mutable whole = 0.0
            let mutable frac = 0.0
            let mutable scale = 1.0
            let mutable digits = 0
            let mutable ok = i < n
            let mutable dot = false
            while ok && i < n do
                let c = s.[i]
                if c >= '0' && c <= '9' then
                    let v = float (int c - int '0')
                    if dot then
                        scale <- scale / 10.0
                        frac <- frac + v * scale
                    else whole <- whole * 10.0 + v
                    digits <- digits + 1
                elif c = '.' && not dot then dot <- true
                else ok <- false
                i <- i + 1
            if not ok || digits = 0 then nan
            else (if neg then -1.0 else 1.0) * (whole + frac)

    /// Parses the dataset's date format, "11/16/2006 0:00" (month/day/year, time ignored).
    let parseSaleDate (s: string) : SaleDate option =
        if isNull s then None
        else
            let datePart = (s.Trim().Split(' ')).[0]
            let parts = datePart.Split('/')
            if parts.Length <> 3 then None
            else
                let m = parseNumber parts.[0]
                let d = parseNumber parts.[1]
                let y = parseNumber parts.[2]
                if System.Double.IsNaN m || System.Double.IsNaN d || System.Double.IsNaN y then None
                else Some(saleDate (int y) (int m) (int d))

    let private epoch1989 = daysFromCivil 1989 1 1

    let yearMade (s: string) =
        let v = parseNumber s
        if v < 1900.0 then nan else v

    let hours (s: string) =
        let v = parseNumber s
        if v <= 0.0 then nan else v

    /// A feature list ready to encode rows, with a lookup table per category feature.
    type Encoder =
        { features: Feature[]
          lookups: Dictionary<string, float>[] }

    let encoder (features: Feature[]) : Encoder =
        let lookups =
            features
            |> Array.map (fun f ->
                let d = Dictionary<string, float>()
                if f.kind = "cat" then
                    f.levels |> Array.iteri (fun i level -> d.[level] <- float i)
                d)
        { features = features; lookups = lookups }

    /// Computes the feature vector for one sale. `get column` returns the raw text of a column
    /// (null or "" when it is blank).
    let encodeInto (enc: Encoder) (get: string -> string) (out: float[]) =
        let mutable date: SaleDate option = None
        let mutable dateRead = false
        let sale () =
            if not dateRead then
                date <- parseSaleDate (get "saledate")
                dateRead <- true
            date
        let datePart (pick: SaleDate -> float) =
            match sale () with
            | Some d -> pick d
            | None -> nan
        for i in 0 .. enc.features.Length - 1 do
            let f = enc.features.[i]
            out.[i] <-
                match f.kind with
                | "cat" ->
                    let s = get f.column
                    if isNull s || s = "" then nan
                    else
                        match enc.lookups.[i].TryGetValue s with
                        | true, code -> code
                        | _ -> nan
                | "num" -> parseNumber (get f.column)
                | "yearMade" -> yearMade (get f.column)
                | "hours" -> hours (get f.column)
                | "saleYear" -> datePart (fun d -> float d.year)
                | "saleMonth" -> datePart (fun d -> float d.month)
                | "saleDay" -> datePart (fun d -> float d.day)
                | "saleDow" -> datePart (fun d -> float d.dayOfWeek)
                | "saleDoy" -> datePart (fun d -> float d.dayOfYear)
                | "saleElapsed" -> datePart (fun d -> float (d.days - epoch1989) / 365.25)
                | "age" ->
                    let ym = yearMade (get f.column)
                    datePart (fun d -> float d.year - ym)
                | other -> failwithf "unknown feature kind %s" other

    let encode (enc: Encoder) (get: string -> string) =
        let out = Array.zeroCreate<float> enc.features.Length
        encodeInto enc get out
        out
