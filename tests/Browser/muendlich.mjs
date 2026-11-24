// Browserprüfung „Mündliche Prüfung live“: nur Tastatur, schnelles Tippen, Zurückstellen, Wiederaufnehmen, Note, Bemerkung,
// Angaben oben (Prüfling, Prüfende mit und ohne Konto, Studiengang – sofort gespeichert), Protokoll (Note anpassen, PDF),
// Löschen per Mülleimer in der Übersicht, 390 px.
// Aufruf: node tests/Browser/muendlich.mjs http://localhost:5310 <Ordner>
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
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
    await seite.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes('Klaus Liebler')).querySelector('button').click()`);
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

    console.log('• Bemerkung per Tab in der Eingabezeile');
    await seite.eval(`document.querySelector('tr[data-entwurf] input[data-spalte=nummer]').focus()`);
    await seite.tippen('12⏎4⏎3⇥Bode-Diagramm sicher⏎');
    await warte(1000);
    z = await zeilen();
    pruefe(z.length === 5 && z[4].startsWith('12|4|3|Bode-Diagramm sicher|'), `Bemerkung gespeichert (${z[4]})`);
    pruefe(await fokus() === 'neu:nummer', 'Fokus wieder in der neuen Zeile');

    console.log('• Angaben oben: Prüfling, Prüfende (mit und ohne Konto), Studiengang');
    const wahl = (id, text) => seite.eval(`(() => { const s = document.getElementById('${id}'); const o = [...s.options].find(o => o.textContent.includes('${text}'));
        s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); return o.textContent; })()`);
    const hilfe = (id) => seite.eval(`document.getElementById('${id}').parentElement.querySelector('.pm-hilfe').textContent`);
    pruefe(await seite.eval(`!!(document.getElementById('angaben').compareDocumentPosition(document.querySelector('.pm-live-tabelle')) & Node.DOCUMENT_POSITION_FOLLOWING)`),
        'Angaben stehen über den Fragen');
    pruefe((await seite.eval(`document.getElementById('erst').value`)).includes('Liebler'), 'Erstprüfer vorbelegt: Liebler');
    pruefe(await seite.eval(`[...document.querySelectorAll('#pm-pruefende option')].some(o => o.value.includes('Niemeyer'))`), 'Niemeyer (Konto) in der Vorschlagsliste');
    await seite.eingeben('#pruefling', 'Mia Muster');
    await seite.eingeben('#matrikel', '1234567');
    pruefe(await seite.eval(`document.querySelectorAll('#pm-studiengaenge option').length`) > 0, 'Studiengänge aus dem Katalog zur Auswahl');
    await seite.eingeben('#studiengang', 'Mechatronik (B.Sc., eigene Eingabe)');
    await seite.eingeben('#zweit', 'Dr. Erika Extern');
    await warte(300);
    pruefe((await hilfe('zweit')).includes('ohne Konto'), 'Zweitprüferin ohne Konto per Namen');
    await seite.oeffnen(await seite.eval('location.href'));
    await warte(600);
    pruefe(await seite.eval(`document.getElementById('pruefling').value === 'Mia Muster' && document.getElementById('zweit').value === 'Dr. Erika Extern'`),
        'Angaben nach dem Neuladen noch da (sofort gespeichert)');
    await seite.eingeben('#zweit', 'Prof. Dr. Philip Niemeyer');
    await warte(300);
    pruefe((await hilfe('zweit')).includes('mit Konto'), 'Niemeyer mit Konto verknüpft');
    await seite.bild(join(bilder, 'm0-angaben.png'));

    console.log('• Protokoll: Note anpassen, PDF');
    const vorschlag = await seite.eval(`document.querySelector('.pm-live-vorschlag b').textContent.trim()`);
    pruefe((await seite.eval(`document.getElementById('note').selectedOptions[0].textContent`)).includes('(Vorschlag)'), `Note steht auf dem Vorschlag ${vorschlag}`);
    await wahl('note', '1,7');
    await warte(300);
    pruefe(await seite.eval(`!!document.getElementById('begruendung')`), 'Abweichung: Feld für die Begründung');
    await seite.eingeben('#begruendung', 'Sehr sichere Herleitung im Gespräch');
    await seite.klickText('button', 'Note festlegen und Protokoll erstellen');
    await warte(800);
    pruefe((await seite.text()).includes('Festgelegt: 1,7 (abweichend vom Vorschlag)'), 'Note 1,7 festgelegt');
    const pdf = await seite.eval(`(async () => { const r = await fetch(document.querySelector('a[href$="protokoll.pdf"]').href);
        const b = new Uint8Array(await r.arrayBuffer()); let s = ''; for (const x of b) { s += String.fromCharCode(x); }
        return { status: r.status, typ: r.headers.get('content-type'), daten: btoa(s) }; })()`);
    const bytes = Buffer.from(pdf.daten, 'base64');
    pruefe(pdf.status === 200 && pdf.typ === 'application/pdf' && bytes.subarray(0, 5).toString() === '%PDF-', `PDF abrufbar (${bytes.length} Bytes)`);
    writeFileSync(join(bilder, 'protokoll.pdf'), bytes);
    await seite.bild(join(bilder, 'm4-protokoll.png'));

    console.log('• Punkte geändert: Festlegung aufgehoben');
    await seite.eval(`document.querySelectorAll('tbody tr[data-zeile]:not([data-entwurf])')[4].querySelector('input[data-spalte=punkte]').select()`);
    await seite.tippen('4⏎');
    await warte(900);
    pruefe((await seite.text()).includes('bitte die Note erneut festlegen'), 'Hinweis: Note erneut festlegen');

    console.log('• Übersicht: Löschen per Mülleimer mit Rückfrage');
    await seite.oeffnen(basis + 'muendlich');
    await warte(600);
    const anzahl = () => seite.eval(`document.querySelectorAll('.pm-card table tbody tr').length`);
    const vorher = await anzahl();
    await seite.klick('button.pm-muell');
    await warte(300);
    pruefe(await anzahl() === vorher, 'erst die Rückfrage, noch nichts gelöscht');
    await seite.bild(join(bilder, 'm5-loeschen-rueckfrage.png'));
    await seite.klickText('button', 'Endgültig löschen');
    await warte(600);
    pruefe(await anzahl() === vorher - 1 || (vorher === 1 && (await seite.text()).includes('Noch keine')), 'Protokoll gelöscht');

    console.log('• 390 px');
    const { seite: schmal, beenden: beenden2 } = await browserStarten({ port: 9338, breite: 390, hoehe: 844 });
    try {
        await schmal.oeffnen(basis + 'anmelden');
        await schmal.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes('Klaus Liebler')).querySelector('button').click()`);
        await schmal.warteAuf(`!location.pathname.endsWith('/anmelden')`);
        await schmal.oeffnen(basis + 'muendlich');
        await schmal.tippen('390 px Test⏎');
        await schmal.warteAuf(`location.pathname.includes('/muendlich/') && document.querySelector('.pm-live-tabelle')`);
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
