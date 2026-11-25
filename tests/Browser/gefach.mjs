// Browserprüfung „Gefache lasern“: Gefach anlegen, 3D-Vorschau, Höhe (höchstens Stapelhöhe), Fachgrößen, Plattenansicht, SVG/DXF/ZIP,
// öffentlich sichtbar (andere Person: ansehen, ausprobieren ohne Speichern, als Vorlage übernehmen), 390 px. Aufruf: node tests/Browser/gefach.mjs http://localhost:5310 <Ordner>
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { browserStarten } from './cdp.mjs';

const basis = (process.argv[2] ?? 'http://localhost:5310').replace(/\/$/, '') + '/';
const bilder = process.argv[3] ?? mkdtempSync(join(tmpdir(), 'profomat-bilder-'));
mkdirSync(bilder, { recursive: true });
const warte = (ms) => new Promise((r) => setTimeout(r, ms));
const pruefe = (bedingung, text) => { if (!bedingung) { throw new Error(text); } console.log('  ✓', text); };

async function anmelden(seite, name = 'Klaus Liebler') {
    await seite.oeffnen(basis + 'anmelden');
    await seite.eval(`[...document.querySelectorAll('.pm-list > div')].find(d => d.textContent.includes(${JSON.stringify(name)})).querySelector('button').click()`);
    await seite.warteAuf(`!location.pathname.endsWith('/anmelden')`);
}

const abrufen = (seite, selektor) => seite.eval(`(async () => { const a = document.querySelector(${JSON.stringify(selektor)}); const r = await fetch(a.href);
    const b = new Uint8Array(await r.arrayBuffer()); let s = ''; for (const x of b) { s += String.fromCharCode(x); }
    return { status: r.status, typ: r.headers.get('content-type'), name: r.headers.get('content-disposition'), daten: btoa(s) }; })()`);

const { seite, beenden } = await browserStarten({ port: 9351, breite: 1400, hoehe: 1000 });
try {
    await anmelden(seite);
    console.log('• Gefach anlegen');
    await seite.oeffnen(basis + 'gefache');
    pruefe(await seite.eval(`[...document.querySelectorAll('nav a')].some(a => a.textContent.includes('Gefache lasern'))`), 'Menüpunkt unter Nerd Tools');
    const name = `Euro-Box Test ${Date.now() % 100000}`;
    await seite.eingeben('#bezeichnung', name);
    await seite.klickText('button', 'Gefach anlegen');
    await seite.warteAuf(`!!document.querySelector('#gf-il')`);
    await warte(1500);
    let text = await seite.text();
    pruefe(text.includes('3 × 2 Fächer · 3 Wände · 1 Platte 800 × 600 mm, 3 mm'), 'Euro-Lagerbox vorbelegt: 3 × 2 Fächer, 3 Wände, 1 Platte');
    pruefe(await seite.eval(`document.querySelector('#gf-il').value === '570' && document.querySelector('#gf-hs').value === '204' && document.querySelector('#gf-h').value === '204'`),
        'Innenlänge 570, Stapelhöhe 204, Gefachhöhe 204');
    const dreiD = await seite.eval(`(() => { const c = document.querySelector('.pm-gf-3d canvas'); return c ? c.width + 'x' + c.height
        : (document.querySelector('.pm-gf-ohne3d')?.textContent ?? 'nichts'); })()`);
    pruefe(/^\d+x\d+$/.test(dreiD) && !dreiD.startsWith('0x'), `3D-Vorschau gezeichnet (${dreiD})`);
    pruefe(await seite.eval(`document.querySelectorAll('svg.pm-gf-platte path').length === 3`), 'Plattenansicht mit 3 Wänden');
    await seite.bild(join(bilder, 'g1-euro.png'));

    console.log('• Höhe höchstens Stapelhöhe');
    await seite.eingeben('#gf-h', '250');
    await warte(600);
    pruefe((await seite.text()).includes('höchstens so hoch sein wie die nutzbare Innenhöhe im Stapel'), 'Höhe über Stapelhöhe abgelehnt');
    await seite.eingeben('#gf-h', '120');
    await warte(600);
    text = await seite.text();
    pruefe(text.includes('Gefach 120 mm hoch') && !text.includes('höchstens so hoch'), 'Höhe 120 mm übernommen, Meldung weg');
    await seite.eingeben('#gf-hs', '180');
    await warte(600);
    pruefe(await seite.eval(`document.querySelector('#gf-h').value === '120'`), 'andere Stapelhöhe lässt eine eigene, niedrigere Gefachhöhe stehen');
    await seite.eingeben('#gf-hs', '100');
    await warte(600);
    pruefe(await seite.eval(`document.querySelector('#gf-h').value === '100'`), 'Stapelhöhe unter der Gefachhöhe zieht diese mit');
    await seite.eingeben('#gf-hs', '204');
    await warte(600);
    pruefe(await seite.eval(`document.querySelector('#gf-h').value === '204'`), 'gleich hohe Gefachhöhe folgt der Stapelhöhe');
    await seite.eingeben('#gf-h', '150');
    await warte(600);

    console.log('• Fachgrößen in mm');
    await seite.klick('#gf-l-masse');
    await warte(600);
    await seite.eingeben('#gf-l-m', '100 * *');
    await warte(700);
    text = await seite.text();
    pruefe(text.includes('3 Fächer: 100 · 232 · 232 mm'), 'Fachgrößen 100 · 232 · 232 mm');
    await seite.eingeben('#gf-l-m', '400 300');
    await warte(600);
    pruefe((await seite.text()).includes('innen sind nur 570 mm'), 'zu große Fächer begründet abgelehnt');
    await seite.eingeben('#gf-l-m', '100 * *');
    await warte(600);
    await seite.eingeben('#gf-b-n', '4');
    await warte(700);
    text = await seite.text();
    pruefe(text.includes('3 × 4 Fächer · 5 Wände'), `3 × 4 Fächer, 5 Wände`);

    console.log('• Schräge Wände');
    await seite.klick('#gf-schraeg');
    await warte(700);
    pruefe(await seite.eval(`document.querySelector('#gf-bl')?.value === '550'`), 'Bodenmaße vorgeschlagen (550 mm)');
    await seite.klick('#gf-mitkiste');
    await warte(400);
    await seite.bild(join(bilder, 'g2-ohne-kiste.png'));
    await seite.klick('#gf-mitkiste');
    await warte(400);

    console.log('• Schnittvorlagen');
    const zip = await abrufen(seite, 'a[href$="schnittvorlagen.zip"]');
    const zipBytes = Buffer.from(zip.daten, 'base64');
    pruefe(zip.status === 200 && zip.typ === 'application/zip' && zipBytes.subarray(0, 2).toString() === 'PK', `ZIP abrufbar (${zipBytes.length} Bytes, ${zip.name})`);
    writeFileSync(join(bilder, 'schnittvorlagen.zip'), zipBytes);
    const svg = await abrufen(seite, 'a[href$="platte-1.svg"]');
    const svgText = Buffer.from(svg.daten, 'base64').toString('utf8');
    pruefe(svg.status === 200 && svgText.includes('width="800mm"') && svgText.includes('#FF0000'), 'SVG in mm mit roten Schnittlinien');
    writeFileSync(join(bilder, 'platte-1.svg'), svgText);
    const dxf = await abrufen(seite, 'a[href$="platte-1.dxf"]');
    pruefe(dxf.status === 200 && Buffer.from(dxf.daten, 'base64').toString().includes('POLYLINE'), 'DXF abrufbar');
    await seite.bild(join(bilder, 'g3-geaendert.png'));

    console.log('• Öffentlich sichtbar');
    const adresse = await seite.eval('location.href');
    await seite.klick('#gf-oeffentlich');
    await warte(600);
    pruefe(await seite.eval(`document.querySelector('#gf-oeffentlich').checked`), 'als öffentlich sichtbar markiert');
    const { seite: andere, beenden: beenden3 } = await browserStarten({ port: 9353, breite: 1400, hoehe: 1000 });
    try {
        await anmelden(andere, 'Erika Mustermann');
        await andere.oeffnen(basis + 'gefache');
        pruefe(await andere.eval(`[...document.querySelectorAll('#oeffentlich td')].some(td => td.textContent === ${JSON.stringify(name)})
            && document.querySelector('#oeffentlich').textContent.includes('Klaus Liebler')`), 'Erika Mustermann sieht es unter „Öffentliche Gefache anderer“');
        await andere.oeffnen(adresse);
        await andere.warteAuf(`!!document.querySelector('.pm-gf-fremd')`);
        await warte(800);
        pruefe((await andere.text()).includes('nur zum Ansehen') && await andere.eval(`!document.querySelector('#gf-oeffentlich') && document.querySelector('#gf-bez').disabled`),
            'Ansicht ohne Freigabe, Löschen und Umbenennen');
        await andere.eingeben('#gf-h', '90');
        await warte(700);
        let t = await andere.text();
        pruefe(t.includes('Gefach 90 mm hoch') && t.includes('Änderungen verwerfen'), 'Höhe ausprobiert (90 mm), Hinweis auf nicht gespeicherte Änderungen');
        pruefe(await andere.eval(`!document.querySelector('a[href$="schnittvorlagen.zip"]')`), 'Download gesperrt, solange Änderungen nicht gespeichert sind');
        await andere.klickText('button', 'Änderungen verwerfen');
        await warte(600);
        pruefe(await andere.eval(`document.querySelector('#gf-h').value === '150' && !!document.querySelector('a[href$="schnittvorlagen.zip"]')`),
            'Verwerfen zeigt wieder das Original (150 mm), Download möglich');
        await andere.eingeben('#gf-h', '90');
        await warte(700);
        await andere.klickText('button', 'Als Vorlage übernehmen');
        await andere.warteAuf(`location.href !== ${JSON.stringify(adresse)} && !!document.querySelector('#gf-oeffentlich')`);
        await warte(800);
        t = await andere.text();
        pruefe(t.includes('Gefach 90 mm hoch') && !t.includes('nur zum Ansehen') && await andere.eval(`!document.querySelector('#gf-oeffentlich').checked`),
            'als eigene, private Vorlage mit 90 mm übernommen und änderbar');
        await andere.bild(join(bilder, 'g5-vorlage.png'));
    } finally {
        await beenden3();
    }
    await seite.oeffnen(adresse);
    await warte(800);
    pruefe(await seite.eval(`document.querySelector('#gf-h').value === '150'`), 'Original bei Klaus Liebler unverändert');

    console.log('• 390 px');
    const { seite: schmal, beenden: beenden2 } = await browserStarten({ port: 9352, breite: 390, hoehe: 844 });
    try {
        await anmelden(schmal);
        await schmal.oeffnen(adresse);
        await warte(1200);
        const breit = await schmal.eval(`document.documentElement.scrollWidth`);
        pruefe(breit <= 392, `keine waagerechte Rollleiste (scrollWidth ${breit})`);
        pruefe(await schmal.eval(`document.querySelector('.pm-gf-3d').getBoundingClientRect().top < document.querySelector('#gf-il').getBoundingClientRect().top`),
            '3D-Vorschau steht auf dem Telefon oben');
        await schmal.bild(join(bilder, 'g4-390px.png'));
    } finally {
        await beenden2();
    }
    console.log('Alles in Ordnung. Bilder:', bilder);
} finally {
    await beenden();
}
