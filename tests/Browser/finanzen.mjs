// Browserprüfung „Finanzen“: Laborteam anlegen, SAP-Auszüge (erfundene Buchungen im Format des SAP-Exports, UTF-16 ohne BOM) mehrfach
// importieren, Cockpit mit Übertragsgrenze und Fristen, SQM-Maßnahme mit Stand, Buchungssuche, Team (Aufnehmen, fremder Zugriff), 390 px.
// Aufruf: node tests/Browser/finanzen.mjs http://localhost:5310 <Ordner>   (frische Datenbank; die Uhr der Instanz bestimmt „heute“)
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const warte = (ms) => new Promise((r) => setTimeout(r, ms));
const pruefe = (bedingung, text) => { if (!bedingung) { throw new Error(text); } console.log('  ✓', text); };
const jahr = new Date().getFullYear();

async function anmelden(seite, name) {
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes(${JSON.stringify(name)})).querySelector('button').click()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);
}

// ------------------------------------------------------------------ Testdateien im SAP-Format
const betrag = (d) => (d < 0 ? '- ' : ' ') + Math.abs(d).toLocaleString('de-DE', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
function auszug(datei, zeilen) {
    const kopf = ' Kostenst.\tErfaßt am\tBelegdatum\tBuchDatum\tKostenart\tKostenartenbezeichn.\t Wert/KWähr\tBezeichnung\tRefBelnr';
    const text = [kopf, ...zeilen.map(([datum, art, artText, wert, bez, ref]) => [' 12103012', datum, datum, datum, art, artText, betrag(wert), bez, ref ?? ''].join('\t')),
        ['* ', '', '', '', '', '', betrag(zeilen.reduce((s, z) => s + z[3], 0)), '', ''].join('\t')].join('\r\n') + '\r\n';
    const pfad = join(bilder, datei);
    writeFileSync(pfad, Buffer.from(text, 'utf16le'));   // ohne BOM wie der SAP-Export
    return pfad;
}
const v = jahr - 1;
const budget = [`01.01.${jahr}`, '930300', 'Budget Sachmitte Fak', -9866, `Co/Budget ${jahr} / 12103012`];
const dateien = [
    auszug(`${v + 1}0121 12103012 Tabelle.xls`, [[`01.01.${v}`, '930300', 'Budget Sachmitte Fak', -6314, `Co/Budget ${v} / 12103012`],
        [`12.05.${v}`, '602110', 'Hilfsstoffe', 1200.5, 'Händler A/Kleinteile', '1960001'], [`30.11.${v}`, '685100', 'Dienstreisen', 389, '*DR Tagung', '77472']]),
    auszug(`${jahr}0316 12103012 Tabelle.xls`, [budget, [`20.02.${jahr}`, '607150', 'EDV Mat.u.-zubehör', 32.9, 'Händler B/WLAN-Verstärker', '1963429']]),
    auszug(`${jahr}0622 12103012 Tabelle.xls`, [budget, [`20.02.${jahr}`, '607150', 'EDV Mat.u.-zubehör', 32.9, 'Händler B/WLAN-Verstärker', '1963429'],
        [`15.04.${jahr}`, '634000', 'Vergüt. stud.Hilfskr', 559.2, '0001234567 Hilfskraft, Erika', '908665'],
        [`31.05.${jahr}`, '602110', 'Hilfsstoffe', 58.85, 'Händler A/Elektrobedarf', '1971518'],
        [`29.05.${jahr}`, '602110', 'Hilfsstoffe', -10.67, 'Händler A/Gutschrift LED-Leiste', '680132']]),
    auszug(`${v + 1}0217 12103012 Tabelle.xls`, [[`07.01.${jahr}`, '607505', 'Ausstattung/Kleingeg', 18.98, 'Teilauszug ohne Budget', '680011']]),
];

const { seite, beenden } = await browserStarten({ port: 9352, breite: 1400, hoehe: 1000 });
try {
    await anmelden(seite, 'Klaus Liebler');
    console.log('• Laborteam anlegen');
    await seite.oeffnen(basis + 'finanzen');
    pruefe(await seite.eval(`[...document.querySelectorAll('nav a')].some(a => a.textContent.trim() === 'Finanzen')`), 'Menüpunkt „Finanzen“');
    const teamname = `Labor Automatisierungstechnik ${Date.now() % 100000}`;
    await seite.eingeben('#teamname', teamname);
    await seite.klickText('button', 'Laborteam anlegen');
    await seite.warteAuf(`!!document.querySelector('input[type=file]')`);
    const teamUrl = await seite.eval('location.pathname');
    pruefe((await seite.text()).includes('ersetzt'), 'nach dem Anlegen direkt im Reiter Import');

    console.log('• Mehrere Auszüge auf einmal');
    await seite.dateiSetzen('input[type=file]', dateien);
    await seite.warteAuf(`document.querySelectorAll('.pm-tbl tbody tr').length === 4`);
    let text = await seite.text();
    pruefe(text.includes('legt Kostenstelle 12103012 an'), 'neue Kostenstelle wird beim Import angelegt');
    pruefe(text.includes(`neuerer Stand für ${jahr}`), 'älterer Stand desselben Jahres wird übersprungen');
    pruefe(text.includes('Teilauszug'), 'Teilauszug ohne Budgetzeile abgewiesen');
    pruefe(text.includes('Alle übernehmen (2)'), 'zwei Jahresstände bereit');
    await seite.bild(join(bilder, 'f1-import.png'));
    await seite.klickText('button', 'Alle übernehmen (2)');
    await seite.warteAuf(`document.body.innerText.includes('Zum Cockpit')`);
    pruefe(!(await seite.text()).includes('Alle übernehmen (2)'), 'übernommene Dateien verschwinden aus der Vorschau');

    console.log('• Cockpit');
    await seite.klickText('a', 'Zum Cockpit');
    await seite.warteAuf(`!!document.querySelector('.pm-kachel')`);
    text = await seite.text();
    const rest = 9866 - 32.9 - 559.2 - 58.85 + 10.67;
    const restVorjahr = 6314 - 1200.5 - 389;
    const euro = (d) => d.toLocaleString('de-DE', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' €';
    pruefe(text.includes(`davon Vortrag erwartet ${euro(restVorjahr)}`), `Vortrag aus ${v} erwartet (${euro(restVorjahr)})`);
    pruefe(text.includes(`frei ${euro(rest + restVorjahr)}`), `frei = Rest + Vortrag erwartet (${euro(rest + restVorjahr)})`);
    pruefe(text.includes('Übertragsgrenze Kostenstelle 12103012') && text.includes(euro(1183.92)), 'Warnung vor der Übertragsgrenze (12 % von 9.866 €)');
    pruefe(text.includes(`Jahresende Kostenstelle 12103012`) && text.includes(`SQM-Anträge für`), 'Fristen: Jahresende und SQM-Antrag im Oktober');
    await seite.bild(join(bilder, 'f2-cockpit.png'));

    console.log('• SQM-Maßnahme mit Stand');
    await seite.klickText('button', 'Töpfe (1)');
    await seite.warteAuf(`!!document.querySelector('#kennung')`);
    await seite.eingeben('#kennung', `SQM-${jahr}-15`);
    await seite.eingeben('#bez', 'Hiwis für Aufbauten neue Lehre');
    await seite.eingeben('#personal', '5.000');
    await seite.klickText('button', 'Topf anlegen');
    await seite.warteAuf(`document.body.innerText.includes('SQM-${jahr}-15')`);
    pruefe((await seite.text()).includes(`31.01.${jahr + 1}`), `Rechnungsabgabe 31.01.${jahr + 1} aus dem Zeitraster`);
    await seite.klickText('a', `SQM-${jahr}-15`);
    await seite.warteAuf(`!!document.querySelector('#verausgabt')`);
    await seite.eval(`(() => { const e = document.querySelector('#stichtag'); e.value = '${jahr}-09-22'; e.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await seite.eingeben('#verausgabt', '3586');
    await seite.klickText('button', 'Stand eintragen');
    await seite.warteAuf(`document.body.innerText.includes('1.414,00 €')`);
    pruefe(true, 'Stand eingetragen: Rest 1.414,00 €');
    await seite.oeffnen(basis + teamUrl.replace(/^\//, ''));
    await seite.warteAuf(`!!document.querySelector('.pm-kachel')`);
    text = await seite.text();
    pruefe(text.includes(`Rechnungsabgabe SQM-${jahr}-15`) && text.includes('zweckgebunden frei 1.414,00 €'), 'SQM im Cockpit und in den Fristen');

    console.log('• Buchungen suchen');
    await seite.klickText('a', 'Kostenstelle 12103012');
    await seite.warteAuf(`document.body.innerText.includes('Buchungen')`);
    pruefe(await seite.eval(`document.querySelectorAll('.pm-tbl')[1].querySelectorAll('tbody tr').length === 5`), `5 Buchungen ${jahr}`);
    await seite.eingeben('input[aria-label=Suchen]', 'Gutschrift');
    await warte(400);
    pruefe(await seite.eval(`document.querySelectorAll('.pm-tbl')[1].querySelectorAll('tbody tr').length === 1`), 'Suche findet die Gutschrift');
    pruefe((await seite.text()).includes('Hilfskräfte'), 'Summen je Kostenartengruppe (Hilfskräfte)');

    const setze = (sel, wert) => seite.eval(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); e.value = ${JSON.stringify(wert)};
        e.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    const waehle = (sel, text) => seite.eval(`(() => { const s = document.querySelector(${JSON.stringify(sel)}); s.value = [...s.options].find(o => o.text.includes(${JSON.stringify(text)})).value;
        s.dispatchEvent(new Event('change', { bubbles: true })); })()`);

    console.log('• Planung');
    await seite.oeffnen(basis + teamUrl.replace(/^\//, '') + '?bereich=planung');
    await seite.warteAuf(`!!document.querySelector('#ag-bez')`);
    await seite.eingeben('#ag-bez', 'Zubehör ABB-Roboter');
    await setze('#ag-monat', `${jahr}-12`);
    await waehle('select[aria-label="Topf 1"]', 'Kostenstelle 12103012');
    await seite.eingeben('input[aria-label="Betrag 1"]', '5000');
    await seite.klickText('button', '+ auf einen weiteren Topf aufteilen');
    await seite.warteAuf(`!!document.querySelector('select[aria-label="Topf 2"]')`);
    await waehle('select[aria-label="Topf 2"]', `SQM-${jahr}-15`);
    await seite.eingeben('input[aria-label="Betrag 2"]', '200');
    await seite.warteAuf(`[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Ausgabe planen' && !b.disabled)`);
    await seite.klickText('button', 'Ausgabe planen');
    await seite.warteAuf(`document.body.innerText.includes('12103012: 5.000,00 €')`);
    pruefe((await seite.text()).includes(`SQM-${jahr}-15: 200,00 €`), 'Ausgabe auf Kostenstelle und SQM aufgeteilt');
    await seite.klickText('button', 'Cockpit');
    await seite.warteAuf(`!!document.querySelector('.pm-kachel')`);
    text = await seite.text();
    pruefe(text.includes('5.000,00 € (1)') && text.includes(`frei ${euro(rest + restVorjahr - 5000)}`), 'Cockpit: 5.000 € gebunden, frei entsprechend kleiner');
    pruefe(text.includes(euro(rest + restVorjahr - 5000 - 1183.92)), 'Übertragsgrenze rechnet die geplante Ausgabe ein');

    console.log('• Zeitachse');
    pruefe(await seite.eval(`document.querySelectorAll('.pm-za svg polyline').length >= 2`), 'Zeitachse mit Linien je Topf');
    text = await seite.text();
    pruefe(text.includes('verfällt') && text.includes('Grenze'), 'Marken: Verfall (SQM) und Übertragsgrenze (Dezember)');
    pruefe(await seite.eval(`[...document.querySelectorAll('.pm-za-spalte title')].some(t => t.textContent.includes('Summe:'))`), 'Monatswerte beim Darüberfahren (title)');
    await seite.klickText('button', 'Summe');
    await seite.warteAuf(`document.querySelectorAll('.pm-za svg polyline').length === 1`);
    pruefe(true, 'Umschalten auf die Summe');
    await seite.klickText('button', 'je Topf');
    await seite.bild(join(bilder, 'f6-zeitachse.png'));

    console.log('• Neue Ausgabe aufteilen');
    await seite.klickText('button', 'Planung');
    await seite.warteAuf(`!!document.querySelector('#wa-betrag')`);
    await seite.eingeben('#wa-betrag', '2500');
    await setze('#wa-monat', `${jahr}-12`);
    await seite.warteAuf(`[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Aufteilung vorschlagen' && !b.disabled)`);
    await seite.klickText('button', 'Aufteilung vorschlagen');
    await seite.warteAuf(`document.body.innerText.includes('Wirkung auf die Zeitachse')`);
    text = await seite.text();
    pruefe(text.includes('Bagatellgrenze') && text.includes('Übertragsgrenze'), 'Vorschlag: Überhang der Kostenstelle zuerst, Hinweis Bagatellgrenze');
    await seite.eingeben('#wa-bez', 'Oszilloskop Embedded');
    await seite.warteAuf(`[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Als geplante Ausgabe übernehmen' && !b.disabled)`);
    await seite.bild(join(bilder, 'f7-aufteilen.png'));
    await seite.klickText('button', 'Als geplante Ausgabe übernehmen');
    await seite.warteAuf(`document.body.innerText.includes('Oszilloskop Embedded') && !document.body.innerText.includes('Wirkung auf die Zeitachse')`);
    pruefe(true, 'als geplante Ausgabe übernommen');

    console.log('• Auslagen');
    await seite.klickText('button', 'Auslagen');
    await seite.warteAuf(`!!document.querySelector('input[aria-label="Bezeichnung der Abrechnung"]')`);
    await seite.eingeben('input[aria-label="Bezeichnung der Abrechnung"]', 'Sammelabrechnung Test');
    await seite.klickText('button', 'Anlegen');
    await seite.warteAuf(`!!document.querySelector('#p-datum')`);
    const auslage = async (datum, haendler, artikel, betrag, topf) => {
        await setze('#p-datum', datum);
        await seite.eingeben('#p-haendler', haendler);
        await seite.eingeben('#p-artikel', artikel);
        await seite.eingeben('#p-betrag', betrag);
        await waehle('#p-topf', topf);
        await seite.warteAuf(`[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Position eintragen' && !b.disabled)`);
        await seite.klickText('button', 'Position eintragen');
        await seite.warteAuf(`document.body.innerText.includes(${JSON.stringify(artikel)})`);
    };
    await auslage(`${jahr}-11-03`, 'Händler A', 'Schrittmotoren', '89,90', 'Kostenstelle');
    await auslage(`${jahr}-02-20`, 'Händler B', 'Kabel vor der Laufzeit', '12', `SQM-${jahr}-15`);
    text = await seite.text();
    pruefe(text.includes('außerhalb der Laufzeit') && text.includes('Gesamt'), 'Prüfung: Kauf vor der SQM-Laufzeit wird gemeldet');
    const xlsx = await seite.eval(`(async () => { const a = [...document.querySelectorAll('a')].find(x => x.textContent.includes('Aufstellung je Topf'));
        const r = await fetch(a.href); const b = new Uint8Array(await r.arrayBuffer()); return { status: r.status, typ: r.headers.get('content-type'), pk: b[0] === 80 && b[1] === 75 }; })()`);
    pruefe(xlsx.status === 200 && xlsx.typ.includes('spreadsheetml') && xlsx.pk, 'Aufstellung als Excel-Datei');
    await seite.klickText('button', 'Als eingereicht markieren');
    await seite.warteAuf(`document.body.innerText.includes('Wieder öffnen')`);
    pruefe(!(await seite.eval(`!!document.querySelector('#p-datum')`)), 'eingereicht: Positionen nicht mehr änderbar');
    await seite.bild(join(bilder, 'f4-auslage.png'));

    console.log('• Hilfskräfte');
    await seite.oeffnen(basis + teamUrl.replace(/^\//, '') + '?bereich=hilfskraefte');
    await seite.warteAuf(`document.body.innerText.includes('Was ist möglich?') && document.querySelectorAll('.pm-tbl tbody tr').length >= 2`);
    text = await seite.text();
    pruefe(text.includes(`SQM-${jahr}-15`) && text.includes('12103012') && !text.includes('AUT-'), 'Möglichkeiten je Topf (Kostenstelle und SQM)');
    await seite.eingeben('#r-person', 'N. N. Tutor Embedded');
    await seite.eingeben('#r-std', '20');
    await setze('#r-bis', `${jahr + 1}-06`);
    await seite.klickText('button', 'Berechnen');
    await seite.warteAuf(`document.body.innerText.includes('Reicht bis')`);
    pruefe(true, 'Rechner liefert einen Vorschlag');
    await seite.bild(join(bilder, 'f5-hilfskraefte.png'));
    await seite.klickText('button', 'Als Beschäftigung übernehmen');
    await seite.warteAuf(`document.body.innerText.includes('Studentische Hilfskraft') && document.body.innerText.includes('N. N. Tutor Embedded')`);
    pruefe(true, 'Vorschlag als Beschäftigung übernommen');
    await seite.klickText('button', 'Cockpit');
    await seite.warteAuf(`!!document.querySelector('.pm-kachel')`);
    pruefe((await seite.text()).includes('Hilfskraft N. N. Tutor Embedded'.slice(0, 10)) || (await seite.eval(`document.querySelectorAll('.pm-fin-bindungen').length >= 1`)), 'Cockpit zeigt die Bindungen');

    console.log('• SQM-Liste und Investitionen (KI-JSON)');
    await seite.oeffnen(basis + teamUrl.replace(/^\//, '') + '?bereich=import');
    await seite.warteAuf(`!!document.querySelector('#sqm-datei')`);
    await seite.dateiSetzen('#sqm-datei', join(process.cwd(), 'tools', 'Testdaten', 'sqm-liste-beispiel.xlsx'));
    await seite.warteAuf(`document.body.innerText.includes('Anträge anderer Personen übergangen')`);
    text = await seite.text();
    pruefe(text.includes('3 Anträge anderer Personen übergangen') && !text.includes('SQM-2026-13') && !text.includes('SQM-2026-14'),
        'nur Anträge des Teams sichtbar (Niemeyer ist noch nicht im Team; andere nur gezählt)');
    pruefe(text.includes(`SQM-${jahr}-15`) && text.includes('aktualisieren') && text.includes('5.000,00 €'), `SQM-${jahr}-15 wird aktualisiert`);
    await seite.eval(`[...document.querySelectorAll('section')].find(s => s.innerText.includes('SQM-Liste der Fakultät')).querySelector('button.pm-btn-haupt').click()`);
    await seite.warteAuf(`document.body.innerText.includes('Maßnahme(n) übernommen')`);
    pruefe(true, 'SQM-Liste übernommen');

    await seite.klickText('button', 'Prompt kopieren');
    await seite.warteAuf(`document.body.innerText.includes('In der Zwischenablage') || document.body.innerText.includes('Kopieren nicht möglich')`);
    pruefe(true, 'Prompt kopieren (oder Hinweis zum Markieren)');
    const json = JSON.stringify({ format: 'profomat-investitionen-v1', stichtag: `${jahr}-10-27`, massnahmen: [
        { antragsnummer: `AUT-07-${String(jahr).slice(2)}`, laborbereich: 'Automatisierungstechnik', budget: 3200, ausgegeben: 0, rest: 3200, buchungen: [] }] });
    await seite.eingeben('#ki-json', json);
    await seite.klickText('button', 'Prüfen');
    await seite.warteAuf(`[...document.querySelectorAll('tr')].some(t => t.innerText.includes('AUT-07-'))`);
    await seite.eval(`[...document.querySelectorAll('tr')].find(t => t.innerText.includes('AUT-07-')).querySelector('input[type=checkbox]').click()`);
    await warte(300);
    await seite.eval(`[...document.querySelectorAll('section')].find(s => s.innerText.includes('Investitionsstand per KI-Chat')).querySelector('button.pm-btn-haupt').click()`);
    await seite.warteAuf(`document.body.innerText.includes('Töpfe (3)')`);
    pruefe(true, 'Investitionsmaßnahme aus dem JSON angelegt');

    const antraege = JSON.stringify({ format: 'profomat-investitionsantraege-v1', beschluss: `${jahr}-05-19`, antraege: [
        { antragsnummer: `AUT-07-${String(jahr).slice(2)}`, innenauftrag: '71211637', laborbereich: 'Automatisierungstechnik', antragsteller: 'Liebler',
            investition: 'Erstellung von Lehraufbauten für das Fach Embedded Systems: 4× STEMlab', sachkosten: 3200, art: 'Neu', begruendung: 'Sonst nur Simulationen.' },
        { antragsnummer: `AUT-01-${String(jahr).slice(2)}`, innenauftrag: '71211631', antragsteller: 'Andere', investition: 'Fremder Antrag', sachkosten: 7500 }] });
    await seite.eingeben('#an-json', antraege);
    await seite.eval(`[...[...document.querySelectorAll('section')].find(s => s.innerText.includes('Investitionsanträge (Beschlussliste)')).querySelectorAll('button')].find(b => b.textContent.trim() === 'Prüfen').click()`);
    await seite.warteAuf(`document.body.innerText.includes('1 Anträge anderer Personen übergangen')`);
    text = await seite.text();
    pruefe(text.includes('Innenauftrag – → 71211637') && !text.includes('Fremder Antrag'), 'Beschlussliste: eigener Antrag aktualisiert, fremder nur gezählt');
    await seite.eval(`[...document.querySelectorAll('section')].find(s => s.innerText.includes('Investitionsanträge (Beschlussliste)')).querySelector('button.pm-btn-haupt').click()`);
    await seite.warteAuf(`document.body.innerText.includes('1 Maßnahme(n) übernommen')`);
    await seite.oeffnen(basis + teamUrl.replace(/^\//, ''));
    await seite.warteAuf(`document.body.innerText.includes('Innenauftrag 71211637')`);
    pruefe((await seite.text()).includes('Erstellung von Lehraufbauten für das Fach Embedded Systems'), 'Verwendungszweck und Innenauftrag im Cockpit');
    await seite.bild(join(bilder, 'f8-import.png'));

    console.log('• Notizen, Verlauf, Prognose, Einzelbuchungen');
    await seite.oeffnen(basis + teamUrl.replace(/^\//, ''));
    await seite.warteAuf(`!!document.querySelector('.pm-kachel')`);
    await seite.klickText('a', 'Kostenstelle 12103012');
    await seite.warteAuf(`!!document.querySelector('.pm-fin-buchungen')`);
    await seite.eval(`[...document.querySelectorAll('.pm-fin-buchungen tbody tr')].find(t => t.innerText.includes('Gutschrift LED')).querySelector('button').click()`);
    await seite.warteAuf(`!!document.querySelector('#notiz-text')`);
    await seite.eingeben('#notiz-text', 'Zweite Gutschrift fehlt noch');
    await seite.eval(`[...document.querySelectorAll('.pm-fin-editor label')].find(l => l.innerText.includes('zur Nachverfolgung')).querySelector('input').click()`);
    await warte(300);
    await seite.klickText('button', 'Speichern');
    await seite.warteAuf(`!document.querySelector('#notiz-text') && document.body.innerText.includes('Zweite Gutschrift fehlt noch')`);
    pruefe((await seite.text()).includes('zur Nachverfolgung'), 'Kommentar und Markierung an der Buchung');
    await seite.oeffnen(basis + teamUrl.replace(/^\//, ''));
    await seite.warteAuf(`document.body.innerText.includes('1 Buchung(en) markiert')`);
    await seite.klickText('a', '1 Buchung(en) markiert');
    await seite.warteAuf(`document.querySelectorAll('.pm-fin-buchungen tbody tr').length === 1`);
    pruefe(true, 'Cockpit verlinkt die offen markierten Buchungen (gefiltert)');

    await seite.klickText('button', 'Verlauf');
    await seite.warteAuf(`document.body.innerText.includes('Jahresvergleich der Ausgaben')`);
    text = await seite.text();
    pruefe(text.includes('Dezember') && text.includes('Rest am Monatsende') && text.includes('Hilfskräfte'), 'Verlauf je Monat und Jahresvergleich je Gruppe');

    await seite.klickText('button', 'Budgetprognose');
    await seite.warteAuf(`!!document.querySelector('#p-prof')`);
    await seite.eingeben('input[aria-label=Veranstaltung]', 'Regelungstechnik Praktikum');
    await seite.eingeben('input[aria-label=Teilnehmende]', '80');
    await seite.eingeben('input[aria-label=SWS]', '2');
    await seite.eingeben('#p-prof', '2');
    await seite.eingeben('#p-ma', '1');
    await warte(300);
    pruefe((await seite.text()).includes(`Prognose ${jahr + 1}`) && (await seite.text()).includes('5.300,00 €'), 'Prognose rechnet live (80 × 2 × 12,50 + 2 × 1.250 + 800)');
    await seite.klickText('button', 'Prognose speichern');
    await seite.warteAuf(`document.body.innerText.includes('Budgetprognose ${jahr + 1} gespeichert')`);
    pruefe(true, 'Prognose gespeichert');
    await seite.bild(join(bilder, 'f9-prognose.png'));

    await seite.oeffnen(basis + teamUrl.replace(/^\//, ''));
    await seite.warteAuf(`!!document.querySelector('.pm-kachel')`);
    await seite.klickText('a', `SQM-${jahr}-15`);
    await seite.warteAuf(`!!document.querySelector('#eb-text')`);
    await setze('#eb-datum', `${jahr}-10-15`);
    await seite.eingeben('#eb-text', 'Bauteile Reichelt');
    await seite.eingeben('#eb-betrag', '100');
    await seite.warteAuf(`[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Eintragen' && !b.disabled)`);
    await seite.klickText('button', 'Eintragen');
    await seite.warteAuf(`document.body.innerText.includes('Bauteile Reichelt')`);
    pruefe(true, 'Einzelbuchung an der SQM-Maßnahme');

    await seite.oeffnen(basis + teamUrl.replace(/^\//, '') + '?bereich=team');
    await seite.warteAuf(`!!document.querySelector('#vorlauf')`);
    await seite.eingeben('#vorlauf', '45');
    await warte(300);
    await seite.eval(`[...document.querySelectorAll('section')].find(s => s.innerText.includes('Erinnerungen an Fristen')).querySelector('button').click()`);
    await seite.warteAuf(`document.body.innerText.includes('Erinnerungen eingestellt')`);
    pruefe(true, 'Vorlauf der Erinnerungen eingestellt');

    console.log('• Team');
    await seite.oeffnen(basis + teamUrl.replace(/^\//, '') + '?bereich=team');
    await seite.warteAuf(`!!document.querySelector('select[aria-label="Person auswählen"]')`);
    await seite.eval(`(() => { const s = document.querySelector('select[aria-label="Person auswählen"]'); s.value = [...s.options].find(o => o.text.includes('Niemeyer')).value;
        s.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await seite.warteAuf(`[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Aufnehmen' && !b.disabled)`);
    await seite.klickText('button', 'Aufnehmen');
    await seite.warteAuf(`document.body.innerText.includes('Philip Niemeyer')`);
    pruefe(true, 'Niemeyer aufgenommen');

    await anmelden(seite, 'Philip Niemeyer');
    await seite.oeffnen(basis + 'finanzen');
    pruefe((await seite.text()).includes(teamname), 'Niemeyer sieht das Team');
    await anmelden(seite, 'Erika Mustermann');
    await seite.oeffnen(basis + teamUrl.replace(/^\//, ''));
    await seite.warteAuf(`document.body.innerText.includes('nur, wer zum Laborteam gehört')`);
    pruefe(true, 'fremde Person wird abgewiesen');

    console.log('• 390 px');
    await anmelden(seite, 'Klaus Liebler');
    await seite.breite(390, 844);
    await seite.oeffnen(basis + teamUrl.replace(/^\//, ''));
    await seite.warteAuf(`!!document.querySelector('.pm-kachel')`);
    await warte(300);
    pruefe(await seite.eval(`document.documentElement.scrollWidth <= 392`), 'Cockpit ohne seitliches Scrollen auf 390 px');
    await seite.bild(join(bilder, 'f3-cockpit-390.png'));
    console.log(`\nAlles geprüft. Bildschirmfotos: ${bilder}`);
} finally {
    await beenden();
}
