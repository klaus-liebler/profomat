#!/usr/bin/env python3
# Stellt die Anwendung im Test dar (Vertrag des Deploy-Kits, siehe server/README.md). Steuerdateien im Programmordner:
#   MIGRATIONEN  bekannte Migrationen, eine je Zeile     VERHALTEN  gut | absturz | haengt | spaet-absturz | neuaufbau
# Datenbank: das lokale PostgreSQL der Simulation (psql über den Socket), Tabellen __EFMigrationsHistory und "Konten".
import os, sys, sqlite3, subprocess, http.server, threading, time
ordner = os.path.dirname(os.path.abspath(__file__))
migrationen = [l.strip() for l in open(os.path.join(ordner, "MIGRATIONEN")) if l.strip()]
verhalten = open(os.path.join(ordner, "VERHALTEN")).read().strip()
verbindung = next(v for k, v in os.environ.items() if k.startswith("ConnectionStrings__"))
teile = dict(t.split("=", 1) for t in verbindung.split(";") if "=" in t)
db, benutzer = teile["Database"], teile["Username"]


def sql(datenbank, befehl):
    r = subprocess.run(["psql", "-h", "/var/run/postgresql", "-U", benutzer, "-d", datenbank, "-v", "ON_ERROR_STOP=1", "-tAc", befehl],
                       capture_output=True, text=True)
    if r.returncode:
        raise RuntimeError(r.stderr.strip())
    return r.stdout.strip()


def vorhanden(d):
    return sql("postgres", f"SELECT 1 FROM pg_database WHERE datname = '{d}'") == "1"


def eingespielt(d):
    if sql(d, "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL") != "t":
        return []
    return [z for z in sql(d, 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1').splitlines() if z]


def migrieren(d):
    if not vorhanden(d):
        sql("postgres", f'CREATE DATABASE "{d}"')
    sql(d, 'CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY, "ProductVersion" text)')
    sql(d, 'CREATE TABLE IF NOT EXISTS "Konten" ("Kennung" text PRIMARY KEY)')
    for m in migrationen:
        sql(d, f"INSERT INTO \"__EFMigrationsHistory\" VALUES ('{m}', '11.0.0') ON CONFLICT DO NOTHING")


if sys.argv[1:2] == ["--datenbankstand"]:
    if verhalten == "neuaufbau":
        print(f"DATENBANKSTAND|anwendung|{db}|Neuaufbau|1|0|Schema geändert, keine Migration"); sys.exit(3)
    if not vorhanden(db):
        print(f"DATENBANKSTAND|anwendung|{db}|Neu|0|{len(migrationen)}|fehlt"); sys.exit(0)
    ist = eingespielt(db)
    fremd = [m for m in ist if m not in migrationen]
    offen = [m for m in migrationen if m not in ist]
    if not ist:
        print(f"DATENBANKSTAND|anwendung|{db}|Neu|0|{len(offen)}|leer"); sys.exit(0)
    if fremd:
        print(f"DATENBANKSTAND|anwendung|{db}|Fremd|{len(ist)}|{len(offen)}|kennt {','.join(fremd)}"); sys.exit(4)
    print(f"DATENBANKSTAND|anwendung|{db}|{'Migrierbar' if offen else 'Aktuell'}|{len(ist)}|{len(offen)}|"); sys.exit(0)

if sys.argv[1:2] == ["--sqlite-uebernehmen"]:
    quelle, probe = sys.argv[2], "--probe" in sys.argv
    ziel = db + "_uebernahmeprobe" if probe else db
    if probe:
        sql("postgres", f'DROP DATABASE IF EXISTS "{ziel}"')
    migrieren(ziel)
    if sql(ziel, 'SELECT count(*) FROM "Konten"') != "0":
        print("UEBERNAHME_ERGEBNIS|fehler|Zieldatenbank ist nicht leer"); sys.exit(1)
    zeilen = [r[0] for r in sqlite3.connect(f"file:{quelle}?mode=ro", uri=True).execute("SELECT Kennung FROM Konten")]
    for k in zeilen:
        sql(ziel, f"INSERT INTO \"Konten\" VALUES ('{k}')")
    n = sql(ziel, 'SELECT count(*) FROM "Konten"')
    print("Quelle: " + quelle)
    print(f"UEBERNAHME|Konten|{len(zeilen)}|{n}|{'gleich' if int(n) == len(zeilen) else 'ABWEICHUNG'}")
    if probe:
        sql("postgres", f'DROP DATABASE IF EXISTS "{ziel}"')
    print(f"UEBERNAHME_ERGEBNIS|ok|1 Tabellen|{n} Zeilen|{n} Datensätze per EF gelesen|{'Probe' if probe else 'übernommen'}"); sys.exit(0)

print("[10:00:00 INF] Starte Anwendung (Test)", flush=True)
if verhalten == "absturz":
    print("Unhandled exception. System.InvalidOperationException: Testabsturz", flush=True); sys.exit(134)
if vorhanden(db):
    fremd = [m for m in eingespielt(db) if m not in migrationen]
    if fremd:
        print(f"[10:00:01 FTL] Unhandled exception: Datenbank kennt fremde Migrationen {fremd}", flush=True); sys.exit(1)
migrieren(db)
if verhalten == "haengt":
    time.sleep(3600)
port = int(os.environ["ASPNETCORE_URLS"].rsplit(":", 1)[1])
pfad = os.environ.get("PathBase", "")


class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        ok = self.path == pfad + "/gesund"
        self.send_response(200 if ok else 404); self.end_headers(); self.wfile.write(b"ok" if ok else b"")

    def log_message(self, *a):
        pass


if verhalten == "spaet-absturz":
    threading.Timer(4, lambda: (print("[10:00:05 FTL] Hosting failed to start", flush=True), os._exit(1))).start()
print("[10:00:02 INF] Application started.", flush=True)
http.server.HTTPServer(("127.0.0.1", port), H).serve_forever()
