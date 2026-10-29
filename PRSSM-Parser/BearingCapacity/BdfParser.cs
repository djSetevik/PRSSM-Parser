using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PrssmParser.BearingCapacity;

public sealed class BdfParser
{
    public BdfDocument Parse(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = XDocument.Load(reader).Root;
        if (root?.Name.LocalName != "ais7BearingCapacityCalculate")
            throw new FormatException("Это не модель BearingCapacity (.bdf).");

        // The bundled old example uses RainLow/RainTop instead of ReinLow/ReinTop.
        foreach (var element in root.Elements().Where(e => e.Name.LocalName.StartsWith("Rain", StringComparison.Ordinal)))
            element.Name = "Rein" + element.Name.LocalName[4..];

        var beam = root.Element("Beam") ?? throw new FormatException("В .bdf отсутствует Beam.");
        string kind = (string?)beam.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type") ?? "";
        int type = kind.Split(':').Last() switch
        {
            "ais7GbBeamType1" => 1,
            "ais7GbBeamType2" => 2,
            _ => throw new NotSupportedException($"Тип сечения {kind} не поддерживается; доступны ais7GbBeamType1/2.")
        };
        var doc = new BdfDocument
        {
            SourceFileName = Path.GetFileName(path),
            ProjectType = Text(root, "ProjectType"), Author = Text(root, "Author"), CalcLoad = Text(root, "CalcLoad"),
            FullLength = Number(root, "L_full", true), SpanLength = Number(root, "Lp", true),
            BearingLength = Number(root, "Loch"), SymmetricReinforcement = Boolean(root, "IsSymm", true),
            ConcreteClass = Text(root, "ConcreteClass"), LowerClass = Text(root, "ReinLowClass", "None"),
            UpperClass = Text(root, "ReinTopClass", "None"), PrestressedLowerClass = Text(root, "ReinPresLowClass", "None"),
            PrestressedUpperClass = Text(root, "ReinPresTopClass", "None"), ClampClass = Text(root, "ClampClass", "None"),
            UpperPrestressAfterLosses = Number(root, "sigma_pc1"),
            Beam = new BdfBeam
            {
                Type = type, TopWidth = Number(beam, "bf", true), WebWidth = Number(beam, "b", true),
                Height = Number(beam, "h", true), TopThickness = Number(beam, "hf", true),
                HaunchHeight = Number(beam, "hf1"), HaunchWidth = Number(beam, "b1"),
                TopHaunch = Text(beam, "Тип", "Отсутствует"), TopX = Number(beam, "ПараметрX"), TopY = Number(beam, "ПараметрY"),
                BottomThickness = Number(beam, "hd"), BottomWidth = Number(beam, "bd"), BottomEdgeWidth = Number(beam, "bd1"),
                BottomHaunch = Text(beam, "Тип2", "Отсутствует"), BottomX = Number(beam, "ПараметрX2"), BottomY = Number(beam, "ПараметрY2"),
                ConsoleSide = Text(beam, "Положение", "Отсутствует"), ConsoleLength = Number(beam, "k"),
                SupportWebWidth = Number(beam, "b2"), WideningLength = Number(beam, "a1"), TransitionLength = Number(beam, "a2")
            },
            // *List nodes are getter-generated copies, never additional bars.
            LowerBars = ReadBars<BdfBar>(root.Element("ReinLow")),
            UpperBars = ReadBars<BdfBar>(root.Element("ReinTop")),
            Clamps = ReadBars<BdfClamp>(root.Element("Clamps"))
        };
        for (int i = 1; i <= 4; i++)
        {
            int count = Integer(root, $"HorizBarCount{i}");
            if (count > 0) doc.UnlocatedBars.Add(new(count, Number(root, $"HorizBarD{i}")));
        }
        foreach (string name in new[] { "Rs_int", "Rp_int", "Rpw_int", "Rsc_int", "Rsw_int" })
            doc.ResistanceOverrides[name] = Number(root, name);
        if (doc.FullLength <= 0 || doc.SpanLength <= 0 || doc.SpanLength > doc.FullLength ||
            doc.Beam.Height <= 0 || doc.Beam.WebWidth <= 0 || doc.Beam.TopWidth <= 0 || doc.Beam.TopThickness <= 0)
            throw new FormatException("Недопустимые длины балки или размеры сечения в .bdf.");
        return doc;
    }

    private static List<T> ReadBars<T>(XElement? parent) where T : BdfBar, new()
    {
        var result = new List<T>();
        foreach (var e in parent?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var bar = new T
            {
                Prestressed = Boolean(e, "Преднапряжение"), Count = Integer(e, "Count"), Diameter = Number(e, "Diameter"),
                X = Number(e, "X"), Y = Number(e, "Y"), Z = Number(e, "Z"), A = Number(e, "A"), B = Number(e, "B"), C = Number(e, "C"),
                H1 = Number(e, "H1"), H2 = Number(e, "H2"), H3 = Number(e, "H3"), H4 = Number(e, "H4"),
                BarType = Text(e, "BarType", "DirectType"), MaterialClass = Text(e, "classInternal", "None")
            };
            if (bar is BdfClamp clamp)
            {
                clamp.NCount = Integer(e, "NCount"); clamp.Step = Number(e, "Step");
                clamp.HorizontalCount = Integer(e, "HBarCount"); clamp.HorizontalDiameter = Number(e, "HBarD");
            }
            result.Add(bar);
        }
        return result;
    }

    private static string Text(XElement e, string name, string fallback = "") => (string?)e.Element(name) ?? fallback;
    private static bool Boolean(XElement e, string name, bool fallback = false) =>
        e.Element(name) is { } value ? XmlConvert.ToBoolean(value.Value.Trim()) : fallback;
    private static int Integer(XElement e, string name)
    {
        double value = Number(e, name);
        if (value < 0 || value > int.MaxValue || value != Math.Truncate(value)) throw new FormatException($"Некорректное число {name}.");
        return (int)value;
    }
    private static double Number(XElement e, string name, bool required = false)
    {
        var value = e.Element(name);
        if (value is null && !required) return 0;
        if (value is null || !double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            throw new FormatException($"Некорректное или отсутствующее поле {name} в .bdf.");
        return number;
    }
}
