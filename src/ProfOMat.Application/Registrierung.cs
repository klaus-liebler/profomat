using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Drittpruefung;
using ProfOMat.Application.Katalog;
using ProfOMat.Application.Konten;

namespace ProfOMat.Application;

public static class Registrierung
{
    public static IServiceCollection AddProfOMatAnwendung(this IServiceCollection dienste, IConfiguration konfiguration)
    {
        dienste.Configure<ProfOMatOptionen>(konfiguration.GetSection(ProfOMatOptionen.Abschnitt));
        dienste.AddSingleton(TimeProvider.System);
        dienste.AddSingleton<Ortszeit>();
        dienste.AddScoped<Protokoll>();
        dienste.AddScoped<Postausgang>();
        dienste.AddScoped<Mailentwuerfe>();
        dienste.AddScoped<Mailsignaturen>();
        dienste.AddScoped<KontoDienst>();
        dienste.AddScoped<KatalogDienst>();
        dienste.AddScoped<DrittpruefungDienst>();
        dienste.AddScoped<Klausuren.KlausurDienst>();
        dienste.AddScoped<Muendlich.MuendlichDienst>();
        dienste.AddSingleton<Beschriftung.Streifenlayout>();
        dienste.AddScoped<Beschriftung.BeschriftungDienst>();
        dienste.AddScoped<Hausarbeiten.HausarbeitDienst>();
        dienste.AddScoped<Gefache.GefachDienst>();
        dienste.AddScoped<Finanzen.FinanzDienst>();
        return dienste;
    }
}
