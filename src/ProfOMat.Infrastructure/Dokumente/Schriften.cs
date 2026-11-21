using System.Collections.Concurrent;
using System.Reflection;
using PdfSharp.Fonts;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>
/// Schriften für alle PDFs (PDFsharp/MigraDoc) – fest eingebettet, damit Anträge auf jedem System gleich
/// aussehen und unter Linux ohne installierte Schriften funktionieren. Verwendet werden Liberation Sans und Liberation Mono
/// (SIL Open Font License 1.1, siehe Schriften/LICENSE), die maßgleich zu Arial, Times New Roman und Courier New sind.
/// Bekannte Namen werden abgebildet; unbekannte Namen fallen auf Liberation Sans zurück (auch MigraDocs Fehlerschrift „Courier New“).
/// </summary>
public static class Schriften
{
    private static readonly Lock Sperre = new();
    private static bool eingerichtet;

    /// <summary>Setzt den Schriftauflöser einmalig (PDFsharp erlaubt das nur vor der ersten Schriftverwendung).</summary>
    public static void Einrichten()
    {
        if (Volatile.Read(ref eingerichtet))
        {
            return;
        }

        lock (Sperre)
        {
            if (!eingerichtet)
            {
                GlobalFontSettings.FontResolver = new EingebetteteSchriften();
                Volatile.Write(ref eingerichtet, true);
            }
        }
    }

    internal sealed class EingebetteteSchriften : IFontResolver
    {
        private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

        internal static string Familie(string name)
        {
            var n = name.Trim().ToLowerInvariant();
            if (n.Contains("courier") || n.Contains("mono") || n.Contains("consolas") || n == "lucida console")
            {
                return "LiberationMono";
            }

            // Arial, Helvetica, Segoe UI, Calibri, Verdana, Liberation Sans … und alles Unbekannte
            return "LiberationSans";
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var schnitt = (isBold, isItalic) switch
            {
                (true, true) => "BoldItalic",
                (true, false) => "Bold",
                (false, true) => "Italic",
                _ => "Regular",
            };
            return new FontResolverInfo($"{Familie(familyName)}-{schnitt}");
        }

        public byte[] GetFont(string faceName) => Cache.GetOrAdd(faceName, Laden);

        private static byte[] Laden(string faceName)
        {
            var ressource = $"ProfOMat.Schriften.{faceName}.ttf";
            using var strom = Assembly.GetExecutingAssembly().GetManifestResourceStream(ressource)
                              ?? throw new InvalidOperationException($"Eingebettete Schrift {ressource} fehlt.");
            using var puffer = new MemoryStream();
            strom.CopyTo(puffer);
            return puffer.ToArray();
        }
    }
}
