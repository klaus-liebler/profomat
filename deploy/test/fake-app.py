#!/usr/bin/env python3
# Stellt ProfOMat.Web im Test dar. Verhalten über die Datei VERHALTEN im Programmordner: gut | absturz | haengt | spaet-absturz.
# Datenbank wie die echte App: anlegen, aufwerten (ab migrierbar_ab) oder „Datenbankschema veraltet“.
import os, sys, sqlite3, http.server, threading, time
ordner = os.path.dirname(os.path.abspath(__file__))
if sys.argv[1:] == ["--schemainfo"]:
    print(open(os.path.join(ordner, "schemainfo.txt")).read(), end=""); sys.exit(0)
info = dict(l.strip().split("=") for l in open(os.path.join(ordner, "schemainfo.txt")) if "=" in l)
soll, ab = int(info["schemaversion"]), int(info["migrierbar_ab"])
verhalten = open(os.path.join(ordner, "VERHALTEN")).read().strip()
print("[10:00:00 INF] Starte ProfOMat (Test)", flush=True)
if verhalten == "absturz":
    print("Unhandled exception. System.InvalidOperationException: Testabsturz", flush=True); sys.exit(134)
db = os.environ["ConnectionStrings__ProfOMat"].split("=", 1)[1]
neu = not os.path.exists(db)
c = sqlite3.connect(db)
if neu:
    c.execute("CREATE TABLE Schemainfo (Version INTEGER)"); c.execute("CREATE TABLE Konten (Id TEXT)")
    c.execute("INSERT INTO Schemainfo VALUES (?)", (soll,)); c.commit()
ist = c.execute("SELECT Version FROM Schemainfo").fetchone()[0]
if ab <= ist < soll:
    c.execute("UPDATE Schemainfo SET Version=?", (soll,)); c.commit(); ist = soll
    print(f"[10:00:01 INF] Datenbank aufgewertet auf {soll}", flush=True)
if ist != soll:
    print(f"[10:00:01 FTL] Datenbankschema veraltet (Datei: {ist}, erwartet: {soll})", flush=True); sys.exit(1)
if verhalten == "haengt":
    time.sleep(3600)
port = int(os.environ["ASPNETCORE_URLS"].rsplit(":", 1)[1]); pfad = os.environ.get("PathBase", "")
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        ok = self.path == pfad + "/gesund"
        self.send_response(200 if ok else 404); self.end_headers(); self.wfile.write(b"ok" if ok else b"")
    def log_message(self, *a): pass
if verhalten == "spaet-absturz":
    threading.Timer(4, lambda: (print("[10:00:05 FTL] Hosting failed to start", flush=True), os._exit(1))).start()
print("[10:00:02 INF] Application started.", flush=True)
http.server.HTTPServer(("127.0.0.1", port), H).serve_forever()
