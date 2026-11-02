using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PrssmParser.Argo;
using PrssmParser.Geometry;

namespace PrssmParser.BearingCapacity;

/// <summary>Matches actual BDF dimensions against the local PARIS RCI/RCT catalogue, in millimetres.</summary>
internal sealed class BdfSectionCatalog
{
    private const double Tolerance = .01;
    private static readonly Lazy<BdfSectionCatalog> Default = new(Load);
    private static readonly string[] Columns =
        ["H", "B", "TW", "TF1", "B2", "TF2", "R1", "R2", "TW_", "B1", "TF1_", "A1", "A2", "A3", "A4", "A5"];
    private readonly List<Entry> entries = [];
    private string? problem;

    internal static PrssmSection? TryCreate(BdfDocument document, ArgoToPrssmConverter shared, out string? warning)
    {
        var catalogue = Default.Value;
        var sourceNumbers = Regex.Matches(document.ProjectType + " " + Regex.Replace(document.SourceFileName, @"^\[\d+\]", ""), @"\d+")
            .Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var match = catalogue.entries.Where(e => e.Matches(document.Beam))
            .OrderByDescending(e => e.IdentityScore(sourceNumbers))
            .ThenBy(e => Math.Abs(e.NominalLength - document.FullLength))
            .ThenBy(e => e.Catalogue, StringComparer.Ordinal).FirstOrDefault();
        if (match is null)
        {
            warning = catalogue.problem is null
                ? "Сечение не найдено в TypicalShapes.csv по размерам и вутам; сохранён исходный контур Custom."
                : $"Каталог TypicalShapes.csv недоступен: {catalogue.problem}; сохранён исходный контур Custom.";
            return null;
        }

        _BaseShape native = match.Type == "RCI" ? new RCIShape() : new RCTShape();
        var parameters = new Dictionary<string, object>();
        foreach (var (name, value) in match.Dimensions)
        {
            var property = native.GetType().GetProperty(name);
            if (property is null) continue;
            property.SetValue(native, value);
            parameters.Add(name, value);
        }
        double left = document.Beam.ConsoleSide == "Слева" ? document.Beam.ConsoleLength * 1000 : 0;
        double right = document.Beam.ConsoleSide == "Справа" ? document.Beam.ConsoleLength * 1000 : 0;
        native.GetType().GetProperty("K1")!.SetValue(native, left);
        native.GetType().GetProperty("K2")!.SetValue(native, right);
        parameters.Add("K1", left); parameters.Add("K2", right);
        var points = native.Profile.Single().Points.ToArray();
        // PARIS centers native shapes internally; restore the BDF axis and the bottom elevation.
        double dx = -match.Dimensions["B1"] / 2 - left - points.Min(p => p.X);
        double dy = -points.Min(p => p.Y);
        var section = shared.CreateCustomSection(points.Select(p => (X: p.X + dx, Y: p.Y + dy)).ToList(),
            Path.GetFileNameWithoutExtension(document.SourceFileName));
        section.SectionType = 0; // FEModel.SectionType.Simple
        var shape = section.Shapes.Single();
        shape.CadType = match.Type == "RCI" ? 108 : 110; // FEModel.ShapeType
        shape.Name = $"{match.Catalogue}: {match.Name}";
        shape.Parameters = parameters;
        shape.Location = new PrssmPoint(section.OffsetY + dx, section.OffsetZ + dy);
        shape.GC = new PrssmPoint();
        shape.Profile = [new PrssmProfileRegion
        {
            Points = points.Select(p => new PrssmProfilePoint
            {
                X = Math.Round(p.X, 2), Y = Math.Round(p.Y, 2), IsAnchor = p.IsAnchor, IsProfilePoint = p.IsProfilePoint
            }).ToList()
        }];
        warning = null;
        return section;
    }

    private static BdfSectionCatalog Load()
    {
        var result = new BdfSectionCatalog();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TypicalShapes.csv"), Encoding.GetEncoding(1251));
            const string header = "catalogue;type;name;h;b;tw;tf1;b2;tf2;r1;r2;tw_;b1;tf1_;a1;a2;a3;a4;a5";
            if (lines.Length == 0 || lines[0] != header) throw new FormatException("неверный заголовок CSV");
            for (int i = 1; i < lines.Length; i++)
            {
                var cells = lines[i].Split(';');
                if (cells.Length < 2 || cells[1] is not ("RCI" or "RCT")) continue;
                if (cells.Length != 19) throw new FormatException($"строка {i + 1}: неверное число столбцов");
                var dimensions = new Dictionary<string, double>();
                for (int j = 0; j < Columns.Length; j++)
                {
                    double value = 0;
                    if (cells[j + 3].Length != 0 &&
                        (!double.TryParse(cells[j + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value)))
                        throw new FormatException($"строка {i + 1}: неверное значение {Columns[j]}");
                    dimensions.Add(Columns[j], value);
                }
                if (dimensions["TF1_"] == 0) dimensions["TF1_"] = dimensions["TF1"];
                if (dimensions["TW_"] == 0) dimensions["TW_"] = dimensions["TW"];
                var length = Regex.Match(cells[2], @"^[\d.]+").Value;
                if (!double.TryParse(length, NumberStyles.Float, CultureInfo.InvariantCulture, out double nominalLength))
                    throw new FormatException($"строка {i + 1}: неверная номинальная длина");
                result.entries.Add(new Entry(cells[0], cells[1], cells[2], nominalLength, dimensions));
            }
            if (result.entries.Count == 0) throw new FormatException("в каталоге нет сечений RCI/RCT");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            result.entries.Clear();
            result.problem = error.Message;
        }
        return result;
    }

    private sealed record Entry(string Catalogue, string Type, string Name, double NominalLength, Dictionary<string, double> Dimensions)
    {
        internal int IdentityScore(HashSet<string> sourceNumbers)
        {
            var numbers = Regex.Matches(Catalogue, @"\d+").Select(m => m.Value).ToArray();
            return numbers.All(sourceNumbers.Contains) ? numbers.Length : 0;
        }

        internal bool Matches(BdfBeam beam)
        {
            bool Same(string name, double metres) => Math.Abs(Dimensions[name] - metres * 1000) <= Tolerance;
            bool Zero(string name) => Math.Abs(Dimensions[name]) <= Tolerance;
            if (Type != (beam.Type == 1 ? "RCT" : "RCI") ||
                !Same("H", beam.Height) || !Same("B1", beam.TopWidth) || !Same("TW", beam.WebWidth) ||
                !Same("TF1", beam.TopThickness) || !Same("TF1_", beam.HaunchHeight == 0 ? beam.TopThickness : beam.HaunchHeight) ||
                !Same("TW_", beam.Type == 1 && beam.HaunchWidth != 0 ? beam.HaunchWidth : beam.WebWidth)) return false;
            if (beam.ConsoleSide is "Слева" or "Справа" && beam.ConsoleLength < 0) return false;
            bool topMatches = beam.TopHaunch switch
            {
                "Радиус" => Same("R1", beam.TopX) && beam.TopX > 0,
                "Линейный" => Zero("R1") && Same("A1", beam.TopX) && Same("A2", beam.TopY),
                "Отсутствует" => Zero("R1") && Zero("A1") && Zero("A2"),
                _ => false
            };
            if (!topMatches || beam.Type == 1) return topMatches;
            if (!Same("B2", beam.BottomEdgeWidth) || !Same("TF2", beam.BottomThickness) ||
                !Same("A5", (beam.BottomWidth - beam.BottomEdgeWidth) / 2)) return false;
            // In RCI the lower haunch ends at the flange edge; BDF also permits shorter chamfers.
            return beam.BottomHaunch switch
            {
                "Линейный" => Zero("R2") && Same("A3", beam.BottomX) && Same("A4", beam.BottomY) &&
                    Math.Abs((beam.WebWidth + 2 * beam.BottomX - beam.BottomWidth) * 1000) <= Tolerance,
                "Отсутствует" => Zero("R2") && Zero("A3") && Zero("A4"),
                _ => false
            };
        }
    }
}
