// #claude
// Soundtrack Finder dashboard page: progress, title list, timeline editor, settings.

const pluginId = '5f3c1a2e-9b7d-4c1e-a8f2-6d0e3b9c7a41';
const cfgNumbers = ['SnippetSpacingSeconds', 'SnippetLengthSeconds', 'RequestDelayMs', 'MaxRequestsPerItem', 'MusicSensitivity', 'MaxItemsPerRun', 'ExportFadeSeconds', 'ExportBitrateKbps', 'NowPlayingSeconds'];
const cfgTexts = ['MusicExportPath', 'ExportAlbumArtist', 'PublicUrl'];

const RULER_H = 26;
const LANE_H = 36;
const DOTS_H = 22;
const MAX_PPS = 60; // pixels per second at maximum zoom
const MIN_LEN = 1; // seconds
const UNDO_MAX = 40;

// ---------- helpers ----------

function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function fmtTime(sec) {
    sec = Math.max(0, sec || 0);
    const whole = Math.floor(sec + 1e-6);
    const tenth = Math.round((sec - whole) * 10);
    const h = Math.floor(whole / 3600), m = Math.floor((whole % 3600) / 60), s = whole % 60;
    const base = (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(s).padStart(2, '0');
    return tenth > 0 && tenth < 10 ? base + '.' + tenth : base;
}

function parseTime(str) {
    str = String(str || '').trim().replace(',', '.');
    if (!str) return NaN;
    const parts = str.split(':');
    if (parts.length > 3 || parts.some(p => p === '' || isNaN(Number(p)))) return NaN;
    return parts.map(Number).reduce((a, p) => a * 60 + p, 0);
}

function fmtDuration(sec) {
    if (!sec || sec < 0) return '0 s';
    if (sec < 60) return Math.round(sec) + ' s';
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = Math.round(sec % 60);
    if (h) return `${h} h ${m} min`;
    return s ? `${m}:${String(s).padStart(2, '0')} min` : `${m} min`;
}

function songKey(s) {
    return ((s.artist || '') + '|' + (s.title || '')).trim().toLowerCase();
}

// distinct colors per song (golden angle), the same song always gets the same color
let colorMap = new Map();
function assignColors(list) {
    colorMap = new Map();
    for (const s of [...list].sort((a, b) => a.start - b.start)) {
        const k = songKey(s);
        if (!colorMap.has(k)) colorMap.set(k, `hsl(${Math.round((colorMap.size * 137.508 + 265) % 360)}, 55%, 40%)`);
    }
}

function colorFor(s) {
    return colorMap.get(songKey(s)) || 'hsl(210, 40%, 40%)';
}

function songName(s) {
    const t = s.title || 'Unbenannt';
    return s.artist ? `${s.artist} – ${t}` : t;
}

function clamp(v, lo, hi) {
    return Math.min(hi, Math.max(lo, v));
}

function snap(v, step) {
    return Math.round(v / step) * step;
}

function unionLength(ranges) {
    const r = ranges.map(x => [x[0], x[1]]).sort((a, b) => a[0] - b[0]);
    let total = 0, cs = -1, ce = -1;
    for (const [s, e] of r) {
        if (s > ce) { if (ce > cs) total += ce - cs; cs = s; ce = e; } else ce = Math.max(ce, e);
    }
    if (ce > cs) total += ce - cs;
    return total;
}

function apiUrl(path) {
    return ApiClient.getUrl('SoundtrackFinder/' + path);
}

async function errorText(e) {
    if (e && typeof e.text === 'function') {
        try {
            const t = await e.text();
            if (!t) return 'Fehler ' + (e.status || '');
            try {
                const j = JSON.parse(t);
                if (typeof j === 'string') return j;
                return j.detail || j.title || t;
            } catch {
                return t;
            }
        } catch {
            return 'Fehler ' + (e.status || '');
        }
    }
    return (e && e.message) || String(e);
}

async function call(method, path, body, json) {
    try {
        return await ApiClient.ajax({
            type: method,
            url: apiUrl(path),
            data: body !== undefined ? JSON.stringify(body) : undefined,
            contentType: body !== undefined ? 'application/json' : undefined,
            dataType: json ? 'json' : undefined
        });
    } catch (e) {
        throw new Error(await errorText(e));
    }
}

async function getBlob(path) {
    let res;
    try {
        res = await ApiClient.ajax({ type: 'GET', url: apiUrl(path) });
    } catch (e) {
        throw new Error(await errorText(e));
    }
    if (res && typeof res.blob === 'function') return res.blob();
    const r = await fetch(apiUrl(path), { headers: { Authorization: `MediaBrowser Token="${ApiClient.accessToken()}"` } });
    if (!r.ok) throw new Error('Fehler ' + r.status);
    return r.blob();
}

function toDto(s) {
    return {
        Id: s.id,
        Key: s.key || '',
        Artist: s.artist || '',
        Title: s.title || '',
        StartSeconds: s.start,
        EndSeconds: s.end,
        AppleMusicUrl: s.appleMusicUrl || null,
        Manual: !!s.manual
    };
}

function changedBy(s) {
    if (!s.modifiedBy) return 'manuell geändert';
    let when = '';
    if (s.modifiedAt) {
        const d = new Date(s.modifiedAt);
        if (!isNaN(d)) when = ' am ' + d.toLocaleDateString('de-DE') + ' ' + d.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
    }
    return `geändert von ${s.modifiedBy}${when}`;
}

function songsSignature(list) {
    return list.map(s => [s.id, s.start, s.end, s.artist, s.title].join('|')).sort().join('\n');
}

function stateBadge(state) {
    if (state === 'saved') return '<span class="stf-badge saved" title="In Jellyfin gespeichert">✓ in Jellyfin</span>';
    if (state === 'stale') return '<span class="stf-badge stale" title="Nach dem Speichern geändert – erneut speichern">⟳ neu speichern</span>';
    return '';
}

// ---------- page ----------

export default function (view) {
    const $ = sel => view.querySelector(sel);
    // "user" = the Soundtracks page for all signed-in users (no analysis, settings or bulk jobs)
    const userMode = view.dataset.mode === 'user';
    const st = {
        items: [],
        filter: 'songs',
        item: null,
        sel: null,
        cursor: 0,
        pps: 1,
        fitPps: 1,
        undo: [],
        showSeg: true,
        showDots: true,
        lanes: new Map(),
        laneCount: 1,
        busy: false,
        drag: null,
        pending: null,
        lastRunning: null,
        pan: null,
        progScroll: false
    };
    let statusTimer = null;
    let rafTicks = 0;

    // ----- toast -----
    function toast(msg, isErr) {
        const d = document.createElement('div');
        d.className = 'stf-toast' + (isErr ? ' err' : '');
        d.textContent = msg;
        view.appendChild(d);
        setTimeout(() => d.remove(), isErr ? 6000 : 3000);
    }

    // run a mutating action exclusively, with error reporting
    async function act(fn) {
        if (st.busy) return;
        st.busy = true;
        try {
            await flushPending();
            await fn();
        } catch (e) {
            toast(e.message || String(e), true);
            if (st.item) await reloadItem().catch(() => {});
        } finally {
            st.busy = false;
        }
    }

    // ===================== status =====================
    async function loadStatus() {
        try {
            const s = await call('GET', 'Status', undefined, true);
            const pct = s.total ? Math.round(100 * s.analyzed / s.total) : 0;
            $('#stfStatusText').innerHTML =
                `<b>${s.analyzed} von ${s.total}</b> Filmen/Folgen analysiert (${pct} %), ${s.withSongs} mit Songs.` +
                (s.remaining > 0 ? (s.averageSeconds
                    ? ` Geschätzte Restzeit: <b>${fmtDuration(s.etaSeconds)}</b> (Ø ${fmtDuration(s.averageSeconds)} pro Titel)`
                    : ' Restzeit: wird nach dem ersten analysierten Titel geschätzt.') : '');
            $('#stfTotalBar').style.width = pct + '%';
            if (s.currentItem) {
                $('#stfCurrent').textContent = `Läuft${s.taskRunning ? ' (geplante Aufgabe)' : ''}: ${s.currentItem} – ${s.phase} – ${Math.round(s.progress * 100)} %` + (s.queue ? ` · ${s.queue} in der Warteschlange` : '');
                $('#stfItemBar').style.width = Math.round(s.progress * 100) + '%';
            } else {
                $('#stfCurrent').textContent = s.queue ? `${s.queue} in der Warteschlange` : 'Keine Analyse aktiv.';
                $('#stfItemBar').style.width = '0';
            }
            $('#stfExportStatus').textContent = s.exportRunning
                ? `Speichere in Jellyfin: ${s.exportDone} von ${s.exportTotal} Titeln${s.exportCurrent ? ' – ' + s.exportCurrent : ''}`
                : '';
            const appUrl = new URL(s.appUrl || '/SoundtrackFinder/App', location.href).href;
            $('#stfAppLink').href = appUrl;
            $('#stfAppLink').textContent = appUrl;
            const w = $('#stfExportWarn');
            w.style.display = s.exportWarning ? '' : 'none';
            w.textContent = s.exportWarning ? '⚠ ' + s.exportWarning : '';
            $('#stfExportAll').disabled = !s.exportPossible || s.exportRunning;

            // an analysis of the open title just finished -> refresh it
            if (st.lastRunning && st.lastRunning !== s.currentItem && st.item && st.lastRunning === st.item.name) {
                await reloadItem();
                await loadItems();
            }
            st.lastRunning = s.currentItem;
        } catch {
            $('#stfStatusText').textContent = 'Status konnte nicht geladen werden.';
        }
    }

    // ===================== title list =====================
    async function loadItems() {
        st.items = await call('GET', 'Items', undefined, true);
        renderItems();
        loadStats().catch(() => {});
        if (($('#srQuery').value || '').trim().length >= 2) runSearch();
    }

    // ===================== search & statistics =====================
    let searchTimer = 0;
    let searchSeq = 0;

    async function runSearch() {
        const q = ($('#srQuery').value || '').trim();
        const box = $('#srResults');
        if (q.length < 2) {
            box.innerHTML = '';
            return;
        }
        const seq = ++searchSeq;
        const res = await call('GET', 'Search?q=' + encodeURIComponent(q), undefined, true);
        if (seq !== searchSeq) return; // a newer search is running
        if (!res.songs.length) {
            box.innerHTML = `<div class="stf-small" style="padding:.5em 0">Nichts gefunden für „${esc(q)}“.</div>`;
            return;
        }
        box.innerHTML = `<div class="stf-small" style="padding:.2em 0">${res.total} ${res.total === 1 ? 'Song' : 'Songs'} gefunden${res.total > res.songs.length ? ` (die ersten ${res.songs.length} angezeigt)` : ''}:</div>` +
            res.songs.map(sg => `
            <div class="sr-song">
                <b>${esc(sg.artist ? sg.artist + ' – ' : '')}${esc(sg.title)}</b>
                <span class="stf-small">in ${sg.titleCount} ${sg.titleCount === 1 ? 'Titel' : 'Titeln'}</span><br>
                ${sg.occurrences.map(o => `<button type="button" class="sr-occ" data-item="${esc(o.itemId)}" data-song="${esc(o.songId)}" title="Auf der Timeline öffnen">${esc(o.itemName)} · ${fmtTime(o.start)}</button>`).join('')}
            </div>`).join('');
    }

    async function loadStats() {
        const s = await call('GET', 'Stats', undefined, true);
        $('#stTotals').innerHTML = s.entries ? `
            <span><b>${s.titlesWithSongs}</b> Titel mit Songs</span>
            <span><b>${s.distinctSongs}</b> verschiedene Songs</span>
            <span><b>${s.distinctArtists}</b> Interpreten</span>
            <span><b>${fmtDuration(s.totalMinutes * 60)}</b> Musik insgesamt</span>` : 'Noch keine Songs erkannt.';
        const bars = (list, label, value, sub) => {
            const max = Math.max(1, ...list.map(value));
            return list.map(x => `
                <div class="st-row" data-q="${esc(label(x))}" title="Suchen: ${esc(label(x))}">
                    <span class="st-name">${esc(label(x))}</span>
                    <span class="stf-small" style="white-space:nowrap">${sub(x)}</span>
                    <div class="st-bar" style="width:${Math.max(3, 100 * value(x) / max)}%"></div>
                </div>`).join('') || '<div class="stf-small">–</div>';
        };
        $('#stArtists').innerHTML = bars(s.topArtists, a => a.artist, a => a.titles,
            a => `${a.titles} ${a.titles === 1 ? 'Titel' : 'Titel'} · ${a.songs} ${a.songs === 1 ? 'Song' : 'Songs'}`);
        $('#stSongs').innerHTML = bars(s.topSongs, x => (x.artist ? x.artist + ' – ' : '') + x.title, x => x.titles,
            x => `${x.titles} ${x.titles === 1 ? 'Titel' : 'Titel'}${x.count > x.titles ? ` · ${x.count}×` : ''}`);
    }

    async function openSong(itemId, songId) {
        await openItem(itemId, true);
        const s = songs().find(x => x.id === songId);
        if (s) {
            select(s.id, true);
            setCursor(s.start);
        }
        $('#tlCard').scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    // ===================== "Jetzt läuft" (personal switch, test, live diagnosis) =====================
    let npTimer = 0;

    async function loadNowPlayingStatus() {
        const boxes = view.querySelectorAll('.np-status');
        if (!boxes.length || view.classList.contains('hide')) return;
        let s;
        try {
            s = await call('GET', 'Me/NowPlaying/Status', undefined, true);
        } catch {
            return;
        }
        const html = !s.devices.length
            ? 'Gerade läuft bei dir nichts. Starte einen Film mit Songs – hier siehst du dann, ob und wann eingeblendet wird.'
            : s.devices.map(d => {
                const where = `<b>${esc(d.item)}</b> auf ${esc(d.device)} (${esc(d.client)}) bei ${fmtTime(d.position)}${d.paused ? ' – pausiert' : ''}`;
                if (!d.supports) {
                    return /web/i.test(d.client || '')
                        ? `▶ ${where}<br>⚠ Der Browser hat sich noch nicht für Einblendungen gemeldet (passiert nach einem Jellyfin-Neustart). Lade den Tab, in dem das Video läuft, einmal neu.`
                        : `▶ ${where}<br>❌ Diese App kann keine Einblendungen anzeigen (sie unterstützt keine Server-Nachrichten).`;
                }
                if (!s.enabled) return `▶ ${where}<br>Einblendungen sind ausgeschaltet.`;
                if (!d.songCount) return `▶ ${where}<br>Für diesen Titel sind keine Songs erkannt.`;
                return `▶ ${where}<br>✓ Einblendungen aktiv` +
                    (d.current ? ` · läuft gerade: ${esc(d.current)}` : '') +
                    (d.next ? ` · nächster Song bei ${fmtTime(d.nextStart)}: ${esc(d.next)}` : ' · danach kein weiterer Song') +
                    (d.connections > 1 ? `<br>ℹ ${d.connections} Jellyfin-Fenster/Tabs dieses Geräts sind offen – die Einblendung erscheint in allen.` : '');
            }).join('<br><br>');
        boxes.forEach(b => { b.innerHTML = html; });
    }

    async function sendTest() {
        try {
            const res = await call('POST', 'Me/NowPlaying/Test', undefined, true);
            if (!res.devices.length) {
                toast('Du bist auf keinem Gerät angemeldet, das gerade mit Jellyfin verbunden ist.', true);
                return;
            }
            const ok = res.devices.filter(d => d.sent).map(d => `${d.device} (${d.client})` + (d.connections > 1 ? ` – ${d.connections} Fenster/Tabs` : ''));
            const no = res.devices.filter(d => !d.sent).map(d => `${d.device} (${d.client})`);
            toast((ok.length ? 'Test gesendet an: ' + ok.join(', ') + '.' : '') + (no.length ? ' Kann keine Einblendungen: ' + no.join(', ') + '.' : ''), !ok.length);
        } catch (err) {
            toast(err.message, true);
        }
    }
    async function loadMe() {
        const me = await call('GET', 'Me', undefined, true);
        view.querySelectorAll('.np-box').forEach(b => { b.style.display = me.nowPlayingAvailable ? '' : 'none'; });
        view.querySelectorAll('.np-check').forEach(c => { c.checked = !!me.nowPlaying; });
    }

    // Soundtracks page: ?item=<id> opens a title directly (link under the song list in Jellyfin)
    function openFromAddress() {
        if (!userMode) return;
        const id = new URLSearchParams(location.search).get('item');
        if (id) openItem(id).catch(() => toast('Dieser Titel ist nicht verfügbar (oder du hast keinen Zugriff).', true));
    }

    function rememberInAddress(id) {
        if (!userMode || !window.history || !history.replaceState) return;
        const url = new URL(location.href);
        url.searchParams.set('item', id);
        history.replaceState(null, '', url.pathname + url.search);
    }

    function renderItems() {
        const q = ($('#stfSearch').value || '').toLowerCase();
        const list = st.items.filter(i => {
            if (q && !i.name.toLowerCase().includes(q)) return false;
            if (st.filter === 'songs') return i.songs > 0;
            if (st.filter === 'nomatch') return i.analyzed && i.songs === 0;
            if (st.filter === 'open') return !i.analyzed;
            return true;
        });
        const openId = st.item && st.item.id;
        $('#stfItems').innerHTML = list.length ? list.map(i => `
            <tr class="stf-row${i.id === openId ? ' sel' : ''}" data-id="${esc(i.id)}">
                <td>${esc(i.name)}${i.error ? ` <span class="stf-small">⚠ ${esc(i.error)}</span>` : ''}</td>
                <td class="stf-small">${i.analyzed ? (i.songs ? i.songs + (i.songs === 1 ? ' Song' : ' Songs') : 'keine Treffer') : 'offen'}${i.exported ? ` · ${i.exported} in Jellyfin` : ''}</td>
                <td style="text-align:right"><button type="button" class="stf-b stf-an stf-admin" data-id="${esc(i.id)}">${i.analyzed ? 'Neu analysieren' : 'Jetzt analysieren'}</button></td>
            </tr>`).join('') : '<tr><td class="stf-small">Keine Einträge.</td></tr>';
    }

    async function analyze(id) {
        await call('POST', `Items/${id}/Analyze`);
        toast('Analyse gestartet (bzw. in die Warteschlange gestellt).');
        loadStatus();
    }

    // ===================== timeline: data =====================
    function songs() {
        return st.item ? st.item.songs : [];
    }

    function selected() {
        return songs().find(s => s.id === st.sel) || null;
    }

    function snapshot() {
        return songs().map(s => ({ ...s }));
    }

    function pushUndo(snap) {
        st.undo.push(snap);
        if (st.undo.length > UNDO_MAX) st.undo.shift();
    }

    async function openItem(id, keepView) {
        await flushPending();
        const changed = !st.item || st.item.id !== id;
        const data = await call('GET', 'Items/' + id, undefined, true);
        data.songs.sort((a, b) => a.start - b.start || a.end - b.end);
        assignColors(data.songs);
        const prevScroll = $('#tlScroll').scrollLeft;
        st.item = data;
        st.serverSig = songsSignature(data.songs);
        if (changed) {
            stopPlay();
            st.undo = [];
            st.sel = null;
            st.cursor = 0;
        } else if (st.sel && !data.songs.some(s => s.id === st.sel)) {
            st.sel = null;
        }

        $('#tlCard').style.display = '';
        rememberInAddress(String(id));
        computeFit();
        if (changed) setPps(st.fitPps, 0, 0, true);
        renderAll();
        setScroll(changed ? 0 : prevScroll);
        renderTicks();
        renderMiniViewport();
        renderItems();
        if (changed && !keepView) $('#tlCard').scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    function reloadItem() {
        return st.item ? openItem(st.item.id, true) : Promise.resolve();
    }

    // ===================== timeline: geometry =====================
    // programmatic scrolling is marked, so a scroll event without the mark is the user scrolling
    function setScroll(v) {
        const sc = $('#tlScroll');
        const nv = clamp(v, 0, Math.max(0, sc.scrollWidth - sc.clientWidth));
        if (Math.abs(sc.scrollLeft - nv) >= 1) {
            st.progScroll = true;
            sc.scrollLeft = nv;
        }
    }

    function userScrolled() {
        if (play.active && play.follow) {
            play.follow = false;
            $('#tlFollow').checked = false;
        }
    }
    function runtime() {
        return Math.max(1, (st.item && st.item.runtimeSeconds) || 1);
    }

    function viewWidth() {
        return Math.max(200, $('#tlScroll').clientWidth);
    }

    function computeFit() {
        st.fitPps = viewWidth() / runtime();
        if (st.pps < st.fitPps) st.pps = st.fitPps;
    }

    function maxPps() {
        return Math.max(MAX_PPS, st.fitPps);
    }

    // set zoom, keeping time `anchorT` at viewport x `anchorX`
    function setPps(pps, anchorT, anchorX, silent) {
        st.pps = clamp(pps, st.fitPps, maxPps());
        const ratio = maxPps() / st.fitPps;
        $('#tlZoom').value = ratio > 1.0001 ? Math.round(1000 * Math.log(st.pps / st.fitPps) / Math.log(ratio)) : 0;
        if (silent) return;
        renderTimeline();
        setScroll(anchorT * st.pps - anchorX);
        renderTicks();
        renderMiniViewport();
    }

    function zoomToWindow(seconds) {
        const center = selected() ? (selected().start + selected().end) / 2
            : ($('#tlScroll').scrollLeft + viewWidth() / 2) / st.pps;
        setPps(viewWidth() / seconds, center, viewWidth() / 2);
    }

    function timeAtClientX(x) {
        const r = $('#tlInner').getBoundingClientRect();
        return clamp((x - r.left) / st.pps, 0, runtime());
    }

    function computeLanes() {
        const lanesEnd = [];
        st.lanes = new Map();
        for (const s of [...songs()].sort((a, b) => a.start - b.start || a.end - b.end)) {
            let lane = lanesEnd.findIndex(e => e <= s.start + 0.05);
            if (lane < 0) { lane = lanesEnd.length; lanesEnd.push(0); }
            lanesEnd[lane] = s.end;
            st.lanes.set(s.id, lane);
        }
        st.laneCount = Math.max(1, lanesEnd.length);
    }

    // ===================== timeline: rendering =====================
    function renderAll() {
        renderHeader();
        renderTimeline();
        renderDetail();
        renderList();
    }

    function renderHeader() {
        const it = st.item;
        $('#tlTitle').textContent = it.name;
        $('#tlPrev').style.display = it.isEpisode ? '' : 'none';
        $('#tlNext').style.display = it.isEpisode ? '' : 'none';
        $('#tlPrev').disabled = !it.prevId;
        $('#tlNext').disabled = !it.nextId;

        const list = it.songs;
        const songTime = unionLength(list.map(s => [s.start, s.end]));
        const segTime = unionLength(it.musicSegments.map(m => [m.start, m.end]));
        const saved = list.filter(s => s.exportState === 'saved').length;
        const stale = list.filter(s => s.exportState === 'stale').length;
        let txt = `${list.length} ${list.length === 1 ? 'Song' : 'Songs'} · ${fmtDuration(songTime)} mit Songs · Laufzeit ${fmtTime(runtime())}`;
        if (it.musicSegments.length) txt += ` · ${fmtDuration(segTime)} Musik vermutet`;
        if (it.snippets.length) txt += ` · ${it.snippets.filter(x => x.matched).length}/${it.snippets.length} Ausschnitte erkannt`;
        if (saved || stale) txt += ` · ${saved} in Jellyfin gespeichert` + (stale ? `, ${stale} geändert` : '');
        if (!it.analyzed) txt += ' · noch nicht analysiert';
        else if (!it.musicSegments.length && !it.snippets.length) txt += ' · Musikabschnitte und Shazam-Punkte erscheinen nach „Neu analysieren“';
        if (it.error) txt += ` · Fehler: ${it.error}`;
        $('#tlSummary').textContent = txt;

        const w = $('#tlWarn');
        w.style.display = it.exportWarning ? '' : 'none';
        w.textContent = it.exportWarning ? '⚠ ' + it.exportWarning : '';
        const exp = $('#tlExportItem');
        exp.disabled = !it.exportPossible || !list.length;
        exp.textContent = stale ? `Geänderte + alle Songs in Jellyfin speichern` : 'Alle Songs in Jellyfin speichern';
        $('#tlUndo').disabled = !st.undo.length;
        $('#tlUndo').textContent = st.undo.length ? `↶ Rückgängig (${st.undo.length})` : '↶ Rückgängig';
    }

    function renderTimeline() {
        if (!st.item) return;
        const it = st.item;
        const pps = st.pps;
        computeLanes();
        const inner = $('#tlInner');
        inner.style.width = Math.ceil(runtime() * pps) + 'px';
        inner.style.height = (RULER_H + 8 + st.laneCount * LANE_H + DOTS_H) + 'px';

        $('#tlSegs').innerHTML = st.showSeg ? it.musicSegments.map(m =>
            `<div class="tl-seg" style="left:${m.start * pps}px;width:${Math.max(1, (m.end - m.start) * pps)}px" title="Vermutete Musik ${fmtTime(m.start)} – ${fmtTime(m.end)}"></div>`).join('') : '';

        const counts = new Map();
        for (const s of it.songs) counts.set(songKey(s), (counts.get(songKey(s)) || 0) + 1);
        $('#tlBlocks').innerHTML = it.songs.map(s => {
            const n = counts.get(songKey(s));
            const icon = s.exportState === 'saved' ? '✓' : (s.exportState === 'stale' ? '⟳' : '');
            return `<div class="tl-block${s.id === st.sel ? ' sel' : ''}" data-id="${esc(s.id)}"
                    style="background:${colorFor(s)}"
                    title="${esc(songName(s))}&#10;${fmtTime(s.start)} – ${fmtTime(s.end)} (${fmtDuration(s.end - s.start)})${n > 1 ? `&#10;${n}× in diesem Titel` : ''}">
                    <div class="h l"></div>
                    <div class="lbl"><b>${esc(s.title || 'Unbenannt')}</b>${n > 1 ? ` <span class="stf-badge">${n}×</span>` : ''}<br><small>${esc(s.artist)}</small></div>
                    ${icon ? `<span class="st">${icon}</span>` : ''}
                    <div class="h r"></div>
                </div>`;
        }).join('');
        for (const s of it.songs) positionBlock(s);

        $('#tlDots').innerHTML = st.showDots ? it.snippets.map(d => {
            const center = d.start + 6;
            const cls = d.excluded ? 'excl' : (d.matched ? 'hit' : 'miss');
            const tip = d.excluded ? 'ausgeschlossen: ' + d.label : (d.matched ? d.label : 'nichts erkannt');
            return `<div class="tl-dot ${cls}" data-t="${d.start}" style="left:${center * pps}px" title="${fmtTime(d.start)}: ${esc(tip)}"></div>`;
        }).join('') : '';

        $('#tlCursor').style.left = (st.cursor * pps) + 'px';
        renderTicks();
        renderMini();
    }

    function blockEl(id) {
        return $('#tlBlocks').querySelector(`.tl-block[data-id="${CSS.escape(id)}"]`);
    }

    function positionBlock(s) {
        const el = blockEl(s.id);
        if (!el) return;
        const lane = st.lanes.get(s.id) || 0;
        el.style.left = (s.start * st.pps) + 'px';
        el.style.width = Math.max(6, (s.end - s.start) * st.pps) + 'px';
        el.classList.toggle('narrow', (s.end - s.start) * st.pps < 70);
        el.style.top = (RULER_H + 6 + lane * LANE_H) + 'px';
    }

    function renderTicks() {
        if (!st.item) return;
        const pps = st.pps;
        const steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        const major = steps.find(x => x * pps >= 80) || 3600;
        const minor = [...steps].reverse().find(x => x < major && major % x === 0 && x * pps >= 12);
        const sc = $('#tlScroll');
        const from = Math.max(0, sc.scrollLeft / pps - major);
        const to = Math.min(runtime(), (sc.scrollLeft + viewWidth()) / pps + major);
        let html = '';
        if (minor) {
            for (let t = Math.floor(from / minor) * minor; t <= to; t += minor) {
                if (t % major !== 0) html += `<div class="tl-tick minor" style="left:${t * pps}px"></div>`;
            }
        }
        for (let t = Math.floor(from / major) * major; t <= to; t += major) {
            html += `<div class="tl-tick" style="left:${t * pps}px">${fmtTime(t)}</div>`;
        }
        $('#tlRuler').innerHTML = html;
        stickLabels();
    }

    function stickLabels() {
        const left = $('#tlScroll').scrollLeft;
        $('#tlBlocks').querySelectorAll('.tl-block').forEach(el => {
            const x = el.offsetLeft, w = el.offsetWidth;
            const lbl = el.querySelector('.lbl');
            const shift = clamp(left - x, 0, Math.max(0, w - 80));
            lbl.style.left = (10 + shift) + 'px';
        });
    }

    function renderMini() {
        const it = st.item;
        const R = runtime();
        const pct = t => (100 * t / R) + '%';
        $('#tlMini').innerHTML =
            it.musicSegments.map(m => `<div class="ms" style="left:${pct(m.start)};width:${pct(m.end - m.start)}"></div>`).join('') +
            it.songs.map(s => `<div class="mb" style="left:${pct(s.start)};width:max(2px,${pct(s.end - s.start)});background:${colorFor(s)}"></div>`).join('') +
            '<div class="vp" id="tlMiniVp"></div>';
        renderMiniViewport();
    }

    function renderMiniViewport() {
        const vp = view.querySelector('#tlMiniVp');
        if (!vp || !st.item) return;
        const total = runtime() * st.pps;
        const sc = $('#tlScroll');
        vp.style.left = (100 * sc.scrollLeft / total) + '%';
        vp.style.width = Math.min(100, 100 * viewWidth() / total) + '%';
    }

    function renderDetail() {
        const s = selected();
        const box = $('#tlDetail');
        if (!s) {
            box.style.display = 'none';
            return;
        }
        box.style.display = '';
        const n = st.item.songs.filter(x => songKey(x) === songKey(s)).length;
        $('#tlDetailHead').textContent = songName(s) + (n > 1 ? ` (${n}× in diesem Titel)` : '') + (s.manual ? ' · ✎ ' + changedBy(s) : '');
        $('#tlDetailState').innerHTML = stateBadge(s.exportState);
        fillDetailFields(s);
        $('#tlApple').value = s.appleMusicUrl || '';
        const link = $('#tlAppleLink');
        if (s.appleMusicUrl) {
            link.href = s.appleMusicUrl;
            link.textContent = 'In Apple Music öffnen';
        } else {
            link.href = 'https://music.apple.com/de/search?term=' + encodeURIComponent(((s.artist || '') + ' ' + (s.title || '')).trim());
            link.textContent = 'In Apple Music suchen';
        }
        const exp = $('#tlExportSong');
        exp.disabled = !st.item.exportPossible;
        exp.textContent = s.exportState === 'saved' ? 'In Jellyfin gespeichert ✓' : (s.exportState === 'stale' ? 'Erneut in Jellyfin speichern' : 'In Jellyfin speichern');
        const ex = $('#tlExclude');
        const target = (s.artist || s.title || '').trim();
        ex.classList.remove('confirm');
        ex.dataset.pattern = target;
        ex.textContent = target ? `„${target}“ immer ausschließen` : 'Interpret immer ausschließen';
        ex.disabled = target.length < 2;
        const next = nextSong(s);
        $('#tlMerge').disabled = !next;
        $('#tlMerge').title = next ? `Mit „${songName(next)}“ (${fmtTime(next.start)}) zusammenführen` : 'Kein weiterer Song';
    }

    // only the fields that change while dragging (keeps the inputs the user is typing in untouched otherwise)
    function fillDetailFields(s) {
        const ae = document.activeElement;
        if (ae !== $('#tlArtist')) $('#tlArtist').value = s.artist || '';
        if (ae !== $('#tlSongTitle')) $('#tlSongTitle').value = s.title || '';
        if (ae !== $('#tlStart')) $('#tlStart').value = fmtTime(s.start);
        if (ae !== $('#tlEnd')) $('#tlEnd').value = fmtTime(s.end);
        $('#tlLen').textContent = `(Länge ${fmtDuration(s.end - s.start)})`;
    }

    function renderList() {
        const list = st.item.songs;
        $('#tlList').innerHTML = list.length ? list.map(s => `
            <tr class="stf-row${s.id === st.sel ? ' sel' : ''}" data-id="${esc(s.id)}">
                <td style="white-space:nowrap"><span style="display:inline-block;width:.8em;height:.8em;border-radius:2px;background:${colorFor(s)};margin-right:.4em"></span>${fmtTime(s.start)} – ${fmtTime(s.end)}</td>
                <td>${esc(songName(s))}${s.manual ? ` <span class="stf-small" title="${esc(changedBy(s))}">✎${s.modifiedBy ? ' ' + esc(s.modifiedBy) : ''}</span>` : ''}</td>
                <td class="stf-small">${fmtDuration(s.end - s.start)}</td>
                <td>${stateBadge(s.exportState)}</td>
            </tr>`).join('') : '<tr><td class="stf-small">Noch keine Songs – Doppelklick in die Leiste legt einen an.</td></tr>';
    }

    function select(id, scrollTo) {
        if (st.pending && st.pending.id !== id) schedulePendingFlush(0);
        st.sel = id;
        $('#tlBlocks').querySelectorAll('.tl-block').forEach(el => el.classList.toggle('sel', el.dataset.id === id));
        $('#tlList').querySelectorAll('.stf-row').forEach(el => el.classList.toggle('sel', el.dataset.id === id));
        renderDetail();
        const s = selected();
        if (s && scrollTo) {
            const sc = $('#tlScroll');
            const x0 = s.start * st.pps, x1 = s.end * st.pps;
            if (x0 < sc.scrollLeft || x1 > sc.scrollLeft + viewWidth()) setScroll(x0 - viewWidth() * 0.2);
        }
    }

    function setCursor(t) {
        st.cursor = clamp(t, 0, runtime());
        $('#tlCursor').style.left = (st.cursor * st.pps) + 'px';
    }

    function nextSong(s) {
        const list = [...songs()].sort((a, b) => a.start - b.start || a.end - b.end);
        const i = list.findIndex(x => x.id === s.id);
        return i >= 0 && i < list.length - 1 ? list[i + 1] : null;
    }

    function showTip(e, text) {
        const tip = $('#tlTip');
        tip.textContent = text;
        tip.style.display = 'block';
        tip.style.left = (e.clientX + 14) + 'px';
        tip.style.top = (e.clientY - 34) + 'px';
    }

    function hideTip() {
        $('#tlTip').style.display = 'none';
    }

    // ===================== timeline: saving =====================
    async function saveSong(s) {
        await call('PUT', `Items/${st.item.id}/Songs/${s.id}`, {
            Artist: s.artist || '',
            Title: s.title || '',
            StartSeconds: s.start,
            EndSeconds: s.end,
            AppleMusicUrl: s.appleMusicUrl || null
        });
    }

    // keyboard / ±1 s nudges are collected and saved together after a short pause
    function nudge(field, delta) {
        const s = selected();
        if (!s) return;
        if (!st.pending) {
            st.pending = { id: s.id, snap: snapshot(), timer: 0 };
        }
        const R = runtime();
        if (field === 'move') {
            const len = s.end - s.start;
            s.start = clamp(Math.round((s.start + delta) * 10) / 10, 0, R - len);
            s.end = s.start + len;
        } else if (field === 'start') {
            s.start = clamp(Math.round((s.start + delta) * 10) / 10, 0, s.end - MIN_LEN);
        } else {
            s.end = clamp(Math.round((s.end + delta) * 10) / 10, s.start + MIN_LEN, R);
        }
        positionBlock(s);
        fillDetailFields(s);
        schedulePendingFlush(700);
    }

    function schedulePendingFlush(ms) {
        if (!st.pending) return;
        clearTimeout(st.pending.timer);
        st.pending.timer = setTimeout(() => {
            if (st.busy) schedulePendingFlush(300);
            else act(() => Promise.resolve());
        }, ms);
    }

    async function flushPending() {
        const p = st.pending;
        if (!p) return;
        st.pending = null;
        clearTimeout(p.timer);
        const s = songs().find(x => x.id === p.id);
        if (!s) return;
        pushUndo(p.snap);
        await saveSong(s);
        await reloadItem();
    }

    async function saveDetail() {
        const s = selected();
        if (!s) return;
        const start = parseTime($('#tlStart').value);
        const end = parseTime($('#tlEnd').value);
        if (isNaN(start) || isNaN(end)) {
            toast('Zeit bitte als m:ss oder h:mm:ss angeben (z. B. 1:02:30).', true);
            fillDetailFields(s);
            return;
        }
        if (end - start < MIN_LEN) {
            toast('Das Ende muss nach dem Start liegen.', true);
            fillDetailFields(s);
            return;
        }
        const next = {
            artist: $('#tlArtist').value.trim(),
            title: $('#tlSongTitle').value.trim(),
            start: clamp(start, 0, runtime()),
            end: clamp(end, 0, runtime()),
            appleMusicUrl: $('#tlApple').value.trim() || null
        };
        if (next.artist === (s.artist || '') && next.title === (s.title || '') && Math.abs(next.start - s.start) < 0.05
            && Math.abs(next.end - s.end) < 0.05 && (next.appleMusicUrl || null) === (s.appleMusicUrl || null)) {
            return;
        }
        await act(async () => {
            pushUndo(snapshot());
            Object.assign(s, next);
            await saveSong(s);
            await reloadItem();
        });
    }

    async function addSongAt(t) {
        await act(async () => {
            const R = runtime();
            const start = clamp(Math.round(t), 0, R - MIN_LEN);
            const following = songs().filter(s => s.start > start + 5).sort((a, b) => a.start - b.start)[0];
            const end = Math.min(start + 30, following ? following.start : R, R);
            pushUndo(snapshot());
            const res = await call('POST', `Items/${st.item.id}/Songs`, { Artist: '', Title: 'Neuer Song', StartSeconds: start, EndSeconds: Math.max(end, start + MIN_LEN) }, true);
            await reloadItem();
            select(res.id);
            const inp = $('#tlSongTitle');
            inp.focus();
            inp.select();
        });
    }

    async function splitAtCursor() {
        let s = selected();
        if (!s || !(st.cursor > s.start + MIN_LEN && st.cursor < s.end - MIN_LEN)) {
            s = songs().find(x => st.cursor > x.start + MIN_LEN && st.cursor < x.end - MIN_LEN) || null;
        }
        if (!s) {
            toast('Setze den roten Cursor (Klick in die Leiste) in einen Song, dann teilen.', true);
            return;
        }
        await act(async () => {
            pushUndo(snapshot());
            const res = await call('POST', `Items/${st.item.id}/Songs/${s.id}/Split`, { At: Math.round(st.cursor * 10) / 10 }, true);
            await reloadItem();
            select(res.id);
            toast('Song geteilt.');
        });
    }

    async function mergeWithNext() {
        const s = selected();
        const n = s && nextSong(s);
        if (!n) return;
        await act(async () => {
            pushUndo(snapshot());
            await call('POST', `Items/${st.item.id}/Songs/${s.id}/Merge`, { OtherId: n.id });
            await reloadItem();
            select(s.id);
            toast('Zusammengeführt.');
        });
    }

    async function deleteSelected() {
        const s = selected();
        if (!s) return;
        await act(async () => {
            pushUndo(snapshot());
            await call('DELETE', `Items/${st.item.id}/Songs/${s.id}`);
            st.sel = null;
            await reloadItem();
            await loadItems();
            toast('Song gelöscht' + (s.exportState !== 'none' ? ' (auch aus Jellyfin entfernt)' : '') + ' – „Rückgängig“ holt ihn zurück.');
        });
    }

    async function undo() {
        await act(async () => {
            if (!st.undo.length) return;
            // somebody else may have changed this title meanwhile -> never overwrite their work
            const fresh = await call('GET', 'Items/' + st.item.id, undefined, true);
            if (songsSignature(fresh.songs) !== st.serverSig) {
                st.undo = [];
                await reloadItem();
                toast('Rückgängig nicht möglich: Jemand anderes hat diesen Titel inzwischen geändert. Die aktuelle Fassung wird angezeigt.', true);
                return;
            }
            const snap = st.undo.pop();
            await call('PUT', `Items/${st.item.id}/Songs`, snap.map(toDto));
            await reloadItem();
            toast('Rückgängig gemacht.');
        });
    }

    async function exportSongs(ids) {
        await act(async () => {
            const btn = ids ? $('#tlExportSong') : $('#tlExportItem');
            const label = btn.textContent;
            btn.disabled = true;
            btn.textContent = 'Speichere …';
            try {
                const res = await call('POST', `Items/${st.item.id}/Export`, { SongIds: ids || null }, true);
                toast(res.written ? `${res.written} ${res.written === 1 ? 'Datei' : 'Dateien'} in Jellyfin gespeichert.` : 'Alles war schon aktuell gespeichert.');
            } finally {
                btn.textContent = label;
            }
            await reloadItem();
            await loadItems();
        });
    }

    // ===================== playback =====================
    // streamed from the server: plays until the end of the range or until stopped
    const audio = new Audio();
    audio.preload = 'none';
    const play = { active: false, from: 0, to: 0, raf: 0, follow: true };

    function streamUrl(from, to) {
        const q = new URLSearchParams({ start: from.toFixed(1), end: to.toFixed(1), ApiKey: ApiClient.accessToken() });
        return apiUrl(`Items/${st.item.id}/Stream`) + '?' + q.toString();
    }

    async function playRange(from, to) {
        stopPlay();
        if (!st.item) return;
        from = clamp(from, 0, runtime() - 0.5);
        to = to ? clamp(to, from + 1, runtime()) : runtime();
        play.active = true;
        play.from = from;
        play.to = to;
        play.follow = $('#tlFollow').checked;
        const head = $('#tlPlayhead');
        head.style.left = (from * st.pps) + 'px';
        head.style.display = 'block';
        $('#tlStopPlay').disabled = false;
        audio.src = streamUrl(from, to);
        const tick = () => {
            if (!play.active) return;
            const t = play.from + (audio.currentTime || 0);
            head.style.left = (t * st.pps) + 'px';
            if (play.follow) {
                const x = t * st.pps;
                const sc = $('#tlScroll');
                // turn the page when the playhead reaches the right edge
                if (x > sc.scrollLeft + viewWidth() - 40 || x < sc.scrollLeft) setScroll(x - viewWidth() * 0.1);
            }
            play.raf = requestAnimationFrame(tick);
        };
        play.raf = requestAnimationFrame(tick);
        try {
            await audio.play();
        } catch (e) {
            if (!play.active || (e && e.name === 'AbortError')) return; // stopped meanwhile
            stopPlay();
            if (e && e.name === 'NotAllowedError') toast('Der Browser hat das Abspielen blockiert – bitte nochmal klicken.', true);
            else toast('Abspielen nicht möglich: ' + ((e && e.message) || e), true);
        }
    }

    function stopPlay() {
        const wasActive = play.active;
        play.active = false;
        cancelAnimationFrame(play.raf);
        if (wasActive || audio.src) {
            audio.pause();
            audio.removeAttribute('src');
            audio.load(); // closes the stream -> server stops ffmpeg
        }
        const head = view.querySelector('#tlPlayhead');
        if (head) head.style.display = 'none';
        const btn = view.querySelector('#tlStopPlay');
        if (btn) btn.disabled = true;
    }

    function playSongStart(s) {
        return playRange(s.start, s.end);
    }

    function playSongEnd(s) {
        return playRange(Math.max(s.start, s.end - 15), s.end + 5);
    }

    // ===================== timeline: pointer =====================
    function onPointerDown(e) {
        if (!st.item || (e.pointerType === 'mouse' && e.button !== 0)) return;
        const el = e.target.closest('.tl-block');
        const t = timeAtClientX(e.clientX);
        if (!el) {
            // empty area: drag = scroll, click = set cursor (and jump there while playing)
            const dot = e.target.closest('.tl-dot');
            st.pan = { x: e.clientX, left: $('#tlScroll').scrollLeft, moved: false, t: dot ? Number(dot.dataset.t) : Math.round(t), mouse: e.pointerType !== 'touch' };
            if (st.pan.mouse) {
                $('#tlInner').setPointerCapture(e.pointerId);
                e.preventDefault();
            }
            return;
        }
        const s = songs().find(x => x.id === el.dataset.id);
        if (!s) return;
        select(s.id);
        const mode = e.target.classList.contains('h') ? (e.target.classList.contains('l') ? 'l' : 'r') : 'move';
        st.drag = { mode, id: s.id, el, x: e.clientX, t, orig: { start: s.start, end: s.end }, moved: false, snap: snapshot() };
        el.setPointerCapture(e.pointerId);
        e.preventDefault();
    }

    function onPointerMove(e) {
        const pan = st.pan;
        if (pan) {
            const dx = e.clientX - pan.x;
            if (!pan.moved && Math.abs(dx) < 4) return;
            pan.moved = true;
            if (pan.mouse) {
                $('#tlInner').classList.add('panning');
                userScrolled();
                $('#tlScroll').scrollLeft = pan.left - dx;
            }
            return;
        }
        const d = st.drag;
        if (!d) return;
        const dx = e.clientX - d.x;
        if (!d.moved && Math.abs(dx) < 4) return;
        d.moved = true;
        d.el.classList.add('drag');
        const s = songs().find(x => x.id === d.id);
        if (!s) return;
        const R = runtime();
        const step = e.altKey ? 0.1 : 1;
        const dt = dx / st.pps;
        if (d.mode === 'move') {
            const len = d.orig.end - d.orig.start;
            s.start = clamp(snap(d.orig.start + dt, step), 0, R - len);
            s.end = s.start + len;
        } else if (d.mode === 'l') {
            s.start = clamp(snap(d.orig.start + dt, step), 0, d.orig.end - MIN_LEN);
        } else {
            s.end = clamp(snap(d.orig.end + dt, step), d.orig.start + MIN_LEN, R);
        }
        positionBlock(s);
        fillDetailFields(s);
        showTip(e, `${fmtTime(s.start)} – ${fmtTime(s.end)}  (${fmtDuration(s.end - s.start)})`);

        // auto-scroll near the edges
        const sc = $('#tlScroll');
        const r = sc.getBoundingClientRect();
        if (e.clientX > r.right - 30) sc.scrollLeft += 12;
        else if (e.clientX < r.left + 30) sc.scrollLeft -= 12;
    }

    function onPointerUp(e) {
        const pan = st.pan;
        if (pan) {
            st.pan = null;
            $('#tlInner').classList.remove('panning');
            if (!pan.moved && !(e && e.type === 'pointercancel')) {
                setCursor(pan.t);
                if (play.active) playRange(pan.t);
            }
            return;
        }
        const d = st.drag;
        if (!d) return;
        st.drag = null;
        hideTip();
        d.el.classList.remove('drag');
        const s = songs().find(x => x.id === d.id);
        if (!d.moved) {
            setCursor(Math.round(d.t));
            return;
        }
        if (s && (s.start !== d.orig.start || s.end !== d.orig.end)) {
            act(async () => {
                pushUndo(d.snap);
                await saveSong(s);
                await reloadItem();
            });
        }
    }

    function onDblClick(e) {
        if (!st.item) return;
        const el = e.target.closest('.tl-block');
        if (el) {
            select(el.dataset.id);
            $('#tlSongTitle').focus();
            $('#tlSongTitle').select();
            return;
        }
        if (e.target.closest('.tl-ruler')) return;
        addSongAt(timeAtClientX(e.clientX));
    }

    function onWheel(e) {
        if (!st.item) return;
        if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
            userScrolled();
            if (e.shiftKey) {
                e.preventDefault();
                $('#tlScroll').scrollLeft += e.deltaY || e.deltaX;
            }
            return;
        }
        e.preventDefault();
        const r = $('#tlScroll').getBoundingClientRect();
        const anchorX = e.clientX - r.left;
        const t = timeAtClientX(e.clientX);
        setPps(st.pps * Math.exp(-e.deltaY * 0.0018), t, anchorX);
    }

    // minimap: click/drag to scroll
    let miniDrag = false;
    function miniScroll(e) {
        const r = $('#tlMini').getBoundingClientRect();
        const t = clamp((e.clientX - r.left) / r.width, 0, 1) * runtime();
        userScrolled();
        setScroll(t * st.pps - viewWidth() / 2);
    }

    // ===================== keyboard =====================
    function onKey(e) {
        if (!st.item || $('#tlCard').style.display === 'none' || !view.isConnected || view.classList.contains('hide')) return;
        const tag = (e.target && e.target.tagName) || '';
        const typing = tag === 'TEXTAREA' || tag === 'SELECT' || (e.target && e.target.isContentEditable)
            || (tag === 'INPUT' && !/^(checkbox|radio|range|button|submit)$/i.test(e.target.type || ''));
        if (typing) return;
        const s = selected();
        const k = e.key;
        if ((k === ' ' || k === 'Enter') && (tag === 'BUTTON' || tag === 'A')) return;
        if ((e.ctrlKey || e.metaKey) && k.toLowerCase() === 'z') {
            e.preventDefault();
            undo();
            return;
        }
        if (e.ctrlKey || e.metaKey) return;
        if (k === 'ArrowLeft' || k === 'ArrowRight') {
            if (!s) return;
            e.preventDefault();
            const delta = (k === 'ArrowLeft' ? -1 : 1) * (e.altKey ? 0.1 : 1);
            nudge(e.shiftKey ? 'end' : 'move', delta);
        } else if (k === '[' && s) {
            e.preventDefault();
            nudge('start', st.cursor - s.start);
        } else if (k === ']' && s) {
            e.preventDefault();
            nudge('end', st.cursor - s.end);
        } else if ((k === 'Delete' || k === 'Backspace') && s) {
            e.preventDefault();
            deleteSelected();
        } else if (k === ' ') {
            e.preventDefault();
            if (play.active) stopPlay();
            else playRange(st.cursor); // same as "▶ Ab Cursor abspielen"
        } else if (k === 's' || k === 'S') {
            e.preventDefault();
            splitAtCursor();
        } else if (k === 'Escape') {
            select(null);
        }
    }

    // ===================== settings =====================
    async function loadConfig() {
        const [config, libs] = await Promise.all([
            ApiClient.getPluginConfiguration(pluginId),
            call('GET', 'Libraries', undefined, true)
        ]);
        cfgNumbers.forEach(f => { $('#' + f).value = config[f]; });
        cfgTexts.forEach(f => { $('#' + f).value = config[f] || ''; });
        $('#WriteToOverview').checked = config.WriteToOverview;
        $('#OverviewLink').checked = config.OverviewLink;
        $('#NowPlayingEnabled').checked = config.NowPlayingEnabled;
        $('#ExcludePatterns').value = (config.ExcludePatterns || []).join('\n');
        const sel = new Set(config.LibraryIds || []);
        $('#stfLibraries').innerHTML = libs.map(l => `
            <label><input is="emby-checkbox" type="checkbox" class="stf-lib" value="${esc(l.id)}" ${sel.has(l.id) ? 'checked' : ''}><span>${esc(l.name)}</span></label>`).join('');
    }

    async function saveConfig(e) {
        e.preventDefault();
        try {
            const config = await ApiClient.getPluginConfiguration(pluginId);
            cfgNumbers.forEach(f => {
                const v = parseFloat($('#' + f).value);
                config[f] = isNaN(v) ? 0 : (f === 'ExportFadeSeconds' ? v : Math.round(v));
            });
            cfgTexts.forEach(f => { config[f] = $('#' + f).value.trim(); });
            config.WriteToOverview = $('#WriteToOverview').checked;
            config.OverviewLink = $('#OverviewLink').checked;
            config.NowPlayingEnabled = $('#NowPlayingEnabled').checked;
            config.ExcludePatterns = $('#ExcludePatterns').value.split('\n').map(x => x.trim()).filter(Boolean);
            config.LibraryIds = [...view.querySelectorAll('.stf-lib:checked')].map(c => c.value);
            const r = await ApiClient.updatePluginConfiguration(pluginId, config);
            if (window.Dashboard && Dashboard.processPluginConfigurationUpdateResult) Dashboard.processPluginConfigurationUpdateResult(r);
            else toast('Einstellungen gespeichert.');
            loadStatus();
            loadItems();
            if (st.item) reloadItem();
        } catch (err) {
            toast('Speichern fehlgeschlagen: ' + await errorText(err), true);
        }
        return false;
    }

    // ===================== wiring =====================
    $('#stfItems').addEventListener('click', e => {
        const btn = e.target.closest('.stf-an');
        if (btn) {
            e.stopPropagation();
            analyze(btn.dataset.id).catch(err => toast(err.message, true));
            return;
        }
        const row = e.target.closest('.stf-row');
        if (row) openItem(row.dataset.id).catch(err => toast(err.message, true));
    });
    $('#stfSearch').addEventListener('input', renderItems);
    $('#srQuery').addEventListener('input', () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => runSearch().catch(err => toast(err.message, true)), 250);
    });
    $('#srResults').addEventListener('click', e => {
        const b = e.target.closest('.sr-occ');
        if (b) openSong(b.dataset.item, b.dataset.song).catch(err => toast(err.message, true));
    });
    ['#stArtists', '#stSongs'].forEach(sel => $(sel).addEventListener('click', e => {
        const row = e.target.closest('.st-row');
        if (!row) return;
        $('#srQuery').value = row.dataset.q;
        runSearch().catch(err => toast(err.message, true));
        $('#srCard').scrollIntoView({ behavior: 'smooth', block: 'start' });
    }));
    view.querySelectorAll('.np-test').forEach(b => b.addEventListener('click', sendTest));
    view.querySelectorAll('.np-check').forEach(c => c.addEventListener('change', async () => {
        const on = c.checked;
        try {
            await call('PUT', 'Me/NowPlaying', { Enabled: on });
            view.querySelectorAll('.np-check').forEach(x => { x.checked = on; });
            toast(on ? 'Einblendungen beim Schauen sind an.' : 'Einblendungen beim Schauen sind aus.');
        } catch (err) {
            c.checked = !on;
            toast(err.message, true);
        }
    }));
    view.querySelectorAll('.stf-tabs [data-filter]').forEach(b => b.addEventListener('click', () => {
        view.querySelectorAll('.stf-tabs [data-filter]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        st.filter = b.dataset.filter;
        renderItems();
    }));
    $('#stfAnalyzeAll').addEventListener('click', () => act(async () => {
        await call('POST', 'AnalyzeAll');
        toast('Alle noch offenen Titel wurden in die Warteschlange gestellt.');
        loadStatus();
    }));
    $('#stfExportAll').addEventListener('click', () => act(async () => {
        await call('POST', 'ExportAll');
        toast('Alle Songs werden im Hintergrund in Jellyfin gespeichert.');
        loadStatus();
    }));
    $('#stfAppCopy').addEventListener('click', async () => {
        const url = $('#stfAppLink').href;
        try {
            await navigator.clipboard.writeText(url);
            toast('Adresse kopiert – z. B. per Nachricht an deine Benutzer schicken.');
        } catch {
            toast(url);
        }
    });
    $('#stfStop').addEventListener('click', () => act(async () => {
        await call('POST', 'Stop');
        loadStatus();
    }));

    // timeline: buttons/checkboxes give the focus back, so Space/arrow keys always reach the timeline
    $('#tlCard').addEventListener('click', e => {
        const c = e.target.closest('button, input[type=checkbox], input[type=range], a.stf-b');
        if (c) setTimeout(() => c.blur(), 0);
    });

    // timeline
    const inner = $('#tlInner');
    inner.addEventListener('pointerdown', onPointerDown);
    inner.addEventListener('pointermove', onPointerMove);
    inner.addEventListener('pointerup', onPointerUp);
    inner.addEventListener('pointercancel', onPointerUp);
    inner.addEventListener('dblclick', onDblClick);
    $('#tlScroll').addEventListener('wheel', onWheel, { passive: false });
    $('#tlScroll').addEventListener('scroll', () => {
        if (st.progScroll) st.progScroll = false;
        else userScrolled();
        cancelAnimationFrame(rafTicks);
        rafTicks = requestAnimationFrame(() => { renderTicks(); renderMiniViewport(); });
    });
    $('#tlMini').addEventListener('pointerdown', e => { miniDrag = true; $('#tlMini').setPointerCapture(e.pointerId); miniScroll(e); });
    $('#tlMini').addEventListener('pointermove', e => { if (miniDrag) miniScroll(e); });
    $('#tlMini').addEventListener('pointerup', () => { miniDrag = false; });
    $('#tlZoom').addEventListener('input', () => {
        const v = Number($('#tlZoom').value) / 1000;
        const center = ($('#tlScroll').scrollLeft + viewWidth() / 2) / st.pps;
        setPps(st.fitPps * Math.pow(maxPps() / st.fitPps, v), center, viewWidth() / 2);
    });
    view.querySelectorAll('[data-zoom]').forEach(b => b.addEventListener('click', () => {
        if (!st.item) return;
        if (b.dataset.zoom === 'fit') setPps(st.fitPps, 0, 0);
        else zoomToWindow(Number(b.dataset.zoom));
    }));
    $('#tlShowSeg').addEventListener('change', e => { st.showSeg = e.target.checked; renderTimeline(); });
    $('#tlShowDots').addEventListener('change', e => { st.showDots = e.target.checked; renderTimeline(); });
    $('#tlPrev').addEventListener('click', () => st.item && st.item.prevId && openItem(st.item.prevId, true).catch(err => toast(err.message, true)));
    $('#tlNext').addEventListener('click', () => st.item && st.item.nextId && openItem(st.item.nextId, true).catch(err => toast(err.message, true)));
    $('#tlUndo').addEventListener('click', undo);
    $('#tlAdd').addEventListener('click', () => addSongAt(st.cursor));
    $('#tlPlayCursor').addEventListener('click', () => playRange(st.cursor));
    $('#tlFollow').addEventListener('change', e => {
        play.follow = e.target.checked;
        if (play.follow && play.active) setScroll((play.from + audio.currentTime) * st.pps - viewWidth() * 0.1);
    });
    $('#tlStopPlay').addEventListener('click', stopPlay);
    $('#tlReanalyze').addEventListener('click', () => st.item && act(() => analyze(st.item.id)));
    $('#tlExportItem').addEventListener('click', () => exportSongs(null));

    // detail panel
    ['#tlArtist', '#tlSongTitle', '#tlStart', '#tlEnd', '#tlApple'].forEach(sel => {
        const inp = $(sel);
        inp.addEventListener('change', saveDetail);
        inp.addEventListener('keydown', e => {
            if (e.key === 'Enter') { e.preventDefault(); inp.blur(); }
            if (e.key === 'Escape') { const s = selected(); if (s) { fillDetailFields(s); $('#tlApple').value = s.appleMusicUrl || ''; } inp.blur(); }
        });
    });
    view.querySelectorAll('[data-nudge]').forEach(b => b.addEventListener('click', () => {
        const [field, delta] = b.dataset.nudge.split(':');
        nudge(field, Number(delta));
    }));
    $('#tlPlayStart').addEventListener('click', () => selected() && playSongStart(selected()));
    $('#tlPlayEnd').addEventListener('click', () => selected() && playSongEnd(selected()));
    $('#tlSplit').addEventListener('click', splitAtCursor);
    $('#tlMerge').addEventListener('click', mergeWithNext);
    $('#tlDelete').addEventListener('click', deleteSelected);
    let excludeTimer = 0;
    $('#tlExclude').addEventListener('click', () => {
        const btn = $('#tlExclude');
        const pattern = btn.dataset.pattern || '';
        if (!btn.classList.contains('confirm')) {
            // first click only arms the button: this changes all titles
            btn.classList.add('confirm');
            btn.textContent = `Wirklich? „${pattern}“ aus allen Titeln entfernen`;
            clearTimeout(excludeTimer);
            excludeTimer = setTimeout(() => { if (selected()) renderDetail(); }, 5000);
            return;
        }
        clearTimeout(excludeTimer);
        act(async () => {
            const res = await call('POST', 'Exclude', { Pattern: pattern }, true);
            st.sel = null;
            st.undo = []; // removed songs must not come back through "Rückgängig"
            await reloadItem();
            await loadItems();
            loadConfig().catch(() => {});
            toast(`„${res.pattern}“ steht jetzt auf der Ausschlussliste – ${res.removed} ${res.removed === 1 ? 'Song' : 'Songs'} entfernt.`);
        });
    });
    $('#tlExportSong').addEventListener('click', () => selected() && exportSongs([selected().id]));
    $('#tlMp3').addEventListener('click', () => act(async () => {
        const s = selected();
        if (!s) return;
        const btn = $('#tlMp3');
        btn.disabled = true;
        btn.textContent = 'Schneide …';
        try {
            const blob = await getBlob(`Items/${st.item.id}/Songs/${s.id}/Mp3`);
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = `${s.artist ? s.artist + ' - ' : ''}${s.title || 'Song'}.mp3`.replace(/[\\/:*?"<>|]/g, '_');
            document.body.appendChild(a);
            a.click();
            setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 3000);
        } finally {
            btn.disabled = false;
            btn.textContent = 'Szene als MP3';
        }
    }));
    $('#tlList').addEventListener('click', e => {
        const row = e.target.closest('.stf-row');
        if (!row) return;
        select(row.dataset.id, true);
        const s = selected();
        if (s) setCursor(s.start);
    });
    audio.addEventListener('ended', () => { if (play.active) stopPlay(); });
    audio.addEventListener('timeupdate', () => { if (play.active && play.from + audio.currentTime >= play.to) stopPlay(); });
    audio.addEventListener('pause', () => { if (play.active && !audio.ended) stopPlay(); }); // e.g. media keys / headphones
    audio.addEventListener('error', () => {
        if (!play.active) return;
        stopPlay();
        toast('Wiedergabe abgebrochen (Stream nicht verfügbar).', true);
    });

    $('#stfConfigForm').addEventListener('submit', saveConfig);

    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(() => {
        if (!st.item) return;
        const center = ($('#tlScroll').scrollLeft + viewWidth() / 2) / st.pps;
        const wasFit = Math.abs(st.pps - st.fitPps) < 1e-9;
        computeFit();
        setPps(wasFit ? st.fitPps : st.pps, center, viewWidth() / 2);
    }) : null;

    view.addEventListener('viewshow', () => {
        loadItems().then(openFromAddress).catch(err => toast(err.message, true));
        loadMe().catch(() => {});
        loadNowPlayingStatus();
        clearInterval(npTimer);
        npTimer = setInterval(loadNowPlayingStatus, 5000);
        clearInterval(statusTimer);
        if (!userMode) {
            loadConfig().catch(err => toast(err.message, true));
            loadStatus();
            statusTimer = setInterval(loadStatus, 5000);
        }
        document.addEventListener('keydown', onKey);
        if (ro) ro.observe($('#tlScroll'));
    });
    view.addEventListener('viewhide', () => {
        clearInterval(statusTimer);
        clearInterval(npTimer);
        document.removeEventListener('keydown', onKey);
        if (ro) ro.disconnect();
        stopPlay();
        flushPending().catch(() => {});
    });
}
