/// Grows one regression tree on binned data. Both learners use it: gradient boosting fits it to
/// residuals, the random forest to the target itself.
///
/// A node's value is sum(y) / (count + lambda) and a split's gain is the drop in squared error:
///   gain = SL^2/(nL + lambda) + SR^2/(nR + lambda) - S^2/(n + lambda)
/// With lambda = 0 that is exactly the variance reduction a CART regression tree maximises.
/// Leaves are split best-gain-first until `maxLeaves` is reached or no split helps.
module Gavel.TreeBuilder

open System
open System.Collections.Generic
open System.Threading.Tasks
open Gavel.Binning

type Params =
    { maxLeaves: int
      maxDepth: int
      /// smallest number of (sampled) rows allowed in a leaf
      minLeaf: int
      lambda: float
      /// fraction of the tree's features tried at each node (random forest); 1.0 tries all
      featureFraction: float
      /// every node value is multiplied by this (the learning rate, for boosting)
      shrink: float }

/// A tree with its split points both as raw values (for Core's Tree) and as bins (fast to apply
/// to the binned training data).
type Built = { tree: Tree; binThreshold: int[] }

type private Split =
    { gain: float
      feature: int
      bin: int
      leftCount: int }

let private noSplit = { gain = 0.0; feature = -1; bin = -1; leftCount = 0 }

type private Leaf =
    { node: int
      start: int
      stop: int
      sum: float
      depth: int
      split: Split }

/// Best split of rows idx.[start..stop-1] on feature f.
let private bestOnFeature
    (p: Params)
    (data: Binned)
    (y: float[])
    (idx: int[])
    (start: int)
    (stop: int)
    (total: float)
    (parentScore: float)
    (hs: float[])
    (hc: int[])
    (f: int)
    =
    let n = stop - start
    let nb = data.nBins.[f]
    let col = data.bins.[f]
    let lam = p.lambda
    let best = ref noSplit
    let consider (sl: float) (cl: int) (bin: int) =
        let cr = n - cl
        if cl >= p.minLeaf && cr >= p.minLeaf then
            let sr = total - sl
            let g = sl * sl / (float cl + lam) + sr * sr / (float cr + lam) - parentScore
            if g > best.Value.gain then
                best.Value <- { gain = g; feature = f; bin = bin; leftCount = cl }
    if nb <= 8 * n then
        // dense histogram
        Array.Clear(hs, 0, nb)
        Array.Clear(hc, 0, nb)
        for k in start .. stop - 1 do
            let row = idx.[k]
            let b = int col.[row]
            hs.[b] <- hs.[b] + y.[row]
            hc.[b] <- hc.[b] + 1
        let mutable sl = 0.0
        let mutable cl = 0
        for b in 0 .. nb - 2 do
            if hc.[b] > 0 then
                sl <- sl + hs.[b]
                cl <- cl + hc.[b]
                consider sl cl b
    else
        // few rows, many bins: sort the rows' bins instead
        let keys = Array.init n (fun k -> int col.[idx.[start + k]])
        let vals = Array.init n (fun k -> y.[idx.[start + k]])
        Array.Sort(keys, vals)
        let mutable sl = 0.0
        let mutable k = 0
        while k < n do
            let b = keys.[k]
            while k < n && keys.[k] = b do
                sl <- sl + vals.[k]
                k <- k + 1
            if k < n then consider sl k b
    best.Value

/// Grows a tree on `rows` (indices into the data; duplicates allowed, as in a bootstrap sample),
/// using only `features`, fitting targets y (indexed by row).
let build (p: Params) (data: Binned) (features: int[]) (y: float[]) (rows: int[]) (rng: Random) : Built =
    let idx = Array.copy rows
    let tmp = Array.zeroCreate<int> idx.Length
    let maxNb = data.nBins |> Array.max
    let fs = ResizeArray<int>()
    let ts = ResizeArray<float>()
    let ls = ResizeArray<int>()
    let rs = ResizeArray<int>()
    let vs = ResizeArray<float>()
    let bts = ResizeArray<int>()
    let addNode (value: float) =
        fs.Add -1
        ts.Add 0.0
        ls.Add -1
        rs.Add -1
        vs.Add value
        bts.Add -1
        fs.Count - 1
    let nPick =
        if p.featureFraction >= 1.0 then features.Length
        else max 1 (int (Math.Round(p.featureFraction * float features.Length)))
    let pool = Array.copy features
    let seqHs = Array.zeroCreate<float> maxNb
    let seqHc = Array.zeroCreate<int> maxNb
    let pickFeatures () =
        if nPick = features.Length then features
        else
            for i in 0 .. nPick - 1 do
                let j = i + rng.Next(pool.Length - i)
                let t = pool.[i]
                pool.[i] <- pool.[j]
                pool.[j] <- t
            Array.sub pool 0 nPick
    let evaluate (start: int) (stop: int) (sum: float) (depth: int) =
        let n = stop - start
        if depth >= p.maxDepth || n < 2 * p.minLeaf then noSplit
        else
            let cand = pickFeatures ()
            let parentScore = sum * sum / (float n + p.lambda)
            if n * cand.Length < 200_000 then
                let hs = seqHs
                let hc = seqHc
                let mutable best = noSplit
                for f in cand do
                    let s = bestOnFeature p data y idx start stop sum parentScore hs hc f
                    if s.gain > best.gain then best <- s
                best
            else
                let results = Array.create cand.Length noSplit
                Parallel.For(
                    0,
                    cand.Length,
                    (fun () -> Array.zeroCreate<float> maxNb, Array.zeroCreate<int> maxNb),
                    (fun j _ (hs, hc) ->
                        results.[j] <- bestOnFeature p data y idx start stop sum parentScore hs hc cand.[j]
                        (hs, hc)),
                    (fun _ -> ())
                )
                |> ignore
                // ties go to the earlier feature, so results don't depend on thread timing
                results |> Array.fold (fun b s -> if s.gain > b.gain then s else b) noSplit
    let value (sum: float) (n: int) = p.shrink * sum / (float n + p.lambda)
    let mutable total = 0.0
    for r in idx do
        total <- total + y.[r]
    let rootNode = addNode (value total idx.Length)
    let queue = PriorityQueue<Leaf, struct (float * int)>()
    let push (leaf: Leaf) =
        if leaf.split.feature >= 0 then queue.Enqueue(leaf, struct (-leaf.split.gain, leaf.node))
    push
        { node = rootNode
          start = 0
          stop = idx.Length
          sum = total
          depth = 0
          split = evaluate 0 idx.Length total 0 }
    let mutable leaves = 1
    while leaves < p.maxLeaves && queue.Count > 0 do
        let leaf = queue.Dequeue()
        let s = leaf.split
        let col = data.bins.[s.feature]
        // partition the leaf's rows: bin <= s.bin to the left
        let mutable a = leaf.start
        let mutable b = leaf.stop - 1
        let mutable sumLeft = 0.0
        for k in leaf.start .. leaf.stop - 1 do
            let row = idx.[k]
            if int col.[row] <= s.bin then
                tmp.[a] <- row
                a <- a + 1
                sumLeft <- sumLeft + y.[row]
            else
                tmp.[b] <- row
                b <- b - 1
        Array.blit tmp leaf.start idx leaf.start (leaf.stop - leaf.start)
        let mid = a
        let sumRight = leaf.sum - sumLeft
        let left = addNode (value sumLeft (mid - leaf.start))
        let right = addNode (value sumRight (leaf.stop - mid))
        fs.[leaf.node] <- s.feature
        ts.[leaf.node] <- threshold data.cuts.[s.feature] s.bin
        bts.[leaf.node] <- s.bin
        ls.[leaf.node] <- left
        rs.[leaf.node] <- right
        leaves <- leaves + 1
        push
            { node = left
              start = leaf.start
              stop = mid
              sum = sumLeft
              depth = leaf.depth + 1
              split = evaluate leaf.start mid sumLeft (leaf.depth + 1) }
        push
            { node = right
              start = mid
              stop = leaf.stop
              sum = sumRight
              depth = leaf.depth + 1
              split = evaluate mid leaf.stop sumRight (leaf.depth + 1) }
    { tree =
        { f = fs.ToArray()
          t = ts.ToArray()
          l = ls.ToArray()
          r = rs.ToArray()
          v = vs.ToArray() }
      binThreshold = bts.ToArray() }

/// Applies a tree to row `row` of the binned data it was trained on.
let predictBinned (b: Built) (data: Binned) (row: int) =
    let t = b.tree
    let mutable i = 0
    while t.f.[i] >= 0 do
        i <- if int data.bins.[t.f.[i]].[row] <= b.binThreshold.[i] then t.l.[i] else t.r.[i]
    t.v.[i]
