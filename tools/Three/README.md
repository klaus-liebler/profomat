# three.js-Bündel für die Gefach-Vorschau

`src/ProfOMat.Web/wwwroot/lib/three-gefach.min.js` enthält nur die Teile von three.js (MIT-Lizenz, Hinweis im Bündel), die
`wwwroot/gefach.js` braucht, samt `OrbitControls`. Als eine ES-Moduldatei mit relativem Import kommt sie ohne Import-Map aus, die CSP
muss also nicht geändert werden.

Neu bauen (z. B. für ein three.js-Update) in einem leeren Ordner außerhalb des Repositorys:

```
npm init -y
npm install three@0.187.0 esbuild@0.25
copy C:\repos\profomat\tools\Three\eintrag.js .
npx esbuild eintrag.js --bundle --minify --format=esm --legal-comments=inline --outfile=C:\repos\profomat\src\ProfOMat.Web\wwwroot\lib\three-gefach.min.js
```

Braucht `gefach.js` weitere Klassen, diese in `eintrag.js` ergänzen und neu bauen.
