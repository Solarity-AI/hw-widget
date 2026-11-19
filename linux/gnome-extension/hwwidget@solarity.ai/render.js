// HW Widget - cairo drawing for the GNOME top-bar pill and the details card (same design as Windows).
// Works on any cairo context: St.DrawingArea in GNOME Shell, or an ImageSurface in the tests.
import Cairo from 'cairo';
import Pango from 'gi://Pango';
import PangoCairo from 'gi://PangoCairo';
import {COLORS, DASH, hexToRgb} from './model.js';

export const PILL = {h: 34, pad: 8, cell: 44};
export const CARD = {w: 300, rowH: 46, headH: 40, footH: 28};
const FONT = 'Inter,Cantarell,Ubuntu,Noto Sans,DejaVu Sans';

const rgba = (hex, a = 1) => [...hexToRgb(hex), a];

function set(cr, c) {
    if (typeof c === 'string') cr.setSourceRGBA(...rgba(c));
    else cr.setSourceRGBA(...c);
}

function roundRect(cr, x, y, w, h, r) {
    r = Math.min(r, w / 2, h / 2);
    cr.newSubPath();
    cr.arc(x + w - r, y + r, r, -Math.PI / 2, 0);
    cr.arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
    cr.arc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
    cr.arc(x + r, y + r, r, Math.PI, 1.5 * Math.PI);
    cr.closePath();
}

function linear(x0, y0, x1, y1, stops) {
    const g = new Cairo.LinearGradient(x0, y0, x1, y1);
    stops.forEach(([off, c]) => g.addColorStopRGBA(off, ...(typeof c === 'string' ? rgba(c) : c)));
    return g;
}

function rainbow(x, w, alpha) {
    const n = COLORS.rainbow.length;
    return linear(x, 0, x + w, 0, COLORS.rainbow.map((c, i) => [i / (n - 1), rgba(c, alpha)]));
}

// align: 0 left, 0.5 centre, 1 right. Returns the drawn width.
function text(cr, s, px, weight, color, x, y, w, h, align = 0.5) {
    const layout = PangoCairo.create_layout(cr);
    const fd = Pango.FontDescription.from_string(FONT);
    fd.set_absolute_size(px * Pango.SCALE);
    fd.set_weight(weight);
    layout.set_font_description(fd);
    layout.set_text(s, -1);
    layout.set_width(Math.max(1, Math.round(w * Pango.SCALE)));
    layout.set_ellipsize(Pango.EllipsizeMode.END);
    layout.set_alignment(align === 0 ? Pango.Alignment.LEFT : align === 1 ? Pango.Alignment.RIGHT : Pango.Alignment.CENTER);
    const [, logical] = layout.get_pixel_extents();
    set(cr, color);
    cr.moveTo(x, y + (h - logical.height) / 2);
    PangoCairo.show_layout(cr, layout);
    return logical.width;
}

function bar(cr, x, y, w, h, frac, a, b, p) {
    roundRect(cr, x, y, w, h, h / 2);
    const tr = [...p.track];
    if (!Number.isFinite(frac)) tr[3] /= 2;
    set(cr, tr);
    cr.fill();
    if (!Number.isFinite(frac)) return;
    const fw = Math.max(Math.min(w, h), w * Math.max(0, Math.min(1, frac)));
    roundRect(cr, x, y, fw, h, h / 2);
    cr.setSource(linear(x, 0, x + w, 0, [[0, a], [1, b]]));
    cr.fill();
}

export function pillSize(cells, scale = 1) {
    return {w: Math.ceil((PILL.pad * 2 + PILL.cell * cells) * scale), h: Math.ceil(PILL.h * scale)};
}

// Draws at 1 unit = 1 DIP; call cr.scale() first for HiDPI or to fit a shorter panel.
export function drawPill(cr, cells, p, hover = false) {
    const W = PILL.pad * 2 + PILL.cell * cells.length, H = PILL.h;
    const x0 = 0.75, y0 = 0.75, w = W - 1.5, h = H - 1.5;
    roundRect(cr, x0, y0, w, h, h / 2);
    cr.setSource(linear(0, 0, 0, H, [[0, p.bg1], [1, p.bg2]]));
    cr.fillPreserve();
    if (hover) {
        set(cr, p.dark ? [1, 1, 1, 0.07] : [0.12, 0.16, 0.23, 0.04]);
        cr.fillPreserve();
    }
    cr.save();
    cr.clip();
    cells.forEach((c, i) => {
        const a = c.level === 2 ? (p.dark ? 0.13 : 0.12) : c.level === 1 ? (p.dark ? 0.09 : 0.07) : (p.dark ? 0.04 : 0.025);
        const tint = c.level === 2 ? p.crit : c.level === 1 ? p.warn : c.bar2;
        const cx = PILL.pad + i * PILL.cell - (i === 0 ? PILL.pad : 0);
        const cw = PILL.cell + (i === 0 || i === cells.length - 1 ? PILL.pad : 0);
        cr.rectangle(cx, 0, cw, H);
        cr.setSource(linear(0, 0, 0, H, [[0, rgba(tint, 0)], [1, rgba(tint, a)]]));
        cr.fill();
    });
    cr.restore();

    roundRect(cr, x0, y0, w, h, h / 2);
    set(cr, p.edge);
    cr.setLineWidth(2.2);
    cr.strokePreserve();
    cr.setSource(rainbow(0, W, hover ? 1 : p.borderAlpha));
    cr.setLineWidth(1.3);
    cr.stroke();

    cells.forEach((c, i) => {
        const x = PILL.pad + i * PILL.cell;
        if (i > 0) {
            set(cr, p.sep);
            cr.setLineWidth(1);
            cr.moveTo(x, 9.5);
            cr.lineTo(x, H - 9.5);
            cr.stroke();
        }
        text(cr, c.label, 8.6, Pango.Weight.BOLD, c.labelColor, x, 3.7, PILL.cell, 9.4);
        const unknown = c.value === DASH;
        text(cr, unknown ? '–' : c.value, 13, Pango.Weight.BOLD, unknown ? rgba(p.sub, 0.6) : c.valueColor,
            x - 2, unknown ? 12.6 : 11.6, PILL.cell + 4, 15);
        bar(cr, x + 8, 27, PILL.cell - 16, 2.6, c.frac, c.bar1, c.bar2, p);
    });
}

export function cardSize(rows) {
    return {w: CARD.w, h: CARD.headH + CARD.rowH * rows + CARD.footH};
}

export function drawCard(cr, rows, p, {pinned = false, title = 'Hardware', footer = ''} = {}) {
    const {w: W, h: H} = cardSize(rows.length);
    roundRect(cr, 0.5, 0.5, W - 1, H - 1, 14);
    cr.setSource(linear(0, 0, 0, H, [[0, p.card1], [1, p.card2]]));
    cr.fillPreserve();
    set(cr, p.edge);
    cr.setLineWidth(2.2);
    cr.strokePreserve();
    cr.setSource(rainbow(0, W, p.dark ? 0.63 : 0.78));
    cr.setLineWidth(1.3);
    cr.stroke();

    const x = 16, w = W - 32;
    let y = 10;
    cr.arc(x + 6, y + 15, 6, 0, 2 * Math.PI);
    cr.setSource(linear(x, y + 9, x + 12, y + 21, [[0, '#22D3EE'], [1, '#F472B6']]));
    cr.fill();
    text(cr, title, 14, Pango.Weight.BOLD, p.text, x + 20, y + 3, 150, 24, 0);
    text(cr, pinned ? 'Pinned' : 'Live', 10.5, Pango.Weight.NORMAL, pinned ? '#A78BFA' : p.sub, x + w - 120, y + 3, 120, 24, 1);
    y = CARD.headH;
    for (const r of rows) {
        cr.arc(x + 4.5, y + 12.5, 4.5, 0, 2 * Math.PI);
        if (r.dot) set(cr, r.dot);
        else cr.setSource(linear(x, y + 8, x + 9, y + 17, [[0, r.a], [1, r.b]]));
        cr.fill();
        const vw = text(cr, r.value, 15, Pango.Weight.BOLD, r.value === DASH ? p.sub : r.valueColor, x + w - 110, y + 1, 110, 22, 1);
        text(cr, r.title, 12.5, Pango.Weight.BOLD, p.text, x + 16, y + 3, w - 16 - vw - 6, 18, 0);
        text(cr, r.sub || '', 11, Pango.Weight.NORMAL, p.sub, x + 16, y + 21, w - 16, 15, 0);
        bar(cr, x + 16, y + 38, w - 16, 3.5, r.frac, r.a, r.b, p);
        y += CARD.rowH;
    }
    if (footer) text(cr, footer, 10.5, Pango.Weight.NORMAL, p.sub, x, y + 2, w, CARD.footH - 6, 0);
}
