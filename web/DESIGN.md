# Gavel demo: design notes

The page is **a machine's data plate**. Every dozer, loader and excavator that goes through an auction yard carries a riveted metal plate with its model, serial number, year and weight class stamped into it, and the yard's paperwork ends up taped or tied to it. The demo is that plate: the visitor's description is stamped into the plate's fields, the estimate is stamped into an orange enamel panel, and a manila lot tag hangs off the side with a real 2012 sale on it. The dark theme is the same plate at night in the yard, under sodium lights. The thumbnail (`.github/thumbnail.png`, source in `.github/thumbnail/`) is the plate lying at an angle on the dark yard, with an orange lot tag tied to it.

## The plate

- **Metal.** Brushed aluminium built from three layers: a light-to-dark linear gradient for the sheet, a fine repeating gradient for the brushing (1px light, 1px gap, 1px dark), and stretched fractal noise (`feTurbulence` with a base frequency of 0.004 across and 0.9 down, so the noise becomes horizontal streaks) blended in with `overlay`. The noise strength is a token (`--streak`), so the night plate can be quieter.
- **Edges.** A one-pixel light bevel on the top and left, a dark one on the bottom and right, a contact shadow and a long soft drop shadow. The plate's corners are rounded like a stamped sheet.
- **Rivets.** Four domed rivets in the corners: a radial gradient lit from the upper left, a dark rim, and a faint light ring where the head meets the sheet.
- **Engraving.** An inset border line runs around the plate 16px in. Every engraved line (the border, the field dividers, the spec-table rules, the ruler ticks) is drawn twice: a dark line, and a light line 1px below it where the lower wall of the groove catches the light.
- **Stamped text.** Text is dark enamel filled into recessed letters: enamel colour, a 1px light shadow below and a darker one above. In the dark theme the enamel is pale and the shadows swap weight.

## Signature pieces

1. **Data plate header.** `MODEL`, `SERIAL No.`, `YEAR`, `HOURS`, `WEIGHT CLASS` in a row of stamped fields with engraved dividers, filled from the form as the visitor types (blank year shows `UNK.`, blank hours `N/R`). The serial number is a page reference built from the inputs (group, year, and a hash of the description), not a real serial; it changes whenever the description does.
2. **Estimate panel.** `EST. HAMMER PRICE` and the price in a recessed panel of orange enamel. The digits are raised bare metal (pale, lit from above, with a dark lower edge). The price is shown to the nearest $100, since hammer prices are round numbers. When it changes, the digits re-stamp: a 0.26s press.
3. **Range ruler.** The range under the price is drawn as a tape-measure scale: a recessed strip with graduations (minor, half and major ticks, the majors labelled `$40k`, `$60k`...), the range as an orange band across it, and a black pointer at the estimate. The SVG is drawn at the column's real width, so the type stays at its real size on every screen.
4. **What moved the price.** A ruler-style tornado chart. Each field (year made, size class, model, cab...) gets a recessed track; orange bars to the right raise the price, enamel bars to the left lower it, and the percentages are printed at the end. The scale along the top is logarithmic (the ticks read `−50% −25% +25% +50% +100%`), like a slide rule, because the effects multiply: halving and doubling sit the same distance from the centre. A screen-reader list carries the same numbers.
5. **Lot tag.** A manila tag with a reinforced punched hole, hung on a string from a rivet on the plate's edge, so it overhangs the plate like real paperwork. The lot number and the real hammer price are handwritten in pencil (Nothing You Could Do); the estimate and the verdict are typed. Its button, "Pull a real 2012 sale", is a strip of orange label tape. The tag says plainly that these are held-out validation sales from January to April 2012.
6. **Spec plate.** The results are a second riveted plate, `SPECIFICATIONS`, with a stamped spec table (held-out error for every model and baseline, then by product group) and three short notes. Every number on it is filled in at build time from `results/*.json`.

## DYMO label tape

Form labels and section labels are embossed label-maker tape: black plastic with raised whitish letters (Red Hat Mono Bold, wide tracking), a sheen gradient, slightly lighter trimmed ends, and a small rotation. The page script presses each label by hand: every letter sits up to 0.7px above or below the line and each strip gets its own tilt of up to about a degree, both derived from a hash of the text so they don't jump on reload. Screen readers get the plain text; the pressed letters are hidden from them. Orange tape is used only for the buttons (the lot tag's and the floating estimate strip).

## Type

- **Big Shoulders** (Bold) for the wordmark, the plate fields, the group codes and the price: a condensed industrial face that reads like stamped plate lettering.
- **Red Hat Mono** for values, labels and body text: the serials, numbers and spec values line up.
- **Nothing You Could Do** for the handwriting on the lot tag.
- All three are self-hosted from `docs/fonts/` under the SIL Open Font License (licence files next to the fonts).

## Colour

| Token | Day (aluminium) | Night (gunmetal) |
|---|---|---|
| Plate | `#e6e9ec` to `#d3d7db` | `#262a2f` to `#1c1f23` |
| Enamel text | `#15181b` (12.3 to 14.6:1) | `#dfe3e7` (11.2 to 12.8:1) |
| Secondary text | `#3a4047` (7.2:1) | `#a9b0b8` (6.6:1) |
| Accent fill (bars, band) | safety orange `#e8590c` | brighter `#ff7a2e` |
| Accent text | darkened `#a03806` (4.7 to 5.6:1) | `#ff8b47` (6.2 to 7.1:1) |
| Price panel | `#bf3f0b` with `#fff5ee` digits (5.0:1) | `#c2470d` with `#fff6f0` (4.7:1) |
| Label tape | `#f3f3ef` on `#161616` (16:1); white on orange `#b5420a` (5.6:1) | same tape |
| Lot tag | `#2d2416` print and `#1d2a57` pencil on manila (6.5:1 and up) | darker manila, 5.0:1 and up |
| Yard behind the plate | painted steel `#bdb7ac` with grit | `#0c0e0f` with a warm sodium-light falloff from the upper left |

Safety orange is the only accent, and it always means the same thing: the estimate, its range, and whatever raises the price. Things that lower it are enamel. The `+` and `−` signs carry the direction as well, so it never rests on colour alone. The night theme is not black and yellow: it is dark anodised metal with pale engraving and a warm vignette.

## Layout

- At full width the plate is one object: header, data plate fields, then three columns (form, readout, lot tag) with the tag overhanging the right edge.
- Below 1180px the tag drops under the readout and turns sideways, hole on the left. Below 900px everything is one column, the data-plate fields wrap to three and then two per row, and a strip of orange label tape with the current estimate floats at the bottom of the screen while the estimate panel is out of view.
- At 375px there is no sideways scroll; rivets and the engraved border move closer to the edge.

## Access

- **Focus:** a 3px safety-orange outline on every control, including the product-group keys and the theme switch.
- **Theme:** follows `prefers-color-scheme`; the DAY/NIGHT switch overrides it and is remembered when `localStorage` is available.
- **Motion:** the only motion is the price re-stamp and the switch knob; `prefers-reduced-motion` turns both off. The page is complete at rest.
- **Charts:** the ruler has a text title, the tornado has a hidden list with the same effects, and the spec tables are real tables.
