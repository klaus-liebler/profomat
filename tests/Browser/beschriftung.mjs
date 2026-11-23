// Browserprüfung „Verteiler beschriften“: Schrank anlegen, Geräte per Klick und per Ziehen einsetzen, beschriften (Text, Symbol, Farbe),
// mit Pfeiltasten verschieben, Feld hinzufügen, PDF mit Schnittmarken abrufen, 390 px.
// Aufruf: node tests/Browser/beschriftung.mjs http://localhost:5310 <Ordner>
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const warte = (ms) => new Promise((r) => setTimeout(r, ms));
const pruefe = (bedingung, text) => { if (!bedingung) { throw new Error(text); } console.log('  ✓', text); };

async function anmelden(seite) {
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes('Klaus Liebler')).querySelector('button').click()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);
}

const { seite, beenden } = await browserStarten({ port: 9341, breite: 1400, hoehe: 1000 });
const geraete = () => seite.eval(`[...document.querySelectorAll('.pm-bs-feld')].map((f, i) => [...f.querySelectorAll('.pm-bs-geraet')].map(g =>
    i + ':' + g.closest('.pm-bs-reihe').querySelector('.pm-bs-rnr').textContent + ':' + g.style.gridColumn.replace(/\\s/g, '') + ':' + g.getAttribute('aria-label'))).flat()`);
const platz = (feld, reihe, nr) => `document.querySelectorAll('.pm-bs-feld')[${feld}].querySelector('[data-platz="${reihe}-${nr}"]')`;

try {
    await anmelden(seite);
    console.log('• Schrank anlegen');
    await seite.oeffnen(basis + 'beschriftung');
    await seite.eingeben('#bezeichnung', 'Hauptverteiler Test');
    await seite.eingeben('#temm', '17,5');
    await seite.eingeben('#hmm', '12');
    await seite.klickText('button', 'Schaltschrank anlegen');
    await seite.warteAuf(`document.querySelector('.pm-bs-feld')`);
    await warte(800);
    pruefe(await seite.eval(`document.querySelectorAll('.pm-bs-frei').length`) === 48, '1 Feld, 4 Reihen × 12 freie TE');
    pruefe((await seite.text()).includes('12 TE × 17,5 mm = 210 mm · Streifenhöhe 12 mm'), 'Maße aus dem Anlegen übernommen');
    pruefe(await seite.eval(`document.getElementById('masse').getBoundingClientRect().top < document.querySelector('.pm-bs-feld').getBoundingClientRect().top`),
        'Maße stehen oben');
    await seite.eingeben('#bs-hmm', '10');
    await seite.klickText('button', 'Maße übernehmen');
    await warte(500);
    pruefe((await seite.text()).includes('Streifenhöhe 10 mm'), 'Streifenhöhe im Editor geändert');

    console.log('• Einsetzen per Klick: Platz wählen, dann Gerät');
    await seite.eval(`${platz(0, 1, 1)}.click()`);
    await warte(300);
    await seite.klickText('.pm-bs-teil', 'FI');
    await warte(500);
    await seite.eval(`${platz(0, 1, 5)}.click()`);
    await warte(300);
    await seite.klickText('.pm-bs-teil', 'LS');
    await warte(500);
    let g = await geraete();
    pruefe(g.length === 2 && g[0].startsWith('0:1:1/span4:FI, 4 TE') && g[1].startsWith('0:1:5/span1:LS, 1 TE'), `FI (4 TE) und LS gesetzt, ohne Kennzeichen: ${g.join(' | ')}`);
    pruefe(await seite.eval(`document.activeElement.id === 'bs-kz'`), 'Cursor steht im Kennzeichen (aus dem Stromlaufplan)');

    console.log('• Beschriften: Text, Zusatz, Symbol, Farbe');
    pruefe(await seite.eval(`document.querySelector('.pm-bs-seite h2').textContent.includes('Leitungsschutzschalter')`), 'neues Gerät ist gewählt');
    await seite.tippen('-F12⇥');
    await warte(500);
    await seite.eingeben('#bs-text', 'Küche Steckdosen');
    await warte(400);
    await seite.eingeben('#bs-zusatz', 'B16');
    await warte(400);
    await seite.klick('.pm-bs-symbolwahl[aria-label="Steckdose"]');
    await seite.klick('.pm-bs-farbe[aria-label="Grün"]');
    await warte(400);
    g = await geraete();
    pruefe(g[1].includes('LS, -F12, Küche Steckdosen, B16'), `Kennzeichen, Text und Zusatz gespeichert (${g[1]})`);
    pruefe(await seite.eval(`[...document.querySelectorAll('.pm-bs-geraet')][1].querySelector('svg rect[fill="#DDF5E3"]') !== null`), 'Hintergrund grün im Etikett');
    pruefe(await seite.eval(`[...document.querySelectorAll('.pm-bs-geraet')][1].querySelectorAll('svg circle').length >= 3`), 'Steckdosensymbol im Etikett');

    console.log('• Kopie daneben, Pfeiltasten');
    await seite.klickText('.pm-bs-aktionen button', 'Kopie daneben');
    await warte(500);
    await seite.eval(`document.querySelector('.pm-bs-geraet.pm-bs-aktiv').focus()`);
    await seite.tippen('↓');
    await warte(700);
    await seite.tippen('→');
    await warte(700);
    g = await geraete();
    pruefe(g.some((x) => x.startsWith('0:2:7/span1:LS, Küche Steckdosen')), `Kopie nach unten und rechts verschoben: ${g.join(' | ')}`);

    console.log('• Kein Platz: verständliche Meldung');
    await seite.eval(`${platz(0, 1, 6)}.click()`);
    await seite.klickText('.pm-bs-teil', 'Zähler');   // TE 6–9
    await warte(300);
    await seite.eval(`${platz(0, 1, 10)}.click()`);
    await seite.klickText('.pm-bs-teil', 'FI');       // 4 TE ab Platz 10 → passt nicht
    await warte(600);
    pruefe((await seite.text()).includes('passt nicht in die Reihe'), 'Meldung „Das passt nicht in die Reihe …“');

    console.log('• Feld hinzufügen, Gerät hinüberziehen');
    await seite.klickText('button', 'Feld hinzufügen');
    await warte(600);
    pruefe(await seite.eval(`document.querySelectorAll('.pm-bs-feld').length`) === 2, 'zweites Feld');
    const zieh = (quelle, ziel) => seite.eval(`(() => { const dt = new DataTransfer(); const q = ${quelle}; const z = ${ziel};
        q.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt }));
        setTimeout(() => { z.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
                           z.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt })); }, 250); })()`);
    await zieh(`[...document.querySelectorAll('.pm-bs-teil')].find(b => b.textContent.includes('Automation'))`, platz(1, 1, 1));
    await warte(900);
    await zieh(`document.querySelector('.pm-bs-geraet')`, platz(1, 2, 9));   // FI ins zweite Feld
    await warte(900);
    g = await geraete();
    pruefe(g.some((x) => x.startsWith('1:1:1/span4:Automation')) && g.some((x) => x.startsWith('1:2:9/span4:FI')), `Ziehen: ${g.join(' | ')}`);
    await seite.bild(join(bilder, 'b1-editor.png'));

    console.log('• PDF');
    const pdf = await seite.eval(`(async () => { const a = document.querySelector('a[href*="streifen.pdf"]'); const r = await fetch(a.href);
        const b = new Uint8Array(await r.arrayBuffer()); let s = ''; for (const x of b) { s += String.fromCharCode(x); }
        return { status: r.status, typ: r.headers.get('content-type'), daten: btoa(s), text: a.textContent }; })()`);
    const bytes = Buffer.from(pdf.daten, 'base64');
    pruefe(pdf.status === 200 && pdf.typ === 'application/pdf' && bytes.subarray(0, 5).toString() === '%PDF-', `PDF abrufbar (${pdf.text.trim()}, ${bytes.length} Bytes)`);
    writeFileSync(join(bilder, 'beschriftung.pdf'), bytes);

    console.log('• 390 px');
    const adresse = await seite.eval('location.href');
    const { seite: schmal, beenden: beenden2 } = await browserStarten({ port: 9342, breite: 390, hoehe: 844 });
    try {
        await anmelden(schmal);
        await schmal.oeffnen(adresse);
        await warte(600);
        const breit = await schmal.eval(`document.documentElement.scrollWidth`);
        pruefe(breit <= 392, `keine waagerechte Rollleiste (scrollWidth ${breit})`);
        await schmal.bild(join(bilder, 'b2-390px.png'));
    } finally {
        await beenden2();
    }
    console.log('Alles in Ordnung. Bilder:', bilder);
} finally {
    await beenden();
}
