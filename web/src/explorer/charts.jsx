import { useCallback, useMemo, useRef, useState } from "react";

/**
 * The pixel width a chart actually has. Charts draw at 1:1 - viewBox = real size - so text stays
 * 11-12px and heights stay put on any screen. (They used to draw at a fixed 640 wide and scale
 * with CSS, which made everything - text, bars, row heights - balloon at full-screen widths.)
 */
function useWidth(fallback = 640) {
  const [w, setW] = useState(fallback);
  const ro = useRef(null);
  // A callback ref, so the observer follows the element even when the chart swaps its
  // "no data" placeholder for the real thing.
  const ref = useCallback((el) => {
    ro.current?.disconnect();
    if (!el) return;
    const measure = () => { const x = Math.round(el.getBoundingClientRect().width); if (x > 0) setW(x); };
    measure();
    ro.current = new ResizeObserver(measure);
    ro.current.observe(el);
  }, []);
  return [ref, Math.max(260, w)];
}
import { big, when, href } from "./format.js";

// Clean round ticks: 3 or 4 values across the range, never odd numbers.
function ticks(lo, hi, want = 4, whole = false) {
  if (lo === hi) { lo = lo * 0.95; hi = hi * 1.05 || 1; }
  const raw = (hi - lo) / want;
  const mag = Math.pow(10, Math.floor(Math.log10(raw)));
  // Counts get whole-number steps; a 2.5 step on a head count reads as nonsense.
  let step = (whole ? [1, 2, 5, 10] : [1, 2, 2.5, 5, 10]).map((m) => m * mag).find((s) => s >= raw) || mag * 10;
  if (whole) step = Math.max(1, Math.round(step));
  const out = [];
  // Run until a tick sits AT or ABOVE the max - stopping at the last one below it put the
  // highest points outside the plot.
  for (let v = Math.floor(lo / step) * step; ; v += step) { out.push(v); if (v >= hi - step * 0.001) break; }
  return out;
}

/**
 * One series over time. Few points is the normal case here (a snapshot a day), so markers are
 * always drawn and every point is hoverable. One axis only - a second measure gets its own chart.
 */
export function LineChart({ points, label, height = 180, format = big }) {
  const [hover, setHover] = useState(null);
  const box = useRef(null);
  const [wrap, W] = useWidth();
  const H = height, L = 56, R = 16, T = 12, B = 26;

  const data = useMemo(
    () => points.filter((p) => p.y !== null && p.y !== undefined)
      .map((p) => ({ t: new Date(p.x).getTime(), y: Number(p.y), x: p.x })),
    [points]
  );
  if (data.length === 0) return <p className="muted small" ref={wrap}>No data yet.</p>;

  const t0 = Math.min(...data.map((d) => d.t)), t1 = Math.max(...data.map((d) => d.t));
  const whole = data.every((d) => Number.isInteger(d.y));
  const ys = ticks(Math.min(...data.map((d) => d.y)), Math.max(...data.map((d) => d.y)), 4, whole);
  const y0 = ys[0], y1 = ys[ys.length - 1];
  const sx = (t) => (t1 === t0 ? (L + W - R) / 2 : L + ((t - t0) / (t1 - t0)) * (W - L - R));
  const sy = (y) => T + (1 - (y - y0) / (y1 - y0 || 1)) * (H - T - B);
  const path = data.map((d, i) => `${i ? "L" : "M"}${sx(d.t).toFixed(1)},${sy(d.y).toFixed(1)}`).join("");

  const move = (e) => {
    const r = box.current.getBoundingClientRect();
    const px = ((e.clientX - r.left) / r.width) * W;
    let best = 0;
    data.forEach((d, i) => { if (Math.abs(sx(d.t) - px) < Math.abs(sx(data[best].t) - px)) best = i; });
    setHover(best);
  };

  const h = hover !== null ? data[hover] : null;
  return (
    <div className="chart" ref={wrap} onMouseLeave={() => setHover(null)}>
      <svg ref={box} viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img" aria-label={label} onMouseMove={move}>
        {ys.map((v) => (
          <g key={v}>
            <line x1={L} x2={W - R} y1={sy(v)} y2={sy(v)} className="grid" />
            <text x={L - 8} y={sy(v) + 4} textAnchor="end" className="axis">{format(v)}</text>
          </g>
        ))}
        <text x={L} y={H - 6} className="axis">{when(data[0].x)}</text>
        {data.length > 1 && <text x={W - R} y={H - 6} textAnchor="end" className="axis">{when(data[data.length - 1].x)}</text>}
        {h && <line x1={sx(h.t)} x2={sx(h.t)} y1={T} y2={H - B} className="crosshair" />}
        <path d={path} className="series-line" />
        {data.map((d, i) => (
          <circle key={i} cx={sx(d.t)} cy={sy(d.y)} r={hover === i ? 5.5 : 4} className="series-dot" />
        ))}
        {/* The latest value, labelled at the end - the one number worth reading off a line. */}
        <text x={Math.min(sx(data[data.length - 1].t) + 8, W - R)} y={sy(data[data.length - 1].y) - 8}
          textAnchor={sx(data[data.length - 1].t) > W - 90 ? "end" : "start"} className="end-label">
          {format(data[data.length - 1].y)}
        </text>
      </svg>
      {h && (
        <div className="tooltip" style={{ left: `${(sx(h.t) / W) * 100}%` }}>
          <div className="muted small">{when(h.x)}</div>
          <strong>{format(h.y)}</strong>
        </div>
      )}
    </div>
  );
}

/**
 * Ranked magnitudes as horizontal bars: label on the left (a link when given), value at the tip.
 * Bars are capped at 14px and rounded only at the data end, square on the baseline.
 */
/**
 * Stacked parts for a BarList row: the given alliances (strongest first, one hue, strongest darkest -
 * an order, not identities), then "other" for the rest of the total when others is on.
 */
export function splitParts(alliances, total, others) {
  const n = alliances.length;
  const shade = (i) => (n <= 1 ? 1 : 1 - (i * 0.68) / (n - 1));
  const rest = total - alliances.reduce((s, a) => s + Number(a.value), 0);
  return [
    // An alliance's own colour when given (then full strength, dark label); else one hue by rank.
    ...alliances.map((a, i) => ({ label: a.tag || "?", value: Number(a.value), color: a.color || "var(--series-1)", opacity: a.color ? 1 : shade(i), ink: !!a.color,
      href: a.allianceId ? href.alliance(a.allianceId) : undefined })),
    ...(others && rest > 0 ? [{ label: "other", value: rest, color: "var(--muted)", opacity: 0.3 }] : []),
  ];
}

export function BarList({ rows, format = big, limit = 15, onlyTop }) {
  const [hover, setHover] = useState(null);
  const [wrap, W] = useWidth();
  const shown = (onlyTop ? rows.slice(0, limit) : rows).filter((r) => r.value !== null && r.value !== undefined);
  if (shown.length === 0) return <p className="muted small" ref={wrap}>No data yet.</p>;
  const max = Math.max(...shown.map((r) => Number(r.value))) || 1;
  // Labels get less room on a phone; the bar area takes whatever width is left.
  const BAR = 14, ROW = 26, LBL = W < 480 ? 110 : 160, VALW = 70;
  const H = shown.length * ROW;
  const len = (v) => Math.max(2, (Number(v) / max) * (W - LBL - VALW));
  const bar = (x, y, w) => `M${x},${y}h${w - 4}a4,4 0 0 1 4,4v${BAR - 8}a4,4 0 0 1 -4,4h${-(w - 4)}z`;

  return (
    <div className="chart" ref={wrap} onMouseLeave={() => setHover(null)}>
      <svg viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img">
        {shown.map((r, i) => {
          const y = i * ROW + (ROW - BAR) / 2;
          const w = len(r.value);
          return (
            <g key={r.key ?? i} onMouseEnter={() => setHover(i)}>
              <rect x={0} y={i * ROW} width={W} height={ROW} className="hit" />
              {r.onClick ? <text x={LBL - 10} y={y + BAR - 2} textAnchor="end" className="bar-label pick" onClick={r.onClick}>{r.label}</text> : r.href
                ? <a href={r.href}><text x={LBL - 10} y={y + BAR - 2} textAnchor="end" className="bar-label link">{r.label}</text></a>
                : <text x={LBL - 10} y={y + BAR - 2} textAnchor="end" className="bar-label">{r.label}</text>}
              {r.parts ? (() => {
                // Stacked: each part's share of the row's value, left to right, a 1px gap between.
                let x = LBL;
                return r.parts.map((p, j) => {
                  const pw = (w * Number(p.value)) / Number(r.value);
                  const seg = p.href ? (
                    <a key={j} href={p.href}>
                      <title>{p.label}</title>
                      <rect x={x} y={y} width={Math.max(0, pw - 1)} height={BAR} rx={2} className={hover === i ? "seg on link" : "seg link"}
                        style={{ fill: p.color, fillOpacity: p.opacity ?? 1 }} />
                      {pw > 40 && <text x={x + 4} y={y + BAR - 3} className={p.ink ? "seg-label ink" : "seg-label"}>{p.label}</text>}
                    </a>
                  ) : (
                    <g key={j}>
                      <rect x={x} y={y} width={Math.max(0, pw - 1)} height={BAR} rx={2} className={hover === i ? "seg on" : "seg"}
                        style={{ fill: p.color, fillOpacity: p.opacity ?? 1 }} />
                      {pw > 40 && <text x={x + 4} y={y + BAR - 3} className={p.ink ? "seg-label ink" : "seg-label"}>{p.label}</text>}
                    </g>
                  );
                  x += pw;
                  return seg;
                });
              })() : <path d={bar(LBL, y, w)} className={hover === i ? "series-bar on" : "series-bar"} style={r.color ? { fill: r.color } : undefined} />}
              <text x={LBL + w + 6} y={y + BAR - 2} className="bar-value">{format(r.value)}</text>
            </g>
          );
        })}
      </svg>
      {hover !== null && shown[hover].detail && (
        <div className="tooltip" style={{ top: `${((hover + 1) * ROW / H) * 100}%`, left: "40%" }}>
          <strong>{shown[hover].label}</strong>
          <div className="small">{shown[hover].detail}</div>
        </div>
      )}
    </div>
  );
}

/** A table that sorts on any column. Cells can be anything; sort uses the raw value. */
// rowKey: a unique key per row. The default (uid / allianceId / server) repeats when one player or server has several rows
// (secret tasks, trucks) and React then mixes rows up as filters change - pass rowKey for those.
export function SortTable({ columns, rows, initial, limit = 500, empty = "Nothing here yet.", rowKey }) {
  const [sort, setSort] = useState(initial || { key: columns[0].key, dir: -1 });
  const sorted = useMemo(() => {
    const col = columns.find((c) => c.key === sort.key);
    const val = col?.sort || ((r) => r[sort.key]);
    return [...rows].sort((a, b) => {
      const x = val(a), y = val(b);
      if (x === y) return 0;
      if (x === null || x === undefined) return 1;
      if (y === null || y === undefined) return -1;
      return (typeof x === "number" || !isNaN(x)) && (typeof y === "number" || !isNaN(y))
        ? (Number(x) - Number(y)) * sort.dir
        : String(x).localeCompare(String(y)) * sort.dir;
    });
  }, [rows, sort, columns]);
  if (rows.length === 0) return <p className="muted small">{empty}</p>;
  return (
    <div className="scroll">
      <table>
        <thead>
          <tr>
            {columns.map((c) => (
              <th key={c.key} className={c.num ? "num sortable" : "sortable"}
                onClick={() => setSort((s) => ({ key: c.key, dir: s.key === c.key ? -s.dir : -1 }))}>
                {c.label}{sort.key === c.key ? (sort.dir < 0 ? " ↓" : " ↑") : ""}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {sorted.slice(0, limit).map((r, i) => (
            <tr key={rowKey ? rowKey(r) : (r.uid || r.allianceId || r.server || i)}>
              {columns.map((c) => (
                <td key={c.key} className={c.num ? "num" : ""}>{c.render ? c.render(r) : r[c.key]}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {sorted.length > limit && <p className="muted small">Showing {limit} of {sorted.length.toLocaleString()}.</p>}
    </div>
  );
}

/**
 * Several entities over time on ONE shared axis - the comparison chart. Each series keeps its
 * slot colour for the life of the page (colour follows the entity, never its rank), carries a
 * legend AND a direct end label, so identity never rests on colour alone. Max four series.
 */
export function MultiLineChart({ series, label, height = 220, format = big }) {
  const [hover, setHover] = useState(null);
  const box = useRef(null);
  const [wrap, W] = useWidth();
  const H = height, L = 60, R = 70, T = 14, B = 26;

  const lines = useMemo(() => series.map((s) => ({
    ...s,
    data: s.points.filter((p) => p.y !== null && p.y !== undefined)
      .map((p) => ({ t: new Date(p.x).getTime(), y: Number(p.y), x: p.x }))
      .sort((a, b) => a.t - b.t),
  })).filter((s) => s.data.length > 0), [series]);
  if (lines.length === 0) return <p className="muted small" ref={wrap}>No data for this measure yet.</p>;

  const all = lines.flatMap((s) => s.data);
  const t0 = Math.min(...all.map((d) => d.t)), t1 = Math.max(...all.map((d) => d.t));
  const whole = all.every((d) => Number.isInteger(d.y));
  const ys = ticks(Math.min(...all.map((d) => d.y)), Math.max(...all.map((d) => d.y)), 4, whole);
  const y0 = ys[0], y1 = ys[ys.length - 1];
  const sx = (t) => (t1 === t0 ? (L + W - R) / 2 : L + ((t - t0) / (t1 - t0)) * (W - L - R));
  const sy = (y) => T + (1 - (y - y0) / (y1 - y0 || 1)) * (H - T - B);

  const move = (e) => {
    const r = box.current.getBoundingClientRect();
    const px = ((e.clientX - r.left) / r.width) * W;
    let best = all[0];
    all.forEach((d) => { if (Math.abs(sx(d.t) - px) < Math.abs(sx(best.t) - px)) best = d; });
    setHover(best.t);
  };
  // At the hovered moment, each series' nearest reading - snapshots are not taken in step.
  const at = hover === null ? null : lines.map((s) => {
    let best = s.data[0];
    s.data.forEach((d) => { if (Math.abs(d.t - hover) < Math.abs(best.t - hover)) best = d; });
    return { s, d: best };
  });

  // End labels, nudged apart so they never sit on top of each other.
  const ends = lines.map((s) => ({ s, d: s.data[s.data.length - 1], y: sy(s.data[s.data.length - 1].y) }))
    .sort((a, b) => a.y - b.y);
  for (let i = 1; i < ends.length; i++) if (ends[i].y - ends[i - 1].y < 13) ends[i].y = ends[i - 1].y + 13;

  return (
    <div className="chart" ref={wrap} onMouseLeave={() => setHover(null)}>
      <div className="legend">
        {lines.map((s) => (
          <span key={s.key}><i style={{ background: s.color }} />{s.label}</span>
        ))}
      </div>
      <svg ref={box} viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img" aria-label={label} onMouseMove={move}>
        {ys.map((v) => (
          <g key={v}>
            <line x1={L} x2={W - R} y1={sy(v)} y2={sy(v)} className="grid" />
            <text x={L - 8} y={sy(v) + 4} textAnchor="end" className="axis">{format(v)}</text>
          </g>
        ))}
        <text x={L} y={H - 6} className="axis">{when(new Date(t0).toISOString())}</text>
        {t1 !== t0 && <text x={W - R} y={H - 6} textAnchor="end" className="axis">{when(new Date(t1).toISOString())}</text>}
        {hover !== null && <line x1={sx(hover)} x2={sx(hover)} y1={T} y2={H - B} className="crosshair" />}
        {lines.map((s) => (
          <g key={s.key}>
            <path d={s.data.map((d, i) => `${i ? "L" : "M"}${sx(d.t).toFixed(1)},${sy(d.y).toFixed(1)}`).join("")}
              fill="none" stroke={s.color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
            {s.data.map((d, i) => (
              <circle key={i} cx={sx(d.t)} cy={sy(d.y)} r={at?.some((a) => a.s === s && a.d === d) ? 5.5 : 4}
                fill={s.color} stroke="var(--panel)" strokeWidth="2" />
            ))}
          </g>
        ))}
        {ends.map(({ s, d, y }) => (
          <text key={s.key} x={sx(d.t) + 8} y={y + 4} className="end-label">{format(d.y)}</text>
        ))}
      </svg>
      {at && (
        <div className="tooltip" style={{ left: `${Math.min(80, Math.max(20, (sx(hover) / W) * 100))}%`, top: 24 }}>
          {at.map(({ s, d }) => (
            <div key={s.key} className="row" style={{ gap: 6 }}>
              <i className="swatch" style={{ background: s.color }} />
              <span>{s.label}</span> <strong>{format(d.y)}</strong> <span className="muted small">{when(d.x)}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
