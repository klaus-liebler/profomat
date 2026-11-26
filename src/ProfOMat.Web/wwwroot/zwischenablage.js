// Text in die Zwischenablage kopieren (z. B. Einladungslink). Kein Inline-Skript (CSP).
export async function kopieren(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}
