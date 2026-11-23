// Kleiner CDP-Treiber für Headless-Edge (ohne Abhängigkeiten, Node ≥ 22). Für Browserprüfungen des ProfOMat.
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const EDGE = process.env.EDGE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const warte = (ms) => new Promise((r) => setTimeout(r, ms));

export async function browserStarten({ port = 9333, breite = 1280, hoehe = 900 } = {}) {
    const profil = mkdtempSync(join(tmpdir(), 'profomat-edge-'));
    const edge = spawn(EDGE, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profil}`, '--no-first-run', '--disable-gpu', 'about:blank'],
        { stdio: 'ignore' });
    let version;
    for (let i = 0; i < 50 && !version; i++) {
        try { version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); } catch { await warte(200); }
    }
    if (!version) { throw new Error('Edge startet nicht'); }
    const ws = new WebSocket(version.webSocketDebuggerUrl);
    await new Promise((r) => ws.addEventListener('open', r, { once: true }));
    let nr = 0;
    const offen = new Map();
    const ereignisse = [];
    ws.addEventListener('message', (e) => {
        const m = JSON.parse(e.data);
        if (m.id && offen.has(m.id)) { offen.get(m.id)(m); offen.delete(m.id); } else if (m.method) { ereignisse.push(m); }
    });
    const senden = (method, params = {}, sessionId) => new Promise((ok, fehler) => {
        const i = ++nr;
        offen.set(i, (m) => (m.error ? fehler(new Error(`${method}: ${m.error.message}`)) : ok(m.result)));
        ws.send(JSON.stringify({ id: i, method, params, sessionId }));
    });
    const { targetId } = await senden('Target.createTarget', { url: 'about:blank' });
    const { sessionId } = await senden('Target.attachToTarget', { targetId, flatten: true });
    const s = (method, params) => senden(method, params, sessionId);
    await s('Page.enable');
    await s('Runtime.enable');
    await s('Emulation.setDeviceMetricsOverride', { width: breite, height: hoehe, deviceScaleFactor: 1, mobile: false });

    const seite = {
        ereignisse,
        async eval(ausdruck) {
            const r = await s('Runtime.evaluate', { expression: ausdruck, awaitPromise: true, returnByValue: true });
            if (r.exceptionDetails) { throw new Error(`eval: ${r.exceptionDetails.exception?.description ?? r.exceptionDetails.text}`); }
            return r.result.value;
        },
        async warteAuf(ausdruck, ms = 15000) {
            const ende = Date.now() + ms;
            while (Date.now() < ende) {
                try { if (await seite.eval(ausdruck)) { return; } } catch { /* Seite lädt */ }
                await warte(150);
            }
            throw new Error(`Zeitüberschreitung: ${ausdruck}`);
        },
        async oeffnen(url) {
            await s('Page.navigate', { url });
            await warte(300);
            await seite.warteAuf('document.readyState === "complete"');
            await warte(900);   // Blazor-Verbindung aufbauen
        },
        async bild(datei) {
            const { data } = await s('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
            writeFileSync(datei, Buffer.from(data, 'base64'));
        },
        async eingeben(selektor, wert) {
            await seite.eval(`(() => { const el = document.querySelector(${JSON.stringify(selektor)}); if (!el) throw new Error('fehlt: ${selektor}');
                el.focus(); el.value = ${JSON.stringify(wert)}; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); })()`);
            await warte(150);
        },
        async klickText(selektor, text) {
            await seite.eval(`(() => { const el = [...document.querySelectorAll(${JSON.stringify(selektor)})].find(e => e.textContent.includes(${JSON.stringify(text)}) && !e.disabled);
                if (!el) throw new Error('nicht gefunden: ${selektor} „${text}“'); el.scrollIntoView({ block: 'center' }); el.click(); })()`);
            await warte(500);
        },
        async klick(selektor) {
            await seite.eval(`(() => { const el = document.querySelector(${JSON.stringify(selektor)}); if (!el) throw new Error('fehlt: ${selektor}'); el.scrollIntoView({ block: 'center' }); el.click(); })()`);
            await warte(400);
        },
        /** Radzen-DropDown (n-tes auf der Seite) öffnen und Eintrag mit Text wählen. */
        async auswahl(n, text) {
            await seite.eval(`document.querySelectorAll('.rz-dropdown')[${n}].click()`);
            await warte(400);
            await seite.klickText('.rz-dropdown-item, li[role=option]', text);
            await warte(400);
        },
        /** Unterschrift in ein Canvas zeichnen (echte Maus-Ereignisse → Pointer-Ereignisse). */
        async zeichnen(selektor) {
            const r = await seite.eval(`(() => { const el = document.querySelector(${JSON.stringify(selektor)}); el.scrollIntoView({ block: 'center' }); const b = el.getBoundingClientRect(); return { x: b.left, y: b.top, w: b.width, h: b.height }; })()`);
            const maus = (type, x, y) => s('Input.dispatchMouseEvent', { type, x, y, button: 'left', buttons: type === 'mouseReleased' ? 0 : 1, clickCount: 1 });
            const x0 = r.x + r.w * 0.12, y0 = r.y + r.h * 0.55;
            await maus('mouseMoved', x0, y0);
            await maus('mousePressed', x0, y0);
            for (let i = 1; i <= 40; i++) {
                const t = i / 40;
                await maus('mouseMoved', x0 + t * r.w * 0.7, y0 + Math.sin(t * 9) * r.h * 0.18 - t * r.h * 0.1);
            }
            await maus('mouseReleased', x0 + r.w * 0.7, y0 - r.h * 0.1);
            await warte(400);
        },
        async dateiSetzen(selektor, pfad) {
            const { root } = await s('DOM.getDocument', { depth: -1, pierce: true });
            const { nodeId } = await s('DOM.querySelector', { nodeId: root.nodeId, selector: selektor });
            await s('DOM.setFileInputFiles', { nodeId, files: [pfad] });
            await warte(800);
        },
        /** Echte Tastendrücke an das fokussierte Element: Zeichen einzeln, „⏎“ = Enter, „⇥“ = Tab, „↑“/„↓“ = Pfeiltasten, „⎋“ = Escape. */
        async tippen(folge, pause = 25) {
            const sonder = { '⏎': ['Enter', 13, '\r'], '⇥': ['Tab', 9], '↑': ['ArrowUp', 38], '↓': ['ArrowDown', 40], '⎋': ['Escape', 27] };
            for (const z of folge) {
                const [key, code, text] = sonder[z] ?? [z, z.toUpperCase().charCodeAt(0), z];
                await s('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', key, code: key, windowsVirtualKeyCode: code, text, unmodifiedText: text });
                await s('Input.dispatchKeyEvent', { type: 'keyUp', key, code: key, windowsVirtualKeyCode: code });
                if (pause) { await warte(pause); }
            }
        },
        text: () => seite.eval('document.body.innerText'),
    };
    return {
        seite,
        async beenden() {
            try { ws.close(); } catch { /* egal */ }
            edge.kill();
            await warte(500);
            try { rmSync(profil, { recursive: true, force: true }); } catch { /* gesperrt */ }
        },
    };
}
