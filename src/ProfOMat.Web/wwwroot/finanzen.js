// Finanzen: Text in die Zwischenablage kopieren (Prompt für den KI-Chat). Kein Inline-Skript (CSP).
export async function kopieren(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}
