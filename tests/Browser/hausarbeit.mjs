// Browserprüfung Hausarbeit: Signatur hochladen, Liste mit mehreren Prüfungen, Themen einlesen, Teams mit Wünschen, Zuteilung,
// Bestätigungsmails als .eml (Outlook-Entwurf mit Signatur), Bestätigung über den persönlichen Link, 390 px.
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
    pruefen(text.includes('Roboterarm') && text.includes('mit Paul Wolf') && text.includes('Prof. Dr. Erika Mustermann'), 'Mailtext unvollständig');
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
    console.log('\nHausarbeit vollständig. Bildschirmfotos: ' + bilder);
} catch (e) {
    await foto('fehler');
    console.error('FEHLER:', e.message);
    process.exitCode = 1;
} finally {
    await beenden();
}
