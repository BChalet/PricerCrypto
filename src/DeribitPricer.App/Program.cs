// Le point de départ de l'appli : on lit la config, on crée les objets principaux (marché, blotter, book), on déclare les exports et on démarre le serveur web.
using DeribitPricer.App.Components;
using DeribitPricer.App.Services;
using DeribitPricer.Core.Booking;
using DeribitPricer.Core.Export;
using DeribitPricer.Core.Feeds;
using DeribitPricer.Core.Market;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Configuration : appsettings.json, surchargeable en ligne de commande (--MarketData:Mode=Replay).
var marketOptions = builder.Configuration.GetSection(MarketDataOptions.SectionName).Get<MarketDataOptions>() ?? new();
var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new();

builder.Services.AddSingleton(marketOptions);
builder.Services.AddSingleton(appOptions);
builder.Services.AddSingleton(_ => new MarketDataService(marketOptions, DataPaths.Resolve));
builder.Services.AddSingleton(_ =>
{
    var file = DataPaths.Resolve(marketOptions.IsReplay ? appOptions.ReplayBlotterFile : appOptions.BlotterFile);
    if (marketOptions.IsReplay && appOptions.ResetReplayBlotterOnStart && File.Exists(file)) File.Delete(file);
    return new Blotter(file);
});
builder.Services.AddSingleton<BookService>();
builder.Services.AddHostedService<MarketDataHostedService>();
var historyOptions = builder.Configuration.GetSection("SurfaceHistory").Get<SurfaceHistoryOptions>() ?? new();
builder.Services.AddSingleton(historyOptions);
builder.Services.AddSingleton(new SurfaceSnapshotRepository(DataPaths.Resolve(historyOptions.Directory)));
builder.Services.AddSingleton<VolatilityService>();
builder.Services.AddHostedService<SurfaceHistoryHostedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseAntiforgery();
app.MapPricingApi();

// Exports téléchargeables à tout moment.
string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");
app.MapGet("/export/blotter.csv", (Blotter blotter) =>
    Results.File(BookExporter.ToCsvBytes(BookExporter.BlotterCsv(blotter.Trades)), "text/csv", $"blotter-{Stamp()}.csv"));
app.MapGet("/export/book.csv", (BookService book) =>
    Results.File(BookExporter.ToCsvBytes(BookExporter.BookCsv(book.Compute())), "text/csv", $"book-{Stamp()}.csv"));
app.MapGet("/export/book.xlsx", (Blotter blotter, BookService book) =>
    Results.File(BookExporter.Workbook(blotter.Trades, book.Compute()),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"book-blotter-{Stamp()}.xlsx"));

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Instancie le book au démarrage pour qu'il s'abonne aux instruments en position.
app.Services.GetRequiredService<BookService>();

app.Run();

public partial class Program { }
