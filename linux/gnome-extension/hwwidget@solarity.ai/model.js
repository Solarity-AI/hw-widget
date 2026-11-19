// HW Widget - shared model: formatting, thresholds, colours and the 4 pill cells.
// Pure JavaScript (no GNOME imports) so the tests can run it with plain gjs.

export const DASH = '—';

export const THRESHOLDS = {temp: [80, 90], cpu: [75, 90], ram: [80, 90], gpu: [85, 95], disk: [80, 95]};

export const COLORS = {
    cpu: ['#38BDF8', '#818CF8'],
    ram: ['#C084FC', '#F472B6'],
    gpu: ['#4ADE80', '#2DD4BF'],
    disk: ['#FBBF24', '#F97316'],
    net: ['#60A5FA', '#A78BFA'],
    hot: '#F43F5E',
    warm: '#F59E0B',
    rainbow: ['#22D3EE', '#60A5FA', '#A78BFA', '#F472B6', '#FB923C', '#FACC15', '#34D399'],
};

export const PALETTES = {
    dark: {
        bg1: '#2A2A40', bg2: '#18182A', text: '#F8FAFC', sub: '#A5ABC3',
        track: [1, 1, 1, 0.14], sep: [1, 1, 1, 0.12], warn: '#FCD34D', crit: '#FF8FA3',
        card1: '#26263B', card2: '#15151F', edge: [0, 0, 0, 0.27], borderAlpha: 0.69, dark: true,
    },
    light: {
        bg1: '#FFFFFF', bg2: '#EEF1F8', text: '#111827', sub: '#5B6478',
        track: [0.07, 0.09, 0.15, 0.18], sep: [0.07, 0.09, 0.15, 0.12], warn: '#B45309', crit: '#E11D48',
        card1: '#FFFFFF', card2: '#F3F5FB', edge: [0.06, 0.09, 0.16, 0.13], borderAlpha: 0.92, dark: false,
    },
};

const RAMP_T = [35, 50, 65, 75, 85, 95];
const RAMP_C = ['#22D3EE', '#34D399', '#A3E635', '#FACC15', '#FB923C', '#F43F5E'];

export function hexToRgb(h) {
    const s = h.replace('#', '');
    return [parseInt(s.slice(0, 2), 16) / 255, parseInt(s.slice(2, 4), 16) / 255, parseInt(s.slice(4, 6), 16) / 255];
}

export function rgbToHex(c) {
    return `#${c.slice(0, 3).map(v => Math.round(Math.max(0, Math.min(1, v)) * 255).toString(16).padStart(2, '0')).join('').toUpperCase()}`;
}

export function mix(a, b, t) {
    const x = hexToRgb(a), y = hexToRgb(b);
    t = Math.max(0, Math.min(1, t));
    return rgbToHex(x.map((v, i) => v + (y[i] - v) * t));
}

export const lighten = (c, t) => mix(c, '#FFFFFF', t);
export const darken = (c, t) => mix(c, '#000000', t);

export function tempColor(c) {
    if (!Number.isFinite(c)) return '#94A3B8';
    if (c <= RAMP_T[0]) return RAMP_C[0];
    for (let i = 1; i < RAMP_T.length; i++) {
        if (c <= RAMP_T[i])
            return mix(RAMP_C[i - 1], RAMP_C[i], (c - RAMP_T[i - 1]) / (RAMP_T[i] - RAMP_T[i - 1]));
    }
    return RAMP_C[RAMP_C.length - 1];
}

export function level(v, [warn, crit]) {
    if (!Number.isFinite(v)) return 0;
    return v >= crit ? 2 : v >= warn ? 1 : 0;
}

export const fmt = {
    temp(c, f) {
        if (!Number.isFinite(c)) return DASH;
        return `${Math.round(f ? c * 9 / 5 + 32 : c)}°`;
    },
    tempLong(c, f) {
        return Number.isFinite(c) ? `${fmt.temp(c, f)}${f ? 'F' : 'C'}` : DASH;
    },
    pct(p) {
        return Number.isFinite(p) ? `${Math.round(Math.max(0, Math.min(100, p)))}%` : DASH;
    },
    rate(bps) {
        if (!Number.isFinite(bps)) return DASH;
        const u = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
        let v = Math.max(0, bps), i = 0;
        while (v >= 1000 && i < u.length - 1) {
            v /= 1024;
            i++;
        }
        return `${v < 10 && i > 0 ? v.toFixed(1) : Math.round(v)} ${u[i]}`;
    },
    gb(g) {
        return Number.isFinite(g) ? `${g < 10 ? g.toFixed(1) : Math.round(g)} GB` : DASH;
    },
};

const labelTint = (accent, p) => (p.dark ? lighten(accent, 0.2) : darken(accent, 0.22));
const barTone = (c, p) => (p.dark ? c : darken(c, 0.1));
const valueColor = (lvl, p) => (lvl === 2 ? p.crit : lvl === 1 ? p.warn : p.text);

export function showsGpu(s, fourth) {
    if (fourth === 'gpu') return true;
    if (fourth === 'disk') return false;
    return s.gpus.length > 0 && Number.isFinite(s.gpuPercent);
}

function metric(label, v, th, [a, b], p) {
    const lvl = level(v, th);
    return {
        label,
        value: fmt.pct(v),
        frac: Number.isFinite(v) ? v / 100 : NaN,
        bar1: barTone(lvl === 2 ? COLORS.warm : a, p),
        bar2: barTone(lvl === 2 ? COLORS.hot : lvl === 1 ? COLORS.warm : b, p),
        labelColor: labelTint(mix(a, b, 0.4), p),
        valueColor: valueColor(lvl, p),
        level: lvl,
    };
}

export function emptySnapshot() {
    return {
        cpuName: '', cpuPercent: NaN, cpuTempC: NaN, cpuTempSource: '',
        ramPercent: NaN, ramUsedGB: NaN, ramTotalGB: NaN,
        gpuPercent: NaN, gpuTempC: NaN, gpuName: '', gpus: [],
        diskPercent: NaN, netDownBps: NaN, netUpBps: NaN,
    };
}

export function buildCells(s, {fahrenheit = false, fourth = 'auto', palette = PALETTES.dark} = {}) {
    const p = palette;
    let tc = s.cpuTempC, label = 'TEMP';
    if (!Number.isFinite(tc) && Number.isFinite(s.gpuTempC)) {
        tc = s.gpuTempC;
        label = 'GPU°';
    }
    const tcol = tempColor(tc), tl = level(tc, THRESHOLDS.temp);
    const cells = [{
        label,
        value: fmt.temp(tc, fahrenheit),
        frac: Number.isFinite(tc) ? (tc - 30) / 70 : NaN,
        bar1: barTone(tempColor(35), p),
        bar2: barTone(tcol, p),
        labelColor: labelTint(tcol, p),
        valueColor: tl > 0 ? valueColor(tl, p) : (p.dark ? lighten(tcol, 0.45) : darken(tcol, 0.45)),
        level: tl,
    }];
    cells.push(metric('CPU', s.cpuPercent, THRESHOLDS.cpu, COLORS.cpu, p));
    cells.push(metric('RAM', s.ramPercent, THRESHOLDS.ram, COLORS.ram, p));
    if (showsGpu(s, fourth))
        cells.push(metric('GPU', s.gpuPercent, THRESHOLDS.gpu, COLORS.gpu, p));
    else
        cells.push(metric('DISK', s.diskPercent, THRESHOLDS.disk, COLORS.disk, p));
    return cells;
}

// Busy rows turn their bar amber (warm) or amber-to-red (critical), matching the pill.
function heat(row, lvl) {
    if (lvl === 2) Object.assign(row, {a: COLORS.warm, b: COLORS.hot});
    else if (lvl === 1) row.b = COLORS.warm;
    return row;
}

export function cardRows(s, {fahrenheit = false, palette = PALETTES.dark} = {}) {
    const p = palette, rows = [];
    const tc = tempColor(s.cpuTempC), tl = level(s.cpuTempC, THRESHOLDS.temp);
    rows.push({
        title: 'Temperature',
        sub: Number.isFinite(s.cpuTempC) ? `CPU · ${s.cpuTempSource || 'sensor'}` : 'No CPU temperature sensor found',
        value: fmt.tempLong(s.cpuTempC, fahrenheit),
        frac: (s.cpuTempC - 30) / 70, a: tempColor(35), b: tc, dot: tc,
        valueColor: tl > 0 ? valueColor(tl, p) : (p.dark ? lighten(tc, 0.35) : darken(tc, 0.45)),
    });
    rows.push(heat({
        title: 'CPU', sub: s.cpuName || 'Processor', value: fmt.pct(s.cpuPercent), frac: s.cpuPercent / 100,
        a: COLORS.cpu[0], b: COLORS.cpu[1], valueColor: valueColor(level(s.cpuPercent, THRESHOLDS.cpu), p),
    }, level(s.cpuPercent, THRESHOLDS.cpu)));
    rows.push(heat({
        title: 'Memory',
        sub: Number.isFinite(s.ramUsedGB) ? `${fmt.gb(s.ramUsedGB).replace(' GB', '')} of ${fmt.gb(s.ramTotalGB)} in use` : 'Usage unavailable',
        value: fmt.pct(s.ramPercent), frac: s.ramPercent / 100,
        a: COLORS.ram[0], b: COLORS.ram[1], valueColor: valueColor(level(s.ramPercent, THRESHOLDS.ram), p),
    }, level(s.ramPercent, THRESHOLDS.ram)));
    for (const g of s.gpus) {
        const parts = [];
        if (Number.isFinite(g.tempC)) parts.push(fmt.tempLong(g.tempC, fahrenheit));
        if (Number.isFinite(g.memUsedGB)) parts.push(`${fmt.gb(g.memUsedGB)} VRAM`);
        parts.push((g.name || 'GPU').replace(/^NVIDIA /, '').replace(/\((R|TM)\)/g, ''));
        rows.push(heat({
            title: 'GPU', sub: parts.join(' · '), value: fmt.pct(g.percent), frac: g.percent / 100,
            a: COLORS.gpu[0], b: COLORS.gpu[1], valueColor: valueColor(level(g.percent, THRESHOLDS.gpu), p),
        }, level(g.percent, THRESHOLDS.gpu)));
    }
    rows.push(heat({
        title: 'Disk', sub: 'Busiest drive, active time', value: fmt.pct(s.diskPercent), frac: s.diskPercent / 100,
        a: COLORS.disk[0], b: COLORS.disk[1], valueColor: valueColor(level(s.diskPercent, THRESHOLDS.disk), p),
    }, level(s.diskPercent, THRESHOLDS.disk)));
    rows.push({
        title: 'Network', sub: `↑ ${fmt.rate(s.netUpBps)} upload`, value: `↓ ${fmt.rate(s.netDownBps)}`,
        frac: Number.isFinite(s.netDownBps) ? Math.log10(1 + s.netDownBps) / Math.log10(1 + 125e6) : NaN,
        a: COLORS.net[0], b: COLORS.net[1], valueColor: p.text,
    });
    return rows;
}

// What a screen reader announces for the pill: "TEMP 61°, CPU 23%, RAM 58%, DISK unknown".
export function accessibleText(cells) {
    return cells.map(c => `${c.label} ${c.value === DASH ? 'unknown' : c.value}`).join(', ');
}

// Settings read from disk, with each wrong or missing value replaced by its default (°C, 2 s, auto).
export function normalizeSettings(raw) {
    const r = raw && typeof raw === 'object' ? raw : {};
    return {
        interval: [1, 2, 5].includes(r.interval) ? r.interval : 2,
        fahrenheit: r.fahrenheit === true,
        fourth: ['auto', 'gpu', 'disk'].includes(r.fourth) ? r.fourth : 'auto',
    };
}
