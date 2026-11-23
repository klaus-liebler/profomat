// Kleine Helfer für statische Seiten (ohne Inline-JavaScript, CSP).
//
// Einladungs- und Passwort-Links (/einladung#<Token>): Das Token steht hinter „#“, damit es nie in einem Server-Log landet.
// Hier wird es einmal in das Prüfformular übernommen, aus der Adresszeile entfernt und abgeschickt.
const pruefen = document.getElementById('pm-einladung-pruefen');
if (pruefen && location.hash.length > 1) {
    pruefen.elements.namedItem('PToken').value = decodeURIComponent(location.hash.slice(1));
    history.replaceState(null, '', location.pathname + location.search);
    pruefen.submit();
}
