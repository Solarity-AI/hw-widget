// HW Widget - GNOME Shell 45+ top-bar pill: temperature + CPU, RAM, GPU (or disk), with a details card.
import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import GObject from 'gi://GObject';
import St from 'gi://St';
import Clutter from 'gi://Clutter';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {PALETTES, buildCells, cardRows, accessibleText, normalizeSettings} from './model.js';
import {PILL, pillSize, drawPill, cardSize, drawCard} from './render.js';
import {Sampler} from './sensors.js';

const CONFIG_DIR = GLib.build_filenamev([GLib.get_user_config_dir(), 'hwwidget']);
const CONFIG_FILE = GLib.build_filenamev([CONFIG_DIR, 'settings.json']);

function loadSettings() {
    // Celsius by default everywhere; Fahrenheit only when chosen in the menu.
    let raw = null;
    try {
        const [ok, bytes] = GLib.file_get_contents(CONFIG_FILE);
        if (ok) raw = JSON.parse(new TextDecoder().decode(bytes));
    } catch {}
    return normalizeSettings(raw);
}

function saveSettings(s) {
    try {
        GLib.mkdir_with_parents(CONFIG_DIR, 0o755);
        GLib.file_set_contents(CONFIG_FILE, JSON.stringify(s, null, 2));
    } catch {}
}

const HwIndicator = GObject.registerClass(
class HwIndicator extends PanelMenu.Button {
    _init() {
        super._init(0.5, 'HW Widget');
        this._settings = loadSettings();
        this._sampler = new Sampler();
        this._snap = this._sampler.sample();
        this._hover = false;

        this._pill = new St.DrawingArea({y_align: Clutter.ActorAlign.CENTER, style_class: 'hwwidget-pill'});
        this._pill.connect('repaint', area => this._paintPill(area));
        this.add_child(this._pill);
        this.connect('notify::hover', () => {
            this._hover = this.hover;
            this._pill.queue_repaint();
        });

        this.menu.box.add_style_class_name('hwwidget-menu');
        const cardItem = new PopupMenu.PopupBaseMenuItem({reactive: false, can_focus: false, style_class: 'hwwidget-card-item'});
        this._card = new St.DrawingArea();
        this._card.connect('repaint', area => this._paintCard(area));
        cardItem.add_child(this._card);
        this.menu.addMenuItem(cardItem);
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._addChoice('Refresh every', [[1, '1 second'], [2, '2 seconds'], [5, '5 seconds']], 'interval', () => this._restartTimer());
        this._addChoice('Temperature unit', [[false, 'Celsius (°C)'], [true, 'Fahrenheit (°F)']], 'fahrenheit');
        this._addChoice('Fourth metric', [['auto', 'Automatic (GPU, else disk)'], ['gpu', 'GPU'], ['disk', 'Disk']], 'fourth');
        const down = new PopupMenu.PopupSwitchMenuItem('Show download speed', this._settings.download);
        down.connect('toggled', (_item, on) => {
            this._settings.download = on;
            saveSettings(this._settings);
            this._refresh(false);
        });
        this.menu.addMenuItem(down);
        this.menu.addAction('Open System Monitor', () => {
            try {
                Gio.AppInfo.create_from_commandline('gnome-system-monitor', null, Gio.AppInfoCreateFlags.NONE).launch([], null);
            } catch {}
        });
        this.menu.connect('open-state-changed', (_m, open) => {
            if (open) this._card.queue_repaint();
        });

        this._themeCtx = St.ThemeContext.get_for_stage(global.stage);
        this._scaleId = this._themeCtx.connect('notify::scale-factor', () => this._resize());
        this._iface = new Gio.Settings({schema_id: 'org.gnome.desktop.interface'});
        this._ifaceId = this._iface.connect('changed::color-scheme', () => this._card.queue_repaint());
        this._resize();
        this.accessible_name = accessibleText(this._cells());
        this._restartTimer();
    }

    _addChoice(title, options, key, after) {
        const sub = new PopupMenu.PopupSubMenuMenuItem(title);
        const items = options.map(([value, label]) => {
            const it = new PopupMenu.PopupMenuItem(label);
            it.connect('activate', () => {
                this._settings[key] = value;
                saveSettings(this._settings);
                items.forEach(([v, i]) => i.setOrnament(v === value ? PopupMenu.Ornament.CHECK : PopupMenu.Ornament.NONE));
                after?.();
                this._refresh(false);
            });
            it.setOrnament(this._settings[key] === value ? PopupMenu.Ornament.CHECK : PopupMenu.Ornament.NONE);
            sub.menu.addMenuItem(it);
            return [value, it];
        });
        this.menu.addMenuItem(sub);
    }

    get _sf() {
        return this._themeCtx.scale_factor || 1;
    }

    _cells() {
        return buildCells(this._snap, {fahrenheit: this._settings.fahrenheit, fourth: this._settings.fourth, download: this._settings.download, palette: PALETTES.dark});
    }

    _pillScale() {
        // Fit the 34-DIP design into the panel, leaving 3 DIP above and below.
        const panelH = Main.panel.height / this._sf;
        return Math.min(1, Math.max(0.6, (panelH - 6) / PILL.h)) * this._sf;
    }

    _resize() {
        const sc = this._pillScale();
        const ps = pillSize(this._cells().length, sc);
        this._pill.set_size(ps.w, ps.h);
        const cs = cardSize(cardRows(this._snap).length);
        this._card.set_size(Math.ceil(cs.w * this._sf), Math.ceil(cs.h * this._sf));
    }

    _paintPill(area) {
        const cr = area.get_context();
        const sc = this._pillScale();
        cr.scale(sc, sc);
        drawPill(cr, this._cells(), PALETTES.dark, this._hover);
        cr.$dispose();
    }

    _paintCard(area) {
        const cr = area.get_context();
        cr.scale(this._sf, this._sf);
        const light = this._iface.get_string('color-scheme') === 'prefer-light';
        const p = light ? PALETTES.light : PALETTES.dark;
        drawCard(cr, cardRows(this._snap, {fahrenheit: this._settings.fahrenheit, palette: p}), p,
            {footer: `Updates every ${this._settings.interval} s \u00B7 no root needed`});
        cr.$dispose();
    }

    _restartTimer() {
        if (this._timer) GLib.source_remove(this._timer);
        this._timer = GLib.timeout_add_seconds(GLib.PRIORITY_LOW, this._settings.interval, () => {
            this._refresh(true);
            return GLib.SOURCE_CONTINUE;
        });
    }

    _refresh(sample) {
        if (sample) {
            try {
                this._snap = this._sampler.sample();
            } catch (e) {
                console.error(`HW Widget: ${e}`);
            }
        }
        this._resize();
        this.accessible_name = accessibleText(this._cells());
        this._pill.queue_repaint();
        if (this.menu.isOpen) this._card.queue_repaint();
    }

    destroy() {
        if (this._timer) GLib.source_remove(this._timer);
        this._timer = 0;
        if (this._scaleId) this._themeCtx.disconnect(this._scaleId);
        if (this._ifaceId) this._iface.disconnect(this._ifaceId);
        this._sampler?.destroy();
        this._sampler = null;
        super.destroy();
    }
});

export default class HwWidgetExtension extends Extension {
    enable() {
        this._indicator = new HwIndicator();
        Main.panel.addToStatusArea(this.uuid, this._indicator, 0, 'right');
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
    }
}
