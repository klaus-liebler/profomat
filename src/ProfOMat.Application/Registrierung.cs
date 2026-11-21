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
        dienste.AddScoped<KontoDienst>();
        dienste.AddScoped<KatalogDienst>();
        dienste.AddScoped<DrittpruefungDienst>();
        return dienste;
    }
}
