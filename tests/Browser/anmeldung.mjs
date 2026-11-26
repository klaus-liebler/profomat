// Browserprüfung der Anmeldung (ASP.NET Core Identity) auf einer LEEREN Datenbank (wie der Server: ohne Demodaten, ohne Testanmeldung):
// Ersteinrichtung mit Pflicht-TOTP (QR-Code, Code, Wiederherstellungscodes), Passkey hinzufügen und damit anmelden (virtueller
// Authenticator über CDP), Feldbreiten, Einladungslink (Token hinter #), Einlösen in einem zweiten Browser, Sperre nach Fehlversuchen,
// öffentliches Antragsformular ohne Anmeldung, 390 px.
// Aufruf: node tests/Browser/anmeldung.mjs http://localhost:5311/profomat <Ordner>
import { createHmac } from 'node:crypto';
import { mkdirSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const warte = (ms) => new Promise((r) => setTimeout(r, ms));
const pruefe = (bedingung, text) => { if (!bedingung) { throw new Error(text); } console.log('  ✓', text); };
const PW = 'ein-langes-Passwort-1';

// Werte in ein statisches Formular schreiben (ohne Blazor-Bindung) und absenden
const formular = (seite, werte, knopf) => seite.eval(`(() => { const w = ${JSON.stringify(werte)};
    for (const [id, v] of Object.entries(w)) { const el = document.getElementById(id); if (!el) throw new Error('fehlt ' + id); el.value = v; }
    [...document.querySelectorAll('button[type=submit]')].find(b => b.textContent.includes(${JSON.stringify(knopf)})).click(); })()`);
// TOTP wie eine Authenticator-App (RFC 6238: SHA-1, 6 Stellen, 30 s)
function totp(base32) {
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
    let bits = '';
    for (const c of base32.replace(/[\s=]/g, '').toUpperCase()) { bits += alphabet.indexOf(c).toString(2).padStart(5, '0'); }
    const schluessel = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
    const zaehler = Buffer.alloc(8);
    zaehler.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
    const h = createHmac('sha1', schluessel).update(zaehler).digest();
    const o = h[h.length - 1] & 0x0f;
    return String((h.readUInt32BE(o) & 0x7fffffff) % 1000000).padStart(6, '0');
}
const breite = (seite, id) => seite.eval(`Math.round(document.getElementById('${id}').getBoundingClientRect().width)`);

const { seite, beenden } = await browserStarten({ port: 9351, breite: 1280, hoehe: 900 });
try {
    console.log('• Ersteinrichtung auf leerer Datenbank');
    await seite.oeffnen(basis + 'anmelden');
    pruefe((await seite.text()).includes('Es gibt noch kein Konto'), 'Anmeldeseite zeigt die Ersteinrichtung');
    pruefe(!(await seite.text()).includes('Testanmeldung'), 'keine Testanmeldung');
    const [wk, wp, wp2] = [await breite(seite, 'e-kennung'), await breite(seite, 'e-pw'), await breite(seite, 'e-pw2')];
    pruefe(wp === wk && wp2 === wk, `Passwortfelder so breit wie die Kennung (${wk}/${wp}/${wp2} px)`);
    await seite.bild(join(bilder, 'a1-ersteinrichtung.png'));
    await formular(seite, { 'e-kennung': 'Liebler', 'e-titel': 'Prof. Dr.', 'e-vorname': 'Klaus', 'e-nachname': 'Liebler', 'e-mail': 'kl@example.invalid',
        'e-pw': PW, 'e-pw2': PW }, 'Konto anlegen');
    await seite.warteAuf(`location.pathname.endsWith('/anmelden/zweiter-faktor')`);
    await warte(600);
    pruefe((await seite.text()).includes('Zweiten Faktor einrichten'), 'Systemverwaltung muss zuerst den zweiten Faktor einrichten');
    pruefe(await seite.eval(`document.querySelector('img.pm-qr')?.src.startsWith('data:image/svg+xml')`), 'QR-Code als SVG');
    await seite.bild(join(bilder, 'a1b-totp-einrichten.png'));
    const geheim = await seite.eval(`document.querySelector('.pm-schluessel').textContent`);
    await formular(seite, { 'e-code': '000000' }, 'Einrichten');
    await warte(900);
    pruefe((await seite.text()).includes('Der Code stimmt nicht'), 'falscher Code abgelehnt');
    await formular(seite, { 'e-code': totp(geheim) }, 'Einrichten');
    await seite.warteAuf(`document.body.innerText.includes('Wiederherstellungscodes')`);
    pruefe(await seite.eval(`document.querySelectorAll('.pm-codes li').length`) === 10, 'zehn Wiederherstellungscodes angezeigt');
    await seite.bild(join(bilder, 'a1c-wiederherstellungscodes.png'));
    await seite.klickText('a', 'Weiter zu ProfOMat');
    await seite.warteAuf(`!location.pathname.includes('/anmelden')`);
    await warte(800);
    pruefe((await seite.text()).includes('Prof. Dr. Klaus Liebler'), 'angemeldet als Prof. Dr. Klaus Liebler');

    console.log('• Passkey hinzufügen und damit anmelden (virtueller Authenticator)');
    await seite.cdp('WebAuthn.enable', { enableUI: false });
    await seite.cdp('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true,
        hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
    await seite.oeffnen(basis + 'profil/sicherheit');
    pruefe((await seite.text()).includes('Anmeldung und Sicherheit'), 'Seite „Anmeldung und Sicherheit“');
    pruefe((await seite.text()).includes('eingerichtet'), 'zweiter Faktor als eingerichtet angezeigt');
    await seite.eval(`document.getElementById('pk-name').value = 'Virtueller Schlüssel'`);
    await seite.klick('[data-passkey="hinzufuegen"]');
    await seite.warteAuf(`document.body.innerText.includes('Passkey hinzugefügt')`);
    pruefe((await seite.text()).includes('Virtueller Schlüssel'), 'Passkey in der Liste');
    await seite.bild(join(bilder, 'a1d-sicherheit.png'));
    await seite.eval(`document.querySelector('form[action$="abmelden"] button').click()`);
    await warte(1200);
    await seite.oeffnen(basis + 'anmelden');
    await seite.klick('[data-passkey="anmelden"]');
    await seite.warteAuf(`!location.pathname.includes('/anmelden')`);
    await warte(800);
    pruefe((await seite.text()).includes('Prof. Dr. Klaus Liebler'), 'mit Passkey angemeldet – ohne Passwort und ohne weiteren Code');

    console.log('• Einladungslink erzeugen');
    await seite.oeffnen(basis + 'konten');
    await seite.eingeben('#notiz', 'Prof. Dr. Niemeyer');
    await seite.klickText('button', 'Einladungslink erzeugen');
    await warte(700);
    const link = await seite.eval(`document.getElementById('pm-link').value`);
    pruefe(/\/einladung#[A-Za-z0-9_-]{40,}$/.test(link), `Link mit Token hinter # (${link.replace(/#.*/, '#…')})`);
    pruefe((await seite.text()).includes('Offene Links'), 'offener Link in der Übersicht');
    await seite.bild(join(bilder, 'a2-konten.png'));

    console.log('• Einladung in einem zweiten Browser einlösen');
    const { seite: neu, beenden: beenden2 } = await browserStarten({ port: 9352, breite: 390, hoehe: 844 });
    try {
        await neu.oeffnen(link);
        await neu.warteAuf(`document.getElementById('i-kennung')`);
        pruefe(!(await neu.eval('location.href')).includes('#'), 'Token aus der Adresszeile entfernt');
        pruefe((await neu.text()).includes('Prüfende'), 'Einladung nennt die Rolle Prüfende');
        const [k, p] = [await breite(neu, 'i-kennung'), await breite(neu, 'i-pw')];
        pruefe(k === p, `Kennung und Passwort gleich breit (${k}/${p} px)`);
        const quer = await neu.eval('document.documentElement.scrollWidth');
        pruefe(quer <= 392, `390 px ohne waagerechte Rollleiste (${quer})`);
        await neu.bild(join(bilder, 'a3-einladung-390px.png'));
        await formular(neu, { 'i-kennung': 'niemeyer', 'i-titel': 'Prof. Dr.', 'i-vorname': 'Philip', 'i-nachname': 'Niemeyer', 'i-mail': 'pn@example.invalid',
            'i-pw': PW, 'i-pw2': PW }, 'Konto anlegen');
        await neu.warteAuf(`!location.pathname.endsWith('/einladung')`);
        await warte(800);
        pruefe((await neu.text()).includes('Prof. Dr. Philip Niemeyer'), 'eingeladene Person ist direkt angemeldet');

        console.log('• Link ein zweites Mal: ungültig');
        await neu.oeffnen(basis + 'anmelden');
        await neu.eval(`document.querySelector('form[action$="abmelden"] button, button.pm-abmelden')?.click()`);
        await neu.oeffnen(link);
        await warte(1200);
        pruefe((await neu.text()).includes('ungültig, abgelaufen oder wurde schon verwendet'), 'zweites Einlösen abgelehnt');
    } finally {
        await beenden2();
    }

    console.log('• Bremse nach Fehlversuchen');
    const { seite: fremd, beenden: beenden3 } = await browserStarten({ port: 9353, breite: 1280, hoehe: 900 });
    try {
        for (let i = 1; i <= 5; i++) {
            await fremd.oeffnen(basis + 'anmelden');
            await formular(fremd, { kennung: 'niemeyer', pw: 'falsch-falsch-' + i }, 'Anmelden');
            await warte(900);
        }
        await fremd.oeffnen(basis + 'anmelden');
        await formular(fremd, { kennung: 'niemeyer', pw: PW }, 'Anmelden');
        await warte(900);
        pruefe((await fremd.text()).includes('Zu viele Fehlversuche'), 'nach 5 Fehlversuchen gesperrt, auch mit richtigem Passwort');
        await fremd.breite(390, 844);
        await warte(400);
        pruefe(await fremd.eval('document.documentElement.scrollWidth') <= 392, 'Anmeldeseite mit 390 px ohne waagerechte Rollleiste');
        await fremd.bild(join(bilder, 'a4-anmelden-390px.png'));
        await fremd.breite(1280, 900);

        console.log('• Persönliche Links der Studierenden ohne Anmeldung');
        pruefe(!(await fremd.eval(`!!document.querySelector('a[href$="drittpruefung/antrag"]')`)), 'kein Link auf das alte Antragsformular');
        await fremd.oeffnen(basis + 'drittpruefung/00000000-0000-0000-0000-000000000000/falsch');
        await warte(600);
        pruefe((await fremd.eval('location.pathname')).includes('/drittpruefung/'), 'Studierendenlink ohne Umleitung zur Anmeldung erreichbar');
        pruefe((await fremd.text()).includes('Link ungültig'), 'falscher Link: „Link ungültig“');
        await fremd.oeffnen(basis + 'konten');
        await warte(600);
        pruefe((await fremd.eval('location.pathname')).endsWith('/anmelden'), 'Konten nur mit Anmeldung');
    } finally {
        await beenden3();
    }
    console.log('Alles in Ordnung. Bilder:', bilder);
} finally {
    await beenden();
}
