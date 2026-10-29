using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using System.Globalization;

namespace PrssmParser.Argo
{
    public class ArgoParser
    {
        private List<string> _tokens = new List<string>();
        private int _tokenIndex;
        private string[] _rawLines = Array.Empty<string>();

        public ArgoDocument Parse(string filePath)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp866 = Encoding.GetEncoding(866);
            var text = File.ReadAllText(filePath, cp866);
            int dosEofIndex = text.IndexOf('\u001a');
            if (dosEofIndex >= 0)
                text = text.Substring(0, dosEofIndex);

            _rawLines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (_rawLines.Length > 0 && _rawLines[_rawLines.Length - 1].Length == 0)
                Array.Resize(ref _rawLines, _rawLines.Length - 1);
            _tokens = new List<string>();
            _tokenIndex = 0;

            int commentCount = int.Parse(_rawLines[0].Trim());

            for (int i = commentCount + 1; i < _rawLines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(_rawLines[i]))
                {
                    // The GRUZ line reader skips blank separator lines before
                    // handing a populated line to the numeric tokenizer.
                    continue;
                }
                else
                {
                    var parts = _rawLines[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    _tokens.AddRange(parts);
                }
            }

            Console.WriteLine($"Файл загружен: {_rawLines.Length} строк, {_tokens.Count} токенов (после {commentCount} комментариев)");

            var doc = new ArgoDocument();

            doc.SourceFileName = Path.GetFileName(filePath);
            doc.FileCode = ArgoFileCode.Parse(filePath);

            doc.Header = new Header { CommentLineCount = commentCount };
            for (int i = 1; i <= commentCount && i < _rawLines.Length; i++)
            {
                doc.Header.Comments.Add(_rawLines[i]);
            }
            Console.WriteLine($"Комментарии: {string.Join(" | ", doc.Header.Comments)}");

            Console.WriteLine("\n=== Парсинг глобальных параметров ===");
            doc.GlobalParams = ParseGlobalParameters();

            for (int i = 0; i < doc.GlobalParams.BeamCount; i++)
            {
                Console.WriteLine($"\n=== Парсинг балки #{i + 1} ===");
                doc.Beams.Add(ParseBeam(i + 1, doc.GlobalParams.BeamCount, doc.FileCode));
            }

            doc.PrintCopies = HasMoreTokens() ? ReadInt() : 0;
            Console.WriteLine($"\nКоличество экземпляров печати: {doc.PrintCopies}");

            doc.DetailedReinforcement = ParseDetailedReinforcement(doc.Beams);
            ParseChangedBarDiameters(doc);

            return doc;
        }

        private GlobalParameters ParseGlobalParameters()
        {
            var gp = new GlobalParameters();

            gp.PrintLevel = ReadDouble();
            gp.ConcreteStrength = ReadDouble();
            Console.WriteLine($"  Прочность бетона: {gp.ConcreteStrength}");

            // 4 типа арматуры
            gp.TensileReinforcementType = ReadDouble();
            gp.CompressedReinforcementType = ReadDouble();
            gp.SlabReinforcementType = ReadDouble();
            gp.StirrupType = ReadDouble();

            gp.SupportAxis1 = ReadDouble();
            gp.SupportAxis2 = ReadDouble();
            gp.InnerSupport1 = ReadDouble();
            gp.InnerSupport2 = ReadDouble();
            gp.FullLength = ReadDouble();
            Console.WriteLine($"  Полная длина: {gp.FullLength}");

            // Количество балок
            gp.BeamCount = ReadInt();
            Console.WriteLine($"  Количество балок: {gp.BeamCount}");

            // Координаты осей балок
            for (int i = 0; i < gp.BeamCount; i++)
                gp.BeamCoordinates.Add(ReadDouble());

            gp.BallastType = ReadDouble();
            gp.SleeperType = ReadDouble();

            gp.TrackAxisZ.Add(ReadDouble());
            gp.TrackAxisZ.Add(ReadDouble());

            // Признак наличия диафрагм только для многобалочных ПС
            if (gp.BeamCount > 1)
            {
                gp.DiaphragmPresence = ReadDouble();
            }

            // Количество точек контура балласта
            int ballastPointCount = ReadInt();
            Console.WriteLine($"  Точек контура балласта: {ballastPointCount}");

            // Точки контура балласта - пары Z Y
            for (int i = 0; i < ballastPointCount; i++)
            {
                double z = ReadDouble();
                double y = ReadDouble();
                gp.BallastContour.Add(new Point2D(z, y));
            }

            return gp;
        }

        private Beam ParseBeam(int beamNumber, int totalBeams, ArgoFileCode fileCode)
        {
            var beam = new Beam { Number = beamNumber };

            int ribCount = fileCode?.MainRibCount ?? 0;

            bool isPlateStructure = (ribCount == 0);
            bool isEdgeBeam = (beamNumber == 1 || beamNumber == totalBeams);
            bool isInnerBeam = (totalBeams > 2) && !isEdgeBeam;
            bool hasLoads = isEdgeBeam || (totalBeams <= 2);

            Console.WriteLine($"  [Тип: ribCount={ribCount}, isEdge={isEdgeBeam}, isInner={isInnerBeam}, hasLoads={hasLoads}, isPlate={isPlateStructure}]");

            // 1. Тротуары и ограждения (4 числа)
            if (hasLoads)
            {
                beam.SidewalkLoadIntensity = ReadDouble();
                beam.SidewalkLoadCoordinate = ReadDouble();
                beam.FenceLoadIntensity = ReadDouble();
                beam.FenceLoadCoordinate = ReadDouble();
                Console.WriteLine($"  Нагрузки: [{beam.SidewalkLoadIntensity}, {beam.SidewalkLoadCoordinate}, {beam.FenceLoadIntensity}, {beam.FenceLoadCoordinate}]");
            }
            else
            {
                Console.WriteLine($"  Нагрузки: пропущены (внутренняя балка)");
            }

            // 2. Арматура плиты
            beam.SlabReinforcement = ParseSlabReinforcement();

            // 3. Сосредоточенные силы
            int concentratedForceCount = ReadInt();
            Console.WriteLine($"  Сосредоточенных сил: {concentratedForceCount}");
            for (int i = 0; i < concentratedForceCount; i++)
            {
                double x = ReadDouble();
                double force = ReadDouble();
                beam.ConcentratedForces.Add(new ConcentratedForce { X = x, Value = force });
            }

            // 4. Расчётные сечения
            beam.SectionCount = ReadInt();
            Console.WriteLine($"  Расчетных сечений: {beam.SectionCount}");
            for (int i = 0; i < beam.SectionCount; i++)
                beam.SectionCoordinates.Add(ReadDouble());

            // 5. Характерные точки
            beam.SlabBeamJunction[0] = ReadInt();
            beam.SlabBeamJunction[1] = ReadInt();
            beam.SlabVuteJunction[0] = ReadInt();
            beam.SlabVuteJunction[1] = ReadInt();

            if (isEdgeBeam)
            {
                beam.BorderSlabJunction[0] = ReadInt();
                beam.BorderSlabJunction[1] = ReadInt();
            }

            // Для однобалочных ПС — ещё одна пара точек
            // (граница борта балластного корыта с большими координатами Z)
            if (totalBeams == 1)
            {
                beam.BorderSlabJunction2[0] = ReadInt();
                beam.BorderSlabJunction2[1] = ReadInt();
            }

            // 6. Продольные разрезы
            beam.LongitudinalCutLower = ReadDouble();
            beam.LongitudinalCutUpper = ReadDouble();
            Console.WriteLine($"  Продольные разрезы: нижний={beam.LongitudinalCutLower}, верхний={beam.LongitudinalCutUpper}");

            // 7. Контур поперечного сечения
            int crossSectionCount = ReadInt();
            Console.WriteLine($"  Точек контура сечения: {crossSectionCount}");

            for (int i = 0; i < crossSectionCount; i++)
            {
                double z = ReadDouble();
                double y = ReadDouble();
                beam.CrossSectionContour.Add(new Point2D(z, y));
            }

            // 8. Группы изменённых точек контура.
            // Без маркера сериализована половина пролёта, с 9999 — все сечения.
            int serializedGroupCount = Math.Max(1, beam.SectionCount / 2);
            int firstGroupCountOrMarker = ReadInt();
            int firstGroupCount;

            if (firstGroupCountOrMarker == 9999)
            {
                beam.HasLongitudinalContourSymmetry = false;
                serializedGroupCount = beam.SectionCount;
                firstGroupCount = ReadInt();
            }
            else
            {
                beam.HasLongitudinalContourSymmetry = true;
                firstGroupCount = firstGroupCountOrMarker;
            }

            for (int groupIndex = 0; groupIndex < serializedGroupCount; groupIndex++)
            {
                int changedPointCount = groupIndex == 0 ? firstGroupCount : ReadInt();
                if (changedPointCount < 0)
                    throw new InvalidDataException($"Отрицательное количество изменённых точек: {changedPointCount}");

                var group = new ChangedContourGroup();
                for (int i = 0; i < changedPointCount; i++)
                    group.PointIndices.Add(ReadInt());
                for (int i = 0; i < changedPointCount; i++)
                    group.Points.Add(new Point2D(ReadDouble(), ReadDouble()));

                beam.ChangedContourGroups.Add(group);
                CopyChangedContourGroupToLegacyProjection(beam, group, groupIndex);
            }

            Console.WriteLine($"  Групп изменения контура: {beam.ChangedContourGroups.Count}, симметрия={beam.HasLongitudinalContourSymmetry}");
            int bendCount = ReadInt();

            // 9. Отогнутые стержни
            Console.WriteLine($"  Отгибов: {bendCount}");
            for (int i = 0; i < bendCount; i++)
            {
                beam.Bends.Add(new BendReinforcement
                {
                    Area = ReadDouble(),
                    UpperCoordinate = ReadDouble(),
                    LowerCoordinate = ReadDouble(),
                    DeltaUpper = ReadDouble(),
                    DeltaLower = ReadDouble()
                });
            }

            // 10. Хомуты
            int stirrupCount = ReadInt();
            Console.WriteLine($"  Секций хомутов: {stirrupCount}");
            for (int i = 0; i < stirrupCount; i++)
            {
                beam.StirrupSections.Add(new StirrupSection
                {
                    EndX = ReadDouble(),
                    Area = ReadDouble(),
                    Step = ReadDouble()
                });
            }

            // 11. Растянутые стержни
            int tensileCount = ReadInt();
            Console.WriteLine($"  Растянутых стержней: {tensileCount}");
            for (int i = 0; i < tensileCount; i++)
            {
                beam.TensileBars.Add(new TensileBar
                {
                    XMin = ReadDouble(),
                    XMax = ReadDouble(),
                    DeltaLower = ReadDouble(),
                    Area = ReadDouble()
                });
            }

            // 12. Сжатые стержни
            int compressedCount = ReadInt();
            Console.WriteLine($"  Сжатых стержней: {compressedCount}");
            for (int i = 0; i < compressedCount; i++)
            {
                beam.CompressedBars.Add(new CompressedBar
                {
                    XMin = ReadDouble(),
                    XMax = ReadDouble(),
                    DeltaUpper = ReadDouble(),
                    Area = ReadDouble()
                });
            }

            Console.WriteLine($"  === Конец балки #{beamNumber}, токен: {_tokenIndex} ===");

            return beam;
        }

        private SlabReinforcement ParseSlabReinforcement()
        {
            var slab = new SlabReinforcement();
            slab.CalculatedBarsCount = ReadInt();
            Console.WriteLine($"  Расчетных стержней плиты: {slab.CalculatedBarsCount}");

            if (slab.CalculatedBarsCount == 0)
                return slab;

            var bendPointCounts = new List<int>();
            for (int i = 0; i < slab.CalculatedBarsCount; i++)
                bendPointCounts.Add(ReadInt());

            var areas = new List<double>();
            for (int i = 0; i < slab.CalculatedBarsCount; i++)
                areas.Add(ReadDouble());

            for (int i = 0; i < slab.CalculatedBarsCount; i++)
            {
                var bar = new CalculatedBar
                {
                    BendPointsCount = bendPointCounts[i],
                    Area = areas[i]
                };

                for (int j = 0; j < bar.BendPointsCount; j++)
                {
                    double z = ReadDouble();
                    double y = ReadDouble();
                    bar.BendPoints.Add(new Point2D(z, y));
                }

                slab.CalculatedBars.Add(bar);
            }

            return slab;
        }

        private DetailedReinforcement ParseDetailedReinforcement(List<Beam> beams)
        {
            Console.WriteLine("\n=== Парсинг детализации ===");
            var detailed = new DetailedReinforcement();

            // Older catalog files may end immediately after the print-copy count.
            // GRUZ accepts that variant and leaves the physical-detail tables empty.
            if (!HasMoreTokens())
                return detailed;

            foreach (var beam in beams)
            {
                Console.WriteLine($"\n  Детализация балки #{beam.Number}");
                var bd = new BeamDetailed { BeamNumber = beam.Number };

                // Растянутые стержни
                for (int i = 0; i < beam.TensileBars.Count; i++)
                {
                    int count = ReadDetailInt();
                    int diameter = ReadDetailInt();
                    var group = new DetailedBarGroup { Count = count, Diameter = diameter };

                    for (int j = 0; j < count; j++)
                        group.ZCoordinates.Add(ReadDetailDouble());

                    bd.TensileBars.Add(group);
                    Console.WriteLine($"    Растянутые #{i + 1}: {count} × Ø{diameter}");
                }

                // Сжатые стержни
                for (int i = 0; i < beam.CompressedBars.Count; i++)
                {
                    int count = ReadDetailInt();
                    int diameter = ReadDetailInt();
                    var group = new DetailedBarGroup { Count = count, Diameter = diameter };

                    for (int j = 0; j < count; j++)
                        group.ZCoordinates.Add(ReadDetailDouble());

                    bd.CompressedBars.Add(group);
                    Console.WriteLine($"    Сжатые #{i + 1}: {count} × Ø{diameter}");
                }

                // Арматура плиты
                if (beam.SlabReinforcement?.CalculatedBarsCount > 0)
                {
                    int detailedSlabGroupCount = beam.SlabReinforcement.CalculatedBarsCount;
                    Console.WriteLine($"    Арматура плиты: {detailedSlabGroupCount} физических групп");
                    for (int i = 0; i < detailedSlabGroupCount; i++)
                    {
                        int barsPerMeter = ReadDetailInt();
                        int diameter = ReadDetailInt();
                        bd.SlabBars.Add(new DetailedSlabBar
                        {
                            BarsPerMeter = barsPerMeter,
                            Diameter = diameter
                        });

                        // Compatibility projection for the untouched converter. The old
                        // model exposed the first token as Diameter and the second as Step.
                        bd.PlateBars.Add(new PlateBarInfo
                        {
                            Diameter = barsPerMeter,
                            Step = diameter
                        });
                    }
                }

                detailed.BeamDetails.Add(bd);
            }

            return detailed;
        }

        private int ReadDetailInt()
        {
            return HasMoreTokens() ? ReadInt() : 0;
        }

        private double ReadDetailDouble()
        {
            return HasMoreTokens() ? ReadDouble() : 0;
        }

        private void ParseChangedBarDiameters(ArgoDocument document)
        {
            if (!HasMoreTokens() || PeekDouble() <= 0)
                return;

            int count = ReadInt();
            for (int i = 0; i < count; i++)
            {
                document.ChangedBarDiameters.Add(new ChangedBarDiameter
                {
                    BeamNumber = ReadOptionalInt(),
                    BarNumber = ReadOptionalInt(),
                    DistanceFromBeamAxis = ReadOptionalInt(),
                    Diameter = ReadOptionalDouble()
                });
            }
        }

        private int ReadOptionalInt()
        {
            return HasMoreTokens() ? ReadInt() : 0;
        }

        private double ReadOptionalDouble()
        {
            return HasMoreTokens() ? ReadDouble() : 0;
        }

        private static void CopyChangedContourGroupToLegacyProjection(
            Beam beam, ChangedContourGroup group, int groupIndex)
        {
            if (groupIndex == 0)
            {
                beam.ChangedPointsCount = group.PointIndices.Count;
                beam.ChangedPointIndices.AddRange(group.PointIndices);
                beam.ChangedPoints.AddRange(group.Points);
            }
            else if (groupIndex == 1)
            {
                beam.ChangedPointsCount2 = group.PointIndices.Count;
                beam.ChangedPointIndices2.AddRange(group.PointIndices);
                beam.ChangedPoints2.AddRange(group.Points);
            }
        }

        private bool HasMoreTokens() => _tokenIndex < _tokens.Count;

        private double PeekDouble()
        {
            if (_tokenIndex >= _tokens.Count) return 0;
            return ParseNumber(_tokens[_tokenIndex]);
        }

        private double ReadDouble()
        {
            if (_tokenIndex >= _tokens.Count)
                throw new InvalidOperationException($"Конец данных на токене {_tokenIndex}");
            var token = _tokens[_tokenIndex++];
            return ParseNumber(token);
        }

        private int ReadInt()
        {
            if (_tokenIndex >= _tokens.Count)
                throw new InvalidOperationException($"Конец данных на токене {_tokenIndex}");
            return ConvertToGruzInteger(ParseNumber(_tokens[_tokenIndex++]));
        }

        private static double ParseNumber(string token)
        {
            string normalized = token.Replace(',', '.');
            const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            if (double.TryParse(normalized, styles, CultureInfo.InvariantCulture, out double value))
                return value;

            throw new FormatException($"Некорректное числовое значение '{token}'");
        }

        private static int ConvertToGruzInteger(double value)
        {
            return checked((int)Math.Truncate(value));
        }
    }
}
