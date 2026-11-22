// #claude
// Start script of the "Soundtracks" page for all signed-in Jellyfin users.
// Uses the login of the Jellyfin web app in this browser (same address) and provides the small
// ApiClient subset the shared timeline code (configPage.js) needs.
import init, { loadLanguage, applyStatic, T } from './AppTimeline';

const base = new URL(document.baseURI).pathname.replace(/\/SoundtrackFinder\/?$/i, '');
const msg = document.getElementById('appMsg');
const page = document.getElementById('SoundtrackFinderPage');
const loginUrl = base + '/web/#/login';

function findToken() {
    try {
        const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
        const servers = (creds.Servers || []).filter(s => s && s.AccessToken);
        const origin = location.origin.toLowerCase();
        const same = servers.find(s => [s.ManualAddress, s.LocalAddress, s.RemoteAddress]
            .some(a => typeof a === 'string' && a.toLowerCase().startsWith(origin)));
        const pick = same || servers.sort((a, b) => (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0))[0];
        return pick ? { token: pick.AccessToken, userId: pick.UserId || null } : null;
    } catch {
        return null;
    }
}

function showMessage(html) {
    msg.innerHTML = html;
    msg.style.display = '';
    page.style.display = 'none';
}

const login = findToken();
const token = login ? login.token : null;
const authHeader = () => `MediaBrowser Token="${token}"`;

window.ApiClient = {
    getUrl: path => base + '/' + String(path).replace(/^\//, ''),
    accessToken: () => token,
    getCurrentUserId: () => (login ? login.userId : null),
    isSoundtrackApp: true,
    ajax: async req => {
        const headers = Object.assign({}, req.headers || {}, { Authorization: authHeader() });
        if (req.contentType) headers['Content-Type'] = req.contentType;
        const res = await fetch(req.url, { method: req.type || 'GET', headers, body: req.data });
        if (res.status >= 400) throw res;
        if (req.dataType === 'json') return res.json();
        return res;
    }
};

async function start() {
    await loadLanguage(false);
    applyStatic(document.querySelector('.app-top'));
    msg.textContent = T('common.loading');
    if (!token) {
        showMessage(T('page.login', { url: loginUrl }));
        return;
    }
    let me;
    try {
        me = await window.ApiClient.ajax({ url: window.ApiClient.getUrl('SoundtrackFinder/Me'), dataType: 'json' });
    } catch (e) {
        if (e && e.status === 401) {
            showMessage(T('page.expired', { url: loginUrl }));
        } else {
            showMessage(T('page.unreachable'));
        }
        return;
    }
    document.getElementById('appWho').textContent = T('page.signedInAs', { name: me.name });
    msg.style.display = 'none';
    page.style.display = '';
    const picker = page.querySelector('.stf-langpick');
    if (picker) document.getElementById('appWho').before(picker); // language choice next to "angemeldet als"
    init(page);
    page.dispatchEvent(new Event('viewshow'));
}

start();
