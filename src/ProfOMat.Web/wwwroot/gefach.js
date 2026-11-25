// Gefache: 3D-Vorschau (three.js) des Teilungseinsatzes in der Kiste. Die Geometrie (Wandumrisse mit Schlitzen und Rundungen) rechnet
// der Server (Gefachplan); hier wird sie nur extrudiert und gezeigt. Koordinaten: x = Länge (links → rechts), y = Höhe, z = Breite
// (vorn = +z, also zur Kamera hin).
import * as THREE from './lib/three-gefach.min.js';

const HOLZ = 0xC9A27B;
const BRANDKANTE = 0x5A3A1E;
const KISTE = 0x6F7F8F;

/** Kontur [[x, y, bulge], …] → THREE.Shape; Bögen (DXF-Bulge) werden fein unterteilt. */
function form(kontur) {
    const s = new THREE.Shape();
    s.moveTo(kontur[0][0], kontur[0][1]);
    for (let i = 0; i < kontur.length; i++) {
        const [x0, y0, b] = kontur[i];
        const [x1, y1] = kontur[(i + 1) % kontur.length];
        if (!b) {
            s.lineTo(x1, y1);
            continue;
        }
        const dx = x1 - x0, dy = y1 - y0, c = Math.hypot(dx, dy);
        const r = c * (1 + b * b) / (4 * Math.abs(b));
        const sagitta = Math.abs(b) * c / 2;
        const sign = Math.sign(b);
        const mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
        const cx = mx - dy / c * (r - sagitta) * sign, cy = my + dx / c * (r - sagitta) * sign;
        const a0 = Math.atan2(y0 - cy, x0 - cx);
        const winkel = 4 * Math.atan(b);
        const n = Math.max(4, Math.ceil(Math.abs(winkel) / (Math.PI / 24)));
        for (let k = 1; k <= n; k++) {
            const a = a0 + winkel * k / n;
            s.lineTo(cx + r * Math.cos(a), cy + r * Math.sin(a));
        }
    }
    return s;
}

function wandMesh(wand, dicke) {
    const geo = new THREE.ExtrudeGeometry(form(wand.kontur), { depth: dicke, bevelEnabled: false, curveSegments: 1 });
    const gruppe = new THREE.Group();
    gruppe.add(new THREE.Mesh(geo, new THREE.MeshStandardMaterial({ color: HOLZ, roughness: 0.85, metalness: 0 })));
    gruppe.add(new THREE.LineSegments(new THREE.EdgesGeometry(geo, 30), new THREE.LineBasicMaterial({ color: BRANDKANTE })));
    return gruppe;
}

function viereck(a, b, c, d) {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute([...a, ...b, ...c, ...a, ...c, ...d], 3));
    g.computeVertexNormals();
    return g;
}

/** Innenraum der Kiste: Boden und vier (ggf. schräge) Wände bis zur Stapelhöhe, durchscheinend, mit Kanten. */
function kisteGruppe(k) {
    const gruppe = new THREE.Group();
    const [ox, oz] = [(k.lt - k.lb) / 2, (k.bt - k.bb) / 2];
    // Ecken unten (y = 0) und oben (y = hs) in Kistenkoordinaten, z = 0 vorn
    const u = [[ox, 0, oz], [ox + k.lb, 0, oz], [ox + k.lb, 0, oz + k.bb], [ox, 0, oz + k.bb]];
    const o = [[0, k.hs, 0], [k.lt, k.hs, 0], [k.lt, k.hs, k.bt], [0, k.hs, k.bt]];
    const p = (q) => [q[0] - k.lt / 2, q[1], k.bt / 2 - q[2]];
    const wand = new THREE.MeshStandardMaterial({ color: KISTE, transparent: true, opacity: 0.22, side: THREE.DoubleSide, depthWrite: false });
    const boden = new THREE.MeshStandardMaterial({ color: KISTE, transparent: true, opacity: 0.45, side: THREE.DoubleSide, depthWrite: false });
    gruppe.add(new THREE.Mesh(viereck(p(u[0]), p(u[1]), p(u[2]), p(u[3])), boden));
    for (let i = 0; i < 4; i++) {
        const j = (i + 1) % 4;
        if (i === 0) { continue; }   // vordere Wand offen lassen: Blick hinein
        gruppe.add(new THREE.Mesh(viereck(p(u[i]), p(u[j]), p(o[j]), p(o[i])), wand));
    }
    const kanten = [];
    for (let i = 0; i < 4; i++) {
        const j = (i + 1) % 4;
        kanten.push(...p(u[i]), ...p(u[j]), ...p(o[i]), ...p(o[j]), ...p(u[i]), ...p(o[i]));
    }
    const lg = new THREE.BufferGeometry();
    lg.setAttribute('position', new THREE.Float32BufferAttribute(kanten, 3));
    gruppe.add(new THREE.LineSegments(lg, new THREE.LineBasicMaterial({ color: 0x3C4650 })));
    return gruppe;
}

function aufbauen(behaelter) {
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.domElement.setAttribute('role', 'img');
    renderer.domElement.setAttribute('aria-label', '3D-Vorschau des Gefachs – mit der Maus drehen, mit dem Rad zoomen');
    behaelter.replaceChildren(renderer.domElement);

    const szene = new THREE.Scene();
    szene.add(new THREE.HemisphereLight(0xffffff, 0x8a7a66, 1.6));
    const licht = new THREE.DirectionalLight(0xffffff, 1.6);
    licht.position.set(-0.6, 1.2, 0.9);
    szene.add(licht);
    szene.add(new THREE.AmbientLight(0xffffff, 0.35));

    const kamera = new THREE.PerspectiveCamera(35, 1, 1, 20000);
    const steuerung = new THREE.OrbitControls(kamera, renderer.domElement);
    steuerung.enableDamping = false;
    const zustand = { renderer, szene, kamera, steuerung, inhalt: null, kiste: null, mitKiste: true, erstesMal: true };
    zustand.zeichnen = () => renderer.render(szene, kamera);
    steuerung.addEventListener('change', zustand.zeichnen);
    zustand.beobachter = new ResizeObserver(() => {
        const b = behaelter.clientWidth, h = behaelter.clientHeight;
        if (b === 0 || h === 0) { return; }
        renderer.setSize(b, h, false);
        kamera.aspect = b / h;
        kamera.updateProjectionMatrix();
        zustand.zeichnen();
    });
    zustand.beobachter.observe(behaelter);
    return zustand;
}

function entsorgen(objekt) {
    objekt?.traverse((o) => {
        o.geometry?.dispose();
        o.material?.dispose();
    });
}

/** Zeigt (oder aktualisiert) die Vorschau. daten: { kiste: {lt, bt, lb, bb, hs}, dicke, spiel, waende: [{ art: 'L'|'Q', lage, kontur }] } */
export function zeigen(behaelter, daten, mitKiste) {
    if (!behaelter) { return false; }
    let z = behaelter.pmGf;
    if (!z) {
        try {
            z = behaelter.pmGf = aufbauen(behaelter);
        } catch (e) {
            behaelter.innerHTML = '';
            const p = document.createElement('p');
            p.className = 'pm-muted pm-gf-ohne3d';
            p.textContent = 'Die 3D-Vorschau braucht WebGL – dieser Browser bietet es gerade nicht an. Die Schnittvorlagen unten sind davon nicht betroffen.';
            behaelter.append(p);
            return false;
        }
    }

    const k = daten.kiste;
    if (z.inhalt) { z.szene.remove(z.inhalt); entsorgen(z.inhalt); }
    if (z.kiste) { z.szene.remove(z.kiste); entsorgen(z.kiste); }

    const inhalt = new THREE.Group();
    const vorlagen = new Map();
    for (const w of daten.waende) {
        const schluessel = w.art;
        if (!vorlagen.has(schluessel)) { vorlagen.set(schluessel, wandMesh(w, daten.dicke)); }
        const m = vorlagen.get(schluessel).clone();
        if (w.art === 'L') {
            // Wand-x → Kisten-x, Dicke nach +z (zur Kamera); Mitte bei z = lage
            m.position.set(daten.spiel - k.lt / 2, 0, k.bt / 2 - w.lage - daten.dicke / 2);
        } else {
            // Wand-x (vorn → hinten) → −z; Dicke nach +x; Mitte bei x = lage
            m.rotation.y = Math.PI / 2;
            m.position.set(w.lage - k.lt / 2 - daten.dicke / 2, 0, k.bt / 2 - daten.spiel);
        }
        inhalt.add(m);
    }
    z.inhalt = inhalt;
    z.szene.add(inhalt);
    z.kiste = kisteGruppe(k);
    z.kiste.visible = mitKiste !== false;
    z.szene.add(z.kiste);

    if (z.erstesMal) {
        z.erstesMal = false;
        const groesse = Math.max(k.lt, k.bt, k.hs);
        z.kamera.position.set(k.lt * 0.55, k.hs * 2.4 + groesse * 0.35, k.bt * 1.25 + groesse * 0.55);
        z.steuerung.target.set(0, k.hs * 0.35, 0);
        z.steuerung.update();
    }
    z.zeichnen();
    return true;
}

export function kisteZeigen(behaelter, an) {
    const z = behaelter?.pmGf;
    if (!z?.kiste) { return; }
    z.kiste.visible = an;
    z.zeichnen();
}

export function abbauen(behaelter) {
    const z = behaelter?.pmGf;
    if (!z) { return; }
    z.beobachter.disconnect();
    z.steuerung.dispose();
    entsorgen(z.szene);
    z.renderer.dispose();
    delete behaelter.pmGf;
}
