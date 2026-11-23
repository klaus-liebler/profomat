// Browserprüfung „Anmeldung wie Podium“ auf einer LEEREN Datenbank (wie der Server: ohne Demodaten, ohne Testanmeldung):
// Ersteinrichtung, Feldbreiten, Einladungslink (Token hinter #), Einlösen in einem zweiten Browser, Bremse nach Fehlversuchen,
// öffentliches Antragsformular ohne Anmeldung, 390 px.
// Aufruf: node tests/Browser/anmeldung.mjs http://localhost:5311/profomat <Ordner>
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
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);
    await warte(800);
    pruefe((await seite.text()).includes('Prof. Dr. Klaus Liebler'), 'angemeldet als Prof. Dr. Klaus Liebler');

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

        console.log('• Öffentliches Antragsformular ohne Anmeldung');
        await fremd.oeffnen(basis + 'drittpruefung/antrag');
        await warte(600);
        pruefe((await fremd.text()).includes('Antrag auf mündliche Prüfung im dritten Versuch'), 'Antragsformular erreichbar');
        pruefe(await fremd.eval(`document.getElementById('webseite') && getComputedStyle(document.querySelector('.pm-koeder')).position === 'absolute'`),
            'Köderfeld vorhanden und unsichtbar');
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
