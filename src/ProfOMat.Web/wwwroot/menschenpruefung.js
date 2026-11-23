// Sicherheitsaufgabe (Captcha nach dem ALTCHA-Prinzip, selbst gehostet): finde n ≤ maximum mit SHA-256(salz + n) = herausforderung.
// Rechnet mit WebCrypto in Blöcken; im Mittel rund eine Sekunde. Liefert n oder -1.

const hexZuBytes = (hex) => Uint8Array.from(hex.match(/../g).map((h) => parseInt(h, 16)));

function gleich(puffer, ziel) {
    const a = new Uint8Array(puffer);
    for (let i = 0; i < ziel.length; i++) { if (a[i] !== ziel[i]) { return false; } }
    return true;
}

export async function loesen(salz, herausforderung, maximum) {
    if (!globalThis.crypto?.subtle) { return -1; }
    const ziel = hexZuBytes(herausforderung);
    const kodierer = new TextEncoder();
    const block = 2000;
    for (let start = 0; start <= maximum; start += block) {
        const ende = Math.min(maximum, start + block - 1);
        const versuche = [];
        for (let n = start; n <= ende; n++) {
            versuche.push(crypto.subtle.digest('SHA-256', kodierer.encode(salz + n)));
        }
        const ergebnisse = await Promise.all(versuche);
        for (let i = 0; i < ergebnisse.length; i++) {
            if (gleich(ergebnisse[i], ziel)) { return start + i; }
        }
    }
    return -1;
}
