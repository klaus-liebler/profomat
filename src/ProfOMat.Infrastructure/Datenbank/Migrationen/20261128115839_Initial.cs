using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfOMat.Infrastructure.Datenbank.Migrationen;

/// <inheritdoc />
public partial class _20261128115839_Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Dokumente",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AntragId = table.Column<Guid>(type: "uuid", nullable: true),
                Dateiname = table.Column<string>(type: "text", nullable: false),
                Inhaltstyp = table.Column<string>(type: "text", nullable: false),
                Inhalt = table.Column<byte[]>(type: "bytea", nullable: false),
                Sha256 = table.Column<string>(type: "text", nullable: false),
                Erstellt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Dokumente", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Fakultaeten",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kuerzel = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                StudiendekanatKontoId = table.Column<Guid>(type: "uuid", nullable: true),
                SekretariatEmail = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Fakultaeten", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Konten",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kennung = table.Column<string>(type: "text", nullable: false),
                Titel = table.Column<string>(type: "text", nullable: true),
                Vorname = table.Column<string>(type: "text", nullable: false),
                Nachname = table.Column<string>(type: "text", nullable: false),
                Email = table.Column<string>(type: "text", nullable: false),
                Herkunft = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Aktiv = table.Column<bool>(type: "boolean", nullable: false),
                PasswortHash = table.Column<string>(type: "text", nullable: true),
                SicherheitsStempel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Fehlversuche = table.Column<int>(type: "integer", nullable: false),
                GesperrtBis = table.Column<long>(type: "bigint", nullable: true),
                ZweiterFaktorAktiv = table.Column<bool>(type: "boolean", nullable: false),
                AuthenticatorSchluessel = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Wiederherstellungscodes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Konten", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Laborteams",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Angelegt = table.Column<long>(type: "bigint", nullable: false),
                ErinnerungVorlaufTage = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Laborteams", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Postausgang",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                An = table.Column<string>(type: "text", nullable: false),
                Betreff = table.Column<string>(type: "text", nullable: false),
                Text = table.Column<string>(type: "text", nullable: false),
                Erstellt = table.Column<long>(type: "bigint", nullable: false),
                AntragId = table.Column<Guid>(type: "uuid", nullable: true),
                AnhangDokumentId = table.Column<Guid>(type: "uuid", nullable: true),
                Versandt = table.Column<long>(type: "bigint", nullable: true),
                Simuliert = table.Column<bool>(type: "boolean", nullable: false),
                Fehlversuche = table.Column<int>(type: "integer", nullable: false),
                LetzterFehler = table.Column<string>(type: "text", nullable: true),
                NaechsterVersuch = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Postausgang", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Protokoll",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Zeitpunkt = table.Column<long>(type: "bigint", nullable: false),
                Wer = table.Column<string>(type: "text", nullable: false),
                Aktion = table.Column<string>(type: "text", nullable: false),
                Text = table.Column<string>(type: "text", nullable: false),
                Bezug = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Protokoll", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Raeume",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kuerzel = table.Column<string>(type: "text", nullable: false),
                Bezeichnung = table.Column<string>(type: "text", nullable: true),
                Plaetze = table.Column<int>(type: "integer", nullable: false),
                Aktiv = table.Column<bool>(type: "boolean", nullable: false),
                RasterSpalten = table.Column<int>(type: "integer", nullable: false),
                RasterZeilen = table.Column<int>(type: "integer", nullable: false),
                Startecke = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Richtung = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Schlange = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Raeume", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Sicherheitsprotokoll",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Nummer = table.Column<long>(type: "bigint", nullable: false),
                Zeitpunkt = table.Column<long>(type: "bigint", nullable: false),
                Art = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: true),
                Text = table.Column<string>(type: "text", nullable: false),
                VorgaengerHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Sicherheitsprotokoll", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Studiengaenge",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FakultaetId = table.Column<Guid>(type: "uuid", nullable: false),
                Kuerzel = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Abschluss = table.Column<string>(type: "text", nullable: false),
                Pruefungsordnung = table.Column<string>(type: "text", nullable: false),
                StudiendekanatKontoId = table.Column<Guid>(type: "uuid", nullable: true),
                Schluessel = table.Column<string>(type: "text", nullable: false),
                Herkunft = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                AltSeit = table.Column<long>(type: "bigint", nullable: true),
                AltGrund = table.Column<string>(type: "text", nullable: true),
                ZuletztAbgeglichen = table.Column<long>(type: "bigint", nullable: true),
                VonHandArchiviert = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Studiengaenge", x => x.Id);
                table.ForeignKey(
                    name: "FK_Studiengaenge_Fakultaeten_FakultaetId",
                    column: x => x.FakultaetId,
                    principalTable: "Fakultaeten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Einladungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Art = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Rollen = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: true),
                Notiz = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ErstelltVon = table.Column<Guid>(type: "uuid", nullable: false),
                Erstellt = table.Column<long>(type: "bigint", nullable: false),
                Ablauf = table.Column<long>(type: "bigint", nullable: false),
                Eingeloest = table.Column<long>(type: "bigint", nullable: true),
                EingeloestVon = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Einladungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Einladungen_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ExterneAnmeldungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Anbieter = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Schluessel = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                Anzeigename = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Verknuepft = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExterneAnmeldungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_ExterneAnmeldungen_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Gefache",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Bezeichnung = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Angelegt = table.Column<long>(type: "bigint", nullable: false),
                InnenLaengeMm = table.Column<decimal>(type: "numeric", nullable: false),
                InnenBreiteMm = table.Column<decimal>(type: "numeric", nullable: false),
                StapelhoeheMm = table.Column<decimal>(type: "numeric", nullable: false),
                HoeheMm = table.Column<decimal>(type: "numeric", nullable: false),
                BodenLaengeMm = table.Column<decimal>(type: "numeric", nullable: false),
                BodenBreiteMm = table.Column<decimal>(type: "numeric", nullable: false),
                RadiusMm = table.Column<decimal>(type: "numeric", nullable: false),
                DickeMm = table.Column<decimal>(type: "numeric", nullable: false),
                PlatteLaengeMm = table.Column<decimal>(type: "numeric", nullable: false),
                PlatteBreiteMm = table.Column<decimal>(type: "numeric", nullable: false),
                SpielMm = table.Column<decimal>(type: "numeric", nullable: false),
                SchlitzzugabeMm = table.Column<decimal>(type: "numeric", nullable: false),
                AbstandMm = table.Column<decimal>(type: "numeric", nullable: false),
                LaengeArt = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                LaengeAnzahl = table.Column<int>(type: "integer", nullable: false),
                LaengeMasse = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                BreiteArt = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                BreiteAnzahl = table.Column<int>(type: "integer", nullable: false),
                BreiteMasse = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Gravur = table.Column<bool>(type: "boolean", nullable: false),
                Oeffentlich = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Gefache", x => x.Id);
                table.ForeignKey(
                    name: "FK_Gefache_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Hausarbeiten",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BestaetigungBetreff = table.Column<string>(type: "text", nullable: true),
                BestaetigungVorlage = table.Column<string>(type: "text", nullable: true),
                GespraechMinuten = table.Column<int>(type: "integer", nullable: false),
                GespraechTageText = table.Column<string>(type: "text", nullable: true),
                TagBeginnMinuten = table.Column<int>(type: "integer", nullable: false),
                TagEndeMinuten = table.Column<int>(type: "integer", nullable: false),
                GespraechOrt = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                TerminBetreff = table.Column<string>(type: "text", nullable: true),
                TerminVorlage = table.Column<string>(type: "text", nullable: true),
                KalenderTokenHash = table.Column<string>(type: "text", nullable: true),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Titel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Pruefungsnummer = table.Column<string>(type: "text", nullable: true),
                Pruefer = table.Column<string>(type: "text", nullable: true),
                Bestaetigungsfrist = table.Column<long>(type: "bigint", nullable: true),
                Erstellt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Hausarbeiten", x => x.Id);
                table.ForeignKey(
                    name: "FK_Hausarbeiten_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Klausuren",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                VerantwortlichKontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Titel = table.Column<string>(type: "text", nullable: false),
                Kuerzel = table.Column<string>(type: "text", nullable: false),
                Pruefungsnummer = table.Column<string>(type: "text", nullable: true),
                Pruefer = table.Column<string>(type: "text", nullable: true),
                Termin = table.Column<long>(type: "bigint", nullable: true),
                AktivSchwelle = table.Column<decimal>(type: "numeric", nullable: false),
                ZufaelligVerteilt = table.Column<bool>(type: "boolean", nullable: false),
                Verteilt = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Klausuren", x => x.Id);
                table.ForeignKey(
                    name: "FK_Klausuren_Konten_VerantwortlichKontoId",
                    column: x => x.VerantwortlichKontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "KontoRollen",
            columns: table => new
            {
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Rolle = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KontoRollen", x => new { x.KontoId, x.Rolle });
                table.ForeignKey(
                    name: "FK_KontoRollen_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Mailentwuerfe",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                An = table.Column<string>(type: "text", nullable: false),
                AnName = table.Column<string>(type: "text", nullable: true),
                Betreff = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                Erstellt = table.Column<long>(type: "bigint", nullable: false),
                Anlass = table.Column<string>(type: "text", nullable: false),
                BezugId = table.Column<Guid>(type: "uuid", nullable: true),
                Geoeffnet = table.Column<long>(type: "bigint", nullable: true),
                Erledigt = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Mailentwuerfe", x => x.Id);
                table.ForeignKey(
                    name: "FK_Mailentwuerfe_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Mailsignaturen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Html = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                Aktualisiert = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Mailsignaturen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Mailsignaturen_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MuendlichePruefungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Bezeichnung = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Begonnen = table.Column<long>(type: "bigint", nullable: false),
                Pruefling = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Matrikelnummer = table.Column<string>(type: "text", nullable: true),
                Studiengang = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ErstpruefendeId = table.Column<Guid>(type: "uuid", nullable: true),
                ZweitpruefendeId = table.Column<Guid>(type: "uuid", nullable: true),
                ErstpruefendeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ZweitpruefendeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Endnote = table.Column<string>(type: "text", nullable: true),
                Abweichungsbegruendung = table.Column<string>(type: "text", nullable: true),
                Festgelegt = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MuendlichePruefungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_MuendlichePruefungen_Konten_ErstpruefendeId",
                    column: x => x.ErstpruefendeId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MuendlichePruefungen_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MuendlichePruefungen_Konten_ZweitpruefendeId",
                    column: x => x.ZweitpruefendeId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Passkeys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                CredentialId = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                OeffentlicherSchluessel = table.Column<byte[]>(type: "bytea", nullable: false),
                Bezeichnung = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Erstellt = table.Column<long>(type: "bigint", nullable: false),
                Hinzugefuegt = table.Column<long>(type: "bigint", nullable: false),
                Zaehler = table.Column<long>(type: "bigint", nullable: false),
                Transporte = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                NutzerGeprueft = table.Column<bool>(type: "boolean", nullable: false),
                SicherungMoeglich = table.Column<bool>(type: "boolean", nullable: false),
                Gesichert = table.Column<bool>(type: "boolean", nullable: false),
                Attestierung = table.Column<byte[]>(type: "bytea", nullable: false),
                ClientDaten = table.Column<byte[]>(type: "bytea", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Passkeys", x => x.Id);
                table.ForeignKey(
                    name: "FK_Passkeys_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Schaltschraenke",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Bezeichnung = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Angelegt = table.Column<long>(type: "bigint", nullable: false),
                TeProReihe = table.Column<int>(type: "integer", nullable: false),
                TeBreiteMm = table.Column<decimal>(type: "numeric", nullable: false),
                HoeheMm = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Schaltschraenke", x => x.Id);
                table.ForeignKey(
                    name: "FK_Schaltschraenke_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Signaturschluessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Fingerabdruck = table.Column<string>(type: "text", nullable: false),
                OeffentlicherSchluesselJwk = table.Column<string>(type: "text", nullable: false),
                Algorithmus = table.Column<string>(type: "text", nullable: false),
                Bezeichnung = table.Column<string>(type: "text", nullable: false),
                Hinterlegt = table.Column<long>(type: "bigint", nullable: false),
                WiderrufenAm = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Signaturschluessel", x => x.Id);
                table.ForeignKey(
                    name: "FK_Signaturschluessel_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Auslagenabrechnungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Bezeichnung = table.Column<string>(type: "text", nullable: false),
                Stand = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Eingereicht = table.Column<DateOnly>(type: "date", nullable: true),
                Angelegt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Auslagenabrechnungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Auslagenabrechnungen_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Auslagenabrechnungen_Laborteams_TeamId",
                    column: x => x.TeamId,
                    principalTable: "Laborteams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Finanzerinnerungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Schluessel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Gesendet = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Finanzerinnerungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Finanzerinnerungen_Laborteams_TeamId",
                    column: x => x.TeamId,
                    principalTable: "Laborteams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FinanzToepfe",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Art = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Kennung = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Bezeichnung = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Innenauftrag = table.Column<string>(type: "text", nullable: true),
                Jahr = table.Column<int>(type: "integer", nullable: true),
                BewilligtPersonal = table.Column<decimal>(type: "numeric", nullable: false),
                BewilligtSach = table.Column<decimal>(type: "numeric", nullable: false),
                Kategorie = table.Column<string>(type: "text", nullable: true),
                LaufzeitVon = table.Column<DateOnly>(type: "date", nullable: true),
                LaufzeitBis = table.Column<DateOnly>(type: "date", nullable: true),
                Bestellschluss = table.Column<DateOnly>(type: "date", nullable: true),
                Rechnungsschluss = table.Column<DateOnly>(type: "date", nullable: true),
                Verantwortlich = table.Column<string>(type: "text", nullable: true),
                UebertragsgrenzeProzent = table.Column<decimal>(type: "numeric", nullable: false),
                Notiz = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                Zweck = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Begruendung = table.Column<string>(type: "character varying(6000)", maxLength: 6000, nullable: true),
                Anschaffung = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                Laborbereich = table.Column<string>(type: "text", nullable: true),
                Beschluss = table.Column<DateOnly>(type: "date", nullable: true),
                Archiviert = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FinanzToepfe", x => x.Id);
                table.ForeignKey(
                    name: "FK_FinanzToepfe_Laborteams_TeamId",
                    column: x => x.TeamId,
                    principalTable: "Laborteams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "GeplanteAusgaben",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Bezeichnung = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Monat = table.Column<DateOnly>(type: "date", nullable: false),
                Stand = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Notiz = table.Column<string>(type: "text", nullable: true),
                Angelegt = table.Column<long>(type: "bigint", nullable: false),
                BuchungVerweis = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GeplanteAusgaben", x => x.Id);
                table.ForeignKey(
                    name: "FK_GeplanteAusgaben_Laborteams_TeamId",
                    column: x => x.TeamId,
                    principalTable: "Laborteams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Hilfskraefte",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Person = table.Column<string>(type: "text", nullable: false),
                Art = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                StundenProMonat = table.Column<decimal>(type: "numeric", nullable: false),
                Beginn = table.Column<DateOnly>(type: "date", nullable: false),
                Ende = table.Column<DateOnly>(type: "date", nullable: false),
                Notiz = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Hilfskraefte", x => x.Id);
                table.ForeignKey(
                    name: "FK_Hilfskraefte_Laborteams_TeamId",
                    column: x => x.TeamId,
                    principalTable: "Laborteams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Labormitglieder",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Seit = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Labormitglieder", x => x.Id);
                table.ForeignKey(
                    name: "FK_Labormitglieder_Konten_KontoId",
                    column: x => x.KontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_Labormitglieder_Laborteams_TeamId",
                    column: x => x.TeamId,
                    principalTable: "Laborteams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Stundensaetze",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Jahr = table.Column<int>(type: "integer", nullable: false),
                Art = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                EuroProStunde = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Stundensaetze", x => x.Id);
                table.ForeignKey(
                    name: "FK_Stundensaetze_Laborteams_TeamId",
                    column: x => x.TeamId,
                    principalTable: "Laborteams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Raumelemente",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RaumId = table.Column<Guid>(type: "uuid", nullable: false),
                Art = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                X = table.Column<int>(type: "integer", nullable: false),
                Y = table.Column<int>(type: "integer", nullable: false),
                Breite = table.Column<int>(type: "integer", nullable: false),
                Hoehe = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Raumelemente", x => x.Id);
                table.ForeignKey(
                    name: "FK_Raumelemente_Raeume_RaumId",
                    column: x => x.RaumId,
                    principalTable: "Raeume",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Pruefungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                StudiengangId = table.Column<Guid>(type: "uuid", nullable: false),
                Nummer = table.Column<string>(type: "text", nullable: false),
                Titel = table.Column<string>(type: "text", nullable: false),
                Pruefungsform = table.Column<string>(type: "text", nullable: true),
                Schluessel = table.Column<string>(type: "text", nullable: false),
                Herkunft = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                AltSeit = table.Column<long>(type: "bigint", nullable: true),
                AltGrund = table.Column<string>(type: "text", nullable: true),
                ZuletztAbgeglichen = table.Column<long>(type: "bigint", nullable: true),
                VonHandArchiviert = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Pruefungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Pruefungen_Studiengaenge_StudiengangId",
                    column: x => x.StudiengangId,
                    principalTable: "Studiengaenge",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "HausarbeitPersonen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                HausarbeitId = table.Column<Guid>(type: "uuid", nullable: false),
                Matrikelnummer = table.Column<string>(type: "text", nullable: false),
                Nachname = table.Column<string>(type: "text", nullable: false),
                Vorname = table.Column<string>(type: "text", nullable: false),
                Email = table.Column<string>(type: "text", nullable: true),
                Angemeldet = table.Column<bool>(type: "boolean", nullable: false),
                VonHand = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HausarbeitPersonen", x => x.Id);
                table.ForeignKey(
                    name: "FK_HausarbeitPersonen_Hausarbeiten_HausarbeitId",
                    column: x => x.HausarbeitId,
                    principalTable: "Hausarbeiten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "HausarbeitTeams",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                HausarbeitId = table.Column<Guid>(type: "uuid", nullable: false),
                Nr = table.Column<int>(type: "integer", nullable: false),
                SprecherId = table.Column<Guid>(type: "uuid", nullable: true),
                Wunsch1 = table.Column<Guid>(type: "uuid", nullable: true),
                Wunsch2 = table.Column<Guid>(type: "uuid", nullable: true),
                Wunsch3 = table.Column<Guid>(type: "uuid", nullable: true),
                ThemaId = table.Column<Guid>(type: "uuid", nullable: true),
                Notiz = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Termin = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HausarbeitTeams", x => x.Id);
                table.ForeignKey(
                    name: "FK_HausarbeitTeams_Hausarbeiten_HausarbeitId",
                    column: x => x.HausarbeitId,
                    principalTable: "Hausarbeiten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "HausarbeitThemen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                HausarbeitId = table.Column<Guid>(type: "uuid", nullable: false),
                Nr = table.Column<int>(type: "integer", nullable: false),
                Titel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Beschreibung = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                Art = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Eigen = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HausarbeitThemen", x => x.Id);
                table.ForeignKey(
                    name: "FK_HausarbeitThemen_Hausarbeiten_HausarbeitId",
                    column: x => x.HausarbeitId,
                    principalTable: "Hausarbeiten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Aufgaben",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KlausurId = table.Column<Guid>(type: "uuid", nullable: false),
                Nr = table.Column<int>(type: "integer", nullable: false),
                Titel = table.Column<string>(type: "text", nullable: false),
                MaxPunkte = table.Column<decimal>(type: "numeric", nullable: false),
                Sonderpunkte = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Aufgaben", x => x.Id);
                table.ForeignKey(
                    name: "FK_Aufgaben_Klausuren_KlausurId",
                    column: x => x.KlausurId,
                    principalTable: "Klausuren",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "KlausurRaeume",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KlausurId = table.Column<Guid>(type: "uuid", nullable: false),
                RaumId = table.Column<Guid>(type: "uuid", nullable: false),
                Reihenfolge = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KlausurRaeume", x => x.Id);
                table.ForeignKey(
                    name: "FK_KlausurRaeume_Klausuren_KlausurId",
                    column: x => x.KlausurId,
                    principalTable: "Klausuren",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Notenstufen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KlausurId = table.Column<Guid>(type: "uuid", nullable: false),
                Mindestpunkte = table.Column<decimal>(type: "numeric", nullable: false),
                Note = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Notenstufen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Notenstufen_Klausuren_KlausurId",
                    column: x => x.KlausurId,
                    principalTable: "Klausuren",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Teilnahmen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KlausurId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "text", nullable: false),
                Matrikelnummer = table.Column<string>(type: "text", nullable: false),
                Nachname = table.Column<string>(type: "text", nullable: false),
                Vorname = table.Column<string>(type: "text", nullable: false),
                Email = table.Column<string>(type: "text", nullable: true),
                Zeile = table.Column<int>(type: "integer", nullable: false),
                Angemeldet = table.Column<bool>(type: "boolean", nullable: false),
                RaumId = table.Column<Guid>(type: "uuid", nullable: true),
                Platz = table.Column<int>(type: "integer", nullable: true),
                Anwesend = table.Column<bool>(type: "boolean", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Teilnahmen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Teilnahmen_Klausuren_KlausurId",
                    column: x => x.KlausurId,
                    principalTable: "Klausuren",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MailsignaturBilder",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SignaturId = table.Column<Guid>(type: "uuid", nullable: false),
                ContentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Dateiname = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Typ = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Inhalt = table.Column<byte[]>(type: "bytea", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MailsignaturBilder", x => x.Id);
                table.ForeignKey(
                    name: "FK_MailsignaturBilder_Mailsignaturen_SignaturId",
                    column: x => x.SignaturId,
                    principalTable: "Mailsignaturen",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "GestellteFragen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PruefungId = table.Column<Guid>(type: "uuid", nullable: false),
                Position = table.Column<int>(type: "integer", nullable: false),
                Nummer = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                MaxPunkte = table.Column<decimal>(type: "numeric", nullable: false),
                Punkte = table.Column<decimal>(type: "numeric", nullable: true),
                Zurueckgestellt = table.Column<bool>(type: "boolean", nullable: false),
                Bemerkung = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GestellteFragen", x => x.Id);
                table.ForeignKey(
                    name: "FK_GestellteFragen_MuendlichePruefungen_PruefungId",
                    column: x => x.PruefungId,
                    principalTable: "MuendlichePruefungen",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SchrankFelder",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SchrankId = table.Column<Guid>(type: "uuid", nullable: false),
                Position = table.Column<int>(type: "integer", nullable: false),
                Bezeichnung = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                Reihen = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SchrankFelder", x => x.Id);
                table.ForeignKey(
                    name: "FK_SchrankFelder_Schaltschraenke_SchrankId",
                    column: x => x.SchrankId,
                    principalTable: "Schaltschraenke",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Auslagenpositionen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AbrechnungId = table.Column<Guid>(type: "uuid", nullable: false),
                Nr = table.Column<int>(type: "integer", nullable: false),
                Kaufdatum = table.Column<DateOnly>(type: "date", nullable: false),
                Haendler = table.Column<string>(type: "text", nullable: false),
                Artikel = table.Column<string>(type: "text", nullable: false),
                Betrag = table.Column<decimal>(type: "numeric", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Auslagenpositionen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Auslagenpositionen_Auslagenabrechnungen_AbrechnungId",
                    column: x => x.AbrechnungId,
                    principalTable: "Auslagenabrechnungen",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_Auslagenpositionen_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Buchungsnotizen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false),
                Jahr = table.Column<int>(type: "integer", nullable: false),
                Fingerabdruck = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                Beschreibung = table.Column<string>(type: "text", nullable: false),
                Kommentar = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                Markierung = table.Column<int>(type: "integer", maxLength: 40, nullable: false),
                GeaendertVon = table.Column<Guid>(type: "uuid", nullable: false),
                Geaendert = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Buchungsnotizen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Buchungsnotizen_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Budgetprognosen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false),
                Jahr = table.Column<int>(type: "integer", nullable: false),
                Professuren = table.Column<decimal>(type: "numeric", nullable: false),
                Mitarbeitende = table.Column<decimal>(type: "numeric", nullable: false),
                VerfalleneSwsSoSe = table.Column<decimal>(type: "numeric", nullable: false),
                VerfalleneSwsWiSe = table.Column<decimal>(type: "numeric", nullable: false),
                NichtAnrechenbareSws = table.Column<decimal>(type: "numeric", nullable: false),
                SatzStudierendeSws = table.Column<decimal>(type: "numeric", nullable: false),
                SatzSws = table.Column<decimal>(type: "numeric", nullable: false),
                HoechstVerfallenJeSemester = table.Column<decimal>(type: "numeric", nullable: false),
                SatzProfessur = table.Column<decimal>(type: "numeric", nullable: false),
                SatzMitarbeitende = table.Column<decimal>(type: "numeric", nullable: false),
                Notiz = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Budgetprognosen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Budgetprognosen_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Einzelbuchungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false),
                Datum = table.Column<DateOnly>(type: "date", nullable: false),
                Betrag = table.Column<decimal>(type: "numeric", nullable: false),
                Text = table.Column<string>(type: "text", nullable: false),
                Erfasst = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Einzelbuchungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Einzelbuchungen_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FinanzBuchungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false),
                Jahr = table.Column<int>(type: "integer", nullable: false),
                Nr = table.Column<int>(type: "integer", nullable: false),
                Erfasst = table.Column<DateOnly>(type: "date", nullable: true),
                Belegdatum = table.Column<DateOnly>(type: "date", nullable: false),
                Buchungsdatum = table.Column<DateOnly>(type: "date", nullable: false),
                Kostenart = table.Column<string>(type: "text", nullable: false),
                KostenartText = table.Column<string>(type: "text", nullable: false),
                Betrag = table.Column<decimal>(type: "numeric", nullable: false),
                Text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Belegnummer = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FinanzBuchungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_FinanzBuchungen_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Kostenstellenstaende",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false),
                Jahr = table.Column<int>(type: "integer", nullable: false),
                Dateiname = table.Column<string>(type: "text", nullable: false),
                Exportdatum = table.Column<DateOnly>(type: "date", nullable: true),
                BisDatum = table.Column<DateOnly>(type: "date", nullable: true),
                Importiert = table.Column<long>(type: "bigint", nullable: false),
                Anzahl = table.Column<int>(type: "integer", nullable: false),
                Summe = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Kostenstellenstaende", x => x.Id);
                table.ForeignKey(
                    name: "FK_Kostenstellenstaende_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Massnahmenstaende",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false),
                Stichtag = table.Column<DateOnly>(type: "date", nullable: false),
                Verausgabt = table.Column<decimal>(type: "numeric", nullable: false),
                Quelle = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Erfasst = table.Column<long>(type: "bigint", nullable: false),
                Notiz = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Massnahmenstaende", x => x.Id);
                table.ForeignKey(
                    name: "FK_Massnahmenstaende_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Ausgabenanteile",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AusgabeId = table.Column<Guid>(type: "uuid", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false),
                Betrag = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Ausgabenanteile", x => x.Id);
                table.ForeignKey(
                    name: "FK_Ausgabenanteile_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Ausgabenanteile_GeplanteAusgaben_AusgabeId",
                    column: x => x.AusgabeId,
                    principalTable: "GeplanteAusgaben",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Hilfskraftanteile",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                HilfskraftId = table.Column<Guid>(type: "uuid", nullable: false),
                TopfId = table.Column<Guid>(type: "uuid", nullable: false),
                Prozent = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Hilfskraftanteile", x => x.Id);
                table.ForeignKey(
                    name: "FK_Hilfskraftanteile_FinanzToepfe_TopfId",
                    column: x => x.TopfId,
                    principalTable: "FinanzToepfe",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Hilfskraftanteile_Hilfskraefte_HilfskraftId",
                    column: x => x.HilfskraftId,
                    principalTable: "Hilfskraefte",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Drittpruefungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Nummer = table.Column<string>(type: "text", nullable: false),
                Verfahren = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Erstellt = table.Column<long>(type: "bigint", nullable: false),
                GueltigBis = table.Column<long>(type: "bigint", nullable: true),
                Vorname = table.Column<string>(type: "text", nullable: false),
                Nachname = table.Column<string>(type: "text", nullable: false),
                Matrikelnummer = table.Column<string>(type: "text", nullable: false),
                Email = table.Column<string>(type: "text", nullable: false),
                StudiengangId = table.Column<Guid>(type: "uuid", nullable: false),
                PruefungId = table.Column<Guid>(type: "uuid", nullable: false),
                ErstpruefendeKontoId = table.Column<Guid>(type: "uuid", nullable: false),
                Terminwuensche = table.Column<string>(type: "text", nullable: true),
                Zweitpruefungswunsch = table.Column<string>(type: "text", nullable: true),
                StatusTokenHash = table.Column<string>(type: "text", nullable: false),
                EmailBestaetigt = table.Column<long>(type: "bigint", nullable: true),
                Termin = table.Column<long>(type: "bigint", nullable: true),
                Ort = table.Column<string>(type: "text", nullable: true),
                ZweitpruefendeKontoId = table.Column<Guid>(type: "uuid", nullable: true),
                ZweitpruefendeName = table.Column<string>(type: "text", nullable: true),
                ZweitpruefendeEmail = table.Column<string>(type: "text", nullable: true),
                Bemerkung = table.Column<string>(type: "text", nullable: true),
                Rueckfrage = table.Column<string>(type: "text", nullable: true),
                StudiendekanatKontoId = table.Column<Guid>(type: "uuid", nullable: true),
                Abgeschlossen = table.Column<long>(type: "bigint", nullable: true),
                Ablehnungsgrund = table.Column<string>(type: "text", nullable: true),
                AbgelehntVon = table.Column<string>(type: "text", nullable: true),
                Beendet = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Drittpruefungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Drittpruefungen_Konten_ErstpruefendeKontoId",
                    column: x => x.ErstpruefendeKontoId,
                    principalTable: "Konten",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Drittpruefungen_Pruefungen_PruefungId",
                    column: x => x.PruefungId,
                    principalTable: "Pruefungen",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Drittpruefungen_Studiengaenge_StudiengangId",
                    column: x => x.StudiengangId,
                    principalTable: "Studiengaenge",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Teammitglieder",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                Benachrichtigt = table.Column<long>(type: "bigint", nullable: true),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Bestaetigt = table.Column<long>(type: "bigint", nullable: true),
                Einschraenkungen = table.Column<string>(type: "text", nullable: true),
                TerminMitgeteilt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Teammitglieder", x => x.Id);
                table.ForeignKey(
                    name: "FK_Teammitglieder_HausarbeitPersonen_PersonId",
                    column: x => x.PersonId,
                    principalTable: "HausarbeitPersonen",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Teammitglieder_HausarbeitTeams_TeamId",
                    column: x => x.TeamId,
                    principalTable: "HausarbeitTeams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Bewertungen",
            columns: table => new
            {
                TeilnahmeId = table.Column<Guid>(type: "uuid", nullable: false),
                AufgabeId = table.Column<Guid>(type: "uuid", nullable: false),
                Punkte = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Bewertungen", x => new { x.TeilnahmeId, x.AufgabeId });
                table.ForeignKey(
                    name: "FK_Bewertungen_Teilnahmen_TeilnahmeId",
                    column: x => x.TeilnahmeId,
                    principalTable: "Teilnahmen",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SchrankGeraete",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FeldId = table.Column<Guid>(type: "uuid", nullable: false),
                Reihe = table.Column<int>(type: "integer", nullable: false),
                Start = table.Column<int>(type: "integer", nullable: false),
                Breite = table.Column<int>(type: "integer", nullable: false),
                Typ = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Kennzeichen = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                Text = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                Zusatz = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                Symbol = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Hintergrund = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                Schrift = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SchrankGeraete", x => x.Id);
                table.ForeignKey(
                    name: "FK_SchrankGeraete_SchrankFelder_FeldId",
                    column: x => x.FeldId,
                    principalTable: "SchrankFelder",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Prognoseveranstaltungen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrognoseId = table.Column<Guid>(type: "uuid", nullable: false),
                Nr = table.Column<int>(type: "integer", nullable: false),
                Semester = table.Column<string>(type: "text", nullable: false),
                Bezeichnung = table.Column<string>(type: "text", nullable: false),
                Art = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Teilnehmende = table.Column<int>(type: "integer", nullable: false),
                Sws = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Prognoseveranstaltungen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Prognoseveranstaltungen_Budgetprognosen_PrognoseId",
                    column: x => x.PrognoseId,
                    principalTable: "Budgetprognosen",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Signaturen",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AntragId = table.Column<Guid>(type: "uuid", nullable: false),
                Schritt = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Art = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Zeitpunkt = table.Column<long>(type: "bigint", nullable: false),
                Unterzeichner = table.Column<string>(type: "text", nullable: false),
                KontoId = table.Column<Guid>(type: "uuid", nullable: true),
                Inhalt = table.Column<string>(type: "text", nullable: false),
                InhaltHash = table.Column<string>(type: "text", nullable: false),
                VorgaengerHash = table.Column<string>(type: "text", nullable: true),
                Algorithmus = table.Column<string>(type: "text", nullable: true),
                SignaturWert = table.Column<string>(type: "text", nullable: true),
                OeffentlicherSchluesselJwk = table.Column<string>(type: "text", nullable: true),
                Fingerabdruck = table.Column<string>(type: "text", nullable: true),
                SchluesselHinterlegt = table.Column<bool>(type: "boolean", nullable: false),
                BildPng = table.Column<byte[]>(type: "bytea", nullable: true),
                BildHash = table.Column<string>(type: "text", nullable: true),
                Systemsiegel = table.Column<string>(type: "text", nullable: false),
                SiegelSchluessel = table.Column<string>(type: "text", nullable: false),
                Ueberholt = table.Column<long>(type: "bigint", nullable: true),
                UeberholtGrund = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Signaturen", x => x.Id);
                table.ForeignKey(
                    name: "FK_Signaturen_Drittpruefungen_AntragId",
                    column: x => x.AntragId,
                    principalTable: "Drittpruefungen",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Aufgaben_KlausurId",
            table: "Aufgaben",
            column: "KlausurId");

        migrationBuilder.CreateIndex(
            name: "IX_Ausgabenanteile_AusgabeId",
            table: "Ausgabenanteile",
            column: "AusgabeId");

        migrationBuilder.CreateIndex(
            name: "IX_Ausgabenanteile_TopfId",
            table: "Ausgabenanteile",
            column: "TopfId");

        migrationBuilder.CreateIndex(
            name: "IX_Auslagenabrechnungen_KontoId",
            table: "Auslagenabrechnungen",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_Auslagenabrechnungen_TeamId_Stand",
            table: "Auslagenabrechnungen",
            columns: new[] { "TeamId", "Stand" });

        migrationBuilder.CreateIndex(
            name: "IX_Auslagenpositionen_AbrechnungId",
            table: "Auslagenpositionen",
            column: "AbrechnungId");

        migrationBuilder.CreateIndex(
            name: "IX_Auslagenpositionen_TopfId",
            table: "Auslagenpositionen",
            column: "TopfId");

        migrationBuilder.CreateIndex(
            name: "IX_Buchungsnotizen_TopfId_Fingerabdruck",
            table: "Buchungsnotizen",
            columns: new[] { "TopfId", "Fingerabdruck" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Budgetprognosen_TopfId_Jahr",
            table: "Budgetprognosen",
            columns: new[] { "TopfId", "Jahr" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Dokumente_AntragId",
            table: "Dokumente",
            column: "AntragId");

        migrationBuilder.CreateIndex(
            name: "IX_Drittpruefungen_ErstpruefendeKontoId",
            table: "Drittpruefungen",
            column: "ErstpruefendeKontoId");

        migrationBuilder.CreateIndex(
            name: "IX_Drittpruefungen_Nummer",
            table: "Drittpruefungen",
            column: "Nummer",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Drittpruefungen_PruefungId",
            table: "Drittpruefungen",
            column: "PruefungId");

        migrationBuilder.CreateIndex(
            name: "IX_Drittpruefungen_Status",
            table: "Drittpruefungen",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_Drittpruefungen_StudiengangId",
            table: "Drittpruefungen",
            column: "StudiengangId");

        migrationBuilder.CreateIndex(
            name: "IX_Einladungen_KontoId",
            table: "Einladungen",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_Einladungen_TokenHash",
            table: "Einladungen",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Einzelbuchungen_TopfId",
            table: "Einzelbuchungen",
            column: "TopfId");

        migrationBuilder.CreateIndex(
            name: "IX_ExterneAnmeldungen_Anbieter_Schluessel",
            table: "ExterneAnmeldungen",
            columns: new[] { "Anbieter", "Schluessel" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ExterneAnmeldungen_KontoId",
            table: "ExterneAnmeldungen",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_Fakultaeten_Kuerzel",
            table: "Fakultaeten",
            column: "Kuerzel",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FinanzBuchungen_TopfId_Jahr_Nr",
            table: "FinanzBuchungen",
            columns: new[] { "TopfId", "Jahr", "Nr" });

        migrationBuilder.CreateIndex(
            name: "IX_Finanzerinnerungen_TeamId_Schluessel",
            table: "Finanzerinnerungen",
            columns: new[] { "TeamId", "Schluessel" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FinanzToepfe_TeamId_Art_Kennung",
            table: "FinanzToepfe",
            columns: new[] { "TeamId", "Art", "Kennung" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Gefache_KontoId",
            table: "Gefache",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_GeplanteAusgaben_TeamId_Stand",
            table: "GeplanteAusgaben",
            columns: new[] { "TeamId", "Stand" });

        migrationBuilder.CreateIndex(
            name: "IX_GestellteFragen_PruefungId",
            table: "GestellteFragen",
            column: "PruefungId");

        migrationBuilder.CreateIndex(
            name: "IX_Hausarbeiten_KontoId",
            table: "Hausarbeiten",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_HausarbeitPersonen_HausarbeitId_Matrikelnummer",
            table: "HausarbeitPersonen",
            columns: new[] { "HausarbeitId", "Matrikelnummer" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_HausarbeitTeams_HausarbeitId",
            table: "HausarbeitTeams",
            column: "HausarbeitId");

        migrationBuilder.CreateIndex(
            name: "IX_HausarbeitThemen_HausarbeitId_Nr",
            table: "HausarbeitThemen",
            columns: new[] { "HausarbeitId", "Nr" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Hilfskraefte_TeamId",
            table: "Hilfskraefte",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_Hilfskraftanteile_HilfskraftId",
            table: "Hilfskraftanteile",
            column: "HilfskraftId");

        migrationBuilder.CreateIndex(
            name: "IX_Hilfskraftanteile_TopfId",
            table: "Hilfskraftanteile",
            column: "TopfId");

        migrationBuilder.CreateIndex(
            name: "IX_Klausuren_VerantwortlichKontoId",
            table: "Klausuren",
            column: "VerantwortlichKontoId");

        migrationBuilder.CreateIndex(
            name: "IX_KlausurRaeume_KlausurId",
            table: "KlausurRaeume",
            column: "KlausurId");

        migrationBuilder.CreateIndex(
            name: "IX_Konten_Kennung",
            table: "Konten",
            column: "Kennung",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Kostenstellenstaende_TopfId_Jahr",
            table: "Kostenstellenstaende",
            columns: new[] { "TopfId", "Jahr" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Labormitglieder_KontoId",
            table: "Labormitglieder",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_Labormitglieder_TeamId_KontoId",
            table: "Labormitglieder",
            columns: new[] { "TeamId", "KontoId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Mailentwuerfe_BezugId",
            table: "Mailentwuerfe",
            column: "BezugId");

        migrationBuilder.CreateIndex(
            name: "IX_Mailentwuerfe_KontoId_Erledigt",
            table: "Mailentwuerfe",
            columns: new[] { "KontoId", "Erledigt" });

        migrationBuilder.CreateIndex(
            name: "IX_MailsignaturBilder_SignaturId",
            table: "MailsignaturBilder",
            column: "SignaturId");

        migrationBuilder.CreateIndex(
            name: "IX_Mailsignaturen_KontoId",
            table: "Mailsignaturen",
            column: "KontoId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Massnahmenstaende_TopfId_Stichtag",
            table: "Massnahmenstaende",
            columns: new[] { "TopfId", "Stichtag" });

        migrationBuilder.CreateIndex(
            name: "IX_MuendlichePruefungen_ErstpruefendeId",
            table: "MuendlichePruefungen",
            column: "ErstpruefendeId");

        migrationBuilder.CreateIndex(
            name: "IX_MuendlichePruefungen_KontoId",
            table: "MuendlichePruefungen",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_MuendlichePruefungen_ZweitpruefendeId",
            table: "MuendlichePruefungen",
            column: "ZweitpruefendeId");

        migrationBuilder.CreateIndex(
            name: "IX_Notenstufen_KlausurId",
            table: "Notenstufen",
            column: "KlausurId");

        migrationBuilder.CreateIndex(
            name: "IX_Passkeys_CredentialId",
            table: "Passkeys",
            column: "CredentialId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Passkeys_KontoId",
            table: "Passkeys",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_Postausgang_Versandt",
            table: "Postausgang",
            column: "Versandt");

        migrationBuilder.CreateIndex(
            name: "IX_Prognoseveranstaltungen_PrognoseId",
            table: "Prognoseveranstaltungen",
            column: "PrognoseId");

        migrationBuilder.CreateIndex(
            name: "IX_Protokoll_Zeitpunkt",
            table: "Protokoll",
            column: "Zeitpunkt");

        migrationBuilder.CreateIndex(
            name: "IX_Pruefungen_StudiengangId_Schluessel",
            table: "Pruefungen",
            columns: new[] { "StudiengangId", "Schluessel" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Raeume_Kuerzel",
            table: "Raeume",
            column: "Kuerzel",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Raumelemente_RaumId",
            table: "Raumelemente",
            column: "RaumId");

        migrationBuilder.CreateIndex(
            name: "IX_Schaltschraenke_KontoId",
            table: "Schaltschraenke",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_SchrankFelder_SchrankId",
            table: "SchrankFelder",
            column: "SchrankId");

        migrationBuilder.CreateIndex(
            name: "IX_SchrankGeraete_FeldId",
            table: "SchrankGeraete",
            column: "FeldId");

        migrationBuilder.CreateIndex(
            name: "IX_Sicherheitsprotokoll_Nummer",
            table: "Sicherheitsprotokoll",
            column: "Nummer",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Signaturen_AntragId",
            table: "Signaturen",
            column: "AntragId");

        migrationBuilder.CreateIndex(
            name: "IX_Signaturschluessel_Fingerabdruck",
            table: "Signaturschluessel",
            column: "Fingerabdruck");

        migrationBuilder.CreateIndex(
            name: "IX_Signaturschluessel_KontoId",
            table: "Signaturschluessel",
            column: "KontoId");

        migrationBuilder.CreateIndex(
            name: "IX_Studiengaenge_AltSeit",
            table: "Studiengaenge",
            column: "AltSeit");

        migrationBuilder.CreateIndex(
            name: "IX_Studiengaenge_FakultaetId",
            table: "Studiengaenge",
            column: "FakultaetId");

        migrationBuilder.CreateIndex(
            name: "IX_Studiengaenge_Schluessel",
            table: "Studiengaenge",
            column: "Schluessel",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Stundensaetze_TeamId_Jahr_Art",
            table: "Stundensaetze",
            columns: new[] { "TeamId", "Jahr", "Art" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Teammitglieder_PersonId",
            table: "Teammitglieder",
            column: "PersonId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Teammitglieder_TeamId",
            table: "Teammitglieder",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_Teilnahmen_KlausurId_Code",
            table: "Teilnahmen",
            columns: new[] { "KlausurId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Teilnahmen_KlausurId_Matrikelnummer",
            table: "Teilnahmen",
            columns: new[] { "KlausurId", "Matrikelnummer" },
            unique: true);

        // Sicherheitsprotokoll: Änderungen und Löschungen verbietet die Datenbank selbst (nur anfügen, hash-verkettet)
        migrationBuilder.Sql("""
            CREATE FUNCTION sicherheitsprotokoll_nur_anfuegen() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Sicherheitsprotokoll: nur anfügen';
            END $$;
            CREATE TRIGGER "Sicherheitsprotokoll_nur_anfuegen" BEFORE UPDATE OR DELETE ON "Sicherheitsprotokoll"
                FOR EACH ROW EXECUTE FUNCTION sicherheitsprotokoll_nur_anfuegen();
            CREATE TRIGGER "Sicherheitsprotokoll_nicht_leeren" BEFORE TRUNCATE ON "Sicherheitsprotokoll"
                FOR EACH STATEMENT EXECUTE FUNCTION sicherheitsprotokoll_nur_anfuegen();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS sicherheitsprotokoll_nur_anfuegen() CASCADE;");
        migrationBuilder.DropTable(
            name: "Aufgaben");

        migrationBuilder.DropTable(
            name: "Ausgabenanteile");

        migrationBuilder.DropTable(
            name: "Auslagenpositionen");

        migrationBuilder.DropTable(
            name: "Bewertungen");

        migrationBuilder.DropTable(
            name: "Buchungsnotizen");

        migrationBuilder.DropTable(
            name: "Dokumente");

        migrationBuilder.DropTable(
            name: "Einladungen");

        migrationBuilder.DropTable(
            name: "Einzelbuchungen");

        migrationBuilder.DropTable(
            name: "ExterneAnmeldungen");

        migrationBuilder.DropTable(
            name: "FinanzBuchungen");

        migrationBuilder.DropTable(
            name: "Finanzerinnerungen");

        migrationBuilder.DropTable(
            name: "Gefache");

        migrationBuilder.DropTable(
            name: "GestellteFragen");

        migrationBuilder.DropTable(
            name: "HausarbeitThemen");

        migrationBuilder.DropTable(
            name: "Hilfskraftanteile");

        migrationBuilder.DropTable(
            name: "KlausurRaeume");

        migrationBuilder.DropTable(
            name: "KontoRollen");

        migrationBuilder.DropTable(
            name: "Kostenstellenstaende");

        migrationBuilder.DropTable(
            name: "Labormitglieder");

        migrationBuilder.DropTable(
            name: "Mailentwuerfe");

        migrationBuilder.DropTable(
            name: "MailsignaturBilder");

        migrationBuilder.DropTable(
            name: "Massnahmenstaende");

        migrationBuilder.DropTable(
            name: "Notenstufen");

        migrationBuilder.DropTable(
            name: "Passkeys");

        migrationBuilder.DropTable(
            name: "Postausgang");

        migrationBuilder.DropTable(
            name: "Prognoseveranstaltungen");

        migrationBuilder.DropTable(
            name: "Protokoll");

        migrationBuilder.DropTable(
            name: "Raumelemente");

        migrationBuilder.DropTable(
            name: "SchrankGeraete");

        migrationBuilder.DropTable(
            name: "Sicherheitsprotokoll");

        migrationBuilder.DropTable(
            name: "Signaturen");

        migrationBuilder.DropTable(
            name: "Signaturschluessel");

        migrationBuilder.DropTable(
            name: "Stundensaetze");

        migrationBuilder.DropTable(
            name: "Teammitglieder");

        migrationBuilder.DropTable(
            name: "GeplanteAusgaben");

        migrationBuilder.DropTable(
            name: "Auslagenabrechnungen");

        migrationBuilder.DropTable(
            name: "Teilnahmen");

        migrationBuilder.DropTable(
            name: "MuendlichePruefungen");

        migrationBuilder.DropTable(
            name: "Hilfskraefte");

        migrationBuilder.DropTable(
            name: "Mailsignaturen");

        migrationBuilder.DropTable(
            name: "Budgetprognosen");

        migrationBuilder.DropTable(
            name: "Raeume");

        migrationBuilder.DropTable(
            name: "SchrankFelder");

        migrationBuilder.DropTable(
            name: "Drittpruefungen");

        migrationBuilder.DropTable(
            name: "HausarbeitPersonen");

        migrationBuilder.DropTable(
            name: "HausarbeitTeams");

        migrationBuilder.DropTable(
            name: "Klausuren");

        migrationBuilder.DropTable(
            name: "FinanzToepfe");

        migrationBuilder.DropTable(
            name: "Schaltschraenke");

        migrationBuilder.DropTable(
            name: "Pruefungen");

        migrationBuilder.DropTable(
            name: "Hausarbeiten");

        migrationBuilder.DropTable(
            name: "Laborteams");

        migrationBuilder.DropTable(
            name: "Studiengaenge");

        migrationBuilder.DropTable(
            name: "Konten");

        migrationBuilder.DropTable(
            name: "Fakultaeten");
    }
}
