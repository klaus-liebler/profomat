// Browserprüfung des ganzen Ablaufs „mündliche Drittprüfung per Einladung“ gegen eine laufende Testinstanz (Entwicklung, Demodaten).
// Erstprüferin erzeugt den Link → Studentin ergänzt und unterschreibt → Erstprüferin ergänzt Termin und unterschreibt →
// Zweitprüferin unterschreibt → Studiendekan unterschreibt mit (passwortgeschütztem) Schlüssel → PDF. Dazu 390 px.
// Aufruf:  node tests/Browser/ablauf.mjs http://localhost:5310 <Ordner für Bildschirmfotos>
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const { seite, beenden } = await browserStarten();
const schritt = (t) => console.log(`• ${t}`);
let nr = 0;
const foto = async (name) => { await seite.bild(join(bilder, `${String(++nr).padStart(2, '0')}-${name}.png`)); };
const pruefen = (bedingung, text) => { if (!bedingung) { throw new Error(text); } };
const quer = async (was) => { const b = await seite.eval('document.documentElement.scrollWidth'); pruefen(b <= 392, `390 px: ${was} rollt waagerecht (${b})`); };
const schmal = async (name) => {
    await seite.breite(390, 844);
    await new Promise((r) => setTimeout(r, 400));
    await quer(name);
    await foto(`${name}-390px`);
    await seite.breite(1280, 900);
};

async function anmelden(name) {
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`(() => { const zeile = [...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes(${JSON.stringify(name)}));
        zeile.querySelector('button').click(); })()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);
    await new Promise((r) => setTimeout(r, 900));
}

async function abmelden() {
    await seite.eval(`fetch('abmelden', { method: 'POST', body: new URLSearchParams({ __RequestVerificationToken: document.querySelector('input[name=__RequestVerificationToken]').value }) })`);
}

async function zeichnenUndUnterschreiben(knopf, warten) {
    await seite.warteAuf(`!!document.querySelector('.pm-zeichenfeld')`);
    await seite.zeichnen('.pm-zeichenfeld');
    await seite.klick('.pm-card input[type=checkbox]');
    await seite.klickText('button', knopf);
    await seite.warteAuf(warten, 20000);
}

async function pdfPruefen(selektor = 'a[href$="/pdf"]') {
    const pdf = await seite.eval(`fetch(document.querySelector('${selektor}').href).then(async r => ({ status: r.status, typ: r.headers.get('content-type'), groesse: (await r.arrayBuffer()).byteLength }))`);
    console.log('  PDF:', JSON.stringify(pdf));
    pruefen(pdf.status === 200 && pdf.typ.includes('pdf'), 'PDF nicht abrufbar');
}

try {
    // ---------------------------------------------------------------- Erstprüferin lädt ein
    schritt('Erstprüferin: Einladungslink erzeugen');
    await anmelden('Erika Mustermann');
    await seite.oeffnen(basis + 'antraege/neu');
    await seite.warteAuf(`!!document.querySelector('#nachname')`);
    const aktiv = await seite.eval(`[...document.querySelectorAll('nav a.on')].map(a => a.textContent.trim())`);
    pruefen(aktiv.length === 1 && aktiv[0] === 'Einladung erstellen', `Navigation hebt hervor: ${aktiv.join(', ')}`);
    await schmal('einladung-leer');
    await seite.eingeben('#nachname', 'Musterstudentin');
    await seite.eingeben('#vorname', 'Mia');
    await seite.auswahl(0, 'Elektrotechnik (B.Sc., PO 2023)');
    await seite.auswahl(1, 'Regelungstechnik');
    await seite.auswahl(2, 'Probst');
    await foto('einladung-ausgefuellt');
    await seite.klickText('button', 'Link erzeugen');
    await seite.warteAuf(`!!document.querySelector('input[aria-label=Einladungslink]')`);
    const link = await seite.eval(`document.querySelector('input[aria-label=Einladungslink]').value`);
    pruefen(/\/drittpruefung\/[0-9a-f-]{36}\/[A-Za-z0-9_-]{40,}$/.test(link), `Link unerwartet: ${link}`);
    const mailto = await seite.eval(`[...document.querySelectorAll('a')].find(a => a.textContent.includes('Mail vorbereiten')).href`);
    pruefen(mailto.startsWith('mailto:?subject=') && decodeURIComponent(mailto).includes(link), 'Mailentwurf ohne Link');
    await foto('einladung-link');
    await schmal('einladung-link');
    await abmelden();

    // ---------------------------------------------------------------- Studierende
    schritt('Studentin: Angaben ergänzen und unterschreiben');
    await seite.oeffnen(link);
    await seite.warteAuf(`!!document.querySelector('#matrikel')`);
    const vorab = await seite.text();
    pruefen(vorab.includes('Musterstudentin') && vorab.includes('Regelungstechnik') && vorab.includes('Lena Probst'), 'Vorausgefüllte Angaben fehlen');
    pruefen(!(await seite.eval(`!!document.querySelector('#nachname')`)), 'Name darf nicht änderbar sein');
    await schmal('studentin-link');
    await seite.eingeben('#matrikel', '987654');
    await seite.eingeben('#termine', 'Nicht montags.\nAb 10 Uhr.');
    await seite.klickText('button', 'Weiter: prüfen und unterschreiben');
    await seite.warteAuf(`!!document.querySelector('.pm-signiertext')`);
    pruefen((await seite.text()).includes('Matrikelnummer: 987654'), 'Matrikelnummer fehlt im Text');
    await schmal('studentin-unterschreiben');
    await zeichnenUndUnterschreiben('Unterschreiben und absenden', `document.body.innerText.includes('Danke!')`);
    await foto('studentin-unterschrieben');
    await pdfPruefen();

    // ---------------------------------------------------------------- Erstprüferin
    schritt('Erstprüferin: Termin ergänzen und unterschreiben');
    await anmelden('Erika Mustermann');
    await foto('start-erstpruefende');
    await seite.klickText('a', 'Bearbeiten');
    await seite.warteAuf(`document.body.innerText.includes('Termin, Ort und Zweitprüfende')`);
    const aktiv2 = await seite.eval(`[...document.querySelectorAll('nav a.on')].map(a => a.textContent.trim())`);
    pruefen(aktiv2.length === 1 && aktiv2[0] === 'Anträge', `Navigation auf der Antragsseite: ${aktiv2.join(', ')}`);
    const termin = new Date(Date.now() + 14 * 864e5);
    const datum = `${String(termin.getDate()).padStart(2, '0')}.${String(termin.getMonth() + 1).padStart(2, '0')}.${termin.getFullYear()} 10:00`;
    await seite.eingeben('.rz-datepicker input', datum);
    await seite.eval(`document.querySelector('.rz-datepicker input').blur()`);
    await seite.eingeben('#ort', 'SI 0012, Campus Westerberg');
    await foto('erstpruefende-termin');
    await schmal('antrag-erstpruefende');
    await seite.klickText('button', 'Speichern und unterschreiben');
    await zeichnenUndUnterschreiben('Unterschreiben und weitergeben', `document.body.innerText.includes('Bei der/dem Zweitprüfenden')`);
    await foto('erstpruefende-unterschrieben');
    await abmelden();

    // ---------------------------------------------------------------- Zweitprüferin
    schritt('Zweitprüferin: unterschreiben');
    await anmelden('Lena Probst');
    await seite.klickText('a', 'Bearbeiten');
    await seite.warteAuf(`document.body.innerText.includes('Zweitprüfung')`);
    await foto('zweitpruefende');
    await seite.klickText('button', 'Unterschreiben');
    await zeichnenUndUnterschreiben('Unterschreiben und weitergeben', `document.body.innerText.includes('Beim Studiendekanat')`);
    await foto('zweitpruefende-unterschrieben');
    await abmelden();

    // ---------------------------------------------------------------- Studiendekan: Schlüssel hinterlegen und mit Schlüssel bestellen
    schritt('Studiendekan: Schlüssel (passwortgeschützt) hinterlegen');
    const ordner = mkdtempSync(join(tmpdir(), 'profomat-key-'));
    const datei = join(ordner, 'dekan.pem');
    execFileSync('openssl', ['genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-256', '-aes-256-cbc', '-pass', 'pass:geheim123', '-out', datei]);
    await anmelden('Max Beispiel');
    await seite.oeffnen(basis + 'profil');
    await seite.eingeben('#bez', 'Testschlüssel');
    await seite.klickText('button', 'Neuen Schlüssel hinterlegen');
    await seite.warteAuf(`!!document.querySelector('.pm-ablage')`);
    await seite.klickText('.pm-tab', 'Mit eigenem Schlüssel');
    await seite.dateiSetzen('.pm-ablage input[type=file]', datei);
    await seite.warteAuf(`!document.querySelector('[data-passwort-bereich]').hidden`);
    await seite.eingeben('[data-passwort-bereich] input[type=password]', 'geheim123');
    await seite.klick('[data-passwort-ok]');
    await seite.warteAuf(`document.body.innerText.includes('Schlüssel geladen')`);
    await seite.klick('.pm-card input[type=checkbox]');
    await seite.klickText('button', 'Schlüssel hinterlegen');
    await seite.warteAuf(`document.body.innerText.includes('Testschlüssel')`);

    schritt('Studiendekan: Prüfende bestellen (Signatur mit Schlüssel)');
    await seite.oeffnen(basis);
    await seite.klickText('a', 'Bearbeiten');
    await seite.warteAuf(`document.body.innerText.includes('Bestellung der Prüfenden')`);
    await seite.klickText('button', 'Prüfende bestellen und unterschreiben');
    await seite.warteAuf(`!!document.querySelector('.pm-ablage')`);
    await seite.klickText('.pm-tab', 'Mit eigenem Schlüssel');
    await seite.dateiSetzen('.pm-ablage input[type=file]', datei);
    await seite.warteAuf(`!document.querySelector('[data-passwort-bereich]').hidden`);
    await seite.eingeben('[data-passwort-bereich] input[type=password]', 'geheim123');
    await seite.klick('[data-passwort-ok]');
    await seite.warteAuf(`document.body.innerText.includes('Schlüssel geladen')`);
    await seite.klick('.pm-card input[type=checkbox]');
    await foto('dekan-unterschreiben');
    await seite.klickText('button', 'Unterschreiben und abschließen');
    await seite.warteAuf(`document.body.innerText.includes('An das Studierendensekretariat')`, 20000);
    await foto('dekan-abgeschlossen');
    await schmal('antrag-abgeschlossen');
    await pdfPruefen();
    const geprueft = await seite.eval(`[...document.querySelectorAll('.pm-sig')].filter(s => s.innerText.includes('geprüft')).length`);
    pruefen(geprueft === 4, `Erwartet 4 geprüfte Unterschriften, gefunden ${geprueft}`);
    const sekretariat = await seite.eval(`[...document.querySelectorAll('a')].find(a => a.href.startsWith('mailto:') && a.textContent.includes('Mail an'))?.href ?? ''`);
    pruefen(sekretariat.length > 0, 'Mail an das Sekretariat fehlt');
    await abmelden();

    // ---------------------------------------------------------------- Studierende sehen den Abschluss
    schritt('Studentin: Stand und PDF');
    await seite.oeffnen(link);
    await seite.warteAuf(`document.body.innerText.includes('Abgeschlossen')`);
    await foto('studentin-abgeschlossen');
    await schmal('studentin-abgeschlossen');
    await pdfPruefen();
    console.log(`\nAblauf vollständig. Bildschirmfotos: ${bilder}`);
} catch (fehler) {
    console.error('FEHLER:', fehler.message);
    await foto('fehler');
    console.error('Seitentext:', (await seite.text()).slice(0, 1500));
    process.exitCode = 1;
} finally {
    await beenden();
}
