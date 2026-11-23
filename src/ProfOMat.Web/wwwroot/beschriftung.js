// Verteiler beschriften: Ziehen (Firefox braucht Daten im dataTransfer, sonst startet kein Ziehen) und Pfeiltasten/Entf im Schrank
// ohne Seitenrollen. Die eigentliche Logik (Einsetzen, Verschieben) läuft im Server über die Blazor-Ereignisse.

const TASTEN = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Delete'];

export function einrichten(wurzel) {
    if (!wurzel || wurzel.pmBs) { return; }
    const ziehen = (e) => {
        if (e.target instanceof Element && e.target.closest('[draggable="true"]') && e.dataTransfer) {
            e.dataTransfer.setData('text/plain', 'profomat');
            e.dataTransfer.effectAllowed = 'copyMove';
        }
    };
    const taste = (e) => {
        const el = e.target;
        if (!(el instanceof Element) || !el.closest('.pm-bs-felder') || el.matches('input, select, textarea')) { return; }
        if (TASTEN.includes(e.key)) { e.preventDefault(); }
    };
    wurzel.addEventListener('dragstart', ziehen);
    wurzel.addEventListener('keydown', taste);
    wurzel.pmBs = { ziehen, taste };
}

export function fokus(wurzel, id) {
    wurzel?.querySelector(`[data-id="${CSS.escape(id)}"]`)?.focus();
}

export function abbauen(wurzel) {
    if (wurzel?.pmBs) {
        wurzel.removeEventListener('dragstart', wurzel.pmBs.ziehen);
        wurzel.removeEventListener('keydown', wurzel.pmBs.taste);
        delete wurzel.pmBs;
    }
}
