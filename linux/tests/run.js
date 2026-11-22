// HW Widget Linux tests: run with `gjs -m linux/tests/run.js <png-output-dir>` (see linux/tests/run-docker.sh).
// Builds fake /proc and /sys trees, checks parsers, temperature choice, the sampler and the cell model,
// then renders the pill and the card to PNG with the real drawing code.
import GLib from 'gi://GLib';
import Cairo from 'cairo';
import System from 'system';
import * as M from '../gnome-extension/hwwidget@solarity.ai/model.js';
import * as S from '../gnome-extension/hwwidget@solarity.ai/sensors.js';
import * as R from '../gnome-extension/hwwidget@solarity.ai/render.js';

let pass = 0, fail = 0;
function check(name, ok, detail = '') {
    if (ok) pass++;
    else fail++;
    print(`${ok ? 'PASS' : 'FAIL'} ${name}${ok || !detail ? '' : `  -> ${detail}`}`);
}
const near = (a, b, eps = 0.05) => Math.abs(a - b) <= eps;

function write(root, rel, text) {
    const path = `${root}/${rel}`;
    GLib.mkdir_with_parents(GLib.path_get_dirname(path), 0o755);
    GLib.file_set_contents(path, text);
}

function fakeRoot(kind) {
    const root = GLib.dir_make_tmp('hwwidget-XXXXXX');
    write(root, 'proc/stat', 'cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 50 0 50 400 0 0 0 0 0 0\n');
    write(root, 'proc/meminfo', 'MemTotal:       16384000 kB\nMemFree:         1000000 kB\nMemAvailable:    4096000 kB\n');
    write(root, 'proc/cpuinfo', kind === 'arm' ? 'processor\t: 0\nModel\t\t: Raspberry Pi 5 Model B Rev 1.0\n' : 'model name\t: AMD Ryzen 7 7840U w/ Radeon 780M Graphics\n');
    write(root, 'proc/diskstats', '   8       0 sda 100 0 0 0 0 0 0 0 0 1000 0\n   8       1 sda1 1 0 0 0 0 0 0 0 0 999999 0\n 259       0 nvme0n1 5 0 0 0 0 0 0 0 0 5000 0\n   7       0 loop0 0 0 0 0 0 0 0 0 0 99999 0\n');
    write(root, 'proc/net/dev', 'Inter-|   Receive\n face |bytes packets\n    lo: 999999 1 0 0 0 0 0 0 999999 0 0 0 0 0 0 0\n  eth0: 1000 1 0 0 0 0 0 0 500 0 0 0 0 0 0 0\ndocker0: 777777 1 0 0 0 0 0 0 777777 0 0 0 0 0 0 0\n');
    if (kind === 'intel') {
        write(root, 'sys/class/hwmon/hwmon0/name', 'acpitz\n');
        write(root, 'sys/class/hwmon/hwmon0/temp1_input', '27800\n');
        write(root, 'sys/class/hwmon/hwmon3/name', 'coretemp\n');
        write(root, 'sys/class/hwmon/hwmon3/temp1_label', 'Package id 0\n');
        write(root, 'sys/class/hwmon/hwmon3/temp1_input', '64000\n');
        write(root, 'sys/class/hwmon/hwmon3/temp2_label', 'Core 0\n');
        write(root, 'sys/class/hwmon/hwmon3/temp2_input', '71000\n');
    } else if (kind === 'amd') {
        write(root, 'sys/class/hwmon/hwmon1/name', 'k10temp\n');
        write(root, 'sys/class/hwmon/hwmon1/temp1_label', 'Tctl\n');
        write(root, 'sys/class/hwmon/hwmon1/temp1_input', '58250\n');
        write(root, 'sys/class/hwmon/hwmon2/name', 'amdgpu\n');
        write(root, 'sys/class/hwmon/hwmon2/temp1_label', 'edge\n');
        write(root, 'sys/class/hwmon/hwmon2/temp1_input', '47000\n');
        write(root, 'sys/class/drm/card1/device/gpu_busy_percent', '37\n');
        write(root, 'sys/class/drm/card1/device/mem_info_vram_used', `${512 * 1024 * 1024}\n`);
        write(root, 'sys/class/drm/card1/device/mem_info_vram_total', `${512 * 1024 * 1024}\n`);
    } else if (kind === 'arm') {
        write(root, 'sys/class/thermal/thermal_zone0/type', 'cpu-thermal\n');
        write(root, 'sys/class/thermal/thermal_zone0/temp', '49950\n');
    }
    return root;
}

function rmrf(path) {
    GLib.spawn_command_line_sync(`rm -rf ${GLib.shell_quote(path)}`);
}

// ---------------------------------------------------------------- parsers
check('cpu: 20% busy between two /proc/stat samples',
    near(S.cpuPercent(S.parseCpuTimes('cpu 100 0 100 800 0 0 0 0'), S.parseCpuTimes('cpu 120 0 120 960 0 0 0 0')), 20));
check('cpu: no previous sample -> NaN', Number.isNaN(S.cpuPercent(null, S.parseCpuTimes('cpu 1 1 1 1'))));
const mem = S.parseMeminfo('MemTotal: 16384000 kB\nMemAvailable: 4096000 kB\n');
check('mem: 75% used', near(mem.percent, 75) && near(mem.totalGB, 15.625, 0.01));
check('mem: old kernels without MemAvailable', near(S.parseMeminfo('MemTotal: 1000 kB\nMemFree: 200 kB\nBuffers: 100 kB\nCached: 200 kB\n').percent, 50));
const d1 = S.parseDiskstats('8 0 sda 1 0 0 0 0 0 0 0 0 1000 0\n8 1 sda1 1 0 0 0 0 0 0 0 0 1000 0\n7 0 loop0 0 0 0 0 0 0 0 0 0 5 0');
check('disk: whole disks only (no partitions, no loop)', Object.keys(d1).join() === 'sda');
check('disk: 25% busy over 2 s', near(S.diskBusy({sda: 1000}, {sda: 1500}, 2000), 25));
const net = S.parseNetDev('  lo: 5 0 0 0 0 0 0 0 5\n eth0: 100 1 0 0 0 0 0 0 40\n wlan0: 50 1 0 0 0 0 0 0 10\n veth12: 9 0 0 0 0 0 0 0 9\n');
check('net: sums real NICs, skips lo/veth', net.rx === 150 && net.tx === 50);
const nv = S.parseNvidiaLine('0, 37, 61, 2048, NVIDIA GeForce RTX 4070');
check('nvidia: csv line', nv.index === 0 && nv.percent === 37 && nv.tempC === 61 && nv.memUsedGB === 2 && nv.name === 'NVIDIA GeForce RTX 4070');
check('nvidia: [N/A] fields become NaN', Number.isNaN(S.parseNvidiaLine('1, [N/A], [N/A], 0, NVIDIA GB10').percent));
check('cpu name: x86 and Raspberry Pi', S.cpuNameFrom('model name\t: Intel i7\n') === 'Intel i7' && S.cpuNameFrom('Model\t: Raspberry Pi 5\n') === 'Raspberry Pi 5');

// ---------------------------------------------------------------- temperature choice
const pick = S.pickCpuTemp;
check('temp: coretemp Package beats acpitz and hotter cores',
    pick([{name: 'acpitz', temps: [{label: '', c: 27.8}]}, {name: 'coretemp', temps: [{label: 'Core 0', c: 71}, {label: 'Package id 0', c: 64}]}], [])?.c === 64);
check('temp: k10temp Tctl', pick([{name: 'k10temp', temps: [{label: 'Tccd1', c: 50}, {label: 'Tctl', c: 58}]}], [])?.source === 'k10temp Tctl');
check('temp: amdgpu is never the CPU', pick([{name: 'amdgpu', temps: [{label: 'edge', c: 47}]}], []) === null);
check('temp: thermal zone fallback (x86_pkg_temp first)', pick([], [{type: 'acpitz', c: 30}, {type: 'x86_pkg_temp', c: 55}])?.c === 55);
check('temp: ARM cpu-thermal zone', pick([], [{type: 'cpu-thermal', c: 49.95}])?.c === 49.95);
check('temp: implausible values ignored', pick([{name: 'coretemp', temps: [{label: 'Package id 0', c: 0}]}], [{type: 'acpitz', c: 200}]) === null);

// ---------------------------------------------------------------- sampler on fake trees
for (const kind of ['intel', 'amd', 'arm', 'empty']) {
    const root = fakeRoot(kind);
    const smp = new S.Sampler({root, nvidia: false});
    smp.sample();
    write(root, 'proc/stat', 'cpu  130 0 130 840 0 0 0 0 0 0\n');   // +60 busy of +100 -> 60%
    write(root, 'proc/diskstats', '   8       0 sda 100 0 0 0 0 0 0 0 0 1000 0\n 259       0 nvme0n1 5 0 0 0 0 0 0 0 0 5600 0\n');
    write(root, 'proc/net/dev', '  eth0: 3000 1 0 0 0 0 0 0 900 0 0 0 0 0 0 0\n');
    smp._prevAt = smp._clock() - 2000;
    const s = smp.sample();
    smp.destroy();
    rmrf(root);
    check(`sampler/${kind}: CPU 60%`, near(s.cpuPercent, 60), s.cpuPercent);
    check(`sampler/${kind}: RAM 75%`, near(s.ramPercent, 75), s.ramPercent);
    check(`sampler/${kind}: busiest disk ~30%`, near(s.diskPercent, 30, 1.5), s.diskPercent);
    check(`sampler/${kind}: download ~1000 B/s`, near(s.netDownBps, 1000, 60), s.netDownBps);
    const want = {intel: 64, amd: 58.3, arm: 50, empty: NaN}[kind];
    check(`sampler/${kind}: CPU temperature ${want}`, Number.isNaN(want) ? Number.isNaN(s.cpuTempC) : near(s.cpuTempC, want, 0.1), `${s.cpuTempC} (${s.cpuTempSource})`);
    if (kind === 'amd') check('sampler/amd: Radeon GPU 37%, 47 C, integrated', s.gpuPercent === 37 && s.gpuTempC === 47 && s.gpus[0].integrated);
}

// #207: the sensor folders are listed once and rescanned every 30 s, or at once when a cached one goes away.
{
    const root = fakeRoot('intel');
    let t = 1000000;
    const smp = new S.Sampler({root, nvidia: false, clock: () => t});
    let s = smp.sample();
    check('topology: first sample reads coretemp', near(s.cpuTempC, 64, 0.1), s.cpuTempC);
    write(root, 'sys/class/hwmon/hwmon3/temp1_input', '66000\n');
    t += 2000; s = smp.sample();
    check('topology: cached inputs are still read every tick', near(s.cpuTempC, 66, 0.1), s.cpuTempC);
    write(root, 'sys/class/drm/card0/device/gpu_busy_percent', '12\n');
    t += 2000; s = smp.sample();
    check('topology: a new folder waits for the next rescan', s.gpus.length === 0, s.gpus.length);
    t += 30000; s = smp.sample();
    check('topology: the rescan after 30 s finds it', s.gpus.length === 1 && s.gpuPercent === 12, s.gpus.length);
    rmrf(`${root}/sys/class/drm/card0`);
    t += 2000; smp.sample();
    t += 2000; s = smp.sample();
    check('topology: a vanished folder triggers a rescan at once', s.gpus.length === 0, s.gpus.length);
    smp.destroy();
    rmrf(root);
}

// ---------------------------------------------------------------- regressions found by the QA bug hunt
// QA: every AMD GPU showed the first amdgpu hwmon chip's temperature.
{
    const root = fakeRoot('empty');
    for (const [card, hw, busy, temp] of [['card0', 'hwmon2', 5, 40000], ['card1', 'hwmon4', 80, 70000]]) {
        write(root, `sys/class/drm/${card}/device/gpu_busy_percent`, `${busy}\n`);
        write(root, `sys/class/drm/${card}/device/hwmon/${hw}/name`, 'amdgpu\n');
        write(root, `sys/class/drm/${card}/device/hwmon/${hw}/temp1_label`, 'edge\n');
        write(root, `sys/class/drm/${card}/device/hwmon/${hw}/temp1_input`, `${temp}\n`);
        write(root, `sys/class/hwmon/${hw}/name`, 'amdgpu\n');
        write(root, `sys/class/hwmon/${hw}/temp1_label`, 'edge\n');
        write(root, `sys/class/hwmon/${hw}/temp1_input`, `${temp}\n`);
    }
    const smp = new S.Sampler({root, nvidia: false});
    const s = smp.sample();
    smp.destroy();
    rmrf(root);
    check('amd: each GPU gets its own temperature', s.gpus.map(g => g.tempC).join() === '40,70', s.gpus.map(g => g.tempC).join());
}
// QA: a network interface that appears between 2 samples added its whole byte history as 1 tick of speed.
check('net: new interface adds no spike',
    (d => d.rx === 2000 && d.tx === 400)(S.netDelta?.(S.parseNetDev('  eth0: 1000 1 0 0 0 0 0 0 500\n'),
        S.parseNetDev('  eth0: 3000 1 0 0 0 0 0 0 900\n  usb0: 5000000000 1 0 0 0 0 0 0 4000000000\n')) || {}));
check('net: removed interface and counter reset add nothing',
    (d => d.rx === 0 && d.tx === 0)(S.netDelta?.(S.parseNetDev('  eth0: 9000 1 0 0 0 0 0 0 900\n  usb0: 50 1 0 0 0 0 0 0 40\n'),
        S.parseNetDev('  eth0: 100 1 0 0 0 0 0 0 10\n')) || {rx: -1}));
// QA: a hand-edited settings file with a non-boolean unit turned on Fahrenheit with no menu item checked.
check('settings: invalid values fall back to defaults',
    (s => s.fahrenheit === false && s.interval === 2 && s.fourth === 'auto')(M.normalizeSettings?.({fahrenheit: 'yes', interval: 3, fourth: 'cpu'}) || {}));
check('settings: valid values kept',
    (s => s.fahrenheit === true && s.interval === 5 && s.fourth === 'disk' && s.download === false)(M.normalizeSettings?.({fahrenheit: true, interval: 5, fourth: 'disk', download: false}) || {}));
check('settings: download speed shown when the key is missing or invalid',
    M.normalizeSettings({}).download === true && M.normalizeSettings({download: 'no'}).download === true && M.normalizeSettings(null).download === true);
// QA: the pill had no accessible name, so screen readers announced nothing.
check('a11y: pill reads as text', M.accessibleText?.(M.buildCells({...M.emptySnapshot(), cpuTempC: 61, cpuPercent: 23, ramPercent: 58})) ===
    'TEMP 61°, CPU 23%, RAM 58%, DISK unknown', M.accessibleText?.(M.buildCells({...M.emptySnapshot(), cpuTempC: 61, cpuPercent: 23, ramPercent: 58})));

// #108: GLib.Dir.open is not introspectable before GLib 2.80, so every /sys folder looked empty on GNOME 42-45.
{
    const root = GLib.dir_make_tmp('hwwidget-ls-XXXXXX');
    write(root, 'b/x', '1');
    write(root, 'a/y', '1');
    check('listDir: lists a folder without GLib.Dir', JSON.stringify(S.listDir(root)) === '["a","b"]', JSON.stringify(S.listDir(root)));
    check('listDir: a missing folder is empty', S.listDir(`${root}/nope`).length === 0);
}

// #109: a long-lived nvidia-smi kept a laptop's sleeping NVIDIA GPU awake. Query only a GPU that is awake on its own.
{
    const root = fakeRoot('intel');
    const dev = 'sys/bus/pci/devices/0000:01:00.0';
    write(root, `${dev}/vendor`, '0x10de\n');
    write(root, `${dev}/class`, '0x030000\n');
    write(root, `${dev}/power/control`, 'auto\n');
    write(root, `${dev}/power/runtime_status`, 'suspended\n');
    const calls = `${root}/smi-calls`;
    const smi = `${root}/nvidia-smi`;
    write(root, 'nvidia-smi', `#!/bin/sh\necho "$*" >> '${calls}'\necho '0, 42, 61, 2048, NVIDIA RTX 4000 Ada Generation Laptop GPU'\n`);
    GLib.spawn_command_line_sync(`chmod +x '${smi}'`);
    let t = 1_000_000;
    const smp = new S.Sampler({root, smi, clock: () => t});
    const ncalls = () => (S.readText(calls) || '').split('\n').filter(Boolean).length;
    const settle = () => {
        const ctx = GLib.MainContext.default();
        const end = GLib.get_monotonic_time() + 3_000_000;
        while (smp._nvBusy && GLib.get_monotonic_time() < end) ctx.iteration(true);
    };
    let s = smp.sample();
    check('nvidia sleep: no nvidia-smi while the GPU sleeps', ncalls() === 0, `${ncalls()} calls`);
    check('nvidia sleep: a sleeping GPU shows 0 % and "asleep"', s.gpuPercent === 0 && /asleep/.test(s.gpus[0]?.name), JSON.stringify(s.gpus));
    write(root, `${dev}/power/runtime_status`, 'active\n');
    t += 2000; s = smp.sample(); settle();
    check('nvidia sleep: no query in the first 25 s awake', ncalls() === 0, `${ncalls()} calls`);
    check('nvidia sleep: an awake GPU is no longer labelled asleep', !/asleep/.test(s.gpus[0]?.name || ''), JSON.stringify(s.gpus));
    t += 25000; smp.sample(); settle();
    check('nvidia sleep: 1 one-shot query after 25 s awake', ncalls() === 1 && !/-lms/.test(S.readText(calls)), S.readText(calls));
    s = smp.sample();
    check('nvidia sleep: the query result is shown', s.gpuPercent === 42 && s.gpuTempC === 61, `${s.gpuPercent} ${s.gpuTempC}`);
    t += 10000; smp.sample(); settle();
    check('nvidia sleep: at most 1 query per 30 s', ncalls() === 1, `${ncalls()} calls`);
    t += 21000; smp.sample(); settle();
    check('nvidia sleep: next query after 30 s', ncalls() === 2, `${ncalls()} calls`);
    smp.destroy();
    // An awake GPU with no reading yet is queried once at start, so the GPU cell and name appear.
    const smp2 = new S.Sampler({root, smi, clock: () => t});
    smp2.sample();
    const ctx = GLib.MainContext.default(), end = GLib.get_monotonic_time() + 3_000_000;
    while (smp2._nvBusy && GLib.get_monotonic_time() < end) ctx.iteration(true);
    s = smp2.sample();
    check('nvidia sleep: an awake GPU is read once at start', ncalls() === 3 && s.gpuPercent === 42, `${ncalls()} calls, ${s.gpuPercent}`);
    t += 2000; s = smp2.sample();
    check('nvidia sleep: between queries the last reading stays', ncalls() === 3 && s.gpuPercent === 42, `${ncalls()} calls, ${s.gpuPercent}`);
    smp2.destroy();
}

// TC-120: k10temp on Ryzen 1000/2000 X and Threadripper lists Tctl (with a fan-control offset) before Tdie.
check('TC-120 temp: k10temp Tdie beats Tctl',
    pick([{name: 'k10temp', temps: [{label: 'Tctl', c: 82}, {label: 'Tdie', c: 55}]}], [])?.source === 'k10temp Tdie');

// TC-127: bridges, bonds and container interfaces repeat bytes a physical NIC already counted.
check('TC-127 net: bridge, bond and podman names skipped without /sys',
    (n => n.rx === 100 && Object.keys(n.ifaces).join() === 'enp3s0')(S.parseNetDev(
        '  enp3s0: 100 1 0 0 0 0 0 0 40\npodman0: 100 1 0 0 0 0 0 0 40\n    br0: 100 1 0 0 0 0 0 0 40\n  bond0: 100 1 0 0 0 0 0 0 40\n')));
{
    const root = fakeRoot('intel');
    write(root, 'sys/class/net/enp3s0/device/vendor', '0x8086\n');
    write(root, 'sys/class/net/mybridge/bridge/hello_time', '200\n');
    write(root, 'proc/net/dev', '  enp3s0: 1000 1 0 0 0 0 0 0 500\nmybridge: 1000 1 0 0 0 0 0 0 500\n');
    const smp = new S.Sampler({root, nvidia: false});
    smp.sample();
    write(root, 'proc/net/dev', '  enp3s0: 3000 1 0 0 0 0 0 0 900\nmybridge: 3000 1 0 0 0 0 0 0 900\n');
    smp._prevAt = smp._clock() - 2000;
    const s = smp.sample();
    smp.destroy();
    rmrf(root);
    check('TC-127 net: only NICs with a device link count', near(s.netDownBps, 1000, 60), s.netDownBps);
}

// TC-129 and TC-131: nvidia-smi that exits, and one that fails.
{
    const wait = (smp, until) => {
        const ctx = GLib.MainContext.default(), end = GLib.get_monotonic_time() + 3_000_000;
        while (!until() && GLib.get_monotonic_time() < end) ctx.iteration(false) || GLib.usleep(10000);
    };
    const root = fakeRoot('intel');
    const calls = `${root}/smi-calls`, smi = `${root}/nvidia-smi`;
    const ncalls = () => (S.readText(calls) || '').split('\n').filter(Boolean).length;
    write(root, 'nvidia-smi', `#!/bin/sh\necho "$*" >> '${calls}'\necho '0, 42, 61, 2048, NVIDIA GeForce RTX 4070'\n`);
    GLib.spawn_command_line_sync(`chmod +x '${smi}'`);
    let t = 1_000_000;
    const smp = new S.Sampler({root, smi, clock: () => t});
    wait(smp, () => smp._nvidia.size && !smp._nvProc);
    let s = smp.sample();
    check('TC-129 nvidia: first reading shown', s.gpuPercent === 42, `${s.gpuPercent}`);
    t += 12000; smp.sample();
    wait(smp, () => smp._nvidia.get(0)?.at === t);
    s = smp.sample();
    check('TC-129 nvidia: nvidia-smi restarted after it exits', ncalls() === 2 && s.gpuPercent === 42, `${ncalls()} calls, gpu ${s.gpuPercent}`);
    smp.destroy();
    rmrf(root);

    const root2 = fakeRoot('intel');
    const dev = 'sys/bus/pci/devices/0000:01:00.0';
    write(root2, `${dev}/vendor`, '0x10de\n');
    write(root2, `${dev}/class`, '0x030000\n');
    write(root2, `${dev}/power/control`, 'auto\n');
    write(root2, `${dev}/power/runtime_status`, 'active\n');
    const calls2 = `${root2}/smi-calls`, smi2 = `${root2}/nvidia-smi`;
    write(root2, 'nvidia-smi', `#!/bin/sh\necho "$*" >> '${calls2}'\necho 'Failed to initialize NVML: Driver/library version mismatch'\nexit 18\n`);
    GLib.spawn_command_line_sync(`chmod +x '${smi2}'`);
    const smp2 = new S.Sampler({root: root2, smi: smi2, clock: () => t});
    for (let i = 0; i < 10; i++) {
        smp2.sample();
        wait(smp2, () => !smp2._nvBusy);
        t += 2000;
    }
    const n2 = (S.readText(calls2) || '').split('\n').filter(Boolean).length;
    check('TC-131 nvidia: a failing query runs at most once per 30 s', n2 === 1, `${n2} calls in 20 s`);
    smp2.destroy();
    rmrf(root2);
}

// TC-252, TC-266 and TC-267: nvidia-smi that stops answering, a reading that stops updating, and a GPU whose
// power/control changes with the power source (TLP).
{
    const wait = until => {
        const ctx = GLib.MainContext.default(), end = GLib.get_monotonic_time() + 3_000_000;
        while (!until() && GLib.get_monotonic_time() < end) ctx.iteration(false) || GLib.usleep(10000);
    };
    const nvRoot = (control, status) => {
        const root = fakeRoot('intel');
        const dev = 'sys/bus/pci/devices/0000:01:00.0';
        if (control) {
            write(root, `${dev}/vendor`, '0x10de\n');
            write(root, `${dev}/class`, '0x030000\n');
            write(root, `${dev}/power/control`, `${control}\n`);
            write(root, `${dev}/power/runtime_status`, `${status}\n`);
        }
        return {root, dev, smi: `${root}/nvidia-smi`, calls: `${root}/smi-calls`};
    };
    const fakeSmi = (r, body) => {
        write(r.root, 'nvidia-smi', `#!/bin/sh\necho "$*" >> '${r.calls}'\n${body}\n`);
        GLib.spawn_command_line_sync(`chmod +x '${r.smi}'`);
    };
    const lines = r => (S.readText(r.calls) || '').split('\n').filter(Boolean);
    let t = 1_000_000;

    // A one-shot query that never returns.
    const a = nvRoot('auto', 'active');
    fakeSmi(a, 'exec sleep 30');
    const smp = new S.Sampler({root: a.root, smi: a.smi, clock: () => t});
    smp.sample();
    wait(() => lines(a).length === 1);
    check('TC-252 nvidia: the first one-shot query starts (and hangs)', smp._nvBusy === true && lines(a).length === 1, `${lines(a).length} calls`);
    t += 12000; smp.sample();
    check('TC-252 nvidia: a one-shot query that hangs is stopped after about 10 s', smp._nvBusy !== true && !smp._nvQuery, `busy ${smp._nvBusy}`);
    t += 20000; smp.sample();
    wait(() => lines(a).length === 2);
    check('TC-252 nvidia: the next query runs 30 s after the hung one', lines(a).length === 2, `${lines(a).length} calls`);
    smp.destroy();
    rmrf(a.root);

    // A stream that prints 1 line, then stops printing without exiting.
    const b = nvRoot();
    fakeSmi(b, "echo '0, 42, 61, 3000, NVIDIA RTX 4000 Ada Generation'\nexec sleep 30");
    const smp2 = new S.Sampler({root: b.root, smi: b.smi, clock: () => t});
    wait(() => smp2._nvidia.size);
    let s = smp2.sample();
    check('TC-252 nvidia: the stream reading is shown', s.gpuPercent === 42, `${s.gpuPercent}`);
    for (let i = 0; i < 4; i++) {
        t += 2000;
        smp2.sample();
    }
    check('TC-252 nvidia: a stream that stops printing is stopped after 3 intervals', smp2._nvProc === null);
    t += 10000; smp2.sample();
    wait(() => lines(b).length === 2);
    check('TC-252 nvidia: the stopped stream restarts with the back-off', lines(b).length === 2 && smp2._nvProc !== null, `${lines(b).length} calls`);
    smp2.destroy();
    rmrf(b.root);

    // The first query reads 95 % / 83 C, every later one fails (driver updated without a reboot).
    const c = nvRoot('auto', 'active');
    fakeSmi(c, `if [ -e '${c.root}/done' ]; then echo 'Failed to initialize NVML: Driver/library version mismatch'; exit 18; fi\n` +
        `touch '${c.root}/done'\necho '0, 95, 83, 7000, NVIDIA GeForce RTX 4070 Laptop GPU'`);
    const smp3 = new S.Sampler({root: c.root, smi: c.smi, clock: () => t});
    smp3.sample();
    wait(() => !smp3._nvBusy);
    s = smp3.sample();
    check('TC-266 nvidia: the first query is shown', s.gpuPercent === 95 && s.gpuTempC === 83, `${s.gpuPercent} ${s.gpuTempC}`);
    for (let i = 0; i < 60; i++) {
        t += 2000;
        s = smp3.sample();
        wait(() => !smp3._nvBusy);
    }
    check('TC-266 nvidia: a reading with no successful query for 40 s drops out', s.gpus.length === 0, `${lines(c).length} calls, ${JSON.stringify(s.gpus)}`);
    check('TC-266 nvidia: failing queries still run every 30 s', lines(c).length >= 4, `${lines(c).length} calls in 120 s`);
    smp3.destroy();
    rmrf(c.root);

    // power/control "on" at log-in (on AC), "auto" after unplugging, "on" again.
    const d = nvRoot('on', 'active');
    fakeSmi(d, "echo '0, 1, 45, 300, NVIDIA GeForce RTX 3050 Laptop GPU'\ncase \"$*\" in *-lms*) exec sleep 30;; esac");
    const lms = () => lines(d).filter(l => /-lms/.test(l)).length;
    const smp4 = new S.Sampler({root: d.root, smi: d.smi, clock: () => t});
    smp4.sample();
    wait(() => lms() === 1);
    check('TC-267 nvidia mode: power/control "on" at start uses the stream', smp4._nvProc !== null && lms() === 1, `${lms()} streams`);
    write(d.root, `${d.dev}/power/control`, 'auto\n');
    write(d.root, `${d.dev}/power/runtime_status`, 'suspended\n');
    t += 30000; smp4.sample();
    check('TC-267 nvidia mode: "auto" later stops the stream at the next rescan', smp4._nvProc === null && smp4._nvPm?.length === 1,
        `stream ${smp4._nvProc ? 'running' : 'stopped'}`);
    const before = lines(d).length;
    for (let i = 0; i < 10; i++) {
        t += 2000;
        s = smp4.sample();
    }
    check('TC-267 nvidia mode: the sleeping GPU is not woken after the switch', lines(d).length === before && s.gpuPercent === 0 && /asleep/.test(s.gpuName),
        `${lines(d).length - before} new calls, ${s.gpuPercent} ${s.gpuName}`);
    write(d.root, `${d.dev}/power/control`, 'on\n');
    write(d.root, `${d.dev}/power/runtime_status`, 'active\n');
    t += 30000; smp4.sample();
    wait(() => lms() === 2);
    check('TC-267 nvidia mode: "on" again restarts the stream', smp4._nvProc !== null && lms() === 2 && !smp4._nvPm?.length, `${lms()} streams`);
    smp4.destroy();
    rmrf(d.root);
}

// TC-268: an AMD dGPU in runtime suspend is shown asleep and its files are not read (reads would wake it).
{
    const root = fakeRoot('amd');
    const card = 'sys/class/drm/card2/device';
    write(root, `${card}/gpu_busy_percent`, '30\n');
    write(root, `${card}/mem_info_vram_total`, `${8 * 1024 ** 3}\n`);
    write(root, `${card}/mem_info_vram_used`, `${1024 ** 3}\n`);
    write(root, `${card}/power/runtime_status`, 'suspended\n');
    for (const d of [`${card}/hwmon/hwmon5`, 'sys/class/hwmon/hwmon5']) {
        write(root, `${d}/name`, 'amdgpu\n');
        write(root, `${d}/temp1_label`, 'edge\n');
        write(root, `${d}/temp1_input`, '50000\n');
    }
    const smp = new S.Sampler({root, nvidia: false});
    let s = smp.sample();
    const dg = s.gpus.find(g => /asleep/.test(g.name));
    check('TC-268 amd sleep: a suspended dGPU shows asleep at 0 %', s.gpus.length === 2 && dg?.percent === 0, JSON.stringify(s.gpus));
    check('TC-268 amd sleep: no temperature or VRAM for a suspended dGPU', dg && Number.isNaN(dg.tempC) && Number.isNaN(dg.memUsedGB), JSON.stringify(dg));
    check('TC-268 amd sleep: the awake iGPU is still read', s.gpus.some(g => g.percent === 37 && g.tempC === 47), JSON.stringify(s.gpus));
    check('TC-268 amd sleep: its temperature is not read through /sys/class/hwmon either', !smp._topo.hwmon.some(l => l.dir.endsWith('/hwmon5')),
        smp._topo.hwmon.map(l => l.dir).join());
    write(root, `${card}/power/runtime_status`, 'active\n');
    s = smp.sample();
    check('TC-268 amd sleep: once awake it is read again', s.gpus.some(g => g.percent === 30 && g.tempC === 50 && !/asleep/.test(g.name)), JSON.stringify(s.gpus));
    smp.destroy();
    rmrf(root);
}

// TC-269: a 2-socket PC has 1 coretemp chip per CPU package; TEMP shows the hotter package.
{
    const s0 = {name: 'coretemp', temps: [{label: 'Package id 0', c: 52}, {label: 'Core 0', c: 50}]};
    const s1 = {name: 'coretemp', temps: [{label: 'Package id 1', c: 91}, {label: 'Core 0', c: 89}]};
    check('TC-269 temp: the hotter of 2 CPU packages', pick([s0, s1], [])?.source === 'coretemp Package id 1', JSON.stringify(pick([s0, s1], [])));
    check('TC-269 temp: folder order does not matter', pick([s1, s0], [])?.c === 91 && pick([s0, s1], [])?.c === 91);
    check('TC-269 temp: a Tdie on any chip still beats a hotter Tctl',
        pick([{name: 'k10temp', temps: [{label: 'Tctl', c: 95}]}, {name: 'k10temp', temps: [{label: 'Tdie', c: 60}]}], [])?.source === 'k10temp Tdie');
    const root = fakeRoot('empty');
    for (const [h, id, c] of [['hwmon10', 1, 91000], ['hwmon2', 0, 52000]]) {
        write(root, `sys/class/hwmon/${h}/name`, 'coretemp\n');
        write(root, `sys/class/hwmon/${h}/temp1_label`, `Package id ${id}\n`);
        write(root, `sys/class/hwmon/${h}/temp1_input`, `${c}\n`);
    }
    const smp = new S.Sampler({root, nvidia: false});
    const s = smp.sample();
    smp.destroy();
    rmrf(root);
    check('TC-269 temp: 2-socket sampler shows 91 C from hwmon10', near(s.cpuTempC, 91, 0.1), `${s.cpuTempC} (${s.cpuTempSource})`);
}

// TC-270: a sensor file that exists but cannot be read waits for the 30 s rescan instead of rescanning every tick.
for (const kind of ['hwmon input', 'thermal zone']) {
    const root = fakeRoot('intel');
    if (kind === 'hwmon input') {   // a folder where a file should be: listed, never readable
        write(root, 'sys/class/hwmon/hwmon3/temp3_label', 'Core 1\n');
        GLib.mkdir_with_parents(`${root}/sys/class/hwmon/hwmon3/temp3_input`, 0o755);
    } else {
        write(root, 'sys/class/thermal/thermal_zone1/type', 'iwlwifi_1\n');
        GLib.mkdir_with_parents(`${root}/sys/class/thermal/thermal_zone1/temp`, 0o755);
    }
    let t = 1_000_000;
    const smp = new S.Sampler({root, nvidia: false, clock: () => t});
    let s = smp.sample();
    const topo = smp._topo;
    for (let i = 0; i < 3; i++) {
        t += 2000;
        s = smp.sample();
    }
    check(`TC-270 topology: an unreadable ${kind} does not rescan every tick`, smp._topo === topo);
    check(`TC-270 topology: the other sensors are still read (${kind})`, near(s.cpuTempC, 64, 0.1), `${s.cpuTempC}`);
    t += 30000; smp.sample();
    check(`TC-270 topology: the 30 s rescan still runs (${kind})`, smp._topo !== topo);
    const topo2 = smp._topo;
    rmrf(`${root}/sys/class/hwmon/hwmon0`);
    t += 2000; smp.sample();
    t += 2000; smp.sample();
    check(`TC-270 topology: a removed sensor folder still rescans at once (${kind})`, smp._topo !== topo2 && !smp._topo.hwmon.some(l => l.dir.endsWith('/hwmon0')));
    smp.destroy();
    rmrf(root);
}

// TC-264: the sampler times its rescans and rates with the monotonic clock, not the wall clock.
{
    const root = fakeRoot('intel');
    const smp = new S.Sampler({root, nvidia: false});
    check('TC-264 clock: the default sampler clock is monotonic', Math.abs(smp._clock() - GLib.get_monotonic_time() / 1000) < 1000, `${smp._clock()}`);
    const realNow = Date.now;
    try {
        smp.sample();
        Date.now = () => realNow() - 3600_000;   // the wall clock steps back 1 h
        write(root, 'proc/diskstats', '   8       0 sda 100 0 0 0 0 0 0 0 0 1500 0\n');
        write(root, 'proc/net/dev', '  eth0: 3000 1 0 0 0 0 0 0 900 0 0 0 0 0 0 0\n');
        const s = smp.sample();
        check('TC-264 clock: a 1 h backward wall-clock step keeps the 30 s rescan', smp._topo.until - smp._clock() <= 30000,
            `next rescan in ${Math.round((smp._topo.until - smp._clock()) / 1000)} s`);
        check('TC-264 clock: the tick after a backward step still has disk and download', Number.isFinite(s.diskPercent) && Number.isFinite(s.netDownBps),
            `disk ${s.diskPercent}, down ${s.netDownBps}`);
    } finally {
        Date.now = realNow;
    }
    smp.destroy();
    rmrf(root);
}

// TC-259: light-mode labels and values reach 4.5:1 text contrast (WCAG) on the light pill and card backgrounds.
{
    const lin = v => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
    const lum = hex => (([r, g, b]) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b))(M.hexToRgb(hex));
    const worst = cols => {
        let w = {r: Infinity};
        for (const c of cols) {
            for (const bg of ['#FFFFFF', '#EEF1F8', '#F3F5FB']) {
                const r = (Math.max(lum(c), lum(bg)) + 0.05) / (Math.min(lum(c), lum(bg)) + 0.05);
                if (r < w.r) w = {r, c, bg};
            }
        }
        return w;
    };
    const show = w => `${w.r.toFixed(2)}:1 for ${w.c} on ${w.bg}`;
    const L = M.PALETTES.light, C = M.COLORS;
    const tint = M.labelTint || (a => a);
    let w = worst([C.cpu, C.ram, C.gpu, C.disk, C.net].flat().map(a => tint(a, L)));
    check('TC-259 contrast: light label tint of every accent end', w.r >= 4.5, show(w));
    const busy = {...M.emptySnapshot(), cpuPercent: 10, ramPercent: 10, gpuPercent: 10, gpus: [{name: 'GPU', percent: 10}]};
    w = worst([...M.buildCells(busy, {download: true, palette: L}), ...M.buildCells(busy, {fourth: 'disk', palette: L})].map(c => c.labelColor));
    check('TC-259 contrast: light pill labels as drawn (CPU RAM GPU DISK DOWN)', w.r >= 4.5, show(w));
    const temps = [];
    for (let c = 20; c <= 100; c += 0.5) temps.push(c);
    w = worst([...temps, NaN].map(c => M.buildCells({...M.emptySnapshot(), cpuTempC: c}, {palette: L})[0].labelColor));
    check('TC-259 contrast: light TEMP label over the whole ramp and unknown grey', w.r >= 4.5, show(w));
    w = worst(temps.filter(c => M.tempLevel(c, false) === 0).flatMap(c => [
        M.buildCells({...M.emptySnapshot(), cpuTempC: c}, {palette: L})[0].valueColor,
        M.cardRows({...M.emptySnapshot(), cpuTempC: c}, {palette: L})[0].valueColor]));
    check('TC-259 contrast: light TEMP values below 80 C (pill and card)', w.r >= 4.5, show(w));
    w = worst([L.warn, L.crit]);
    check('TC-259 contrast: light warn and crit', w.r >= 4.5, show(w));
    w = worst([L.pinned || '#A78BFA']);
    check('TC-259 contrast: light "Pinned"', w.r >= 4.5, show(w));
    const surf = new Cairo.ImageSurface(Cairo.Format.ARGB32, 400, 400);
    const cr = new Cairo.Context(surf);
    let drawn = '';
    try {
        R.drawCard(cr, M.cardRows(M.emptySnapshot(), {palette: L}), L, {pinned: true});
    } catch (e) {
        drawn = `${e}`;
    }
    cr.$dispose();
    check('TC-259 contrast: a pinned light card draws with the light "Pinned" colour', drawn === '', drawn);
    check('TC-259 contrast: dark mode unchanged', M.PALETTES.dark.warn === '#FCD34D' && M.PALETTES.dark.crit === '#FF8FA3' && M.PALETTES.dark.pinned === '#A78BFA');
}

// TC-135: the colour is judged on the number shown.
{
    const c = M.buildCells({...M.emptySnapshot(), cpuTempC: 79.6, cpuPercent: 74.6, ramPercent: 89.6});
    check('TC-135 level: 74.6 % CPU shows 75% in amber', c[1].value === '75%' && c[1].level === 1, `${c[1].value} ${c[1].level}`);
    check('TC-135 level: 89.6 % RAM shows 90% in red', c[2].value === '90%' && c[2].level === 2, `${c[2].value} ${c[2].level}`);
    check('TC-135 level: 79.6 C shows 80° in amber', M.level(79.6, M.THRESHOLDS.temp) === 1 && M.fmt.temp(79.6) === '80°');
}

// QA-01: in °F the colour is judged on the °F number shown, so two readings that both show 175° look the same.
{
    const f1 = M.buildCells({...M.emptySnapshot(), cpuTempC: 79.4}, {fahrenheit: true})[0];
    const f2 = M.buildCells({...M.emptySnapshot(), cpuTempC: 79.6}, {fahrenheit: true})[0];
    check('QA-01 level: 79.4 and 79.6 C both show 175° in °F and share one colour', f1.value === '175°' && f2.value === '175°' && f1.level === 0 && f2.level === 0, `${f2.value} ${f2.level}`);
    const r = M.cardRows({...M.emptySnapshot(), cpuTempC: 79.6}, {fahrenheit: true})[0];
    check('QA-01 level: the °F card row is not amber at 175°F', r.valueColor !== M.PALETTES.dark.warn);
    check('QA-01 level: 176°F is amber, 194°F is red', M.tempLevel(80, true) === 1 && M.tempLevel(90, true) === 2 && M.tempLevel(89.6, false) === 2);
}

// QA-02: the card's rates and sizes roll over at the shown value: never "1000 KB/s" or "10.0 GB".
check('QA-02 fmt: rate rolls over at the shown value', M.fmt.rate(1023999) === '1.0 MB/s' && M.fmt.rate(999.6) === '1.0 KB/s' && M.fmt.rate(10199) === '10 KB/s' && M.fmt.rate(9.94 * 1024) === '9.9 KB/s' && M.fmt.rate(999) === '999 B/s', M.fmt.rate(1023999));
check('QA-02 fmt: 9.96 GB shows 10 GB', M.fmt.gb(9.96) === '10 GB' && M.fmt.gb(9.94) === '9.9 GB', M.fmt.gb(9.96));

// ---------------------------------------------------------------- cells
const hot = {...M.emptySnapshot(), cpuTempC: 93, cpuPercent: 96, ramPercent: 91, gpuPercent: 97, gpus: [{name: 'GPU', percent: 97, tempC: 86, memUsedGB: 7}], gpuTempC: 86};
let cells = M.buildCells(hot);
check('cells: TEMP CPU RAM GPU', cells.map(c => c.label).join() === 'TEMP,CPU,RAM,GPU');
check('cells: hot levels all critical', cells.every(c => c.level === 2));
cells = M.buildCells({...M.emptySnapshot(), cpuPercent: 3});
check('cells: no GPU -> DISK, unknown values are dashes', cells[3].label === 'DISK' && cells[0].value === M.DASH && cells[1].value === '3%');
check('cells: GPU temperature fallback is labelled', M.buildCells({...M.emptySnapshot(), gpuTempC: 50})[0].label === 'GPU°');
check('fmt: 20 C -> 68 F', M.fmt.temp(20, true) === '68°');
check('fmt: rate', M.fmt.rate(1536) === '1.5 KB/s' && M.fmt.rate(48e6) === '46 MB/s');
{
    const rs = M.fmt.rateShort, K = 1024, MB = 1048576;
    check('rate short: K edges', rs(0) === '0K' && rs(999 * K) === '999K' && rs(999.4 * K) === '999K', rs(999.4 * K));
    check('rate short: K to M at 999.5 K', rs(999.5 * K) === '1.0M' && rs(1023 * K) === '1.0M' && rs(1024 * K) === '1.0M', rs(999.5 * K));
    check('rate short: one decimal below 9.95 M', rs(9.94 * MB) === '9.9M' && rs(9.96 * MB) === '10M' && rs(999 * MB) === '999M', rs(9.96 * MB));
    check('rate short: G, negative and unknown', rs(1000 * MB) === '1.0G' && rs(-5) === '0K' && rs(NaN) === M.DASH);
    const dc = M.buildCells({...M.emptySnapshot(), cpuTempC: 61, netDownBps: 182e3}, {download: true});
    check('cells: download cell is 5th, never amber or red', dc.length === 5 && dc[4].label === 'DOWN' && dc[4].value === '178K' && dc[4].level === 0, dc[4]?.value);
    check('cells: download cell off by option', M.buildCells(M.emptySnapshot(), {download: false}).length === 4);
    check('a11y: download cell speaks the full rate',
        M.accessibleText(M.buildCells({...M.emptySnapshot(), netDownBps: 1258291}, {download: true})).endsWith(', DOWN 1.2 MB/s'));
    check('a11y: unknown download rate', M.accessibleText(M.buildCells(M.emptySnapshot(), {download: true})).endsWith(', DOWN unknown'));
}

// #204: edge cases of the shared formatting helpers, the same ones macos/tests/main.swift checks.
check('fmt: pct clamps and rounds', M.fmt.pct(-3) === '0%' && M.fmt.pct(99.5) === '100%' && M.fmt.pct(150) === '100%' && M.fmt.pct(NaN) === M.DASH);
check('fmt: tempLong adds the unit', M.fmt.tempLong(20) === '20°C' && M.fmt.tempLong(20, true) === '68°F' && M.fmt.tempLong(NaN) === M.DASH);
check('fmt: gb has 1 decimal below 10', M.fmt.gb(0) === '0.0 GB' && M.fmt.gb(9.94) === '9.9 GB' && M.fmt.gb(10) === '10 GB' && M.fmt.gb(NaN) === M.DASH);
check('netFrac: 0 -> 0, 1 Gbit/s -> 1, negative -> 0, unknown -> NaN',
    M.netFrac(0) === 0 && near(M.netFrac(125e6), 1, 1e-9) && M.netFrac(-5) === 0 && Number.isNaN(M.netFrac(NaN)));
check('netFrac: grows with the rate', M.netFrac(1e3) < M.netFrac(1e6) && M.netFrac(1e6) < M.netFrac(1e8));
check('tempColor: ends of the ramp and unknown', M.tempColor(20) === '#22D3EE' && M.tempColor(35) === '#22D3EE' && M.tempColor(95) === '#F43F5E' && M.tempColor(120) === '#F43F5E' && M.tempColor(NaN) === '#94A3B8');
check('tempColor: ramp stops are exact', M.tempColor(50).toUpperCase() === '#34D399' && M.tempColor(75).toUpperCase() === '#FACC15', M.tempColor(50));

// ---------------------------------------------------------------- render PNGs with the real drawing code
const out = ARGV[0] || GLib.get_tmp_dir();
GLib.mkdir_with_parents(out, 0o755);
const normal = {...M.emptySnapshot(), cpuName: 'AMD Ryzen 7 7840U w/ Radeon 780M Graphics', cpuTempC: 61, cpuTempSource: 'k10temp Tctl',
    cpuPercent: 23, ramPercent: 58, ramUsedGB: 18.6, ramTotalGB: 31.8, diskPercent: 4, netDownBps: 182e3, netUpBps: 21e3,
    gpus: [{name: 'NVIDIA GeForce RTX 4070', percent: 12, tempC: 51, memUsedGB: 1.2}], gpuPercent: 12, gpuTempC: 51};
const cases = {normal, hot: {...normal, ...hot, gpus: hot.gpus, netDownBps: 48e6}, unknown: M.emptySnapshot()};
for (const [name, snap] of Object.entries(cases)) {
    for (const scale of [1, 2]) {
        const cl = M.buildCells(snap, {download: true});
        const sz = R.pillSize(cl.length, scale);
        const surf = new Cairo.ImageSurface(Cairo.Format.ARGB32, sz.w + 40 * scale, Math.ceil(40 * scale));
        const cr = new Cairo.Context(surf);
        cr.setSourceRGB(0.11, 0.11, 0.11);   // GNOME top bar
        cr.paint();
        cr.translate(20 * scale, (40 * scale - sz.h) / 2);
        cr.scale(scale, scale);
        R.drawPill(cr, cl, M.PALETTES.dark);
        cr.$dispose();
        surf.writeToPNG(`${out}/gnome-pill-${name}-${scale * 100}.png`);
    }
    for (const theme of ['dark', 'light']) {
        const p = M.PALETTES[theme];
        const rows = M.cardRows(snap, {palette: p});
        const cs = R.cardSize(rows.length);
        const surf = new Cairo.ImageSurface(Cairo.Format.ARGB32, cs.w + 24, cs.h + 24);
        const cr = new Cairo.Context(surf);
        if (theme === 'dark') cr.setSourceRGB(0.14, 0.14, 0.16);
        else cr.setSourceRGB(0.96, 0.96, 0.97);
        cr.paint();
        cr.translate(12, 12);
        R.drawCard(cr, rows, p, {footer: 'Updates every 2 s · no root needed'});
        cr.$dispose();
        surf.writeToPNG(`${out}/gnome-card-${theme}-${name}.png`);
    }
}
check('render: PNGs written', GLib.file_test(`${out}/gnome-pill-normal-200.png`, GLib.FileTest.EXISTS));

print(`${pass} passed, ${fail} failed`);
System.exit(fail ? 1 : 0);
