// Browserprüfung Grundriss-Editor und Sitzplan. Aufruf: node tests/Browser/grundriss.mjs http://localhost:5310 <Ordner>
// Erwartet frische Demodaten; legt dafür vorher mit klausur.mjs eine Klausur mit verteilten Plätzen an (SI 0012 + SI 0110).
import { mkdirSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const { seite, beenden } = await browserStarten({ port: 9336, breite: 1440, hoehe: 1000 });
const warte = (ms) => new Promise((r) => setTimeout(r, ms));
try {
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes('Erika Mustermann')).querySelector('button').click()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);

    console.log('• Editor öffnen (SI 0012)');
    await seite.oeffnen(basis + 'raeume');
    await seite.eval(`[...document.querySelectorAll('tr')].find(t => t.textContent.includes('SI 0012')).querySelector('a').click()`);
    await seite.warteAuf(`document.querySelector('.pm-grundriss')`);
    await warte(600);
    const vorher = await seite.eval(`document.querySelectorAll('.pm-grundriss rect.pm-gr-tisch').length`);
    console.log('  Tische:', vorher);
    await seite.bild(join(bilder, 'g1-editor.png'));

    console.log('• Tisch entfernen und setzen, Nummerierung ändern');
    const zelle = (x, y) => seite.eval(`(() => { const z = [...document.querySelectorAll('.pm-gr-zelle')].find(r => r.getAttribute('x') == ${x * 30} && r.getAttribute('y') == ${y * 30});
        z.dispatchEvent(new MouseEvent('click', { bubbles: true })); })()`);
    await zelle(2, 4);   // vorhandenen Tisch entfernen
    await warte(400);
    await zelle(19, 6);  // neuen Tisch setzen
    await warte(400);
    const nachher = await seite.eval(`document.querySelectorAll('.pm-grundriss rect.pm-gr-tisch').length`);
    console.log('  Tische nach Klicks:', nachher);
    if (nachher !== vorher) { throw new Error('Tisch an/aus wirkt nicht'); }
    await seite.eval(`(() => { const s = document.querySelector('#ecke'); s.value = 'UntenRechts'; s.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await warte(500);
    const erste = await seite.eval(`(() => { const t = [...document.querySelectorAll('.pm-grundriss text.pm-gr-nr')].find(x => x.textContent === '1'); return t.getAttribute('x') + '/' + t.getAttribute('y'); })()`);
    console.log('  Tisch 1 jetzt bei', erste);
    await seite.klickText('button', 'Tafel');
    await zelle(1, 1);
    await warte(400);
    await seite.klickText('button', 'Speichern');
    await warte(800);
    await seite.bild(join(bilder, 'g2-editor-geaendert.png'));
    if (!(await seite.text()).includes('Grundriss gespeichert')) { throw new Error('nicht gespeichert'); }

    console.log('• Sitzplan in der Klausur');
    await seite.oeffnen(basis + 'klausuren');
    const link = await seite.eval(`document.querySelector('a[href^="klausuren/"]')?.getAttribute('href')`);
    if (link) {
        await seite.oeffnen(basis + link);
        await seite.klickText('button', 'Räume übernehmen und Plätze verteilen');
        await seite.warteAuf(`document.body.innerText.includes('Sitzplan SI 0012')`);
        await warte(500);
        const belegt = await seite.eval(`document.querySelectorAll('.pm-gr-belegt').length`);
        console.log('  belegte Tische im Sitzplan:', belegt);
        await seite.eval(`[...document.querySelectorAll('h2')].find(h => h.textContent.includes('Sitzplan')).scrollIntoView()`);
        await seite.bild(join(bilder, 'g3-sitzplan.png'));
    } else {
        console.log('  (keine Klausur vorhanden – vorher klausur.mjs laufen lassen)');
    }

    console.log(`\nGrundriss geprüft. Bilder: ${bilder}`);
} catch (fehler) {
    console.error('FEHLER:', fehler.message);
    await seite.bild(join(bilder, 'g-fehler.png'));
    process.exitCode = 1;
} finally {
    await beenden();
}
