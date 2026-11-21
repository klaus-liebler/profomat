// ProfOMat – Unterschreiben im Browser (ES-Modul, per JS-Interop geladen).
//
// Der private Schlüssel wird NUR hier gelesen und benutzt (WebCrypto). Er geht weder an .NET noch an den Server; auch das
// Passwort eines verschlüsselten Schlüssels liest nur dieses Skript. Zum Server gehen ausschließlich Signatur und
// öffentlicher Schlüssel (JWK). Unterstützt: PKCS#8 („PRIVATE KEY“), PKCS#8 verschlüsselt mit PBES2/PBKDF2/AES-CBC
// („ENCRYPTED PRIVATE KEY“, z. B. openssl genpkey … -aes-256-cbc), PKCS#1 („RSA PRIVATE KEY“), SEC1 („EC PRIVATE KEY“),
// DER-Dateien und JWK-Dateien (JSON mit „d“). Algorithmen: ECDSA P-256/P-384/P-521, RSA (PKCS#1 v1.5, SHA-256).

const geladen = new WeakMap();   // Ablage-Element → { key, jwk, algorithmus, signAlg, beschreibung }
const ausstehend = new WeakMap(); // Ablage-Element → verschlüsselte DER-Daten, bis das Passwort eingegeben ist
const zeichnungen = new WeakMap(); // Canvas → { striche, laenge }

const OID = {
    ec: '1.2.840.10045.2.1', rsa: '1.2.840.113549.1.1.1',
    p256: '1.2.840.10045.3.1.7', p384: '1.3.132.0.34', p521: '1.3.132.0.35',
    pbes2: '1.2.840.113549.1.5.13', pbkdf2: '1.2.840.113549.1.5.12',
    hmacSha1: '1.2.840.113549.2.7', hmacSha256: '1.2.840.113549.2.9', hmacSha384: '1.2.840.113549.2.10', hmacSha512: '1.2.840.113549.2.11',
    aes128cbc: '2.16.840.1.101.3.4.1.2', aes256cbc: '2.16.840.1.101.3.4.1.42',
};

// ---------------------------------------------------------------- DER lesen und schreiben (nur was hier gebraucht wird)
function knoten(bytes, pos) {
    if (pos + 2 > bytes.length) { throw new Error('Die Schlüsseldatei ist beschädigt.'); }
    const tag = bytes[pos];
    let laenge = bytes[pos + 1];
    let kopf = 2;
    if (laenge & 0x80) {
        const n = laenge & 0x7f;
        laenge = 0;
        for (let i = 0; i < n; i++) { laenge = (laenge * 256) + bytes[pos + 2 + i]; }
        kopf += n;
    }
    const start = pos + kopf;
    if (start + laenge > bytes.length) { throw new Error('Die Schlüsseldatei ist beschädigt.'); }
    return { tag, start, ende: start + laenge, inhalt: bytes.subarray(start, start + laenge) };
}
function kinder(k, bytes) {
    const liste = [];
    for (let p = k.start; p < k.ende;) { const c = knoten(bytes, p); liste.push(c); p = c.ende; }
    return liste;
}
function oidLesen(inhalt) {
    const teile = [Math.floor(inhalt[0] / 40), inhalt[0] % 40];
    let wert = 0;
    for (let i = 1; i < inhalt.length; i++) {
        wert = (wert * 128) + (inhalt[i] & 0x7f);
        if (!(inhalt[i] & 0x80)) { teile.push(wert); wert = 0; }
    }
    return teile.join('.');
}
function zahlLesen(inhalt) { let n = 0; for (const b of inhalt) { n = (n * 256) + b; } return n; }
function laengeKodieren(n) {
    if (n < 128) { return [n]; }
    const b = [];
    while (n > 0) { b.unshift(n & 0xff); n = Math.floor(n / 256); }
    return [0x80 | b.length, ...b];
}
function tlv(tag, inhalt) {
    const l = laengeKodieren(inhalt.length);
    const r = new Uint8Array(1 + l.length + inhalt.length);
    r[0] = tag; r.set(l, 1); r.set(inhalt, 1 + l.length);
    return r;
}
function verbinden(...teile) {
    const r = new Uint8Array(teile.reduce((s, t) => s + t.length, 0));
    let p = 0;
    for (const t of teile) { r.set(t, p); p += t.length; }
    return r;
}
function oidKodieren(text) {
    const t = text.split('.').map(Number);
    const b = [(t[0] * 40) + t[1]];
    for (const z of t.slice(2)) {
        const g = [z & 0x7f];
        let v = Math.floor(z / 128);
        while (v > 0) { g.unshift(0x80 | (v & 0x7f)); v = Math.floor(v / 128); }
        b.push(...g);
    }
    return tlv(0x06, new Uint8Array(b));
}
const NULL = new Uint8Array([0x05, 0x00]);
const VERSION0 = new Uint8Array([0x02, 0x01, 0x00]);

/** PKCS#1 (RSA PRIVATE KEY) → PKCS#8 */
function rsaPkcs1ZuPkcs8(der) {
    return tlv(0x30, verbinden(VERSION0, tlv(0x30, verbinden(oidKodieren(OID.rsa), NULL)), tlv(0x04, der)));
}
/** SEC1 (EC PRIVATE KEY) → PKCS#8; die Kurve steht im Feld [0] der SEC1-Struktur. */
function ecSec1ZuPkcs8(der) {
    const teile = kinder(knoten(der, 0), der);
    const params = teile.find((t) => t.tag === 0xa0);
    if (!params) { throw new Error('Im EC-Schlüssel fehlt die Angabe der Kurve. Bitte als PKCS#8 exportieren (openssl pkcs8 -topk8).'); }
    const kurve = oidLesen(kinder(params, der)[0].inhalt);
    return tlv(0x30, verbinden(VERSION0, tlv(0x30, verbinden(oidKodieren(OID.ec), oidKodieren(kurve))), tlv(0x04, der)));
}

// ---------------------------------------------------------------- Schlüssel lesen
function base64ZuBytes(b64) { const roh = atob(b64.replace(/\s+/g, '')); return Uint8Array.from(roh, (c) => c.charCodeAt(0)); }
function bytesZuBase64(bytes) { let s = ''; for (let i = 0; i < bytes.length; i += 0x8000) { s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000)); } return btoa(s); }
function base64Url(b64) { return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }

/** Liefert { art: 'pkcs8' | 'verschluesselt' | 'jwk', daten } aus dem Dateiinhalt. */
function dateiDeuten(bytes) {
    const text = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
    if (text.trimStart().startsWith('{')) {
        let jwk;
        try { jwk = JSON.parse(text); } catch { throw new Error('Die JSON-Datei ist kein gültiger Schlüssel (JWK).'); }
        if (!jwk.d) { throw new Error('Die JWK-Datei enthält keinen privaten Schlüssel.'); }
        return { art: 'jwk', daten: jwk };
    }
    const bloecke = [...text.matchAll(/-----BEGIN ([A-Z0-9 ]+)-----([\s\S]*?)-----END \1-----/g)];
    if (bloecke.length === 0) {
        if (bytes[0] === 0x30) { return { art: 'pkcs8', daten: bytes }; }   // DER
        throw new Error('Keine Schlüsseldatei erkannt. Erwartet wird eine PEM-, DER- oder JWK-Datei mit privatem Schlüssel.');
    }
    const schluessel = bloecke.find((b) => b[1].includes('PRIVATE KEY'));
    if (!schluessel) {
        throw new Error('Die Datei enthält keinen privaten Schlüssel (nur ' + bloecke.map((b) => b[1]).join(', ') + ').');
    }
    const typ = schluessel[1];
    if (/Proc-Type:\s*4,ENCRYPTED/.test(schluessel[2])) {
        throw new Error('Dieses ältere Verschlüsselungsformat wird nicht unterstützt. Bitte umwandeln: openssl pkcs8 -topk8 -v2 aes-256-cbc -in alt.pem -out neu.pem');
    }
    const der = base64ZuBytes(schluessel[2]);
    switch (typ) {
        case 'PRIVATE KEY': return { art: 'pkcs8', daten: der };
        case 'ENCRYPTED PRIVATE KEY': return { art: 'verschluesselt', daten: der };
        case 'RSA PRIVATE KEY': return { art: 'pkcs8', daten: rsaPkcs1ZuPkcs8(der) };
        case 'EC PRIVATE KEY': return { art: 'pkcs8', daten: ecSec1ZuPkcs8(der) };
        default: throw new Error('Schlüsseltyp „' + typ + '“ wird nicht unterstützt.');
    }
}

/** PBES2 (PBKDF2 + AES-CBC) entschlüsseln. */
async function entschluesseln(der, passwort) {
    const wurzel = knoten(der, 0);
    const [algId, daten] = kinder(wurzel, der);
    const [algOid, params] = kinder(algId, der);
    if (oidLesen(algOid.inhalt) !== OID.pbes2) {
        throw new Error('Dieses Verschlüsselungsverfahren wird nicht unterstützt (nur PBES2 mit PBKDF2 und AES). Bitte umwandeln: openssl pkcs8 -topk8 -v2 aes-256-cbc');
    }
    const [kdf, verfahren] = kinder(params, der);
    const [kdfOid, kdfParams] = kinder(kdf, der);
    if (oidLesen(kdfOid.inhalt) !== OID.pbkdf2) { throw new Error('Nur PBKDF2 wird als Schlüsselableitung unterstützt.'); }
    const kp = kinder(kdfParams, der);
    const salz = kp[0].inhalt;
    const runden = zahlLesen(kp[1].inhalt);
    let hash = 'SHA-1';
    for (const k of kp.slice(2)) {
        if (k.tag === 0x30) {
            hash = { [OID.hmacSha1]: 'SHA-1', [OID.hmacSha256]: 'SHA-256', [OID.hmacSha384]: 'SHA-384', [OID.hmacSha512]: 'SHA-512' }[oidLesen(kinder(k, der)[0].inhalt)];
            if (!hash) { throw new Error('Unbekannte Hash-Funktion in der Schlüsselableitung.'); }
        }
    }
    const [vOid, iv] = kinder(verfahren, der);
    const bits = { [OID.aes128cbc]: 128, [OID.aes256cbc]: 256 }[oidLesen(vOid.inhalt)];
    if (!bits) { throw new Error('Nur AES-128-CBC und AES-256-CBC werden unterstützt.'); }
    const basis = await crypto.subtle.importKey('raw', new TextEncoder().encode(passwort), 'PBKDF2', false, ['deriveKey']);
    const aes = await crypto.subtle.deriveKey({ name: 'PBKDF2', salt: salz, iterations: runden, hash }, basis, { name: 'AES-CBC', length: bits }, false, ['decrypt']);
    try {
        return new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-CBC', iv: iv.inhalt }, aes, daten.inhalt));
    } catch {
        throw new Error('Das Passwort stimmt nicht.');
    }
}

async function fingerabdruck(jwk) {
    const kanonisch = jwk.kty === 'EC'
        ? `{"crv":"${jwk.crv}","kty":"EC","x":"${jwk.x}","y":"${jwk.y}"}`
        : `{"e":"${jwk.e}","kty":"RSA","n":"${jwk.n}"}`;
    const h = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(kanonisch)));
    const hex = [...h].map((b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
    return hex.substring(0, 16).match(/.{4}/g).join(' ');
}

const KURVEN = { [OID.p256]: 'P-256', [OID.p384]: 'P-384', [OID.p521]: 'P-521' };
const HASH_ZUR_KURVE = { 'P-256': 'SHA-256', 'P-384': 'SHA-384', 'P-521': 'SHA-512' };

function verfahrenAusJwk(jwk) {
    if (jwk.kty === 'EC') {
        if (!HASH_ZUR_KURVE[jwk.crv]) { throw new Error('Kurve ' + jwk.crv + ' wird nicht unterstützt.'); }
        return { importParams: { name: 'ECDSA', namedCurve: jwk.crv }, kurve: jwk.crv };
    }
    if (jwk.kty === 'RSA') { return { importParams: { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' } }; }
    throw new Error('Schlüsseltyp ' + jwk.kty + ' wird nicht unterstützt (RSA oder EC).');
}

function verfahrenAusPkcs8(der) {
    const teile = kinder(knoten(der, 0), der);
    const alg = kinder(teile[1], der);
    const algOid = oidLesen(alg[0].inhalt);
    if (algOid === OID.ec) {
        const kurve = KURVEN[oidLesen(alg[1].inhalt)];
        if (!kurve) { throw new Error('Diese EC-Kurve wird nicht unterstützt (P-256, P-384, P-521).'); }
        return { importParams: { name: 'ECDSA', namedCurve: kurve }, kurve };
    }
    if (algOid === OID.rsa) { return { importParams: { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' } }; }
    throw new Error('Schlüsseltyp nicht unterstützt (RSA oder EC P-256/P-384/P-521; Ed25519 folgt später).');
}

async function importieren(art, daten) {
    const v = art === 'jwk' ? verfahrenAusJwk(daten) : verfahrenAusPkcs8(daten);
    const format = art === 'jwk' ? 'jwk' : 'pkcs8';
    const exportierbar = await crypto.subtle.importKey(format, daten, v.importParams, true, ['sign']);
    const jwk = await crypto.subtle.exportKey('jwk', exportierbar);
    const oeffentlich = jwk.kty === 'EC' ? { kty: 'EC', crv: jwk.crv, x: jwk.x, y: jwk.y } : { kty: 'RSA', n: jwk.n, e: jwk.e };
    // Zum Signieren eine NICHT exportierbare Kopie behalten.
    const key = await crypto.subtle.importKey(format, daten, v.importParams, false, ['sign']);
    let algorithmus, signAlg, beschreibung;
    if (oeffentlich.kty === 'EC') {
        const hash = HASH_ZUR_KURVE[v.kurve];
        algorithmus = 'ECDSA-' + hash.replace('-', '');
        signAlg = { name: 'ECDSA', hash };
        beschreibung = 'EC ' + v.kurve;
    } else {
        const n = oeffentlich.n.replace(/-/g, '+').replace(/_/g, '/');
        const bits = base64ZuBytes(n + '='.repeat((4 - (n.length % 4)) % 4)).length * 8;
        if (bits < 2048) { throw new Error('RSA-Schlüssel müssen mindestens 2048 Bit lang sein.'); }
        algorithmus = 'RSASSA-PKCS1-v1_5-SHA256';
        signAlg = { name: 'RSASSA-PKCS1-v1_5' };
        beschreibung = 'RSA ' + bits + ' Bit';
    }
    return { key, jwk: oeffentlich, algorithmus, signAlg, beschreibung, fingerabdruck: await fingerabdruck(oeffentlich) };
}

// ---------------------------------------------------------------- Ablage (Drag & Drop, Dateiauswahl, Passwort)
/**
 * Richtet die Ablagefläche ein. Erwartet im Element: input[type=file], optional [data-passwort-bereich] mit
 * input[type=password] und [data-passwort-ok]. Meldet an .NET: SchluesselGeladen(info), SchluesselFehler(text), PasswortNoetig().
 */
export function ablageEinrichten(ablage, dotnet) {
    const datei = ablage.querySelector('input[type=file]');
    const bereich = ablage.parentElement.querySelector('[data-passwort-bereich]');
    const passwortFeld = bereich && bereich.querySelector('input[type=password]');
    const ok = bereich && bereich.querySelector('[data-passwort-ok]');

    const melden = async (fn) => {
        try {
            const s = await fn();
            if (s) {
                geladen.set(ablage, s);
                ausstehend.delete(ablage);
                ablage.classList.add('geladen');
                if (passwortFeld) { passwortFeld.value = ''; }
                await dotnet.invokeMethodAsync('SchluesselGeladen', { beschreibung: s.beschreibung, fingerabdruck: s.fingerabdruck, algorithmus: s.algorithmus });
            }
        } catch (fehler) {
            ablage.classList.remove('geladen');
            await dotnet.invokeMethodAsync('SchluesselFehler', fehler && fehler.message ? fehler.message : String(fehler));
        }
    };
    const verarbeiten = (file) => melden(async () => {
        geladen.delete(ablage);
        if (file.size > 64 * 1024) { throw new Error('Die Datei ist zu groß für einen Schlüssel.'); }
        const gedeutet = dateiDeuten(new Uint8Array(await file.arrayBuffer()));
        if (gedeutet.art === 'verschluesselt') {
            ausstehend.set(ablage, gedeutet.daten);
            await dotnet.invokeMethodAsync('PasswortNoetig');
            if (passwortFeld) { setTimeout(() => passwortFeld.focus(), 50); }
            return null;
        }
        return importieren(gedeutet.art, gedeutet.daten);
    });

    ablage.addEventListener('click', (e) => { if (e.target !== datei) { datei.click(); } });
    ablage.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); datei.click(); } });
    datei.addEventListener('change', () => { if (datei.files[0]) { verarbeiten(datei.files[0]); datei.value = ''; } });
    ablage.addEventListener('dragover', (e) => { e.preventDefault(); ablage.classList.add('ueber'); });
    ablage.addEventListener('dragleave', () => ablage.classList.remove('ueber'));
    ablage.addEventListener('drop', (e) => {
        e.preventDefault();
        ablage.classList.remove('ueber');
        if (e.dataTransfer.files[0]) { verarbeiten(e.dataTransfer.files[0]); }
    });
    const entsperren = () => melden(async () => {
        const der = ausstehend.get(ablage);
        if (!der) { return null; }
        return importieren('pkcs8', await entschluesseln(der, passwortFeld.value));
    });
    if (ok) { ok.addEventListener('click', (e) => { e.preventDefault(); entsperren(); }); }
    if (passwortFeld) { passwortFeld.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); entsperren(); } }); }
}

/** Signiert den Text (UTF-8) mit dem geladenen Schlüssel. */
export async function signieren(ablage, text) {
    const s = geladen.get(ablage);
    if (!s) { throw new Error('Bitte zuerst den privaten Schlüssel in das Feld ziehen.'); }
    const sig = new Uint8Array(await crypto.subtle.sign(s.signAlg, s.key, new TextEncoder().encode(text)));
    return { algorithmus: s.algorithmus, signatur: bytesZuBase64(sig), jwk: JSON.stringify(s.jwk) };
}

/** Schlüssel aus dem Speicher der Seite entfernen. */
export function vergessen(ablage) {
    geladen.delete(ablage);
    ausstehend.delete(ablage);
    ablage.classList.remove('geladen');
}

// ---------------------------------------------------------------- Unterschriftsfeld (Maus, Stift, Finger)
const BREITE = 900;
const HOEHE = 300;

function leeren(canvas) {
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    zeichnungen.set(canvas, { striche: 0, laenge: 0 });
}

/** Meldet an .NET: ZeichnungGeaendert(bool ausreichend). */
export function zeichenfeldEinrichten(canvas, dotnet) {
    canvas.width = BREITE;
    canvas.height = HOEHE;
    leeren(canvas);
    const ctx = canvas.getContext('2d');
    ctx.lineWidth = 3.2;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = '#0b1f3a';
    let letzter = null;
    const punkt = (e) => {
        const r = canvas.getBoundingClientRect();
        return { x: (e.clientX - r.left) * canvas.width / r.width, y: (e.clientY - r.top) * canvas.height / r.height };
    };
    canvas.addEventListener('pointerdown', (e) => {
        e.preventDefault();
        canvas.setPointerCapture(e.pointerId);
        letzter = punkt(e);
        const z = zeichnungen.get(canvas);
        z.striche++;
        ctx.beginPath();
        ctx.arc(letzter.x, letzter.y, ctx.lineWidth / 2, 0, Math.PI * 2);
        ctx.fillStyle = ctx.strokeStyle;
        ctx.fill();
    });
    canvas.addEventListener('pointermove', (e) => {
        if (!letzter) { return; }
        const p = punkt(e);
        ctx.beginPath();
        ctx.moveTo(letzter.x, letzter.y);
        ctx.lineTo(p.x, p.y);
        ctx.stroke();
        zeichnungen.get(canvas).laenge += Math.hypot(p.x - letzter.x, p.y - letzter.y);
        letzter = p;
    });
    const ende = () => {
        if (!letzter) { return; }
        letzter = null;
        const z = zeichnungen.get(canvas);
        dotnet.invokeMethodAsync('ZeichnungGeaendert', z.striche > 0 && z.laenge > 120);
    };
    canvas.addEventListener('pointerup', ende);
    canvas.addEventListener('pointercancel', ende);
}

export function zeichenfeldLeeren(canvas) { leeren(canvas); }

/** PNG (Base64) der Unterschrift. */
export function zeichenfeldPng(canvas) { return canvas.toDataURL('image/png').split(',')[1]; }

/** Nur für Tests (tests/Signatur/signatur-test.mjs): Schlüssel lesen und signieren ohne DOM. */
export const intern = {
    async laden(bytes, passwort) {
        const g = dateiDeuten(bytes);
        const daten = g.art === 'verschluesselt' ? await entschluesseln(g.daten, passwort) : g.daten;
        return importieren(g.art === 'verschluesselt' ? 'pkcs8' : g.art, daten);
    },
    async signieren(s, text) {
        const sig = new Uint8Array(await crypto.subtle.sign(s.signAlg, s.key, new TextEncoder().encode(text)));
        return { algorithmus: s.algorithmus, signatur: bytesZuBase64(sig), jwk: JSON.stringify(s.jwk) };
    },
};
