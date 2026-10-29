namespace PrssmParser.BearingCapacity;

/// <summary>BearingCapacity stores lengths in metres and bar diameters in millimetres.</summary>
public sealed class BdfDocument
{
    public string SourceFileName { get; set; } = "";
    public string ProjectType { get; set; } = "";
    public string Author { get; set; } = "";
    public string CalcLoad { get; set; } = "";
    public double FullLength { get; set; }
    public double SpanLength { get; set; }
    public double BearingLength { get; set; }
    public bool SymmetricReinforcement { get; set; } = true;
    public string ConcreteClass { get; set; } = "";
    public string LowerClass { get; set; } = "None";
    public string UpperClass { get; set; } = "None";
    public string PrestressedLowerClass { get; set; } = "None";
    public string PrestressedUpperClass { get; set; } = "None";
    public string ClampClass { get; set; } = "None";
    public double UpperPrestressAfterLosses { get; set; }
    public BdfBeam Beam { get; set; } = new();
    public List<BdfBar> LowerBars { get; set; } = new();
    public List<BdfBar> UpperBars { get; set; } = new();
    public List<BdfClamp> Clamps { get; set; } = new();
    public List<BdfUnlocatedBars> UnlocatedBars { get; set; } = new();
    public Dictionary<string, double> ResistanceOverrides { get; set; } = new();
}

public sealed class BdfBeam
{
    public int Type { get; set; }
    public double TopWidth { get; set; }
    public double WebWidth { get; set; }
    public double Height { get; set; }
    public double TopThickness { get; set; }
    public double HaunchHeight { get; set; }
    public double HaunchWidth { get; set; }
    public string TopHaunch { get; set; } = "Отсутствует";
    public double TopX { get; set; }
    public double TopY { get; set; }
    public double BottomThickness { get; set; }
    public double BottomWidth { get; set; }
    public double BottomEdgeWidth { get; set; }
    public string BottomHaunch { get; set; } = "Отсутствует";
    public double BottomX { get; set; }
    public double BottomY { get; set; }
    public string ConsoleSide { get; set; } = "Отсутствует";
    public double ConsoleLength { get; set; }
    // These fields affect BearingCapacity's shear check, not its Profile getter.
    public double SupportWebWidth { get; set; }
    public double WideningLength { get; set; }
    public double TransitionLength { get; set; }
}

public class BdfBar
{
    public bool Prestressed { get; set; }
    public int Count { get; set; }
    public double Diameter { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double A { get; set; }
    public double B { get; set; }
    public double C { get; set; }
    public double H1 { get; set; }
    public double H2 { get; set; }
    public double H3 { get; set; }
    public double H4 { get; set; }
    public string BarType { get; set; } = "DirectType";
    public string MaterialClass { get; set; } = "None";
}

public sealed class BdfClamp : BdfBar
{
    public int NCount { get; set; }
    public double Step { get; set; }
    public int HorizontalCount { get; set; }
    public double HorizontalDiameter { get; set; }
}

public sealed record BdfUnlocatedBars(int Count, double Diameter);
