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
            foreach (var point in BdfGeometry.BuildProfile(source.Beam))
                Require(points.Any(p => Math.Abs(p.X - point.X * 1000) < .05 && Math.Abs(p.Y - (point.Y + source.Beam.Height) * 1000) < .05),
                    file + ": profile vertex lost in PARIS");
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

    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
}
#endif
