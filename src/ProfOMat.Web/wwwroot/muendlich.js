// Mündliche Prüfung live: Tastatursteuerung der Protokolltabelle.
// Alles, was den Fokus bewegt, geschieht sofort im Browser (ohne Umweg über den Server), damit schnell getippte Zeichen nie im
// falschen Feld landen. Die Eingabezeile gehört ganz dem Browser: Enter im Punktefeld übergibt Frage, Max. und Punkte in einem
// Aufruf an den Server, leert die Zeile und springt zurück in „Frage“. Bestehende Zeilen speichert der Server beim Verlassen eines Feldes.
//   Enter: Frage → Max. → Punkte → nächste Frage; ↑/↓: gleiche Spalte in der Zeile darüber/darunter; Esc: Eingabezeile leeren;
//   Strg+Entf: Zeile löschen. Eine Fragennummer, die schon offen oder zurückgestellt ist, springt direkt in deren Punktefeld.

const feld = (zeile, spalte) => zeile?.querySelector(`input[data-spalte="${spalte}"]`) ?? null;

export function fokus(el) {
    if (!el) { return; }
    el.focus();
    el.select?.();
    el.scrollIntoView?.({ block: 'nearest' });
}

export function einrichten(tabelle, dotnet) {
    if (!tabelle || tabelle.pmTaste) { return; }
    const eingabezeile = () => tabelle.querySelector('tbody tr[data-entwurf]');

    const eintragen = (zeile) => {
        const [n, m, p] = ['nummer', 'max', 'punkte'].map((s) => feld(zeile, s));
        const werte = [n.value.trim(), m.value.trim(), p.value.trim()];
        if (!werte[0]) { fokus(n); return; }
        if (werte[1]) { tabelle.dataset.vorschlag = werte[1]; }
        n.value = m.value = p.value = '';
        fokus(n);
        dotnet.invokeMethodAsync('Eintragen', ...werte).then((fehler) => {
            // Nicht übernommen (z. B. Max. keine Zahl): Werte zurückholen, sofern inzwischen nichts Neues getippt wurde.
            if (fehler && !n.value && !m.value && !p.value) {
                [n.value, m.value, p.value] = werte;
                fokus(fehler === 'max' ? m : n);
            }
        });
    };

    const taste = (e) => {
        const el = e.target;
        if (!(el instanceof HTMLInputElement) || !el.dataset.spalte || e.altKey || e.isComposing) { return; }
        const zeile = el.closest('tr');
        const alle = [...tabelle.querySelectorAll('tbody tr[data-zeile]')];
        const i = alle.indexOf(zeile);
        const spalte = el.dataset.spalte;
        const entwurf = zeile.hasAttribute('data-entwurf');

        if (e.key === 'Enter' && !e.ctrlKey && !e.shiftKey) {
            e.preventDefault();
            if (spalte === 'nummer') {
                const nr = el.value.trim().toLowerCase();
                if (!nr) { return; }
                if (entwurf) {
                    const offen = alle.find((z) => z !== zeile && z.dataset.offen === 'true' && z.dataset.nummer === nr);
                    if (offen) {
                        el.value = '';
                        fokus(feld(offen, 'punkte'));
                        return;
                    }
                    const max = feld(zeile, 'max');
                    if (!max.value.trim() && tabelle.dataset.vorschlag) { max.value = tabelle.dataset.vorschlag; }
                }
                fokus(feld(zeile, 'max'));
            } else if (spalte === 'max') {
                fokus(feld(zeile, 'punkte'));
            } else if (entwurf) {
                eintragen(zeile);
            } else {
                fokus(feld(eingabezeile(), 'nummer'));
            }
        } else if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && !e.ctrlKey && !e.shiftKey) {
            const ziel = alle[i + (e.key === 'ArrowUp' ? -1 : 1)];
            if (ziel) {
                e.preventDefault();
                fokus(feld(ziel, spalte));
            }
        } else if (e.key === 'Escape' && entwurf) {
            e.preventDefault();
            ['nummer', 'max', 'punkte'].forEach((s) => { feld(zeile, s).value = ''; });
            fokus(feld(zeile, 'nummer'));
        } else if (e.key === 'Delete' && e.ctrlKey && !entwurf) {
            e.preventDefault();
            fokus(feld(alle[i + 1], spalte));
        }
    };
    tabelle.addEventListener('keydown', taste);
    tabelle.pmTaste = taste;
    fokus(feld(eingabezeile(), 'nummer'));
}

export function abbauen(tabelle) {
    if (tabelle?.pmTaste) {
        tabelle.removeEventListener('keydown', tabelle.pmTaste);
        delete tabelle.pmTaste;
    }
}
