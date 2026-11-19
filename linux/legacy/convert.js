// HW Widget - converts the GNOME 45+ ES-module extension into GNOME Shell 42-44's legacy `imports` modules.
// Usage: gjs -m linux/legacy/convert.js <extension-dir> <output-dir>
// One source stays the truth; linux/install.sh runs this on GNOME 42-44, CI runs it on Ubuntu 22.04's GJS.
import GLib from 'gi://GLib';
import System from 'system';

const read = path => new TextDecoder().decode(GLib.file_get_contents(path)[1]);
const write = (path, text) => GLib.file_set_contents(path, text);
const ME = 'const Me = imports.misc.extensionUtils.getCurrentExtension();\n';

export function toLegacy(text, {isEntry = false} = {}) {
    const out = [];
    for (const line of text.split('\n')) {
        let m;
        if ((m = line.match(/^import (\w+) from 'gi:\/\/(\w+)';$/))) out.push(`const ${m[1]} = imports.gi.${m[2]};`);
        else if ((m = line.match(/^import (\w+) from 'cairo';$/))) out.push(`const ${m[1]} = imports.cairo;`);
        else if ((m = line.match(/^import \* as (\w+) from 'resource:\/\/\/org\/gnome\/shell\/ui\/(\w+)\.js';$/))) out.push(`const ${m[1]} = imports.ui.${m[2]};`);
        else if (/^import \{Extension\} from 'resource:\/\/\/org\/gnome\/shell\/extensions\/extension\.js';$/.test(line)) continue;
        else if ((m = line.match(/^import \{([^}]+)\} from '\.\/(\w+)\.js';$/))) out.push(`const {${m[1]}} = Me.imports.${m[2]};`);
        else if (/^\s*(import|export)\b/.test(line) && !/^export (const|function|class|default class \w+ extends Extension) /.test(line))
            throw new Error(`convert.js cannot translate: ${line}`);
        else {
            // Legacy modules export only top-level `var` and `function`; classes become `var X = class X`.
            out.push(line.replace(/^export const /, 'var ').replace(/^export function /, 'function ')
                .replace(/^export class (\w+)/, 'var $1 = class $1')
                .replace(/^export default class (\w+) extends Extension \{/, 'class $1 {')
                .replace(/\bthis\.uuid\b/g, 'Me.metadata.uuid'));
        }
    }
    let js = out.join('\n');
    if (/\bMe\./.test(js)) js = ME + js;
    if (isEntry) {
        const cls = text.match(/^export default class (\w+) extends Extension/m)?.[1];
        if (!cls) throw new Error('convert.js: no default Extension class in extension.js');
        js += `\nfunction init() {\n    return new ${cls}();\n}\n`;
    }
    return js;
}

export function convert(src, dest) {
    GLib.mkdir_with_parents(dest, 0o755);
    for (const name of ['extension.js', 'model.js', 'render.js', 'sensors.js'])
        write(`${dest}/${name}`, toLegacy(read(`${src}/${name}`), {isEntry: name === 'extension.js'}));
    write(`${dest}/stylesheet.css`, read(`${src}/stylesheet.css`));
    const meta = JSON.parse(read(`${src}/metadata.json`));
    meta['shell-version'] = ['42', '43', '44'];
    write(`${dest}/metadata.json`, `${JSON.stringify(meta, null, 2)}\n`);

}

if (ARGV.length === 2) {
    try {
        convert(ARGV[0], ARGV[1]);
    } catch (e) {
        printerr(`${e.message}`);
        System.exit(1);
    }
}
