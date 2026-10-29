using PrssmParser.Argo;

namespace PrssmParser.BearingCapacity;

public sealed record BdfConversionResult(PrssmDocument Document, IReadOnlyList<string> Warnings);

public sealed class BdfToPrssmConverter
{
    public BdfConversionResult Convert(BdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var warnings = new List<string>();
        var shared = new ArgoToPrssmConverter();
        var profile = BdfGeometry.BuildProfile(document.Beam)
            .Select(p => (X: p.X * 1000.0, Y: (p.Y + document.Beam.Height) * 1000.0)).ToList();
        var section = shared.CreateCustomSection(profile, Path.GetFileNameWithoutExtension(document.SourceFileName));
        section.It = BdfGeometry.TorsionalInertia(document.Beam) * 1e12;
        double full = document.FullLength * 1000, span = document.SpanLength * 1000;
        double overhang = (full - span) / 2;
        var beam = new PrssmBeam { Id = 1, Length = full, Material = shared.CreateConcreteMaterial(ConcreteStrength(document.ConcreteClass)) };
        void Part(double length, bool pier)
        {
            if (length <= 1e-6) return;
            beam.BeamParts.Add(new PrssmBeamPart
            {
                Id = beam.BeamParts.Count + 1, Section = section, Length = length,
                Division = pier ? 8 : 1, IsStartPier = pier, IsEndPier = pier
            });
        }
        Part(overhang, false); Part(span, true); Part(overhang, false);
        beam.BeamPartsNumber = beam.BeamParts.Count;
        var anchor = section.StressPoints[3];
        var binding = new PrssmBindingPoint
        {
            Id = 4, Position = new PrssmPoint(anchor.X - section.OffsetY, anchor.Y - section.OffsetZ)
        };
        int materialId = 2;
        foreach (var (bars, upper) in new[] { (document.LowerBars, false), (document.UpperBars, true) })
        {
            foreach (var bar in bars)
            {
                if (bar.Count == 0) continue;
                if (bar.Count < 0 || bar.Diameter <= 0) throw new FormatException("Недопустимое количество или диаметр арматуры .bdf.");
                string inherited = bar.Prestressed
                    ? (upper ? document.PrestressedUpperClass : document.PrestressedLowerClass)
                    : (upper ? document.UpperClass : document.LowerClass);
                var material = ReinforcementMaterial(bar.MaterialClass == "None" ? inherited : bar.MaterialClass, materialId++);
                if (material.StandartMaterialType == 0)
                    warnings.Add("В исходной модели не указан класс части арматуры (None). Геометрия сохранена; материал требует назначения в PARIS.");
                var half = HalfPath(bar);
                var left = half.AsEnumerable().Reverse().Select(p => (X: full / 2 - p.X * 1000, Z: p.Z * 1000)).ToList();
                var right = half.Select(p => (X: full / 2 + p.X * 1000, Z: p.Z * 1000)).ToList();
                var paths = Math.Abs(half[0].X) < 1e-9
                    ? new[] { left.Concat(right.Skip(1)).ToList() }
                    : new[] { left, right };
                double[] transverse = document.SymmetricReinforcement && Math.Abs(bar.X) > 1e-6 ? new[] { bar.X, -bar.X } : new[] { bar.X };
                foreach (double x in transverse)
                foreach (var path in paths)
                {
                    var segments = new List<PrssmReinforcementSegment>();
                    for (int i = 1; i < path.Count; i++)
                    {
                        double dx = path[i].X - path[i - 1].X, dz = path[i].Z - path[i - 1].Z;
                        double length = Math.Sqrt(dx * dx + dz * dz);
                        if (length < 1e-6) continue;
                        if (dx < 0) throw new FormatException("Обратный участок продольной арматуры .bdf.");
                        segments.Add(new PrssmReinforcementSegment
                        {
                            Length = length, Angle = Math.Atan2(dz, dx) * 180 / Math.PI, Height = dz,
                            IsFirst = segments.Count == 0
                        });
                    }
                    if (segments.Count == 0) throw new FormatException("Нулевая длина стержня в .bdf.");
                    // Merge the two collinear centre segments into one.
                    for (int i = segments.Count - 1; i > 0; i--)
                        if (Math.Abs(segments[i].Angle - segments[i - 1].Angle) < 1e-8)
                        {
                            segments[i - 1].Length += segments[i].Length;
                            segments[i - 1].Height += segments[i].Height;
                            segments.RemoveAt(i);
                        }
                    beam.ReinforcementLongitudinals.Add(new PrssmLongitudinalReinforcement
                    {
                        Name = $"{(upper ? "Верхняя" : "Нижняя")} {bar.BarType}",
                        Diameter = bar.Diameter, NAtItem = bar.Count, ItemsAtRow = 1, Material = material,
                        IsPrestressed = bar.Prestressed, OffsetFromStart = path[0].X,
                        YOffset = x * 1000 - binding.Position.X, ZOffset = path[0].Z - binding.Position.Y,
                        BindingPoint = binding, SegmentCount = segments.Count, Segments = segments
                    });
                    if (path[0].X < -0.02 || path[^1].X > full + 0.02)
                        warnings.Add("В исходной модели продольная арматура выходит за полную длину балки; координаты сохранены.");
                }
            }
        }
        foreach (var clamp in document.Clamps)
        {
            if (clamp.Count == 0 || clamp.NCount == 0) continue;
            if (clamp.Count < 0 || clamp.NCount < 0 || clamp.Diameter <= 0 || clamp.H1 <= 0 || clamp.Step < 0 || (clamp.NCount > 1 && clamp.Step == 0))
                throw new FormatException("Недопустимые параметры участка хомутов .bdf.");
            var material = ReinforcementMaterial(clamp.MaterialClass == "None" ? document.ClampClass : clamp.MaterialClass, materialId++);
            void Clamps(double start, int count)
            {
                if (count <= 0) return;
                beam.ReinforcementTransverses.Add(new PrssmTransverseReinforcement
                {
                    Name = "Эффективные вертикальные ветви", IsClosed = false, Diameter = clamp.Diameter,
                    Material = material, NAtItem = clamp.Count, ItemsAtRow = count, StepElement = clamp.Step * 1000,
                    OffsetFromStart = start, BindingPoint = binding,
                    YOffset = clamp.X * 1000 - binding.Position.X, ZOffset = clamp.Z * 1000 - binding.Position.Y,
                    SegmentCount = 1, Segments = new() { new() { Length = clamp.H1 * 1000, Angle = 90, Height = clamp.H1 * 1000, IsFirst = true } }
                });
                if (start < -0.02 || start + (count - 1) * clamp.Step * 1000 > full + 0.02)
                    warnings.Add("В исходной модели хомуты выходят за полную длину балки; координаты сохранены.");
            }
            Clamps(full / 2 - (clamp.Y + (clamp.NCount - 1) * clamp.Step) * 1000, clamp.NCount);
            bool atCenter = Math.Abs(clamp.Y) < 1e-9;
            Clamps(full / 2 + (clamp.Y + (atCenter ? clamp.Step : 0)) * 1000, clamp.NCount - (atCenter ? 1 : 0));
        }
        beam.LongitudinalReinforcementNumber = beam.ReinforcementLongitudinals.Count;
        beam.TransverseReinforcementNumber = beam.ReinforcementTransverses.Count;
        if (document.UnlocatedBars.Any() || document.Clamps.Any(c => c.HorizontalCount > 0))
            warnings.Add("Вспомогательные горизонтальные стержни заданы без координат. Количества и диаметры сохранены в отчёте импорта.");
        if (document.Clamps.Any())
            warnings.Add("Хомуты перенесены как эффективные вертикальные ветви: в .bdf отсутствует поперечная раскладка ветвей и геометрия замыкания.");
        if (document.Beam.SupportWebWidth > document.Beam.WebWidth + 1e-6)
            warnings.Add("Экспортирован основной Profile BearingCapacity. Параметры опорного уширения b2/a1/a2 сохранены в отчёте; переменное сечение не создаётся.");
        if (beam.ReinforcementLongitudinals.Any(r => r.IsPrestressed))
            warnings.Add("Перенесены пучки и признак преднапряжения. Усилия натяжения и потери не заданы в .bdf; sigma_pc1 — напряжение верхней арматуры после потерь, оно сохранено в отчёте.");
        if (document.ResistanceOverrides.Values.Any(v => v != 0))
            warnings.Add("Переопределённые расчётные сопротивления BearingCapacity сохранены в отчёте; в PRSSM выбраны соответствующие стандартные классы материалов.");
        // PARIS UI reads SelectedSlab.Material even when the slab is disabled.
        return new(new PrssmDocument { BeamsNumber = 1, Beams = new() { beam }, SelectedSlab = new PrssmSlab() }, warnings.Distinct().ToArray());
    }

    private static List<(double X, double Z)> HalfPath(BdfBar b) => b.BarType switch
    {
        "DirectType" => new() { (0, b.Z), (b.A, b.Z) },
        "ZType" => new() { (b.Y, b.Z), (b.Y + b.A, b.Z), (b.Y + b.A + b.B, b.Z + b.H1), (b.Y + b.A + b.B + b.C, b.Z + b.H1) },
        "CurveType" => new() { (0, b.Z), (b.A, b.Z), (b.A + .25 * b.B, b.Z + b.H1), (b.A + .5 * b.B, b.Z + b.H2), (b.A + .75 * b.B, b.Z + b.H3), (b.A + b.B, b.Z + b.H4) },
        _ => throw new NotSupportedException($"Тип арматуры {b.BarType} не поддерживается.")
    };

    private static double ConcreteStrength(string name) => name switch
    {
        "B75" => 7.5, "B125" => 12.5, "B175" => 17.5, "B225" => 22.5, "B275" => 27.5,
        "B10" => 10, "B15" => 15, "B20" => 20, "B25" => 25, "B30" => 30, "B35" => 35,
        "B40" => 40, "B45" => 45, "B50" => 50, "B55" => 55, "B60" => 60,
        _ => throw new NotSupportedException($"Класс бетона {name} не поддерживается.")
    };

    private static PrssmMaterial ReinforcementMaterial(string name, int id)
    {
        int type = name switch
        {
            "A240" => 1, "A300" => 2, "Ac300" => 3, "A400" => 4, "A600" => 5, "A800" => 6,
            "A_800" => 6, "At600" => 7, "At800" => 8, "At1000" => 9, "B1500_1200" => 10, "Bp1500_1200" => 11, "K7" => 12, "None" => 0,
            _ => throw new NotSupportedException($"Класс арматуры {name} не поддерживается.")
        };
        return new PrssmMaterial
        {
            Id = id, Name = name, MaterialType = 2, StandartMaterialType = type,
            YoungModulus = type switch { 1 or 2 or 3 => 210000, 4 => 200000, 12 => 195000, _ => 190000 },
            PoissonRatio = .3, SpecificWeight = .00007698, ThermalCoefficient = .000012
        };
    }
}
