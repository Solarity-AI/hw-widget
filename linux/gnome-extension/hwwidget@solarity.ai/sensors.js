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

// The static part of a chip folder (its name and which temperature inputs it has, with their labels), so a
// sample reads only the input files.
export function chipLayout(dir) {
    const name = (readText(`${dir}/name`) || '').trim();
    const temps = listDir(dir).filter(f => /^temp\d+_input$/.test(f)).map(f => ({
        label: (readText(`${dir}/${f.replace('_input', '_label')}`) || '').trim(),
        input: `${dir}/${f}`,
    }));
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

// Virtual interfaces whose bytes a physical NIC already counted (bridges, bonds, VPNs, containers). Used
// only when /sys/class/net can't tell physical NICs apart (see Sampler.sample).
const NET_SKIP = /^(lo|docker\d*|veth|br-|br\d|bridge|bond|team|virbr|vnet|vmnet|tap|tun|wg|zt|tailscale|cni|flannel|kube|lxc|lxd|incusbr|podman|cali)/;

// keep(name): which interfaces count; default: every name NET_SKIP doesn't match.
export function parseNetDev(text, keep = null) {
    let rx = 0, tx = 0, n = 0;
    const ifaces = {};
    for (const l of (text || '').split('\n')) {
        const m = /^\s*([^:\s]+):\s*(.*)$/.exec(l);
        if (!m || (keep ? !keep(m[1]) : NET_SKIP.test(m[1]))) continue;
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

// hwmon chips in preference order, with the sensor labels to prefer inside each chip, best first. On AMD,
// Tdie is the die temperature; Tctl adds a 10-27 C fan-control offset on some Ryzen and Threadripper models.
const CPU_CHIPS = [
    ['coretemp', /^Package id/i], ['k10temp', [/^Tdie$/i, /^Tctl$/i]], ['zenpower', [/^Tdie$/i, /^Tctl$/i]],
    ['cpu_thermal', null], ['cpu-thermal', null], ['soc_thermal', null], ['nct6775', /^(CPUTIN|PECI|SMBUSMASTER)/i],
    ['it87', null], ['acpitz', null],
];

// chips: [{name, temps: [{label, c}]}] ; zones: [{type, c}]  ->  {c, source} | null
// A 2-socket PC has 1 chip per CPU package, so the hottest reading of the best label across all chips with
// that name is shown: TEMP warns about heat.
export function pickCpuTemp(chips, zones) {
    const ok = c => Number.isFinite(c) && c > 5 && c < 125;
    for (const [name, pref] of CPU_CHIPS) {
        const temps = chips.filter(x => x.name === name).flatMap(x => x.temps.filter(t => ok(t.c)));
        if (!temps.length) continue;
        const prefs = pref ? [].concat(pref) : [];
        const hits = prefs.map(re => temps.filter(t => re.test(t.label || ''))).find(h => h.length);
        const t = (hits || temps).reduce((a, b) => (b.c > a.c ? b : a));
        return {c: t.c, source: `${name}${t.label ? ` ${t.label}` : ''}`};
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
const NV_LOOP_MS = 2000;     // the nvidia-smi stream prints a line this often
const NV_QUERY_MS = 10000;   // a one-shot query still running after this is stopped

export class Sampler {
    // smi: path of nvidia-smi (default: from PATH); clock: ms timestamps, injectable for tests. The default
    // clock is monotonic, so a wall-clock step (NTP, a dual-boot clock) never stretches or skips a timer.
    constructor({root = '/', nvidia = true, smi = null, clock = () => GLib.get_monotonic_time() / 1000} = {}) {
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

    // NVIDIA display devices, listed once.
    _nvidiaDevices() {
        const base = this._p('sys/bus/pci/devices');
        this._nvDevs ||= listDir(base).filter(d => (readText(`${base}/${d}/vendor`) || '').trim() === '0x10de' &&
            (readText(`${base}/${d}/class`) || '').trim().startsWith('0x03')).map(d => `${base}/${d}`);
        return this._nvDevs;
    }

    // Runtime-PM status files of NVIDIA display devices that may sleep. Reading them never wakes the GPU.
    _nvidiaPm() {
        return this._nvidiaDevices().filter(d => (readText(`${d}/power/control`) || '').trim() === 'auto')
            .map(d => `${d}/power/runtime_status`);
    }

    // TLP and similar tools switch power/control with the power source, so each rescan checks it again: a GPU
    // that may sleep now gets the short queries instead of the stream (which would keep it awake), and back.
    _nvidiaModeCheck() {
        if (!this._smi || this._nvCancel?.is_cancelled()) return;
        const pm = this._nvidiaPm();
        if (pm.length && !this._nvPm.length) {
            const proc = this._nvProc;
            this._nvProc = null;
            this._nvRestartAt = 0;
            try {
                proc?.force_exit();
            } catch {}
            this._nvNext = 0;
            this._nvAwakeSince = 0;
        } else if (!pm.length && this._nvPm.length) {
            this._nvFails = 0;
            this._spawnNvidiaStream();
        }
        this._nvPm = pm;
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
        this._nvFails = 0;
        this._spawnNvidiaStream();
    }

    // 1 long-lived nvidia-smi that prints a line every 2 s. When it exits (GPU reset, driver reload, NVML
    // error) or stops printing, sample() starts it again after 10 s, then every 60 s while it keeps failing
    // without a reading.
    _spawnNvidiaStream() {
        this._nvRestartAt = 0;
        this._nvLineAt = this._clock();
        this._nvLineSeen = false;
        try {
            this._nvProc = Gio.Subprocess.new(
                [this._smi, '--query-gpu=index,utilization.gpu,temperature.gpu,memory.used,name', '--format=csv,noheader,nounits', '-lms', `${NV_LOOP_MS}`],
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
            const stream = new Gio.DataInputStream({base_stream: this._nvProc.get_stdout_pipe(), close_base_stream: true});
            this._readNvidia(stream, this._nvProc);
        } catch {
            this._nvidiaEnded(null);
        }
    }

    _nvidiaEnded(proc) {
        if (this._nvCancel?.is_cancelled() || proc !== this._nvProc) return;
        this._nvProc = null;
        this._nvRestartAt = this._clock() + (this._nvFails++ ? 60000 : 10000);
    }

    _readNvidia(stream, proc) {
        stream.read_line_async(GLib.PRIORITY_LOW, this._nvCancel, (st, res) => {
            let line;
            try {
                [line] = st.read_line_finish_utf8(res);
            } catch {
                line = null;
            }
            if (line === null) {   // nvidia-smi exited
                this._nvidiaEnded(proc);
                return;
            }
            if (proc === this._nvProc) {
                this._nvLineAt = this._clock();
                this._nvLineSeen = true;
            }
            const g = parseNvidiaLine(line);
            if (g) {
                g.at = this._clock();
                this._nvidia.set(g.index, g);
                this._nvFails = 0;
            }
            this._readNvidia(st, proc);
        });
    }

    // Sleep-aware polling for GPUs with runtime power management (see _startNvidia).
    _pollNvidia(now) {
        if (!this._smi || !this._nvPm?.length) return;
        // A query that hangs (NVML blocked after a GPU fault) is stopped, so the next one can run 30 s later.
        if (this._nvBusy && now - this._nvQueryAt >= NV_QUERY_MS) {
            try {
                this._nvQuery?.force_exit();
            } catch {}
            this._nvQuery = null;
            this._nvBusy = false;
            this._nvOk = false;
        }
        if (this._nvPm.every(f => (readText(f) || '').trim() === 'suspended')) {
            if (!this._nvidia.size) this._nvidia.set(0, {index: 0, name: 'NVIDIA GPU', integrated: false});
            for (const g of this._nvidia.values())
                Object.assign(g, {percent: 0, tempC: NaN, memUsedGB: NaN, asleep: true, at: now});
            this._nvAwakeSince = 0;
            this._nvOk = true;
            return;
        }
        this._nvAwakeSince ||= now;
        // Between queries an awake GPU keeps its last reading (0 % right after sleep), so the pill does not
        // flip between GPU and DISK; after a failed query it ages out (maxAge) instead of looking live.
        // The very first reading is taken at once to learn the GPU's name.
        for (const g of this._nvidia.values()) {
            g.asleep = false;
            if (this._nvOk) g.at = now;
        }
        // Only 1 query skips the waits, even when it brings no reading (a failing nvidia-smi).
        const first = !this._nvQueried && !this._nvidia.size;
        if (this._nvBusy || (!first && (now < this._nvNext || now - this._nvAwakeSince < 25000))) return;
        this._nvQueried = true;
        this._nvBusy = true;
        this._nvQueryAt = now;
        this._nvNext = now + 30000;
        try {
            const proc = Gio.Subprocess.new(
                [this._smi, '--query-gpu=index,utilization.gpu,temperature.gpu,memory.used,name', '--format=csv,noheader,nounits'],
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
            this._nvQuery = proc;
            proc.communicate_utf8_async(null, this._nvCancel, (pr, res) => {
                if (proc !== this._nvQuery) return;   // stopped after NV_QUERY_MS
                this._nvQuery = null;
                this._nvBusy = false;
                let ok = false;
                try {
                    const [, out] = pr.communicate_utf8_finish(res);
                    for (const line of (out || '').split('\n')) {
                        const g = parseNvidiaLine(line);
                        if (g) {
                            g.at = this._clock();
                            this._nvidia.set(g.index, g);
                            ok = true;
                        }
                    }
                } catch {}
                this._nvOk = ok;
            });
        } catch {
            this._nvBusy = false;
            this._nvOk = false;
        }
    }

    // The sensor folders, adapters and NICs almost never change, so list them once and rescan every 30 s,
    // or at once when a cached folder disappears (a GPU driver reload, a USB adapter unplugged).
    _topology(now) {
        if (this._topo && now < this._topo.until) return this._topo;
        const hw = this._p('sys/class/hwmon');
        const th = this._p('sys/class/thermal');
        const drm = this._p('sys/class/drm');
        const net = this._p('sys/class/net');
        const cards = listDir(drm).filter(c => /^card\d+$/.test(c))
            .filter(c => GLib.file_test(`${drm}/${c}/device/gpu_busy_percent`, GLib.FileTest.EXISTS))
            .map(c => ({dir: `${drm}/${c}/device`, pm: `${drm}/${c}/device/power/runtime_status`,
                hwmon: listDir(`${drm}/${c}/device/hwmon`).map(h => chipLayout(`${drm}/${c}/device/hwmon/${h}`))}));
        // A card's own sensor is read with its card (and not while the card sleeps), never twice.
        const own = new Set(cards.flatMap(c => c.hwmon.map(l => l.dir.split('/').pop())));
        this._topo = {
            until: now + 30000,
            hwmon: listDir(hw).filter(h => !own.has(h)).map(h => chipLayout(`${hw}/${h}`)),
            zones: listDir(th).filter(z => z.startsWith('thermal_zone')).map(z => ({dir: `${th}/${z}`, type: (readText(`${th}/${z}/type`) || '').trim()})),
            cards,
            phys: listDir(net).filter(n => GLib.file_test(`${net}/${n}/device`, GLib.FileTest.EXISTS)),
        };
        this._nvidiaModeCheck();
        return this._topo;
    }

    // A cached folder that is gone rescans at once; a file that exists but fails to read (a radio switched
    // off) is skipped until the 30 s rescan.
    _rescanSoon(dir) {
        if (this._topo && !GLib.file_test(dir, GLib.FileTest.EXISTS)) this._topo.until = 0;
    }

    // Reads only the temperature inputs of a chip whose name and labels were read at the last rescan.
    _readLayout(l) {
        const temps = [];
        for (const t of l.temps) {
            const raw = readText(t.input);
            if (raw === null) {
                this._rescanSoon(l.dir);
                continue;
            }
            temps.push({label: t.label, c: Number(raw.trim()) / 1000});
        }
        return {name: l.name, dir: l.dir, temps};
    }

    _hwmon(topo) {
        return topo.hwmon.map(l => this._readLayout(l));
    }

    _zones(topo) {
        return topo.zones.map(z => {
            const raw = readText(`${z.dir}/temp`);
            if (raw === null) this._rescanSoon(z.dir);
            return {type: z.type, c: Number((raw || '').trim()) / 1000};
        });
    }

    _amdGpus(chips, topo) {
        const out = [];
        for (const card of topo.cards) {
            // A dGPU in runtime suspend is shown asleep and not read: on older kernels reading its busy,
            // temperature or VRAM files wakes it (newer ones fail the read).
            if ((readText(card.pm) || '').trim() === 'suspended') {
                out.push({name: 'AMD Radeon GPU · asleep', percent: 0, tempC: NaN, memUsedGB: NaN, integrated: false, asleep: true});
                continue;
            }
            const busy = readText(`${card.dir}/gpu_busy_percent`);
            if (busy === null) {
                this._rescanSoon(card.dir);
                continue;
            }
            const g = {name: 'AMD Radeon GPU', percent: Number(busy.trim()), tempC: NaN, memUsedGB: NaN, integrated: false};
            const vram = readText(`${card.dir}/mem_info_vram_used`);
            if (vram !== null) g.memUsedGB = Number(vram.trim()) / 1024 ** 3;
            const total = readText(`${card.dir}/mem_info_vram_total`);
            if (total !== null && Number(total.trim()) < 1024 ** 3) g.integrated = true;
            // Each card's own sensor sits under its device; the first amdgpu chip is only a fallback
            // for kernels that don't link it there.
            const own = card.hwmon.map(l => this._readLayout(l));
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
        const topo = this._topology(now);

        const cpu = parseCpuTimes(readText(this._p('proc/stat')));
        s.cpuPercent = cpuPercent(this._prevCpu, cpu);
        this._prevCpu = cpu;

        const mem = parseMeminfo(readText(this._p('proc/meminfo')));
        if (mem) {
            s.ramPercent = mem.percent;
            s.ramUsedGB = mem.usedGB;
            s.ramTotalGB = mem.totalGB;
        }

        const chips = this._hwmon(topo);
        const t = pickCpuTemp(chips, this._zones(topo));
        if (t) {
            s.cpuTempC = Math.round(t.c * 10) / 10;
            s.cpuTempSource = t.source;
        }

        const disk = parseDiskstats(readText(this._p('proc/diskstats')));
        s.diskPercent = diskBusy(this._prevDisk, disk, elapsed);
        this._prevDisk = disk;

        // Count only physical NICs (those with a device link) where /sys tells them apart: bridge, bond and
        // VPN interfaces repeat bytes a NIC already counted.
        const phys = topo.phys;
        const keep = phys.length ? n => phys.includes(n) : null;
        const net = parseNetDev(readText(this._p('proc/net/dev')), keep);
        if (net && this._prevNet && elapsed > 0) {
            const d = netDelta(this._prevNet, net);
            s.netDownBps = d.rx * 1000 / elapsed;
            s.netUpBps = d.tx * 1000 / elapsed;
        }
        this._prevNet = net;
        this._prevAt = now;

        // A stream that stops printing (NVML blocked after a GPU fault) is stopped after 3 missed lines, so the
        // back-off restarts it. Its first line may take longer while nvidia-smi starts up.
        if (this._nvProc && now - this._nvLineAt > (this._nvLineSeen ? 3 : 5) * NV_LOOP_MS) {
            const proc = this._nvProc;
            try {
                proc.force_exit();
            } catch {}
            this._nvidiaEnded(proc);
        }
        if (this._nvRestartAt && now >= this._nvRestartAt) this._spawnNvidiaStream();
        this._pollNvidia(now);
        const maxAge = this._nvPm?.length ? 40000 : 10000;   // sleep-aware readings come at most every 30 s
        const gpus = [...this._nvidia.values()].filter(g => now - g.at < maxAge)
            .map(g => (g.asleep ? {...g, name: `${g.name} · asleep`} : g))
            .concat(this._amdGpus(chips, topo));
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
        this._nvRestartAt = 0;
        try {
            this._nvCancel?.cancel();
            this._nvProc?.force_exit();
            this._nvQuery?.force_exit();
        } catch {}
        this._nvProc = null;
        this._nvQuery = null;
    }
}
