// Runs the Fable (JavaScript) build of Gavel.Core in Node on the machines in results/parity.json
// and checks it gives the same log prices as the .NET build. Run after scripts/build-js.
import { readFileSync } from "node:fs";
import { load, predictLogs, estimate } from "../fable-out/Exports.js";

const root = new URL("..", import.meta.url);
const model = JSON.parse(readFileSync(new URL("docs/model.json", root), "utf8"));
const parity = JSON.parse(readFileSync(new URL("results/parity.json", root), "utf8"));
const ready = load(model);
const js = predictLogs(ready, parity.machines);
let worst = 0;
js.forEach((v, i) => { worst = Math.max(worst, Math.abs(v - parity.logPrices[i])); });
// explanations must add up to the estimate in JavaScript too
let gap = 0;
for (const m of parity.machines) {
  const e = estimate(ready, m);
  const sum = e.pushes.reduce((s, p) => s + p.effect, Math.log(1 + e.start));
  gap = Math.max(gap, Math.abs(sum - Math.log(1 + e.price)));
}
console.log(`${js.length} machines: largest JS/.NET difference ${worst.toExponential(2)}, largest explanation gap ${gap.toExponential(2)}`);
if (!(worst < 1e-9 && gap < 1e-9)) { console.error("JavaScript build disagrees with .NET"); process.exit(1); }
