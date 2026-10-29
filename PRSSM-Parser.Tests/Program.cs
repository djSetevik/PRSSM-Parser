using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Xml.Linq;
using PrssmParser;
using PrssmParser.Argo;
using PrssmParser.BearingCapacity;

if (args.Length == 2 && args[0] is "--record-argo" or "--compare-argo")
{
    var console = Console.Out;
    Console.SetOut(TextWriter.Null);
    var hashes = Directory.GetFiles("PRSSM-Parser/Data/RAW", "*", SearchOption.AllDirectories)
        .OrderBy(f => f).ToDictionary(f => Path.GetFileName(f)!, f => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new ArgoToPrssmConverter().Convert(new ArgoParser().Parse(f))))));
    Console.SetOut(console);
    if (args[0] == "--record-argo") File.WriteAllText(args[1], JsonSerializer.Serialize(hashes));
    else
    {
        var original = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!;
        Check(hashes.Count == original.Count && hashes.All(p => original[p.Key] == p.Value), "ARGO output changed");
    }
    Console.WriteLine($"ARGO: {hashes.Count} models");
    return;
}

int failures = 0;
var document = new BdfParser().Parse("PRSSM-Parser/Data/BDF/[195000] - 3.503.1-73 .bdf");
Run("BDF source lists and metre units", () =>
{
    Near(12, document.FullLength); Near(11.4, document.SpanLength);
    Check(document.LowerBars.Count == 3 && document.UpperBars.Count == 1, "Computed lists duplicated the original bars");
    Near(0.035, document.LowerBars[0].X); Near(32, document.LowerBars[0].Diameter);
});
Run("BDF export includes the inactive slab object required by PARIS UI", () =>
{
    string source = Directory.GetFiles("PRSSM-Parser/Data/BDF", "*.bdf")
        .Single(f => Path.GetFileName(f).StartsWith("[226000]A2", StringComparison.Ordinal));
    var converted = new BdfToPrssmConverter().Convert(new BdfParser().Parse(source)).Document;
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(converted, new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    }));
    Check(json.RootElement.TryGetProperty("SelectedSlab", out var slab) && slab.ValueKind == JsonValueKind.Object,
        "PARIS LoadBeamSpanData dereferences SelectedSlab.Material, but SelectedSlab is missing");
    Near(0, slab.GetProperty("Thickness").GetDouble());
    Check(!slab.TryGetProperty("Material", out var material) || material.ValueKind == JsonValueKind.Null,
        "The compatibility placeholder must not activate a slab");
    Check(slab.GetProperty("PanelNumber").GetInt32() == 0 && slab.GetProperty("PanelLengths").GetArrayLength() == 0,
        "The compatibility placeholder must not create slab panels");
});
Run("Older RainLow/RainTop BDF spelling is supported", () =>
{
    var old = new BdfParser().Parse("PRSSM-Parser.Tests/Fixtures/legacy-example.bdf");
    Check(old.LowerBars.Count == 11 && old.UpperBars.Count == 1, "Legacy bars lost or duplicated");
    Near(16.76, old.FullLength);
    var converted = new BdfToPrssmConverter().Convert(old).Document.Beams.Single();
    Near(16760, converted.Length);
    Check(converted.ReinforcementLongitudinals.Count > 0, "Legacy reinforcement lost");
});
Run("Profiles match original BearingCapacity DLL", () =>
{
    string fixture = Environment.GetEnvironmentVariable("BEARINGCAPACITY_PROFILES") ?? "PRSSM-Parser.Tests/Fixtures/original-profiles.json";
    using var json = JsonDocument.Parse(File.ReadAllText(fixture));
    foreach (var row in json.RootElement.EnumerateArray())
    {
        var source = new BdfParser().Parse(Path.Combine("PRSSM-Parser/Data/BDF", row.GetProperty("File").GetString()!));
        var points = BdfGeometry.BuildProfile(source.Beam);
        Near(row.GetProperty("Ikr").GetDouble(), BdfGeometry.TorsionalInertia(source.Beam), 1e-10);
        var expected = row.GetProperty("Points");
        Check(points.Count == expected.GetArrayLength(), $"{source.SourceFileName}: profile point count");
        for (int i = 0; i < points.Count; i++)
        {
            Near(expected[i].GetProperty("X").GetDouble(), points[i].X, 1e-7);
            Near(expected[i].GetProperty("Y").GetDouble(), points[i].Y, 1e-7);
        }
    }
});
Run("BDF full beam, supports and reinforcement", () =>
{
    var result = new BdfToPrssmConverter().Convert(document);
    Check(result.Document.Beams.Count == 1 && result.Document.BeamsNumber == 1, "BDF is one beam");
    var beam = result.Document.Beams[0];
    Near(12000, beam.Length);
    Check(beam.BeamParts.Count == 3, "End overhangs must be preserved");
    Near(300, beam.BeamParts[0].Length); Near(11400, beam.BeamParts[1].Length);
    Check(beam.BeamParts[1].IsStartPier && beam.BeamParts[1].IsEndPier, "Support axes lost");
    Check(!beam.BeamParts[0].IsStartPier && !beam.BeamParts[2].IsEndPier, "Unexpected end supports");
    Check(beam.ReinforcementLongitudinals.Count == 8, "Transverse symmetry or computed list handling is wrong");
    var lower = beam.ReinforcementLongitudinals.First(r => r.Diameter == 32 && r.Segments.Count == 1);
    Near(300, lower.OffsetFromStart); Near(11400, lower.Segments[0].Length);
    Near(35, lower.BindingPoint.Position.X + lower.YOffset);
    Near(30, lower.BindingPoint.Position.Y + lower.ZOffset);
    Check(lower.Material.StandartMaterialType == 2, "A300 material lost");
    var bent = beam.ReinforcementLongitudinals.First(r => r.Segments.Count > 1);
    Check(bent.Segments.Count == 5 && bent.Segments[1].Angle < 0 && bent.Segments[3].Angle > 0, "Symmetric bent bar path lost");
    var clamp = beam.ReinforcementTransverses.First();
    Check(!clamp.IsClosed && clamp.NAtItem == 2 && clamp.ItemsAtRow == 14, "Effective vertical branches must not become invented closed clamps");
    Near(200, clamp.StepElement); Near(3300, clamp.OffsetFromStart); Near(745, clamp.Segments[0].Length);
    Check(clamp.Material.StandartMaterialType == 1, "Inherited clamp material A240 lost");
    Check(result.Warnings.Count > 0, "Unlocated auxiliary bars need an explicit notice");
    JsonSerializer.Serialize(result.Document);
});
Run("All supplied BDF models convert", () =>
{
    var files = Directory.GetFiles("PRSSM-Parser/Data/BDF", "*.bdf");
    Check(files.Length == 289, "BDF bank missing");
    foreach (string file in files)
    {
        var source = new BdfParser().Parse(file);
        var converted = new BdfToPrssmConverter().Convert(source).Document;
        var beam = converted.Beams.Single();
        Near(source.FullLength * 1000, beam.Length);
        Check(beam.BeamParts.All(p => p.Length > 0 && p.Section.Area > 0), file);
        Check(beam.ReinforcementLongitudinals.Count > 0, file);
        Check(beam.ReinforcementLongitudinals.All(r => r.NAtItem > 0 && r.Diameter > 0), file);
        JsonSerializer.Serialize(converted);
    }
});
Run("Command line selects BDF and writes a PRSSM with import report", () =>
{
    string output = Path.GetFullPath(Path.Combine("artifacts", "test-cli-" + Guid.NewGuid().ToString("N")));
    var result = Launch("--format", "bdf", "--input", Path.GetFullPath("PRSSM-Parser/Data/BDF/[195000] - 3.503.1-73 .bdf"), "--output", output);
    Check(result.Code == 0, result.Output);
    string file = Path.Combine(output, "[195000] - 3.503.1-73 .prssm");
    Check(File.Exists(file), "CLI did not export the selected BDF");
    using var json = JsonDocument.Parse(File.ReadAllText(file));
    Check(json.RootElement.GetProperty("BeamsNumber").GetInt32() == 1, "Wrong input converter selected");
    using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "conversion-report.json")));
    Check(report.RootElement.GetArrayLength() == 1 && report.RootElement[0].GetProperty("Warnings").GetArrayLength() > 0, "Missing import diagnostics");
});
Run("Unknown command line options fail", () =>
{
    Check(Launch("--format", "unknown").Code != 0, "Unknown input format must not silently select ARGO");
});
Run("Interactive mode accepts a BDF path with Cyrillic characters", () =>
{
    string source = Path.GetFullPath(Directory.GetFiles("PRSSM-Parser/Data/BDF", "*405000*").Single());
    string output = Path.GetFullPath(Path.Combine("artifacts", "интерактивный-тест-" + Guid.NewGuid().ToString("N")));
    var result = LaunchWithInput($"2\n{source}\n{output}\n");
    Check(result.Code == 0 && Directory.GetFiles(output, "*.prssm").Length == 1, result.Output);
});
Run("A broken BDF does not prevent other files from converting", () =>
{
    string folder = Path.GetFullPath(Path.Combine("artifacts", "mixed-input-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(folder);
    File.Copy("PRSSM-Parser/Data/BDF/[195000] - 3.503.1-73 .bdf", Path.Combine(folder, "valid.bdf"));
    File.WriteAllText(Path.Combine(folder, "broken.bdf"), "<wrong />");
    File.Copy("PRSSM-Parser/Data/RAW/A4_0640.02p", Path.Combine(folder, "A4_0640.02p"));
    var result = Launch("--format", "bdf", "--input", folder, "--output", Path.Combine(folder, "results"));
    Check(result.Code == 1 && File.Exists(Path.Combine(folder, "results/valid.prssm")), result.Output);
    Check(Directory.GetFiles(Path.Combine(folder, "results"), "*.prssm").Length == 1, "Wrong input format processed");
    using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "results/conversion-report.json")));
    Check(report.RootElement.GetArrayLength() == 2 && report.RootElement.EnumerateArray().Any(e => e.TryGetProperty("Error", out _)), "Broken file missing from report");
});
Run("ARGO mode skips BDF files and keeps the old output name", () =>
{
    string folder = Path.GetFullPath(Path.Combine("artifacts", "argo-input-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(folder);
    File.Copy("PRSSM-Parser/Data/BDF/[195000] - 3.503.1-73 .bdf", Path.Combine(folder, "A4_0640.bdf"));
    File.Copy("PRSSM-Parser/Data/RAW/A4_0640.02p", Path.Combine(folder, "A4_0640.02p"));
    var result = Launch("--format", "argo", "--input", folder, "--output", Path.Combine(folder, "results"));
    Check(result.Code == 0 && File.Exists(Path.Combine(folder, "results/A4_0640_02p.prssm")), result.Output);
    Check(Directory.GetFiles(Path.Combine(folder, "results"), "*.prssm").Length == 1, "BDF accepted as ARGO");
});
Run("Invalid BDF dimensions and unsupported sections are rejected", () =>
{
    string folder = Path.Combine("artifacts", "invalid-bdf-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    string fixture = "PRSSM-Parser/Data/BDF/[195000] - 3.503.1-73 .bdf";
    foreach (string value in new[] { "NaN", "-1", "0" })
    {
        var xml = XDocument.Load(fixture); xml.Root!.Element("L_full")!.Value = value;
        string path = Path.Combine(folder, value + ".bdf"); xml.Save(path);
        Reject<FormatException>(() => new BdfParser().Parse(path));
    }
    var unsupported = XDocument.Load(fixture);
    unsupported.Root!.Element("Beam")!.SetAttributeValue(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"), "ais7GbBeamType3");
    string unknown = Path.Combine(folder, "type3.bdf"); unsupported.Save(unknown);
    Reject<NotSupportedException>(() => new BdfParser().Parse(unknown));
    var missing = XDocument.Load(fixture); missing.Root!.Element("Beam")!.Element("b")!.Remove();
    string incomplete = Path.Combine(folder, "missing.bdf"); missing.Save(incomplete);
    Reject<FormatException>(() => new BdfParser().Parse(incomplete));
});
Run("Prestressed wires retain count, diameter and standard material", () =>
{
    var file = Directory.GetFiles("PRSSM-Parser/Data/BDF", "*.bdf").Select(f => new BdfParser().Parse(f))
        .First(d => d.LowerBars.Any(b => b.Prestressed && b.MaterialClass == "K7" && b.Count == 28));
    var result = new BdfToPrssmConverter().Convert(file);
    var bar = result.Document.Beams[0].ReinforcementLongitudinals.First(r => r.IsPrestressed && r.NAtItem == 28);
    Near(5, bar.Diameter); Check(bar.Material.StandartMaterialType == 12, "K7 lost"); Near(195000, bar.Material.YoungModulus);
    Check(result.Warnings.Any(w => w.Contains("натяжения")), "Missing prestressing data not reported");
});
Run("Missing classes stay unspecified", () =>
{
    var file = Directory.GetFiles("PRSSM-Parser/Data/BDF", "*405000*").Single();
    var result = new BdfToPrssmConverter().Convert(new BdfParser().Parse(file));
    Check(result.Document.Beams[0].ReinforcementLongitudinals.Any(r => r.Material.StandartMaterialType == 0), "Invented material for None");
    Check(result.Warnings.Any(w => w.Contains("None")), "Missing class not reported");
});
#if PARIS_INTEGRATION
Run("PARIS UI imports BDF without activating a slab", ParisCompatibility.VerifyUiImport);
Run("PARIS reads and saves all BDF models", ParisCompatibility.VerifyBank);
#endif
Environment.ExitCode = failures == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL: {name}: {error.Message}"); }
}

static void Near(double expected, double actual, double tolerance = 0.02) =>
    Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, actual {actual}");

static (int Code, string Output) Launch(params string[] arguments)
    => LaunchWithInput("", arguments);

static (int Code, string Output) LaunchWithInput(string input, params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
    start.ArgumentList.Add(typeof(BdfParser).Assembly.Location);
    foreach (string arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    process.StandardInput.Write(input);
    process.StandardInput.Close();
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(30000)) { process.Kill(); throw new Exception("CLI did not exit"); }
    return (process.ExitCode, output.Result + error.Result);
}

static void Reject<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

static void Check(bool valid, string message)
{
    if (!valid) throw new Exception(message);
}
