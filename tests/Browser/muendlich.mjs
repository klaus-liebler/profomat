// Browserprüfung „Mündliche Prüfung live“: nur Tastatur, schnelles Tippen, Zurückstellen, Wiederaufnehmen, Note, 390 px.
// Aufruf: node tests/Browser/muendlich.mjs http://localhost:5310 <Ordner>
import { mkdirSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const { seite, beenden } = await browserStarten({ port: 9337, breite: 1280, hoehe: 900 });
const warte = (ms) => new Promise((r) => setTimeout(r, ms));
const pruefe = (bedingung, text) => { if (!bedingung) { throw new Error(text); } console.log('  ✓', text); };
const zeilen = () => seite.eval(`[...document.querySelectorAll('.pm-live-tabelle tbody tr:not([data-entwurf])')].map(tr =>
    [...tr.querySelectorAll('input')].map(i => i.value).join('|') + '|' + tr.cells[4].innerText.trim())`);
const note = () => seite.eval(`document.querySelector('.pm-live-gross b').textContent.trim()`);
const fokus = () => seite.eval(`(() => { const a = document.activeElement; const tr = a.closest('tr'); return (tr?.hasAttribute('data-entwurf') ? 'neu:' : 'zeile:') + a.dataset.spalte; })()`);

try {
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes('Erika Mustermann')).querySelector('button').click()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);

    console.log('• Protokoll per Tastatur anlegen');
    await seite.oeffnen(basis + 'muendlich');
    pruefe(await seite.eval(`document.activeElement.id === 'bezeichnung'`), 'Bezeichnung hat den Fokus');
    await seite.tippen('Regelungstechnik Test⏎');
    await seite.warteAuf(`location.pathname.includes('/muendlich/') && document.querySelector('.pm-live-tabelle')`);
    await warte(800);
    pruefe(await fokus() === 'neu:nummer', 'Fokus steht in der Fragennummer der Eingabezeile');

    console.log('• Schnell tippen (25 ms je Taste): 7/5/4, 8/(Max übernommen)/z, 9/10/7,5');
    await seite.tippen('7⏎5⏎4⏎8⏎⏎z⏎9⏎10⏎7,5⏎');
    await warte(1200);
    let z = await zeilen();
    console.log('   ', z.join('  ·  '));
    pruefe(z.length === 3, 'drei Zeilen gespeichert');
    pruefe(z[0].startsWith('7|5|4|') && z[1].startsWith('8|5|z|') && z[2].startsWith('9|10|7,5|'), 'Werte landen in den richtigen Feldern');
    pruefe(z[1].includes('zurückgestellt'), 'Frage 8 ist zurückgestellt');
    pruefe(await note() === '2,3', `Note 2,3 (11,5 von 15 = 76,7 %), angezeigt: ${await note()}`);
    pruefe(await fokus() === 'neu:nummer', 'Fokus wieder in der neuen Zeile');
    await seite.bild(join(bilder, 'm1-live.png'));

    console.log('• Zurückgestellte Frage 8 wieder aufnehmen');
    await seite.tippen('8⏎');
    await warte(300);
    pruefe(await fokus() === 'zeile:punkte', 'Sprung ins Punktefeld von Frage 8');
    await seite.tippen('5⏎');
    await warte(900);
    z = await zeilen();
    pruefe(z.length === 3 && z[1].startsWith('8|5|5|') && z[1].includes('nachgeholt'), 'Frage 8 nachgeholt, keine doppelte Zeile');
    pruefe(await note() === '2,0', `Note 2,0 (16,5 von 20 = 82,5 %), angezeigt: ${await note()}`);

    console.log('• Pfeiltasten und Korrektur');
    await seite.tippen('↑↑');
    await warte(200);
    pruefe(await seite.eval(`document.activeElement.closest('tr').cells[1].querySelector('input').value`) === '8', 'zwei Zeilen hoch: Frage 8');
    await seite.tippen('⏎⏎z⏎');   // Frage → Max. → Punkte (Inhalt markiert) → z
    await warte(900);
    z = await zeilen();
    pruefe(z[1].startsWith('8|5|z|'), 'erneut zurückgestellt');

    console.log('• Fehler: Punkte über dem Maximum');
    await seite.tippen('3⏎2⏎9⏎');
    await warte(900);
    pruefe((await seite.text()).includes('Punkte zwischen 0 und 2'), 'Meldung „Punkte zwischen 0 und 2“');
    await seite.tippen('⎋');
    await warte(600);

    console.log('• Neu laden: alles gespeichert');
    await seite.oeffnen(await seite.eval('location.href'));
    await warte(600);
    z = await zeilen();
    pruefe(z.length === 4, `vier Zeilen nach dem Neuladen (${z.length})`);
    await seite.bild(join(bilder, 'm2-nach-neuladen.png'));

    console.log('• 390 px');
    const { seite: schmal, beenden: beenden2 } = await browserStarten({ port: 9338, breite: 390, hoehe: 844 });
    try {
        await schmal.oeffnen(basis + 'anmelden');
        await schmal.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes('Erika Mustermann')).querySelector('button').click()`);
        await schmal.warteAuf(`!location.pathname.endsWith('/anmelden')`);
        await schmal.oeffnen(await seite.eval('location.href'));
        await warte(600);
        const breit = await schmal.eval(`document.documentElement.scrollWidth`);
        pruefe(breit <= 392, `keine waagerechte Rollleiste (scrollWidth ${breit})`);
        await schmal.bild(join(bilder, 'm3-390px.png'));
    } finally {
        await beenden2();
    }
    console.log('Alles in Ordnung. Bilder:', bilder);
} finally {
    await beenden();
}
