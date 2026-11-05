// Les exports du blotter et du book en CSV et en fichier Excel.
using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using DeribitPricer.Core.Booking;
using DeribitPricer.Core.Structures;

namespace DeribitPricer.Core.Export;

// Exports du blotter et de l'état du book en CSV (séparateur « ; », décimales à la française
// pour s'ouvrir directement dans Excel FR) et en classeur Excel (ClosedXML).
public static class BookExporter
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const string Sep = ";";

    // ---------- Tables communes au CSV et à Excel ----------

    private static readonly string[] BlotterHeader =
    [
        "Trade", "Horodatage (UTC)", "Trade d'origine", "Devise", "Structure", "Description", "Sens", "Quantité",
        "Prime totale (crypto)", "Prime totale (USD)", "Instrument", "Type de jambe", "Quantité signée",
        "Prix d'exécution", "Unité du prix", "Future au trade", "Vol au trade (%)"
    ];

    private static IEnumerable<object?[]> BlotterRows(IReadOnlyList<Trade> trades) =>
        from t in trades
        from l in t.Legs
        select new object?[]
        {
            t.Id, t.TimestampUtc, t.ParentTradeId ?? "", t.Currency, t.StructureType.Label(), t.Description,
            t.Direction.Label(), t.Quantity, t.PremiumCoin, t.PremiumCoin * t.IndexAtTrade, l.Instrument,
            LegKindLabel(l.Kind), l.SignedQuantity, l.ExecutionPrice, l.Kind == LegKind.Future ? "USD" : t.Currency,
            l.Forward, l.Vol * 100
        };

    private static readonly string[] PositionsHeader =
    [
        "Structure", "Description", "Devise", "Instrument", "Statut", "Échéance", "Quantité ouverte",
        "Prix de revient", "Mark", "Théorique", "Valeur mark (crypto)", "Valeur théo (crypto)",
        "P&L réalisé (crypto)", "P&L latent mark (crypto)", "Delta", "Gamma", "Vega", "Theta", "Commentaire"
    ];

    private static IEnumerable<object?[]> PositionRows(BookSnapshot book) =>
        from s in book.Structures
        from l in s.Legs
        select new object?[]
        {
            s.Original.Id, s.Original.Description, s.Original.Currency, l.Instrument, StatusLabel(l.Status),
            l.IsPerpetual ? "Perpétuel" : l.Expiry.ToString("dd/MM/yyyy HH:mm"), l.OpenQuantity, l.AverageCost,
            l.MarkPrice, l.TheoPrice, l.MarkValueCoin, l.TheoValueCoin, l.RealizedCoin, l.UnrealizedMarkCoin,
            l.Greeks.Delta, l.Greeks.Gamma, l.Greeks.Vega, l.Greeks.Theta, l.Note ?? ""
        };

    private static readonly string[] StructuresHeader =
    [
        "Structure", "Horodatage (UTC)", "Description", "Devise", "Sens", "Quantité initiale", "Quantité ouverte",
        "Prime (crypto)", "Valeur mark (crypto)", "Valeur théo (crypto)", "P&L mark (crypto)", "P&L mark (USD)",
        "P&L théo (crypto)", "P&L théo (USD)", "P&L réalisé (crypto)", "Delta", "Gamma", "Vega", "Theta", "Statut"
    ];

    private static IEnumerable<object?[]> StructureRows(BookSnapshot book) =>
        book.Structures.Select(s => new object?[]
        {
            s.Original.Id, s.Original.TimestampUtc, s.Original.Description, s.Original.Currency, s.Original.Direction.Label(),
            s.Original.Quantity, s.OpenQuantity, s.Original.PremiumCoin, s.MarkValueCoin, s.TheoValueCoin, s.PnlMarkCoin,
            s.PnlMarkUsd, s.PnlTheoCoin, s.PnlTheoUsd, s.RealizedCoin, s.Greeks.Delta, s.Greeks.Gamma, s.Greeks.Vega,
            s.Greeks.Theta, s.IsOpen ? "Ouverte" : "Fermée"
        });

    private static readonly string[] GreeksHeader = ["Devise", "Échéance", "Delta", "Gamma", "Vega (USD/pt)", "Theta (USD/jour)"];

    private static IEnumerable<object?[]> GreeksRows(BookSnapshot book) =>
        book.GreeksByExpiry.Select(e => new object?[]
            {
                e.Currency, e.IsPerpetual ? "Perpétuel" : e.Expiry.ToString("dd/MM/yyyy"),
                e.Greeks.Delta, e.Greeks.Gamma, e.Greeks.Vega, e.Greeks.Theta
            })
            .Concat(book.Totals.Select(t => new object?[]
            {
                t.Currency, "TOTAL BOOK", t.Greeks.Delta, t.Greeks.Gamma, t.Greeks.Vega, t.Greeks.Theta
            }));

    private static readonly string[] TotalsHeader =
    [
        "Devise", "Indice (USD)", "Valeur mark (crypto)", "Valeur théo (crypto)", "P&L total mark (crypto)",
        "P&L total mark (USD)", "P&L total théo (crypto)", "P&L total théo (USD)", "P&L réalisé (crypto)",
        "P&L réalisé (USD)", "P&L latent (crypto)", "Delta", "Gamma", "Vega", "Theta"
    ];

    private static IEnumerable<object?[]> TotalsRows(BookSnapshot book) =>
        book.Totals.Select(t => new object?[]
        {
            t.Currency, t.Index, t.MarkValueCoin, t.TheoValueCoin, t.PnlMarkCoin, t.PnlMarkUsd, t.PnlTheoCoin,
            t.PnlTheoUsd, t.RealizedCoin, t.RealizedUsd, t.UnrealizedMarkCoin, t.Greeks.Delta, t.Greeks.Gamma,
            t.Greeks.Vega, t.Greeks.Theta
        });

    // ---------- CSV ----------

    public static string BlotterCsv(IReadOnlyList<Trade> trades) => Csv(BlotterHeader, BlotterRows(trades));

    // État du book : totaux, structures, positions par jambe et grecques par échéance.
    public static string BookCsv(BookSnapshot book)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Book au{Sep}{book.AsOf:dd/MM/yyyy HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("TOTAUX");
        sb.Append(Csv(TotalsHeader, TotalsRows(book)));
        sb.AppendLine();
        sb.AppendLine("STRUCTURES");
        sb.Append(Csv(StructuresHeader, StructureRows(book)));
        sb.AppendLine();
        sb.AppendLine("POSITIONS PAR JAMBE");
        sb.Append(Csv(PositionsHeader, PositionRows(book)));
        sb.AppendLine();
        sb.AppendLine("GRECQUES PAR ÉCHÉANCE");
        sb.Append(Csv(GreeksHeader, GreeksRows(book)));
        return sb.ToString();
    }

    private static string Csv(string[] header, IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(Sep, header.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(Sep, row.Select(v => Escape(FormatCsv(v)))));
        return sb.ToString();
    }

    private static string FormatCsv(object? value) => value switch
    {
        null => "",
        double d => double.IsNaN(d) ? "" : d.ToString("0.##########", Fr),
        DateTime dt => dt.ToString("dd/MM/yyyy HH:mm:ss", Fr),
        _ => value.ToString() ?? ""
    };

    private static string Escape(string s) =>
        s.Contains(Sep) || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    // Encodage UTF-8 avec BOM pour qu'Excel affiche correctement les accents.
    public static byte[] ToCsvBytes(string csv) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();

    // ---------- Excel ----------

    public static byte[] Workbook(IReadOnlyList<Trade> trades, BookSnapshot book)
    {
        using var workbook = new XLWorkbook();
        AddSheet(workbook, "Totaux", TotalsHeader, TotalsRows(book));
        AddSheet(workbook, "Structures", StructuresHeader, StructureRows(book));
        AddSheet(workbook, "Positions", PositionsHeader, PositionRows(book));
        AddSheet(workbook, "Grecques", GreeksHeader, GreeksRows(book));
        AddSheet(workbook, "Blotter", BlotterHeader, BlotterRows(trades));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void AddSheet(XLWorkbook workbook, string name, string[] header, IEnumerable<object?[]> rows)
    {
        var sheet = workbook.Worksheets.Add(name);
        for (int c = 0; c < header.Length; c++)
            sheet.Cell(1, c + 1).Value = header[c];
        var headerRow = sheet.Range(1, 1, 1, header.Length);
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE3F0");

        int r = 2;
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                switch (row[c])
                {
                    case null: break;
                    case double d when !double.IsNaN(d) && !double.IsInfinity(d): cell.Value = d; break;
                    case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = "dd/mm/yyyy hh:mm:ss"; break;
                    default: cell.Value = row[c]!.ToString(); break;
                }
            }
            r++;
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(r, 200));
    }

    private static string LegKindLabel(LegKind kind) => kind switch
    {
        LegKind.ListedOption => "Option Deribit",
        LegKind.CustomOption => "Option sur mesure",
        _ => "Future"
    };

    public static string StatusLabel(LegStatus status) => status switch
    {
        LegStatus.Open => "Ouverte",
        LegStatus.PendingSettlement => "Échue, règlement en attente",
        LegStatus.Settled => "Réglée",
        _ => "Débouclée"
    };
}
