// HW Widget GNOME 42-44 test: converts the extension with linux/legacy/convert.js, then loads the legacy
// modules the way GNOME Shell 42 does (imports + a stub extensionUtils). Run: gjs linux/tests/legacy-test.js
const {GLib} = imports.gi;
const Cairo = imports.cairo;
const System = imports.system;

let pass = 0, fail = 0;
function check(name, ok, detail = '') {
    if (ok) pass++;
    else fail++;
    print(`${ok ? 'PASS' : 'FAIL'} legacy: ${name}${ok || !detail ? '' : `  -> ${detail}`}`);
}

const here = GLib.path_get_dirname(GLib.canonicalize_filename(System.programPath, null));
const linux = GLib.path_get_dirname(here);
const tmp = GLib.dir_make_tmp('hwwidget-legacy-XXXXXX');
const [, , err, status] = GLib.spawn_sync(null, ['gjs', '-m', `${linux}/legacy/convert.js`,
    `${linux}/gnome-extension/hwwidget@solarity.ai`, `${tmp}/hwext`], null, GLib.SpawnFlags.SEARCH_PATH, null);
check('convert.js succeeds', status === 0, new TextDecoder().decode(err));

GLib.mkdir_with_parents(`${tmp}/misc`, 0o755);
GLib.file_set_contents(`${tmp}/misc/extensionUtils.js`,
    "var getCurrentExtension = () => ({imports: imports.hwext, metadata: {uuid: 'hwwidget@solarity.ai'}});\n");
imports.searchPath.unshift(tmp);

const read = n => new TextDecoder().decode(GLib.file_get_contents(`${tmp}/hwext/${n}`)[1]);
for (const n of ['extension.js', 'model.js', 'render.js', 'sensors.js']) {
    const src = read(n);
    let parsed = true;
    try {
        Reflect.parse(src);
    } catch (e) {
        parsed = `${e}`;
    }
    check(`${n} parses as a classic script`, parsed === true, parsed);
    check(`${n} has no import/export left`, !/^\s*(import|export)\b/m.test(src));
}
check('extension.js defines init()', /^function init\(\) \{\n {4}return new \w+\(\);\n\}$/m.test(read('extension.js')));
const meta = JSON.parse(read('metadata.json'));
check('metadata lists shells 42-44', JSON.stringify(meta['shell-version']) === '["42","43","44"]', JSON.stringify(meta['shell-version']));

const M = imports.hwext.model, R = imports.hwext.render, S = imports.hwext.sensors;
const snap = Object.assign(M.emptySnapshot(), {cpuTempC: 61, cpuPercent: 23, ramPercent: 58, diskPercent: 4});
const cells = M.buildCells(snap);
check('model exports work', cells.length === 4 && M.accessibleText(cells).startsWith('TEMP 61°'), M.accessibleText(cells));
const ps = R.pillSize(cells.length);
const surf = new Cairo.ImageSurface(Cairo.Format.ARGB32, Math.ceil(ps.w), Math.ceil(ps.h));
const cr = new Cairo.Context(surf);
let drawn = true;
try {
    R.drawPill(cr, cells, M.PALETTES.dark);
} catch (e) {
    drawn = `${e}`;
}
cr.$dispose();
check('render draws the pill', drawn === true, drawn);
const smp = new S.Sampler({nvidia: false});
const s = smp.sample();
check('Sampler class is exported and samples', Number.isFinite(s.ramPercent), JSON.stringify(s));
check('listDir works on this GLib', S.listDir('/proc').length > 0);

print(`${pass} passed, ${fail} failed`);
System.exit(fail ? 1 : 0);
