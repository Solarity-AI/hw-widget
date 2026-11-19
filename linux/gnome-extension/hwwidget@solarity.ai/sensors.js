// HW Widget - Linux sensors. Reads /proc and /sys under an injectable root so tests can use fixture trees.
// No root rights needed. NVIDIA GPUs come from nvidia-smi: one long-lived `-lms` process on desktops, and on
// laptops whose GPU can sleep (runtime D3) only short queries while the GPU is already awake.
import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import {emptySnapshot} from './model.js';

const decoder = new TextDecoder();

export function readText(path) {
    try {
        const [ok, bytes] = GLib.file_get_contents(path);
        return ok ? decoder.decode(bytes) : null;
    } catch {
        return null;
    }
}

export function listDir(path) {
    const out = [];
    try {
        // Gio, not GLib.Dir: GLib.Dir.open is only introspectable from GLib 2.80 (GNOME 46).
        const en = Gio.File.new_for_path(path).enumerate_children('standard::name', Gio.FileQueryInfoFlags.NONE, null);
        let info;
        while ((info = en.next_file(null)) !== null) out.push(info.get_name());
        en.close(null);
    } catch {}
    return out.sort();
}

// One hwmon chip folder -> {name, dir, temps: [{label, c}]}
export function readChip(dir) {
    const name = (readText(`${dir}/name`) || '').trim();
    const temps = listDir(dir).filter(f => /^temp\d+_input$/.test(f)).map(f => {
        const label = (readText(`${dir}/${f.replace('_input', '_label')}`) || '').trim();
        return {label, c: Number((readText(`${dir}/${f}`) || '').trim()) / 1000};
    });
    return {name, dir, temps};
}

// ---------------------------------------------------------------- pure parsers (unit-tested)
export function parseCpuTimes(stat) {
    const line = (stat || '').split('\n').find(l => l.startsWith('cpu '));
    if (!line) return null;
    const v = line.trim().split(/\s+/).slice(1).map(Number);
    const idle = (v[3] || 0) + (v[4] || 0);
    const total = v.slice(0, 8).reduce((a, b) => a + (b || 0), 0);
    return {idle, total};
}

export function cpuPercent(prev, cur) {
    if (!prev || !cur) return NaN;
    const dt = cur.total - prev.total, di = cur.idle - prev.idle;
    if (dt <= 0) return NaN;
    return Math.max(0, Math.min(100, 100 * (1 - di / dt)));
}

export function parseMeminfo(text) {
    const kv = {};
    for (const l of (text || '').split('\n')) {
        const m = /^(\w+):\s+(\d+)/.exec(l);
        if (m) kv[m[1]] = Number(m[2]) * 1024;
    }
    if (!kv.MemTotal) return null;
    const avail = kv.MemAvailable ?? (kv.MemFree + (kv.Buffers || 0) + (kv.Cached || 0));
    const gb = 1024 ** 3;
    return {totalGB: kv.MemTotal / gb, usedGB: (kv.MemTotal - avail) / gb, percent: 100 * (kv.MemTotal - avail) / kv.MemTotal};
}

export function cpuNameFrom(cpuinfo) {
    const t = cpuinfo || '';
    const m = /^model name\s*:\s*(.+)$/m.exec(t) || /^Hardware\s*:\s*(.+)$/m.exec(t) || /^Model\s*:\s*(.+)$/m.exec(t);
    return m ? m[1].trim().replace(/\s+/g, ' ') : '';
}

const DISK_RE = /^(sd[a-z]+|hd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme\d+n\d+|mmcblk\d+)$/;

export function parseDiskstats(text) {
    const out = {};
    for (const l of (text || '').split('\n')) {
        const f = l.trim().split(/\s+/);
        if (f.length < 13 || !DISK_RE.test(f[2])) continue;
        out[f[2]] = Number(f[12]);   // io_ticks: ms spent doing I/O
    }
    return out;
}

export function diskBusy(prev, cur, elapsedMs) {
    if (!prev || !cur || elapsedMs <= 0) return NaN;
    let best = NaN;
    for (const [k, v] of Object.entries(cur)) {
        if (!(k in prev)) continue;
        const b = Math.max(0, Math.min(100, 100 * (v - prev[k]) / elapsedMs));
        if (!(b <= best)) best = b;
    }
    return best;
}

const NET_SKIP = /^(lo|docker\d*|veth|br-|virbr|vnet|tap|tun|wg|zt|tailscale|cni|flannel|kube|lxc|lxd)/;

export function parseNetDev(text) {
    let rx = 0, tx = 0, n = 0;
    const ifaces = {};
    for (const l of (text || '').split('\n')) {
        const m = /^\s*([^:\s]+):\s*(.*)$/.exec(l);
        if (!m || NET_SKIP.test(m[1])) continue;
        const f = m[2].trim().split(/\s+/).map(Number);
        ifaces[m[1]] = {rx: f[0] || 0, tx: f[8] || 0};
        rx += f[0] || 0;
        tx += f[8] || 0;
        n++;
    }
    return n ? {rx, tx, ifaces} : null;
}

// Bytes moved between 2 parseNetDev samples, counted per interface: an interface that appears or
// disappears between the samples, or whose counter went backwards (driver reset), adds nothing.
export function netDelta(prev, cur) {
    const d = {rx: 0, tx: 0};
    if (!prev || !cur) return d;
    for (const [name, c] of Object.entries(cur.ifaces || {})) {
        const p = (prev.ifaces || {})[name];
        if (!p) continue;
        if (c.rx >= p.rx) d.rx += c.rx - p.rx;
        if (c.tx >= p.tx) d.tx += c.tx - p.tx;
    }
    return d;
}

// hwmon chips in preference order, with the sensor label to prefer inside each chip.
const CPU_CHIPS = [
    ['coretemp', /^Package id/i], ['k10temp', /^(Tctl|Tdie)$/i], ['zenpower', /^(Tdie|Tctl)$/i],
    ['cpu_thermal', null], ['cpu-thermal', null], ['soc_thermal', null], ['nct6775', /^(CPUTIN|PECI|SMBUSMASTER)/i],
    ['it87', null], ['acpitz', null],
];

// chips: [{name, temps: [{label, c}]}] ; zones: [{type, c}]  ->  {c, source} | null
export function pickCpuTemp(chips, zones) {
    const ok = c => Number.isFinite(c) && c > 5 && c < 125;
    for (const [name, pref] of CPU_CHIPS) {
        for (const chip of chips.filter(x => x.name === name)) {
            const temps = chip.temps.filter(t => ok(t.c));
            if (!temps.length) continue;
            const hit = pref ? temps.find(t => pref.test(t.label || '')) : null;
            const t = hit || temps.reduce((a, b) => (b.c > a.c ? b : a));
            return {c: t.c, source: `${name}${t.label ? ` ${t.label}` : ''}`};
        }
    }
    const zonePref = [/x86_pkg_temp/, /cpu/i, /soc/i, /acpitz/];
    for (const re of zonePref) {
        const z = zones.filter(x => re.test(x.type) && ok(x.c));
        if (z.length) {
            const t = z.reduce((a, b) => (b.c > a.c ? b : a));
            return {c: t.c, source: `thermal zone ${t.type}`};
        }
    }
    return null;
}

export function parseNvidiaLine(line) {
    // index, utilization.gpu, temperature.gpu, memory.used [MiB], name
    const f = (line || '').split(',').map(x => x.trim());
    if (f.length < 5 || !/^\d+$/.test(f[0])) return null;
    const num = x => (/^\d+(\.\d+)?$/.test(x) ? Number(x) : NaN);
    return {index: Number(f[0]), name: f.slice(4).join(','), percent: num(f[1]), tempC: num(f[2]), memUsedGB: num(f[3]) / 1024, integrated: false};
}

// ---------------------------------------------------------------- the sampler
export class Sampler {
    // smi: path of nvidia-smi (default: from PATH); clock: ms timestamps, injectable for tests.
    constructor({root = '/', nvidia = true, smi = null, clock = () => Date.now()} = {}) {
        this._clock = clock;
        this.root = root.endsWith('/') ? root : `${root}/`;
        this._prevCpu = null;
        this._prevDisk = null;
        this._prevNet = null;
        this._prevAt = 0;
        this._nvidia = new Map();
        this._nvProc = null;
        this._cpuName = cpuNameFrom(readText(this._p('proc/cpuinfo')));
        if (nvidia) this._startNvidia(smi);
    }

    _p(rel) {
        return this.root + rel;
    }

    // Runtime-PM status files of NVIDIA display devices that may sleep. Reading them never wakes the GPU.
    _nvidiaPm() {
        const base = this._p('sys/bus/pci/devices');
        return listDir(base).filter(d => (readText(`${base}/${d}/vendor`) || '').trim() === '0x10de' &&
            (readText(`${base}/${d}/class`) || '').trim().startsWith('0x03') &&
            (readText(`${base}/${d}/power/control`) || '').trim() === 'auto')
            .map(d => `${base}/${d}/power/runtime_status`);
    }

    _startNvidia(smi) {
        smi ||= GLib.find_program_in_path('nvidia-smi');
        if (!smi) return;
        this._smi = smi;
        this._nvCancel = new Gio.Cancellable();
        this._nvPm = this._nvidiaPm();
        // A GPU that can sleep would never sleep with nvidia-smi holding it open, and 1 query keeps it awake
        // about 21 s. So query only once it has been awake on its own for 25 s, at most every 30 s.
        if (this._nvPm.length) {
            this._nvNext = 0;
            this._nvAwakeSince = 0;
            return;
        }
        try {
            this._nvProc = Gio.Subprocess.new(
                [smi, '--query-gpu=index,utilization.gpu,temperature.gpu,memory.used,name', '--format=csv,noheader,nounits', '-lms', '2000'],
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
            this._nvStream = new Gio.DataInputStream({base_stream: this._nvProc.get_stdout_pipe(), close_base_stream: true});
            this._readNvidia();
        } catch {
            this._nvProc = null;
        }
    }

    _readNvidia() {
        this._nvStream.read_line_async(GLib.PRIORITY_LOW, this._nvCancel, (stream, res) => {
            let line;
            try {
                [line] = stream.read_line_finish_utf8(res);
            } catch {
                return;
            }
            if (line === null) return;   // nvidia-smi exited
            const g = parseNvidiaLine(line);
            if (g) {
                g.at = this._clock();
                this._nvidia.set(g.index, g);
            }
            this._readNvidia();
        });
    }

    // Sleep-aware polling for GPUs with runtime power management (see _startNvidia).
    _pollNvidia(now) {
        if (!this._smi || !this._nvPm?.length) return;
        if (this._nvPm.every(f => (readText(f) || '').trim() === 'suspended')) {
            if (!this._nvidia.size) this._nvidia.set(0, {index: 0, name: 'NVIDIA GPU', integrated: false});
            for (const g of this._nvidia.values())
                Object.assign(g, {percent: 0, tempC: NaN, memUsedGB: NaN, asleep: true, at: now});
            this._nvAwakeSince = 0;
            return;
        }
        this._nvAwakeSince ||= now;
        // Between queries an awake GPU keeps its last reading (0 % right after sleep), so the pill does not
        // flip between GPU and DISK. The very first reading is taken at once to learn the GPU's name.
        for (const g of this._nvidia.values()) Object.assign(g, {asleep: false, at: now});
        const first = !this._nvidia.size;
        if (this._nvBusy || (!first && (now < this._nvNext || now - this._nvAwakeSince < 25000))) return;
        this._nvBusy = true;
        this._nvNext = now + 30000;
        try {
            const proc = Gio.Subprocess.new(
                [this._smi, '--query-gpu=index,utilization.gpu,temperature.gpu,memory.used,name', '--format=csv,noheader,nounits'],
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
            proc.communicate_utf8_async(null, this._nvCancel, (pr, res) => {
                this._nvBusy = false;
                try {
                    const [, out] = pr.communicate_utf8_finish(res);
                    for (const line of (out || '').split('\n')) {
                        const g = parseNvidiaLine(line);
                        if (g) {
                            g.at = this._clock();
                            this._nvidia.set(g.index, g);
                        }
                    }
                } catch {}
            });
        } catch {
            this._nvBusy = false;
        }
    }

    _hwmon() {
        const base = this._p('sys/class/hwmon');
        return listDir(base).map(h => readChip(`${base}/${h}`));
    }

    _zones() {
        const base = this._p('sys/class/thermal');
        return listDir(base).filter(z => z.startsWith('thermal_zone')).map(z => ({
            type: (readText(`${base}/${z}/type`) || '').trim(),
            c: Number((readText(`${base}/${z}/temp`) || '').trim()) / 1000,
        }));
    }

    _amdGpus(chips) {
        const base = this._p('sys/class/drm');
        const out = [];
        for (const card of listDir(base).filter(c => /^card\d+$/.test(c))) {
            const busy = readText(`${base}/${card}/device/gpu_busy_percent`);
            if (busy === null) continue;
            const g = {name: 'AMD Radeon GPU', percent: Number(busy.trim()), tempC: NaN, memUsedGB: NaN, integrated: false};
            const vram = readText(`${base}/${card}/device/mem_info_vram_used`);
            if (vram !== null) g.memUsedGB = Number(vram.trim()) / 1024 ** 3;
            const total = readText(`${base}/${card}/device/mem_info_vram_total`);
            if (total !== null && Number(total.trim()) < 1024 ** 3) g.integrated = true;
            // Each card's own sensor sits under its device; the first amdgpu chip is only a fallback
            // for kernels that don't link it there.
            const own = listDir(`${base}/${card}/device/hwmon`).map(h => readChip(`${base}/${card}/device/hwmon/${h}`));
            const chip = own.find(c => c.temps.length) || chips.find(c => c.name === 'amdgpu' && c.temps.length);
            if (chip) g.tempC = (chip.temps.find(t => /edge/i.test(t.label)) || chip.temps[0]).c;
            out.push(g);
        }
        return out;
    }

    sample() {
        const s = emptySnapshot();
        s.cpuName = this._cpuName;
        const now = this._clock();
        const elapsed = this._prevAt ? now - this._prevAt : 0;

        const cpu = parseCpuTimes(readText(this._p('proc/stat')));
        s.cpuPercent = cpuPercent(this._prevCpu, cpu);
        this._prevCpu = cpu;

        const mem = parseMeminfo(readText(this._p('proc/meminfo')));
        if (mem) {
            s.ramPercent = mem.percent;
            s.ramUsedGB = mem.usedGB;
            s.ramTotalGB = mem.totalGB;
        }

        const chips = this._hwmon();
        const t = pickCpuTemp(chips, this._zones());
        if (t) {
            s.cpuTempC = Math.round(t.c * 10) / 10;
            s.cpuTempSource = t.source;
        }

        const disk = parseDiskstats(readText(this._p('proc/diskstats')));
        s.diskPercent = diskBusy(this._prevDisk, disk, elapsed);
        this._prevDisk = disk;

        const net = parseNetDev(readText(this._p('proc/net/dev')));
        if (net && this._prevNet && elapsed > 0) {
            const d = netDelta(this._prevNet, net);
            s.netDownBps = d.rx * 1000 / elapsed;
            s.netUpBps = d.tx * 1000 / elapsed;
        }
        this._prevNet = net;
        this._prevAt = now;

        this._pollNvidia(now);
        const maxAge = this._nvPm?.length ? 40000 : 10000;   // sleep-aware readings come at most every 30 s
        const gpus = [...this._nvidia.values()].filter(g => now - g.at < maxAge)
            .map(g => (g.asleep ? {...g, name: `${g.name} · asleep`} : g))
            .concat(this._amdGpus(chips));
        s.gpus = gpus;
        let busiest = null;
        for (const g of gpus) {
            if (Number.isFinite(g.percent) && (!busiest || g.percent > busiest.percent)) busiest = g;
            if (Number.isFinite(g.tempC) && !(g.tempC <= s.gpuTempC)) s.gpuTempC = g.tempC;
        }
        if (busiest) {
            s.gpuPercent = busiest.percent;
            s.gpuName = busiest.name;
        }
        return s;
    }

    destroy() {
        try {
            this._nvCancel?.cancel();
            this._nvProc?.force_exit();
        } catch {}
        this._nvProc = null;
    }
}
