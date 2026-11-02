#if PARIS_INTEGRATION
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FEModel.Converters;
using FEModel.TypicalStructures;
using PrssmParser.BearingCapacity;

internal static class ParisCompatibility
{
    internal static void VerifyUiImport()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
                options.Converters.Add(new ShapeJsonConverter());
                options.Converters.Add(new SectionJsonConverter());
                options.Converters.Add(new MovingLoadJsonConverter());
                int count = 0;
                foreach (string file in Directory.GetFiles("PRSSM-Parser/Data/BDF", "*.bdf"))
                {
                    var document = new BdfToPrssmConverter().Convert(new BdfParser().Parse(file)).Document;
                    var data = JsonSerializer.Deserialize<DockingManager.TypicalStructureViews.Span.BeamSpan.BeamSpanData>(
                        JsonSerializer.Serialize(document, options), options)!;
                    Require(data.SelectedSlab != null, file + ": PARIS UI dereferences SelectedSlab.Material");
                    Require(data.SelectedSlab!.Material == null, file + ": unexpected slab material");
                    // LoadBeamSpanData also copies the slab later, even when it is inactive.
                    var slab = new DockingManager.TypicalStructureViews.Span.BeamSpan.Slab(data.SelectedSlab, data.Beams, data.Braces);
                    Require(slab.Material == null && slab.Section == null && slab.Thickness == 0 && slab.PanelNumber == 0,
                        file + ": slab was activated by the compatibility object");
                    Require(data.Beams.Count == 1, file + ": UI beam count");
                    count++;
                }
                Console.WriteLine($"PARIS UI data: {count} BDF models deserialized with an inactive slab");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    internal static void VerifyBank()
    {
        int count = 0;
        foreach (string file in Directory.GetFiles("PRSSM-Parser/Data/BDF", "*.bdf"))
        {
            var source = new BdfParser().Parse(file);
            var converted = new BdfToPrssmConverter().Convert(source).Document;
            var restored = BeamSpanFileAdapter.Deserialize(JsonSerializer.Serialize(converted)).Model;
            var beam = restored.Beams.Single();
            var expected = converted.Beams[0];
            Require(Math.Abs(beam.Length - expected.Length) < .02, file + ": length");
            Require(beam.Longitudinals.Count == expected.LongitudinalReinforcementNumber, file + ": longitudinal count");
            Require(beam.Transverses.Count == expected.TransverseReinforcementNumber, file + ": transverse count");
            var section = beam.Parts[0].Section;
            section.UpdateStressPoints();
            var bindings = BeamSpanBindingPoints.Get(beam, 0, false);
            foreach (var group in beam.Longitudinals.Cast<ReinforcementGroupModel>().Concat(beam.Transverses))
            {
                var actual = bindings.Single(p => p.Id == group.BindingPoint.Id).Point;
                Require(Math.Abs(actual.X - group.BindingPoint.Position.X) < .05 && Math.Abs(actual.Y - group.BindingPoint.Position.Y) < .05,
                    file + ": binding point moved in PARIS");
            }
            var shape = section.Shapes[0];
            var points = shape.Profile.First().Points.Select(p =>
                (X: p.X + shape.Location.X - section.OffsetY, Y: p.Y + shape.Location.Y - section.OffsetZ)).ToArray();
            var expectedShape = expected.BeamParts[0].Section.Shapes.Single();
            Require((int)shape.ShapeType == expectedShape.CadType, file + ": native shape type lost");
            var sourcePoints = BdfGeometry.BuildProfile(source.Beam)
                .Select(p => (X: p.X * 1000.0, Y: (p.Y + source.Beam.Height) * 1000.0)).ToArray();
            if (expectedShape.CadType == 0)
            {
                foreach (var point in sourcePoints)
                    Require(points.Any(p => Math.Abs(p.X - point.X) < .05 && Math.Abs(p.Y - point.Y) < .05),
                        file + ": custom profile vertex lost in PARIS");
            }
            else
            {
                // Native PARIS arcs have ten subdivisions; BearingCapacity uses five.
                // Check the actual native contour, bounds, and source vertices against its edges.
                var emittedSection = expected.BeamParts[0].Section;
                foreach (var point in expectedShape.Profile.Single().Points)
                    Require(points.Any(p => Math.Abs(p.X - point.X - expectedShape.Location.X + emittedSection.OffsetY) < .05 &&
                        Math.Abs(p.Y - point.Y - expectedShape.Location.Y + emittedSection.OffsetZ) < .05),
                        file + ": native profile differs from PARIS");
                Require(Math.Abs(points.Min(p => p.X) - sourcePoints.Min(p => p.X)) < .05 &&
                    Math.Abs(points.Max(p => p.X) - sourcePoints.Max(p => p.X)) < .05 &&
                    Math.Abs(points.Min(p => p.Y)) < .05 && Math.Abs(points.Max(p => p.Y) - source.Beam.Height * 1000) < .05,
                    file + ": native section bounds changed");
                // Type2 inserts arbitrary web-midheight points, sometimes backtracking into the fillet.
                // These split points do not define the physical boundary.
                foreach (var point in sourcePoints.Where(p => expectedShape.CadType != 108 ||
                    Math.Abs(p.Y - source.Beam.Height * 500) > .05 || Math.Abs(Math.Abs(p.X) - source.Beam.WebWidth * 500) > .05))
                    Require(Enumerable.Range(0, points.Length).Any(i => DistanceToEdge(point, points[i], points[(i + 1) % points.Length]) < 1),
                        file + $": source contour changed beyond native arc discretization at {point}; distance={Enumerable.Range(0, points.Length).Min(i => DistanceToEdge(point, points[i], points[(i + 1) % points.Length]))}");
            }
            for (int i = 0; i < beam.Longitudinals.Count; i++)
            {
                var actual = beam.Longitudinals[i]; var original = expected.ReinforcementLongitudinals[i];
                Require(actual.NAtItem == original.NAtItem && Math.Abs(actual.Diameter - original.Diameter) < .001, file + ": bundle area");
                Require((int)actual.Material.StandartMaterialType == original.Material.StandartMaterialType, file + ": material");
                Require(actual.IsPrestressed == original.IsPrestressed, file + ": prestress flag");
                var bars = ReinforcementModelAdapter.Create(actual).GetBars2D();
                Require(bars.Count == 1, file + ": transverse bar layout");
                Require(Math.Abs(bars[0].Center.X - original.BindingPoint.Position.X - original.YOffset) < .05 &&
                    Math.Abs(bars[0].Center.Y - original.BindingPoint.Position.Y - original.ZOffset) < .05, file + ": bar position");
            }
            for (int i = 0; i < beam.Transverses.Count; i++)
                Require(!beam.Transverses[i].IsClosed, file + ": open branches were closed");
            // Exercise PARIS save/open as well as the initial import.
            var second = BeamSpanFileAdapter.Deserialize(BeamSpanFileAdapter.Serialize(restored)).Model;
            Require(second.Beams.Single().Longitudinals.Count == expected.LongitudinalReinforcementNumber, file + ": save/open cycle");
            count++;
        }
        Console.WriteLine($"PARIS: {count} BDF models loaded and saved");
    }

    private static double DistanceToEdge((double X, double Y) point, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length2 = dx * dx + dy * dy;
        double t = length2 == 0 ? 0 : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / length2, 0, 1);
        return Math.Sqrt(Math.Pow(point.X - a.X - t * dx, 2) + Math.Pow(point.Y - a.Y - t * dy, 2));
    }

    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
}
#endif
