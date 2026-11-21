// Browserprüfung des ganzen Ablaufs „mündliche Drittprüfung“ gegen eine laufende Testinstanz (Entwicklung, Demodaten).
// Aufruf:  node tests/Browser/ablauf.mjs http://localhost:5310 <Ordner für Bildschirmfotos>
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
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

async function anmelden(name) {
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`(() => { const zeile = [...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes(${JSON.stringify(name)}));
        zeile.querySelector('button').click(); })()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);
    await new Promise((r) => setTimeout(r, 900));
}

try {
    // ---------------------------------------------------------------- Studierende
    schritt('Antrag stellen');
    await seite.oeffnen(basis + 'drittpruefung/antrag');
    await foto('antrag-leer');
    await seite.eingeben('#vorname', 'Mia');
    await seite.eingeben('#nachname', 'Musterstudentin');
    await seite.eingeben('#matrikel', '987654');
    await seite.eingeben('#email', 'mia.musterstudentin@stud.hs-osnabrueck.de');
    await seite.auswahl(0, 'Elektrotechnik (B.Sc., PO 2023)');
    await seite.auswahl(1, 'Regelungstechnik');
    await seite.auswahl(2, 'Mustermann');
    await seite.eingeben('#termine', 'Nicht montags.\nAb 10 Uhr.');
    await seite.eingeben('#zweit', 'Gern Prof. Probst');
    await foto('antrag-ausgefuellt');
    await seite.klickText('button', 'Weiter: prüfen und unterschreiben');
    await seite.warteAuf(`document.querySelector('.pm-signiertext')`);
    await seite.zeichnen('.pm-zeichenfeld');
    await seite.klick('.pm-card input[type=checkbox]');
    await foto('antrag-unterschrift');
    await seite.klickText('button', 'Unterschreiben und einreichen');
    await seite.warteAuf(`document.body.innerText.includes('Fast geschafft')`);
    await foto('antrag-eingereicht');

    // ---------------------------------------------------------------- Bestätigungslink aus dem Postausgang
    schritt('Bestätigungslink aus dem Postausgang holen');
    await anmelden('Vera Verwaltung');
    await seite.oeffnen(basis + 'postausgang');
    const link = await seite.eval(`(document.body.innerHTML.match(/https?:\\/\\/[^\\s<"]+\\/drittpruefung\\/status\\/[0-9a-f-]+\\/[A-Za-z0-9_-]+/) || [])[0]`);
    if (!link) { throw new Error('Kein Bestätigungslink im Postausgang'); }
    await seite.eval(`fetch('abmelden', { method: 'POST', body: new URLSearchParams({ __RequestVerificationToken: document.querySelector('input[name=__RequestVerificationToken]').value }) })`);

    schritt('Mailadresse bestätigen');
    await seite.oeffnen(link);
    await foto('status-bestaetigen');
    await seite.klickText('button', 'Antrag bestätigen und absenden');
    await seite.warteAuf(`document.body.innerText.includes('Danke!')`);
    await foto('status-bestaetigt');

    // ---------------------------------------------------------------- Erstprüferin
    schritt('Erstprüferin: Termin festlegen und unterschreiben');
    await anmelden('Erika Mustermann');
    await foto('start-pruefende');
    await seite.klickText('a', 'Bearbeiten');
    await seite.warteAuf(`document.body.innerText.includes('Termin, Ort und Zweitprüfende festlegen')`);
    const termin = new Date(Date.now() + 14 * 864e5);
    const datum = `${String(termin.getDate()).padStart(2, '0')}.${String(termin.getMonth() + 1).padStart(2, '0')}.${termin.getFullYear()} 10:00`;
    await seite.eingeben('.rz-datepicker input', datum);
    await seite.eval(`document.querySelector('.rz-datepicker input').blur()`);
    await seite.eingeben('#ort', 'SI 0012, Campus Westerberg');
    await seite.auswahl(0, 'Probst');
    await seite.klickText('button', 'Festlegung speichern');
    await seite.warteAuf(`document.body.innerText.includes('SI 0012, Campus Westerberg') && !document.body.innerText.includes('Ort\\nnoch offen')`);
    await foto('pruefende-festgelegt');
    await seite.klickText('button', 'Unterschreiben und an das Studiendekanat geben');
    await seite.warteAuf(`document.querySelector('.pm-zeichenfeld')`);
    await seite.zeichnen('.pm-zeichenfeld');
    await seite.klick('.pm-card input[type=checkbox]');
    await seite.klickText('button', 'Unterschreiben und weitergeben');
    await seite.warteAuf(`document.body.innerText.includes('Beim Studiendekanat')`);
    await foto('pruefende-unterschrieben');

    // ---------------------------------------------------------------- Studiendekan: Schlüssel hinterlegen und mit Schlüssel bestellen
    schritt('Studiendekan: Schlüssel (passwortgeschützt) hinterlegen');
    const ordner = mkdtempSync(join(tmpdir(), 'profomat-key-'));
    const datei = join(ordner, 'dekan.pem');
    execFileSync('openssl', ['genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-256', '-aes-256-cbc', '-pass', 'pass:geheim123', '-out', datei]);
    await anmelden('Max Beispiel');
    await seite.oeffnen(basis + 'profil');
    await seite.eingeben('#bez', 'Testschlüssel');
    await seite.klickText('button', 'Neuen Schlüssel hinterlegen');
    await seite.warteAuf(`document.querySelector('.pm-ablage')`);
    await seite.klickText('.pm-tab', 'Mit eigenem Schlüssel');
    await seite.dateiSetzen('.pm-ablage input[type=file]', datei);
    await seite.warteAuf(`!document.querySelector('[data-passwort-bereich]').hidden`);
    await seite.eingeben('[data-passwort-bereich] input[type=password]', 'geheim123');
    await seite.klick('[data-passwort-ok]');
    await seite.warteAuf(`document.body.innerText.includes('Schlüssel geladen')`);
    await seite.klick('.pm-card input[type=checkbox]');
    await foto('profil-schluessel-geladen');
    await seite.klickText('button', 'Schlüssel hinterlegen');
    await seite.warteAuf(`document.body.innerText.includes('Testschlüssel')`);
    await foto('profil-schluessel-hinterlegt');

    schritt('Studiendekan: Prüfende bestellen (Signatur mit Schlüssel)');
    await seite.oeffnen(basis);
    await seite.klickText('a', 'Bearbeiten');
    await seite.warteAuf(`document.body.innerText.includes('Bestellung der Prüfenden')`);
    await seite.klickText('button', 'Prüfende bestellen und unterschreiben');
    await seite.warteAuf(`document.querySelector('.pm-ablage')`);
    await seite.klickText('.pm-tab', 'Mit eigenem Schlüssel');
    await seite.dateiSetzen('.pm-ablage input[type=file]', datei);
    await seite.warteAuf(`!document.querySelector('[data-passwort-bereich]').hidden`);
    await seite.eingeben('[data-passwort-bereich] input[type=password]', 'geheim123');
    await seite.klick('[data-passwort-ok]');
    await seite.warteAuf(`document.body.innerText.includes('Schlüssel geladen')`);
    await seite.klick('.pm-card input[type=checkbox]');
    await foto('dekan-unterschreiben');
    await seite.klickText('button', 'Unterschreiben und abschließen');
    await seite.warteAuf(`document.body.innerText.includes('Dokument (PDF) öffnen')`, 20000);
    await foto('dekan-abgeschlossen');
    const pdf = await seite.eval(`fetch(document.querySelector('a[href$="/pdf"]').href).then(async r => ({ status: r.status, typ: r.headers.get('content-type'), groesse: (await r.arrayBuffer()).byteLength }))`);
    console.log('  PDF:', JSON.stringify(pdf));
    if (pdf.status !== 200 || !pdf.typ.includes('pdf')) { throw new Error('PDF nicht abrufbar'); }
    const geprueft = await seite.eval(`document.querySelectorAll('.pm-p-gut').length`);
    console.log('  geprüfte Unterschriften (grüne Marken):', geprueft);

    // ---------------------------------------------------------------- Studierende sehen den Abschluss
    schritt('Studierende: Status und PDF');
    await seite.oeffnen(link);
    await foto('status-abgeschlossen');
    if (!(await seite.text()).includes('Abgeschlossen')) { throw new Error('Status nicht abgeschlossen'); }

    // ---------------------------------------------------------------- Katalog und Handy-Breite
    schritt('Katalog mit alten Einträgen');
    await anmelden('Vera Verwaltung');
    await seite.oeffnen(basis + 'katalog');
    await seite.klick('.pm-card .pm-head input[type=checkbox]');
    await new Promise((r) => setTimeout(r, 800));
    await foto('katalog-mit-alten');
    await seite.oeffnen(basis + 'postausgang');
    await foto('postausgang');
    console.log(`\nAblauf vollständig. Bildschirmfotos: ${bilder}`);
} catch (fehler) {
    console.error('FEHLER:', fehler.message);
    await foto('fehler');
    console.error('Seitentext:', (await seite.text()).slice(0, 1500));
    process.exitCode = 1;
} finally {
    await beenden();
}
