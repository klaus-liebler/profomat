// Passkeys (WebAuthn) für Anmeldung und Profil – ohne Inline-JavaScript (CSP).
//
// Ein Knopf mit data-passkey="anmelden" bzw. "hinzufuegen" und data-optionen="<Pfad>" in einem Formular mit verstecktem Feld
// „Anmeldedaten“: Optionen vom Server holen (mit Antiforgery-Token im Kopf), den Browser den Passkey prüfen bzw. erzeugen lassen und das
// Ergebnis (öffentliche Daten, JSON) mit dem Formular abschicken. Die Prüfung macht ASP.NET Core Identity auf dem Server.
// Ereignis-Delegation am Dokument: funktioniert auch, wenn die Seite den Knopf erst später zeigt.

function meldung(form, text) {
    const status = form.querySelector('[data-passkey-status]');
    if (status) {
        status.textContent = text;
    }
}

async function optionenHolen(url, form) {
    const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';
    const antwort = await fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token } });
    if (!antwort.ok) {
        throw new Error(`Server antwortet mit ${antwort.status}`);
    }

    return JSON.parse(await antwort.text());
}

document.addEventListener('click', async (ereignis) => {
    const knopf = ereignis.target instanceof Element ? ereignis.target.closest('[data-passkey]') : null;
    if (!knopf) {
        return;
    }

    ereignis.preventDefault();
    const form = knopf.closest('form');
    if (!form) {
        return;
    }

    if (!window.PublicKeyCredential || !PublicKeyCredential.parseRequestOptionsFromJSON) {
        meldung(form, 'Dieser Browser unterstützt keine Passkeys. Bitte einen aktuellen Browser verwenden oder ein anderes Verfahren wählen.');
        return;
    }

    knopf.disabled = true;
    meldung(form, 'Bitte am Gerät bestätigen …');
    try {
        const optionen = await optionenHolen(knopf.dataset.optionen, form);
        const anmeldedaten = knopf.dataset.passkey === 'anmelden'
            ? await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(optionen) })
            : await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(optionen) });
        if (!anmeldedaten) {
            throw new Error('kein Passkey');
        }

        form.elements.namedItem('Anmeldedaten').value = JSON.stringify(anmeldedaten);
        form.submit();
    } catch (fehler) {
        knopf.disabled = false;
        meldung(form, fehler?.name === 'NotAllowedError' || fehler?.name === 'AbortError'
            ? 'Abgebrochen oder kein passender Passkey auf diesem Gerät.'
            : `Passkey nicht möglich: ${fehler?.message ?? fehler}`);
    }
});
