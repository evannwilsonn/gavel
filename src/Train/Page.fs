/// Writes docs/index.html from web/template.html, filling in numbers from results/*.json, so the
/// page can't drift from the experiments.
module Gavel.Page

open System
open System.IO
open System.Net
open Gavel.Experiments
open Gavel.Demo

let private f3 (x: float) = x.ToString("0.000")
let private pct0 (x: float) = (100.0 * x).ToString("0") + "%"
let private thousands (n: int) = n.ToString("N0", Globalization.CultureInfo.InvariantCulture)
let private esc (s: string) = WebUtility.HtmlEncode s

let groupNames =
    dict
        [ "BL", "Backhoe loaders"; "MG", "Motor graders"; "SSL", "Skid steers"; "TEX", "Track excavators"
          "TTT", "Dozers"; "WL", "Wheel loaders" ]

let private row (label: string) (sub: string) (value: string) (cls: string) =
    let subHtml = if sub = "" then "" else sprintf "<span class=\"sub\">%s</span>" (esc sub)
    sprintf "          <tr><th scope=\"row\">%s%s</th><td class=\"%s\">%s</td></tr>" (esc label) subHtml cls (esc value)

let tokens (root: string) =
    let results = Path.Combine(root, "results")
    let docs = Path.Combine(root, "docs")
    let h: Holdout = Json.read (Path.Combine(results, "holdout.json"))
    let e: Errors = Json.read (Path.Combine(results, "errors.json"))
    let d: DemoReport = Json.read (Path.Combine(results, "demo.json"))
    let t: Tuning = Json.read (Path.Combine(results, "tuning.json"))
    let byName (prefix: string) (xs: Entry[]) = xs |> Array.find (fun x -> x.name.StartsWith prefix)
    let gbm = byName "Boosting" h.models
    let forest = byName "Random forest" h.models
    let lgbm = byName "ML.NET LightGBM" h.mlnet
    let groupYear = byName "Median by product group and year" h.baselines
    let modelYear = byName "Median by base model" h.baselines
    let finalLabel =
        if h.settings.blendWeight >= 1.0 then "Gavel: boosted trees, written in F#"
        elif h.settings.blendWeight <= 0.0 then "Gavel: random forest, written in F#"
        else "Gavel: boosted trees + forest, in F#"
    let specRows =
        [ row finalLabel "the model in the README" (f3 h.final.score.rmsle) "big hot"
          row "Boosted trees alone (F#)" (sprintf "%d trees, %d leaves" gbm.trees h.settings.gbm.leaves) (f3 gbm.score.rmsle) ""
          row "Random forest alone (F#)" (sprintf "%d trees" h.settings.forest.trees) (f3 forest.score.rmsle) ""
          row "ML.NET LightGBM, same settings" "the library check" (f3 lgbm.score.rmsle) ""
          row "Browser model, this page" (sprintf "%d trees, %d features from the form" d.trees d.features.Length) (f3 d.heldOut.rmsle) ""
          row "Course notebook's forest" "tuned on these same sales" (f3 h.course.validRmsle) ""
          row "Median by base model and year" "baseline" (f3 modelYear.score.rmsle) ""
          row "Median by group and year made" "baseline" (f3 groupYear.score.rmsle) ""
          row (sprintf "Prices inside the %s range" (pct0 d.level)) "browser model, held-out sales" (pct0 d.coverage) ""
          row "Median miss, final model" "half the estimates are closer than this" (sprintf "%.0f%%" h.final.score.medianAbsPctError) "" ]
        |> String.concat "\n"
    let groupRows =
        e.byProductGroup
        |> Array.map (fun g ->
            let bias = 100.0 * (exp g.bias - 1.0)
            sprintf
                "          <tr><th scope=\"row\">%s</th><td>%s</td><td>%s</td><td>%s%.1f%%</td></tr>"
                (esc (match groupNames.TryGetValue g.key with | true, n -> n | _ -> g.key))
                (thousands g.n)
                (f3 g.rmsle)
                (if bias >= 0.0 then "+" else "&minus;")
                (abs bias))
        |> String.concat "\n"
    let never = e.byModelHistory |> Array.tryFind (fun g -> g.key.StartsWith "model never")
    let many = e.byModelHistory |> Array.tryFind (fun g -> g.key.StartsWith "100+")
    let unknownYear = e.byYearMade |> Array.tryFind (fun g -> g.key = "year made unknown")
    let monthBias = e.bySaleMonth |> Array.map (fun g -> 100.0 * (exp g.bias - 1.0))
    let enc = t.encoding
    let alpha = enc |> Array.find (fun x -> x.name.Contains "alphabetical")
    let byPrice = enc |> Array.find (fun x -> x.name.EndsWith "by price")
    let notes =
        [ "What drove errors",
          sprintf
              "Machines whose exact model had never sold before (%d sales) scored %s; models with 100+ earlier sales, %s. Unknown year made: %s. Across Jan to Apr 2012 the estimates ran %.0f%% to %.0f%% %s on average: prices moved after the training data ended and trees can't extrapolate a trend."
              (never |> Option.map (fun g -> g.n) |> Option.defaultValue 0)
              (never |> Option.map (fun g -> f3 g.rmsle) |> Option.defaultValue "-")
              (many |> Option.map (fun g -> f3 g.rmsle) |> Option.defaultValue "-")
              (unknownYear |> Option.map (fun g -> f3 g.rmsle) |> Option.defaultValue "-")
              (Array.min (monthBias |> Array.map abs))
              (Array.max (monthBias |> Array.map abs))
              (if Array.average monthBias < 0.0 then "low" else "high")
          "Against the course",
          sprintf
              "The course's random forest reaches %s on the same 2012 sales, but its settings were picked by scoring them on those sales. Gavel picked everything on late-2011 sales first and opened 2012 once: %s."
              (f3 h.course.validRmsle)
              (f3 h.final.score.rmsle)
          "What didn't work",
          sprintf
              "Ordering category codes by average price, a standard trick, did worse than plain alphabetical codes (%s against %s on the tuning window): the codes leak each category's own prices into training."
              (f3 byPrice.score.rmsle)
              (f3 alpha.score.rmsle) ]
        |> List.map (fun (k, v) -> sprintf "      <div><span class=\"dymo small\" data-dymo>%s</span><p>%s</p></div>" (esc k) (esc v))
        |> String.concat "\n"
    let json (file: string) = File.ReadAllText(Path.Combine(docs, file)).Replace("</", "<\\/")
    dict
        [ "final_rmsle", f3 h.final.score.rmsle
          "heldout_sales", thousands h.heldOutSales
          "train_sales", thousands h.fitSales
          "level_pct", pct0 d.level
          "demo_trees", string d.trees
          "demo_nodes", thousands d.nodes
          "model_kb", string (d.modelBytes / 1024L)
          "spec_rows", specRows
          "group_rows", groupRows
          "spec_notes", notes
          "catalog_json", json "catalog.json"
          "examples_json", json "examples.json" ]

let write (root: string) =
    let mutable html = File.ReadAllText(Path.Combine(root, "web", "template.html"))
    for kv in tokens root do
        html <- html.Replace("{{" + kv.Key + "}}", kv.Value)
    if html.Contains "{{" then failwithf "unfilled token near %s" (html.Substring(html.IndexOf "{{", 40))
    let out = Path.Combine(root, "docs", "index.html")
    File.WriteAllText(out, html)
    printfn "  wrote %s" out
