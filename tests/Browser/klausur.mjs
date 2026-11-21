// Browserprüfung Klausuren: Upload der Prüfungsliste, Räume/Plätze, Aufgaben, Punkte, Ergebnis.
// Aufruf:  node tests/Browser/klausur.mjs http://localhost:5310 <Ordner für Bildschirmfotos>
import { mkdirSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const liste = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tools', 'Testdaten', 'pruefungsliste-beispiel.csv');
const { seite, beenden } = await browserStarten({ port: 9335, hoehe: 1000 });
const schritt = (t) => console.log(`• ${t}`);
let nr = 0;
const foto = async (name) => { await seite.bild(join(bilder, `k${String(++nr).padStart(2, '0')}-${name}.png`)); };
const laden = (pfad) => seite.eval(`fetch(${JSON.stringify(pfad)}).then(async r => { const b = new Uint8Array(await r.arrayBuffer()); const typ = r.headers.get('content-type') ?? '';
    return { status: r.status, typ, groesse: b.length, text: typ.includes('csv') ? new TextDecoder().decode(b) : '' }; })`);

try {
    schritt('Anmelden als Prüfende');
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes('Erika Mustermann')).querySelector('button').click()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);

    schritt('Prüfungsliste hochladen');
    await seite.oeffnen(basis + 'klausuren');
    await seite.dateiSetzen('input[type=file]', liste);
    await seite.warteAuf(`document.body.innerText.includes('57 Teilnehmende')`);
    await seite.eingeben('#kuerzel', 'RT');
    await foto('upload');
    await seite.klickText('button', 'Klausur anlegen');
    await seite.warteAuf(`document.body.innerText.includes('Klausurdruck vorbereiten')`);
    const klausur = await seite.eval('location.pathname');

    schritt('Räume wählen und verteilen');
    for (const raum of ['SI 0012', 'SI 0110']) {
        await seite.eval(`[...document.querySelectorAll('.pm-list label')].find(l => l.textContent.includes(${JSON.stringify(raum)})).querySelector('input').click()`);
        await new Promise((r) => setTimeout(r, 300));
    }
    await seite.warteAuf(`document.body.innerText.includes('72 Plätze für 57 Teilnehmende')`);
    await seite.klickText('button', 'Räume übernehmen und Plätze verteilen');
    await seite.warteAuf(`document.body.innerText.includes('Verteilt am')`);
    await foto('verteilt');
    const serienbrief = await laden(klausur + '/serienbrief.csv');
    console.log('  Serienbrief:', serienbrief.status, serienbrief.text.split('\r\n')[1]);
    for (const d of ['sitzlisten.pdf', 'aushang.pdf']) {
        const r = await laden(`${klausur}/${d}`);
        console.log(`  ${d}:`, r.status, r.typ, r.groesse);
        if (r.status !== 200) { throw new Error(d); }
    }

    schritt('Aufgaben (4 × 25) und Notenschlüssel speichern');
    await seite.oeffnen(basis + klausur.replace(/^\//, '') + '?bereich=bewertung');
    await seite.klickText('button', 'Aufgaben speichern');
    await seite.warteAuf(`document.body.innerText.includes('Standard auf 100 Punkte umrechnen')`);
    await seite.klickText('button', 'Notenschlüssel speichern');
    await foto('bewertung');

    schritt('Punkte eintragen');
    await seite.oeffnen(basis + klausur.replace(/^\//, '') + '?bereich=punkte');
    const punkte = [[25, 25, 25, 20], [20, 15, 10, 5], [12.5, 12, 12, 13], [5, 5, 5, 4]];
    for (let zeile = 0; zeile < punkte.length; zeile++) {
        for (let a = 0; a < 4; a++) {
            await seite.eval(`(() => { const el = document.querySelectorAll('tbody tr')[${zeile}].querySelectorAll('input.pm-input')[${a}];
                el.value = ${JSON.stringify(String(punkte[zeile][a]).replace('.', ','))}; el.dispatchEvent(new Event('change', { bubbles: true })); })()`);
            await new Promise((r) => setTimeout(r, 250));
        }
    }
    await seite.warteAuf(`document.querySelectorAll('tbody tr')[0].innerText.includes('95')`);
    await foto('punkte');
    await seite.klickText('button', 'Rest als nicht erschienen');
    await seite.warteAuf(`document.body.innerText.includes('57 von 57 erfasst')`);

    schritt('Ergebnis');
    await seite.oeffnen(basis + klausur.replace(/^\//, '') + '?bereich=ergebnis');
    await foto('ergebnis');
    const text = await seite.text();
    console.log('  Kennzahlen:', text.match(/Durchschnittsnote\*\s*([\d,–]+)/)?.[1], '| Durchfallquote', text.match(/Durchfallquote\*\s*([\d,% –]+)/)?.[1]?.trim());
    for (const d of ['ergebnis.pdf', 'ergebnis.xlsx', 'noten.csv']) {
        const r = await laden(`${klausur}/${d}`);
        console.log(`  ${d}:`, r.status, r.typ, r.groesse);
        if (r.status !== 200) { throw new Error(d); }
    }

    await seite.oeffnen(basis + 'raeume');
    await foto('raeume');
    console.log(`\nKlausurablauf vollständig. Bildschirmfotos: ${bilder}`);
} catch (fehler) {
    console.error('FEHLER:', fehler.message);
    await foto('fehler');
    console.error('Seitentext:', (await seite.text()).slice(0, 1500));
    process.exitCode = 1;
} finally {
    await beenden();
}
