# ProfOMat als Container (Modus docker). Gebaut wird AUF DEM SERVER aus der Ausgabe von dotnet publish (ausrollen.sh):
#   docker build --build-arg BASIS=mcr.microsoft.com/dotnet/aspnet:11.0 -f docker/Dockerfile <paketordner>
# Die ASP.NET-Core-Runtime steckt im Basisabbild; ein self-contained Paket nimmt runtime-deps als Basis.
# Keine Daten, keine Schlüssel, keine Logs im Abbild – die liegen als Bind-Mounts auf dem Host (compose.yml).
ARG BASIS=mcr.microsoft.com/dotnet/aspnet:11.0
FROM ${BASIS}
ARG APP=ProfOMat.Web
ENV APP=${APP} \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --chown=root:root . /app
# Benutzer setzt compose.yml (UID/GID des Dienstbenutzers auf dem Host, damit die Dateien in /var/lib/<dienst> passen)
USER 65534:65534
EXPOSE 8080
# Läuft mit Runtime aus dem Basisabbild (framework-abhängig) wie auch self-contained (apphost)
ENTRYPOINT ["/bin/sh", "-c", "if [ -f \"/app/$APP.dll\" ] && command -v dotnet >/dev/null; then exec dotnet \"/app/$APP.dll\"; else exec \"/app/$APP\"; fi"]
