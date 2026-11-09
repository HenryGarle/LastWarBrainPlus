// node web/src/explorer/hive.check.mjs - fails loudly if the hive maths or the planner export break.
import assert from "node:assert/strict";
import { hiveOf, scoreHive, plannerExport, territory } from "./hive.js";

const grid = (skip = []) => {
  const out = [];
  for (let i = 0; i < 8; i++) for (let j = 0; j < 8; j++)
    if (!skip.some(([a, b]) => a === i && b === j)) out.push({ x: 500 + i * 4, y: 300 + j * 4, name: `p${i}${j}` });
  return out;
};

const perfect = scoreHive(grid());
assert.ok(perfect.score >= 95, `perfect grid ${perfect.score}`);
assert.equal(perfect.pitch, 4);
assert.equal(perfect.holes.length, 0);

const gapped = scoreHive(grid([[4, 4]]));
assert.ok(gapped.gap, "centre gap is recognised as deliberate");
assert.equal(gapped.holes.length, 0);
assert.ok(gapped.score >= 93, `centre gap ${gapped.score}`);

const holed = scoreHive(grid([[4, 4], [1, 6]]));
assert.equal(holed.holes.length, 1, "an off-centre gap is a hole");
assert.ok(holed.score < gapped.score);

let seed = 7; const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
const scatter = Array.from({ length: 60 }, () => ({ x: 500 + Math.round(rnd() * 40), y: 300 + Math.round(rnd() * 40) }));
const messy = scoreHive(hiveOf(scatter));
assert.ok(messy.score < 70, `scatter ${messy.score}`);

const far = [...grid(), { x: 900, y: 900 }];
assert.equal(hiveOf(far).length, 64, "a distant base is not in the hive");

// Planner: element centre (planner formula) lands back on the game coordinate.
const ex = plannerExport(grid(), [{ x: 514, y: 314, type: 59, level: 3, tag: "ABC" }]);
const { originX, originY, gridWidth } = ex.gridSettings;
for (const e of ex.placedElements) {
  const cx = originX - gridWidth + e.x + Math.floor((e.width - 1) / 2);
  const cy = originY - (e.y + Math.floor((e.height - 1) / 2)) - 1;
  const src = e.elementId === "base" ? grid().find((b) => b.name === e.label) : { x: 514, y: 314 };
  assert.deepEqual([cx, cy], [src.x, src.y], e.label);
}
assert.equal(ex.placedElements.length, 65);

const t = territory([{ x: 49, y: 49, type: 11, allianceId: "A" }, { x: 149, y: 49, type: 11, allianceId: "" }, { x: 300, y: 300, type: 15, buildId: 300001, allianceId: "B" }]);
assert.equal(t.owner[Math.floor(49 / 5) * t.n + Math.floor(49 / 5)], "A");
assert.equal(t.owner[Math.floor(49 / 5) * t.n + Math.floor(149 / 5)], null);
assert.equal(t.owner[Math.floor(310 / 5) * t.n + Math.floor(290 / 5)], "B");
console.log("hive checks passed", { perfect: perfect.score, gapped: gapped.score, holed: holed.score, messy: messy.score });
// Season layouts: zone counts match the game (S2: 13x13 cells; S3: 313 areas, as Cpt Hedgehog's map shows).
import { layout, detectSeason } from "./seasons.js";
const s2 = layout("2"), s3 = layout("3");
assert.equal(s2.zones.length, 169); assert.ok(s2.zones.every((z) => !z.corner));
assert.equal(s3.zones.length, 313); assert.equal(s3.zones.filter((z) => z.corner).length, 144);
const pyramid = s3.zones.find((z) => z.building.type === 3); assert.deepEqual([pyramid.x0, pyramid.x1, pyramid.y0, pyramid.y1], [450, 550, 450, 550]);
const b = s3.buildings.find((x) => x.type === 5);
assert.equal(detectSeason([{ type: 35, buildId: b.id, x: b.x, y: b.y }]), "3");
assert.equal(detectSeason([{ type: 11, buildId: 34, x: 49, y: 49 }]), null);
console.log("season layout checks passed");
