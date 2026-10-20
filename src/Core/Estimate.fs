namespace Gavel

/// The spread of the model's errors (log price) on sales it had not seen, for one product group:
/// the true price fell between estimate * exp(lo) and estimate * exp(hi) for `level` of them.
type Band =
    { key: string
      lo: float
      hi: float }

/// Everything the demo needs, as saved in docs/model.json.
type Deployed =
    { features: Feature[]
      model: Ensemble
      level: float
      bands: Band[] }

/// How much one visitor-facing field moved the estimate, as a log-price change.
/// exp(effect) - 1 is the change as a fraction of the price.
type Push = { field: string; effect: float }

type Estimate =
    { price: float
      low: float
      high: float
      /// The price before any field is taken into account (the training average, in log space).
      start: float
      pushes: Push[] }

module Estimate =

    let private expm1 (x: float) = exp x - 1.0

    type Ready =
        { deployed: Deployed
          encoder: Features.Encoder }

    let ready (d: Deployed) =
        { deployed = d
          encoder = Features.encoder d.features }

    let band (d: Deployed) (productGroup: string) =
        match d.bands |> Array.tryFind (fun b -> b.key = productGroup) with
        | Some b -> b
        | None -> d.bands |> Array.find (fun b -> b.key = "all")

    /// Log-price prediction for raw fields given by `get column`.
    let predictLog (r: Ready) (get: string -> string) =
        Ensemble.predict r.deployed.model (Features.encode r.encoder get)

    let estimate (r: Ready) (get: string -> string) : Estimate =
        let x = Features.encode r.encoder get
        let start, contributions = Ensemble.explain r.deployed.model x.Length x
        let fields = r.deployed.features |> Array.map (fun f -> f.group) |> Array.distinct
        let pushes =
            fields
            |> Array.map (fun field ->
                let mutable s = 0.0
                for i in 0 .. x.Length - 1 do
                    if r.deployed.features.[i].group = field then s <- s + contributions.[i]
                { field = field; effect = s })
            |> Array.sortByDescending (fun p -> abs p.effect)
        let logPrice = start + (contributions |> Array.sum)
        let b = band r.deployed (get "ProductGroup")
        { price = expm1 logPrice
          low = expm1 (logPrice + b.lo)
          high = expm1 (logPrice + b.hi)
          start = expm1 start
          pushes = pushes }
