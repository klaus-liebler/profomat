// Kalender der Gesprächstermine: Firefox startet Ziehen nur mit gesetzten Daten. Blazor kann dataTransfer nicht setzen,
// deshalb hier einmal global für Elemente mit data-zieh. Kein Inline-Skript (CSP).
export function einrichten() {
    if (window.pmZiehenEingerichtet) {
        return;
    }

    window.pmZiehenEingerichtet = true;
    document.addEventListener('dragstart', (e) => {
        const quelle = e.target instanceof Element ? e.target.closest('[data-zieh]') : null;
        if (quelle && e.dataTransfer) {
            e.dataTransfer.setData('text/plain', quelle.getAttribute('data-zieh'));
            e.dataTransfer.effectAllowed = 'move';
        }
    });
}

export async function kopieren(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}
