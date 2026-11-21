// Prüft das Schlüssel-Lesen und Signieren aus wwwroot/signatur.js ohne Browser (Node ≥ 20, WebCrypto) gegen Schlüssel,
// die openssl in allen unterstützten Formaten erzeugt. Aufruf:  node tests/Signatur/signatur-test.mjs
import { execFileSync } from 'node:child_process';
import { createPublicKey, verify } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const hier = dirname(fileURLToPath(import.meta.url));
const { intern } = await import(pathToFileURL(join(hier, '..', '..', 'src', 'ProfOMat.Web', 'wwwroot', 'signatur.js')).href);
const ordner = mkdtempSync(join(tmpdir(), 'profomat-sig-'));
const ssl = (...a) => execFileSync('openssl', a, { cwd: ordner, stdio: ['ignore', 'pipe', 'pipe'] });

ssl('genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-256', '-out', 'ec256.pem');
ssl('ec', '-in', 'ec256.pem', '-out', 'ec256-sec1.pem');
ssl('pkcs8', '-topk8', '-nocrypt', '-in', 'ec256.pem', '-outform', 'DER', '-out', 'ec256.der');
ssl('genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-384', '-aes-256-cbc', '-pass', 'pass:geheim', '-out', 'ec384-enc.pem');
ssl('genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-521', '-out', 'ec521.pem');
ssl('genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', 'rsa.pem');
ssl('rsa', '-in', 'rsa.pem', '-traditional', '-out', 'rsa-pkcs1.pem');
ssl('pkcs8', '-topk8', '-v2', 'aes-128-cbc', '-v2prf', 'hmacWithSHA256', '-in', 'rsa.pem', '-passout', 'pass:geheim', '-out', 'rsa-enc.pem');
ssl('genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:1024', '-out', 'rsa1024.pem');

const text = 'ProfOMat Testtext – Prüfung, Größe, ß\nZweite Zeile\n';
let fehler = 0;
const ok = (name, bedingung, info = '') => {
    console.log(`${bedingung ? 'ok  ' : 'FEHL'} ${name}${info ? ' – ' + info : ''}`);
    if (!bedingung) { fehler++; }
};

async function pruefen(datei, erwartet, passwort) {
    const s = await intern.laden(new Uint8Array(readFileSync(join(ordner, datei))), passwort);
    const sig = await intern.signieren(s, text);
    const jwk = JSON.parse(sig.jwk);
    ok(`${datei}: kein privater Anteil im JWK`, !('d' in jwk));
    const schluessel = createPublicKey({ key: jwk, format: 'jwk' });
    const hash = { 'ECDSA-SHA256': 'sha256', 'ECDSA-SHA384': 'sha384', 'ECDSA-SHA512': 'sha512', 'RSASSA-PKCS1-v1_5-SHA256': 'sha256' }[sig.algorithmus];
    const gueltig = verify(hash, Buffer.from(text, 'utf8'), jwk.kty === 'EC' ? { key: schluessel, dsaEncoding: 'ieee-p1363' } : schluessel, Buffer.from(sig.signatur, 'base64'));
    ok(`${datei}: ${sig.algorithmus} (${s.beschreibung}, ${s.fingerabdruck})`, gueltig && sig.algorithmus === erwartet);
}

async function erwarteFehler(datei, teil, passwort) {
    try {
        await intern.laden(new Uint8Array(readFileSync(join(ordner, datei))), passwort);
        ok(`${datei}: Fehler erwartet`, false);
    } catch (e) {
        ok(`${datei}: abgelehnt`, e.message.includes(teil), e.message);
    }
}

try {
    await pruefen('ec256.pem', 'ECDSA-SHA256');
    await pruefen('ec256-sec1.pem', 'ECDSA-SHA256');
    await pruefen('ec256.der', 'ECDSA-SHA256');
    await pruefen('ec384-enc.pem', 'ECDSA-SHA384', 'geheim');
    await pruefen('ec521.pem', 'ECDSA-SHA512');
    await pruefen('rsa.pem', 'RSASSA-PKCS1-v1_5-SHA256');
    await pruefen('rsa-pkcs1.pem', 'RSASSA-PKCS1-v1_5-SHA256');
    await pruefen('rsa-enc.pem', 'RSASSA-PKCS1-v1_5-SHA256', 'geheim');
    await erwarteFehler('rsa-enc.pem', 'Passwort stimmt nicht', 'falsch');
    await erwarteFehler('rsa1024.pem', '2048');
} finally {
    rmSync(ordner, { recursive: true, force: true });
}

console.log(fehler === 0 ? '\nAlle Prüfungen bestanden.' : `\n${fehler} Prüfung(en) fehlgeschlagen.`);
process.exit(fehler === 0 ? 0 : 1);
