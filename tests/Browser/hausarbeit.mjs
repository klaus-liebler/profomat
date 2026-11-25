// Browserprüfung Hausarbeit: Signatur hochladen, Liste mit mehreren Prüfungen, Themen einlesen, Teams mit Wünschen, Zuteilung,
// Bestätigungsmails als .eml (Outlook-Entwurf mit Signatur), Bestätigung mit terminlichen Einschränkungen über den persönlichen Link,
// Gesprächstermine (Kalender, Einplanen per Klick und automatisch, Mailvorlage mit Platzhaltern, Terminmails, ZIP, .ics, Abo-Link), 390 px.
// Aufruf:  node tests/Browser/hausarbeit.mjs http://localhost:5310 <Ordner für Bildschirmfotos>   (frische Datenbank)
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const liste = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tools', 'Testdaten', 'pruefungsliste-mehrere.csv');
const { seite, beenden } = await browserStarten({ port: 9337, hoehe: 1000 });
const schritt = (t) => console.log(`• ${t}`);
let nr = 0;
const foto = async (name) => { await seite.bild(join(bilder, `h${String(++nr).padStart(2, '0')}-${name}.png`)); };
const wahl = (selektor, text) => seite.eval(`(() => { const s = document.querySelector(${JSON.stringify(selektor)}); if (!s) throw new Error('fehlt: ${selektor}');
    const o = [...s.options].find(o => o.text.includes(${JSON.stringify(text)})); if (!o) throw new Error('Option fehlt: ${text}');
    s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); })()`).then(() => new Promise((r) => setTimeout(r, 400)));
const laden = (pfad) => seite.eval(`fetch(${JSON.stringify(pfad)}).then(async r => ({ status: r.status, typ: r.headers.get('content-type') ?? '', text: await r.text() }))`);
const pruefen = (bedingung, meldung) => { if (!bedingung) { throw new Error(meldung); } };
const quer = async (was) => { const b = await seite.eval('document.documentElement.scrollWidth'); pruefen(b <= 392, `390 px: ${was} rollt waagerecht (${b})`); };

// Signatur wie von Outlook gespeichert (erfunden)
const sigOrdner = mkdtempSync(join(tmpdir(), 'profomat-signatur-'));
writeFileSync(join(sigOrdner, 'Test.htm'), '<html><head><style>p.MsoNormal{margin:0}</style></head><body><p class=MsoNormal><b>Prof. Dr. Erika Mustermann</b></p>'
    + '<![if !vml]><img src="Test-Dateien/image001.png"><![endif]><p class=MsoNormal>Hochschule Osnabrück</p><script>alert(1)</script></body></html>', 'latin1');
writeFileSync(join(sigOrdner, 'image001.png'), Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64'));

const themen = `Themenideen
1. Mathe-Visualisierung und -Demonstration Software
Schreiben Sie eine grafische Python-Anwendung.

2. Roboterarm Hardware
Drei Holzblöcke stapeln.

3. Kommunalwahlassistent Software
Sitzverteilung nach Sainte-Laguë.`;

try {
    schritt('Anmelden als Prüfende');
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes('Erika Mustermann')).querySelector('button').click()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);

    schritt('Mailsignatur im Profil hochladen');
    await seite.oeffnen(basis + 'profil');
    await seite.dateiSetzen('#sig-htm', join(sigOrdner, 'Test.htm'));
    await seite.dateiSetzen('#sig-bilder', [join(sigOrdner, 'image001.png')]);
    await seite.klickText('button', 'Signatur übernehmen');
    await seite.warteAuf(`document.body.innerText.includes('hinterlegt') && document.body.innerText.includes('1 Bild')`);
    const test = await laden('profil/testmail.eml');
    pruefen(test.status === 200 && test.text.startsWith('X-Unsent: 1') && test.text.includes('Content-ID: <sig1@profomat>'), 'Testmail ohne Signaturbild');
    await foto('signatur');

    schritt('Hausarbeit aus Liste mit mehreren Prüfungen anlegen');
    await seite.oeffnen(basis + 'hausarbeiten');
    await seite.dateiSetzen('#liste', liste);
    await seite.warteAuf(`!!document.querySelector('#pruefung')`);
    await wahl('#pruefung', '11B1555-1-PJ-EuI');
    await seite.warteAuf(`document.querySelector('#titel').value === 'Projekt Mechatronik' && document.body.innerText.includes('2 Personen')`);
    await seite.klickText('button', 'Hausarbeit anlegen');
    await seite.warteAuf(`document.body.innerText.includes('Zuteilung und Bestätigung')`);
    const seitePfad = await seite.eval('location.pathname');

    schritt('Themenliste einfügen');
    await seite.klickText('button[role=tab]', 'Themen');
    await seite.eingeben('textarea[aria-label=Themenliste]', themen);
    await seite.klickText('button', 'Themen einlesen');
    await seite.warteAuf(`document.body.innerText.includes('Kommunalwahlassistent') && document.body.innerText.includes('Themen (3)')`);

    schritt('Person von Hand ergänzen');
    await seite.klickText('button[role=tab]', 'Personen');
    await seite.eingeben('#pv', 'Nora'); await seite.eingeben('#pn', 'Nachzügler'); await seite.eingeben('#pm', '1199998'); await seite.eingeben('#pe', 'nora.nachzuegler@hs-osnabrueck.de');
    await seite.klickText('button', 'Person ergänzen');
    await seite.warteAuf(`document.body.innerText.includes('Nachzügler, Nora')`);

    schritt('Teams mit Wünschen anlegen');
    await seite.klickText('button[role=tab]', 'Teams und Zuteilung');
    await wahl('#person0', 'Becker'); await wahl('#person1', 'Wolf'); await wahl('#sprecher', 'Paul Wolf');
    await wahl('#wunsch0', '2. Roboterarm'); await wahl('#wunsch1', '1. Mathe');
    await seite.klickText('button', 'Team anlegen');
    await seite.warteAuf(`document.body.innerText.includes('Teams (1)')`);
    await wahl('#person0', 'Nachzügler'); await wahl('#wunsch0', '2. Roboterarm'); await wahl('#wunsch1', '3. Kommunalwahl');
    await seite.klickText('button', 'Team anlegen');
    await seite.warteAuf(`document.body.innerText.includes('Teams (2)') && document.body.innerText.includes('0 ohne Team')`);

    schritt('Nach Wünschen zuteilen, Bestätigungsmails erstellen');
    await seite.klickText('button', 'Themen nach Wünschen zuteilen');
    await seite.warteAuf(`[...document.querySelectorAll('td select')].every(s => s.value !== '')`);
    await seite.eval(`document.querySelector('details.pm-vorlage').open = true`);
    await seite.warteAuf(`document.querySelector('details.pm-vorlage pre')?.textContent.includes('terminlichen Einschränkungen')`);
    pruefen(await seite.eval(`(() => { const t = document.querySelector('#bv-text').getBoundingClientRect(); const c = document.querySelector('.pm-platzhalter .pm-chip').getBoundingClientRect();
        return c.top >= t.bottom; })()`), 'Platzhalter überlappen das Textfeld');
    await seite.eval(`document.querySelector('.pm-platzhalter').scrollIntoView({ block: 'center' })`);
    await foto('vorlage');
    await seite.eingeben('#bv-text', 'Hallo {Name}, ohne Link');
    await seite.warteAuf(`document.querySelector('details.pm-vorlage pre').textContent.includes('ohne Link')`);
    await seite.klickText('button', 'Vorlage speichern');
    await seite.warteAuf(`document.body.innerText.includes('braucht den Platzhalter {Link}')`);
    await seite.klickText('button', 'Standardtext');
    await seite.warteAuf(`document.querySelector('#bv-text').value.includes('{Link}')`);
    await seite.klickText('button', 'Bestätigungsmails erstellen (3)');
    await seite.warteAuf(`document.body.innerText.includes('3 Mail(s) in Outlook öffnen und senden')`);
    await foto('teams');
    await seite.breite(390, 844);
    await foto('teams-390px');
    await quer('Hausarbeit');
    await seite.breite(1280, 1000);

    schritt('Mails zum Versenden: .eml mit Signatur und persönlichem Link');
    await seite.oeffnen(basis + 'entwuerfe');
    await seite.warteAuf(`document.body.innerText.includes('Offene Entwürfe (3)')`);
    const href = await seite.eval(`[...document.querySelectorAll('tr')].find(t => t.textContent.includes('Lena Becker')).querySelector('a[href$="mail.eml"]').getAttribute('href')`);
    const eml = await laden(href);
    pruefen(eml.status === 200 && eml.text.startsWith('X-Unsent: 1') && eml.text.includes('multipart/related'), '.eml ohne Entwurfskennung oder Signatur');
    const textTeil = eml.text.split('Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n')[1].split('\r\n--')[0];
    const text = Buffer.from(textTeil.replace(/\r\n/g, ''), 'base64').toString('utf8');
    pruefen(text.includes('Roboterarm') && text.includes('mit Paul Wolf') && text.includes('Prof. Dr. Erika Mustermann') && text.includes('terminlichen Einschränkungen'), 'Mailtext unvollständig');
    const link = text.match(/https?:\/\/\S+\/hausarbeit\/bestaetigen\/\S+/)[0];
    await seite.oeffnen(basis + 'entwuerfe');
    await seite.warteAuf(`document.body.innerText.includes('in Outlook geöffnet')`);
    await seite.breite(390, 844);
    await foto('entwuerfe-390px');
    await quer('Mails zum Versenden');
    await seite.breite(1280, 1000);

    schritt('Bestätigung über den persönlichen Link (390 px)');
    await seite.oeffnen(basis + link.split('/').slice(-4).join('/'));
    await seite.breite(390, 844);
    await seite.warteAuf(`document.body.innerText.includes('Ihr Thema für die Hausarbeit') && document.body.innerText.includes('Lena Becker')`);
    await seite.eingeben('#einschraenkungen', 'Montags erst ab 10 Uhr');
    await seite.klickText('button', 'Thema verbindlich annehmen');
    await seite.warteAuf(`document.body.innerText.includes('Danke!')`);
    await foto('bestaetigt-390px');
    await quer('Bestätigungsseite');
    await seite.breite(1280, 1000);
    await seite.oeffnen(basis + link.split('/').slice(-4).join('/') + 'x');
    await seite.warteAuf(`document.body.innerText.includes('Link ungültig')`);

    schritt('Stand in der Hausarbeit');
    await seite.oeffnen(basis + seitePfad.replace(/^\/+/, ''));
    await seite.warteAuf(`document.body.innerText.includes('1 / 3 bestätigt')`);

    schritt('Gesprächstermine: Tag einstellen, per Klick und automatisch einplanen');
    await seite.klickText('button[role=tab]', 'Gesprächstermine');
    await seite.warteAuf(`!!document.querySelector('#g-tag')`);
    await seite.eval(`(() => { const e = document.querySelector('#g-tag'); e.value = '2027-03-01'; e.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await seite.warteAuf(`[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Tag hinzufügen' && !b.disabled)`);
    await seite.klickText('button', 'Tag hinzufügen');
    await seite.warteAuf(`document.body.innerText.includes('01.03.2027')`);
    await seite.eingeben('#g-ort', 'SI 0110');
    await seite.klickText('button', 'Einstellungen speichern');
    await seite.warteAuf(`!!document.querySelector('.pm-kal-slot')`);
    pruefen(await seite.eval(`document.querySelectorAll('.pm-kal-tag').length === 1 && document.querySelectorAll('.pm-kal-tag .pm-kal-slot').length === 40`), 'Kalender 08–18 Uhr im 15-Minuten-Raster fehlt');
    pruefen(await seite.eval(`[...document.querySelectorAll('.pm-kal-karte')].every(k => k.getAttribute('draggable') === 'true')`), 'Teams nicht ziehbar');
    await seite.eval(`[...document.querySelectorAll('.pm-kal-karte')].find(k => k.textContent.includes('Becker')).click()`);
    await seite.warteAuf(`document.body.innerText.includes('Montags erst ab 10 Uhr')`);   // Einschränkungen in der Auswahl
    await seite.eval(`document.querySelectorAll('.pm-kal-tag .pm-kal-slot')[8].click()`);  // 10:00
    await seite.warteAuf(`document.querySelectorAll('.pm-kal-block').length === 1 && document.querySelector('.pm-kal-block').textContent.includes('10:00')`);
    await seite.klickText('button', 'Ohne Termin automatisch einplanen (1)');
    await seite.warteAuf(`document.querySelectorAll('.pm-kal-block').length === 2`);
    pruefen(await seite.eval(`document.querySelectorAll('.pm-kal-konflikt').length === 0`), 'automatisch eingeplant, aber überschneidend');
    await foto('kalender');
    await seite.breite(390, 844);
    await foto('kalender-390px');
    await quer('Gesprächstermine');
    await seite.breite(1280, 1000);

    schritt('Terminmails mit Platzhaltern, ZIP, Kalenderdatei und Abo-Link');
    await seite.eingeben('#v-betreff', 'Gespräch am {Datum} um {Uhrzeit} Uhr');
    await seite.warteAuf(`document.body.innerText.includes('Gespräch am 01.03.2027 um')`);   // Vorschau
    await seite.eingeben('#v-betreff', 'Gespräch {Raum}');
    await seite.warteAuf(`document.body.innerText.includes('Unbekannte Platzhalter: {Raum}')`);
    await seite.eingeben('#v-betreff', 'Gespräch am {Datum} um {Uhrzeit} Uhr');
    await seite.klickText('button', 'Vorlage speichern');
    await seite.warteAuf(`document.body.innerText.includes('Vorlage gespeichert')`);
    await seite.klickText('button', 'Terminmails erstellen (3)');
    await seite.warteAuf(`document.body.innerText.includes('Alle Terminmails als ZIP')`);
    const zipHref = await seite.eval(`[...document.querySelectorAll('a')].find(a => a.textContent.includes('Alle Terminmails als ZIP')).getAttribute('href')`);
    const zip = await seite.eval(`(async () => { const r = await fetch(${JSON.stringify(zipHref)}); const b = new Uint8Array(await r.arrayBuffer());
        return { status: r.status, typ: r.headers.get('content-type') ?? '', pk: b[0] === 80 && b[1] === 75 }; })()`);
    pruefen(zip.status === 200 && zip.typ.includes('zip') && zip.pk, 'ZIP der Terminmails fehlt');
    const ics = await laden(seitePfad.replace(/^\/+/, '') + '/gespraeche.ics');
    pruefen(ics.status === 200 && ics.typ.startsWith('text/calendar') && (ics.text.match(/BEGIN:VEVENT/g) ?? []).length === 2 && ics.text.includes('LOCATION:SI 0110'), 'Kalenderdatei unvollständig');
    await seite.klickText('button', 'Abo-Link für Outlook erzeugen');
    await seite.warteAuf(`!!document.querySelector('input[aria-label=Abo-Link]')`);
    const abo = await seite.eval(`document.querySelector('input[aria-label=Abo-Link]').value`);
    const aboAbruf = await laden(abo.replace(/^https?:\/\/[^/]+\//, ''));
    pruefen(aboAbruf.status === 200 && aboAbruf.text.includes('BEGIN:VCALENDAR'), 'Abo-Link liefert keinen Kalender');
    const falsch = await laden(abo.replace(/^https?:\/\/[^/]+\//, '').replace('.ics', 'x.ics'));
    pruefen(falsch.status === 404, 'falscher Abo-Link nicht abgewiesen');
    await foto('terminmails');
    await seite.oeffnen(basis + 'entwuerfe');
    await seite.warteAuf(`document.body.innerText.includes('Alle offenen als ZIP')`);
    console.log('\nHausarbeit vollständig. Bildschirmfotos: ' + bilder);
} catch (e) {
    await foto('fehler');
    console.error('FEHLER:', e.message);
    process.exitCode = 1;
} finally {
    await beenden();
}
