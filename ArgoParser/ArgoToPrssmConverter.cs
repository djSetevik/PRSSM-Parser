using GeometryLibrary;
using ArgoPoint2D = ArgoParser.Point2D;

namespace ArgoParser
{
    public class ArgoToPrssmConverter
    {
        private int _sectionIdCounter = 1;
        private int _materialIdCounter = 1;
        private string _sectionBaseName = "Сечение";

        public void SetSectionName(string argoFileName)
        {
            if (!string.IsNullOrEmpty(argoFileName))
            {
                _sectionBaseName = Path.GetFileNameWithoutExtension(argoFileName);
            }
        }

        public PrssmDocument Convert(ArgoDocument argoDoc)
        {
            if (argoDoc == null) throw new ArgumentNullException(nameof(argoDoc));

            _sectionIdCounter = 1;
            _materialIdCounter = 1;

            if (!string.IsNullOrEmpty(argoDoc.SourceFileName))
            {
                _sectionBaseName = Path.GetFileNameWithoutExtension(argoDoc.SourceFileName);
            }

            var prssm = new PrssmDocument { BeamsNumber = argoDoc.GlobalParams.BeamCount };

            var material = ConvertConcreteMaterial(argoDoc.GlobalParams);
            double firstBeamCoord = argoDoc.GlobalParams.BeamCoordinates.FirstOrDefault();

            double midAxisZ = 0;
            if (argoDoc.GlobalParams.BeamCoordinates.Count > 0)
                midAxisZ = (argoDoc.GlobalParams.BeamCoordinates.First() + argoDoc.GlobalParams.BeamCoordinates.Last()) / 2.0;

            double globalLeftEdge = double.PositiveInfinity;
            double globalRightEdge = double.NegativeInfinity;

            for (int i = 0; i < argoDoc.Beams.Count; i++)
            {
                var argoBeam = argoDoc.Beams[i];
                double beamAxisZ_cm = argoDoc.GlobalParams.BeamCoordinates[i];

                // 1) Контур + измененные точки
                var contour = ApplyChangedPoints(argoBeam);

                // 2) Перевод в мм относительно оси балки
                var pts = ConvertArgoContour(contour, beamAxisZ_cm);

                // 3) Зеркалирование
                bool desiredRight = beamAxisZ_cm > midAxisZ + 1e-6;
                bool needMirror = NeedMirrorByOverhang(pts, desiredRight, i, argoDoc.Beams.Count);

                if (needMirror)
                    pts = pts.Select(p => (-p.X, p.Y)).ToList();

                // 4) Границы профиля
                double profileMinX = pts.Min(p => p.X);
                double profileMaxX = pts.Max(p => p.X);
                double profileMinY = pts.Min(p => p.Y);

                // 5) Вычисляем ЦТ исходного профиля
                ComputeCentroid(pts, out double gcX_mm, out double gcY_mm);

                // 6) ЦЕНТРИРУЕМ ПРОФИЛЬ ПО ЦТ
                var profileCentered = pts
                    .Select(p => (X: p.X - gcX_mm, Y: p.Y - gcY_mm))
                    .ToList();

                // 7) ParisCadPoint2D из центрированного профиля
                var cadPoints = new List<ParisCadPoint2D>(profileCentered.Count);
                for (int k = 0; k < profileCentered.Count; k++)
                {
                    bool isProfilePoint = IsProfilePoint(profileCentered, k);
                    cadPoints.Add(new ParisCadPoint2D(
                        profileCentered[k].X,
                        profileCentered[k].Y,
                        isAnchor: isProfilePoint,
                        isProfile: isProfilePoint));
                }

                var region = new ParisRegion(cadPoints) { RegionType = RegionType.Body };

                string shapeName = $"{_sectionBaseName}_Б{i + 1}";

                var shape = new Geometry._BaseShape
                {
                    Id = 1,
                    Name = shapeName,
                    Beta = 0,
                    IsReflectX = false,
                    IsReflectY = false,
                    Location = new ParisCadPoint2D(0, 0),
                    Profile = new List<ParisRegion> { region }
                };
                shape.UpdateCenterOfGrav();

                // Раньше здесь создавался ArgoParser.Geometry.Section и вызывался section.Solve(),
                // что тянуло CrossSection.Net и зависимые DLL. Для конвертации в .prssm
                // достаточно записать custom-профиль и базовые геометрические характеристики,
                // поэтому считаем их локально по полигону.
                var section = BuildLocalSectionProperties(profileCentered, _sectionIdCounter++, shapeName);

                // 8) Находим нижние углы РЕБРА по контуру
                double point4_X, point4_Y, point3_X, point3_Y;
                FindRibBottomCorners(profileCentered, out point4_X, out point4_Y, out point3_X, out point3_Y);

                // Ось ребра = середина между точками 3 и 4
                double ribAxisX_centered = (point3_X + point4_X) / 2.0;

                // Ширина ребра
                double ribWidth = Math.Abs(point3_X - point4_X);

                // OffsetY = смещение от оси ребра до ЦТ
                double offsetY = ribAxisX_centered;

                // OffsetZ = Y координата низа ребра
                double offsetZ = point4_Y;

                // 9) Находим верх ребра
                double ribTopY;
                var stressPoints = section.StressPoints;

                if (stressPoints != null && stressPoints.Count >= 4)
                {
                    double sp1Y = stressPoints[0].Y;
                    double sp2Y = stressPoints[1].Y;

                    // Если обе верхние точки примерно на одном уровне (однобалочное сечение с бортиками)
                    if (Math.Abs(sp1Y - sp2Y) < 50.0)
                    {
                        // Ищем верх ребра по центру ширины
                        ribTopY = FindRibTopAtCenter(profileCentered, ribAxisX_centered);
                    }
                    else
                    {
                        // Стандартный случай — берём меньшую Y (верх ребра, не бортик)
                        ribTopY = Math.Min(sp1Y, sp2Y);
                    }
                }
                else
                {
                    ribTopY = FindRibTopAtCenter(profileCentered, ribAxisX_centered);
                }

                // 10) Обновляем section.Yc/Zc в единицах FEModel/Paris (мм).
                // Раньше здесь было деление на 1000, из-за чего Yc/Zc оказывались в метрах,
                // а Area/Iyy/Izz оставались в мм-системе.
                section.Yc = Math.Round(offsetY, 2);
                section.Zc = Math.Round(-offsetZ, 2);

                // 11) Смещения профиля относительно оси ребра (для расчёта позиции балок)
                double ribAxisX_orig = gcX_mm + ribAxisX_centered;
                double profileLeftFromRib = profileMinX - ribAxisX_orig;
                double profileRightFromRib = profileMaxX - ribAxisX_orig;

                // 12) Базовая позиция оси ребра
                double axisPositionMm = (beamAxisZ_cm - firstBeamCoord) * 10.0;
                double positionMm = axisPositionMm;

                // 13) Глобальные координаты краёв профиля
                double currentLeftEdge = positionMm + profileLeftFromRib;
                double currentRightEdge = positionMm + profileRightFromRib;

                // 14) Не сдвигаем балки искусственно: Position должен соответствовать исходным осям АРГО.
                globalLeftEdge = Math.Min(globalLeftEdge, currentLeftEdge);
                globalRightEdge = Math.Max(globalRightEdge, currentRightEdge);

                double stepMm = 0;
                if (i > 0)
                    stepMm = positionMm - prssm.Beams[i - 1].Position;

                // 15) Границы центрированного профиля
                double profMinY = cadPoints.Min(p => p.Y);
                double profMaxY = cadPoints.Max(p => p.Y);

                // 16) PrssmSection
                var prssmSection = ConvertSectionToPrssm(section, shape, i, offsetY, offsetZ);

                var supportExtra = (argoDoc.GlobalParams.FullLength - (argoDoc.GlobalParams.SupportAxis2 - argoDoc.GlobalParams.SupportAxis1)) / 2;
                // 17) Beam
                var prssmBeam = new PrssmBeam
                {
                    Id = i + 1,
                    Material = material,
                    Position = positionMm,
                    Step = stepMm,
                    IsFirst = i == 0
                };

                if (Math.Abs(supportExtra) < 1e-9)
                {
                    prssmBeam.BeamParts.Add(new PrssmBeamPart
                    {
                        Id = 1,
                        Section = prssmSection,
                        Length = argoDoc.GlobalParams.FullLength * 10,
                        Division = 8,
                        IsStartPier = true,
                        IsEndPier = true
                    });
                }
                else
                {
                    prssmBeam.BeamParts.Add(new PrssmBeamPart
                    {
                        Id = 1,
                        Section = prssmSection,
                        Length = supportExtra * 10,
                        Division = 1,
                        IsStartPier = false,
                        IsEndPier = false
                    });
                    prssmBeam.BeamParts.Add(new PrssmBeamPart
                    {
                        Id = 2,
                        Section = prssmSection,
                        Length = (argoDoc.GlobalParams.SupportAxis2 - argoDoc.GlobalParams.SupportAxis1) * 10,
                        Division = 8,
                        IsStartPier = true,
                        IsEndPier = true
                    });
                    prssmBeam.BeamParts.Add(new PrssmBeamPart
                    {
                        Id = 3,
                        Section = prssmSection,
                        Length = supportExtra * 10,
                        Division = 1,
                        IsStartPier = false,
                        IsEndPier = false
                    });
                }

                prssmBeam.BeamPartsNumber = prssmBeam.BeamParts.Count;
                prssmBeam.Length = Math.Round(prssmBeam.BeamParts.Sum(bp => bp.Length), 2);

                // 18) BindingPoint — локальные координаты точки 4 относительно точки привязки (низ-ось ребра)
                double bindingX_local = point4_X - ribAxisX_centered;
                double bindingY_local = 0;

                // В BeamSpanData BindingPoint хранится в общей системе координат Y/Z
                // всего пролётного строения. Это видно по эталонному not_PLITA.prssm:
                // для каждой балки BindingPoint.X = Beam.Position + локальное смещение.
                // Если записывать только локальное bindingX_local, Paris привязывает
                // арматуру всех балок к одной и той же поперечной координате.
                var bindingPointGlobal = new PrssmPoint(
                    x: Math.Round(positionMm + bindingX_local, 2),
                    y: Math.Round(bindingY_local, 2)
                );

                var llLocal = new ParisPoint2D(point4_X, point4_Y);

                // 19) Арматура
                ConvertLongitudinalReinforcement(
                    argoBeam, argoDoc, prssmBeam,
                    cadPoints,
                    bindingPointGlobal,
                    llLocal,
                    profMinY, ribTopY,
                    ribAxisX_centered,
                    ribWidth);

                ConvertTransverseReinforcement(
                    argoBeam, argoDoc, prssmBeam, cadPoints, bindingPointGlobal, llLocal, ribWidth, profMinY, ribTopY);

                // Первый этап переноса плитной арматуры АРГО:
                // верхние разрозненные стержни плиты сводим в один замкнутый
                // контур и записываем как поперечную арматуру балки.
                ConvertPlateTopReinforcementAsTransverse(
                    argoBeam, argoDoc, prssmBeam,
                    bindingPointGlobal, llLocal,
                    beamAxisZ_cm, gcX_mm, gcY_mm,
                    needMirror, prssmBeam.Length,
                    profileCentered, ribTopY);

                prssmBeam.LongitudinalReinforcementNumber = prssmBeam.ReinforcementLongitudinals.Count;
                prssmBeam.TransverseReinforcementNumber = prssmBeam.ReinforcementTransverses.Count;

                prssm.Beams.Add(prssmBeam);
            }

            // Плиту из АРГО пока не переносим в Paris.
            // По эталонному файлу not_PLITA.prssm объект SelectedSlab должен
            // присутствовать, но быть пустой заглушкой: H=0, Material=null,
            // Section=null, PanelNumber=0.
            prssm.SelectedSlab = BuildNoSlabPlaceholder();

            return prssm;
        }

        private PrssmSlab BuildNoSlabPlaceholder()
        {
            // Такая заглушка соответствует .prssm, сохранённому в Paris как
            // балочное ПС без плиты: объект SelectedSlab есть, но плита
            // фактически отключена нулевой толщиной, без материала и сечения.
            return new PrssmSlab
            {
                Thickness = 0,
                K1 = 0,
                K2 = 0,
                PanelNumber = 0,
                DeltaX = 0,
                DeltaZ = 0,
                IsGrouped = false,
                PanelLengths = new List<PrssmSlabPanelLength>(),
                UnionType = 0,
                Section = null,
                Material = null
            };
        }

        #region Поиск верха ребра

        /// <summary>
        /// Находит верх ребра по центру ширины — ищет максимальную Y точки контура
        /// в районе оси ребра (центра нижней грани)
        /// </summary>
        private double FindRibTopAtCenter(List<(double X, double Y)> profileCentered, double ribAxisX)
        {
            // Ищем точки контура вблизи оси ребра (в пределах ±50мм от центра)
            const double tolerance = 50.0;

            var pointsNearAxis = profileCentered
                .Where(p => Math.Abs(p.X - ribAxisX) < tolerance)
                .ToList();

            if (pointsNearAxis.Count > 0)
            {
                // Берём максимальную Y среди точек вблизи оси
                return pointsNearAxis.Max(p => p.Y);
            }

            // Fallback: ищем по пересечению вертикальной линии X=ribAxisX с контуром
            double maxY = double.MinValue;

            for (int k = 0; k < profileCentered.Count; k++)
            {
                var p1 = profileCentered[k];
                var p2 = profileCentered[(k + 1) % profileCentered.Count];

                // Проверяем пересекает ли отрезок p1-p2 вертикальную линию X=ribAxisX
                if ((p1.X <= ribAxisX && p2.X >= ribAxisX) || (p1.X >= ribAxisX && p2.X <= ribAxisX))
                {
                    if (Math.Abs(p2.X - p1.X) > 0.01)
                    {
                        // Линейная интерполяция Y на X=ribAxisX
                        double t = (ribAxisX - p1.X) / (p2.X - p1.X);
                        double y = p1.Y + t * (p2.Y - p1.Y);
                        if (y > maxY)
                        {
                            maxY = y;
                        }
                    }
                }
            }

            if (maxY > double.MinValue)
            {
                return maxY;
            }

            // Крайний fallback — максимальная Y всего контура
            return profileCentered.Max(p => p.Y);
        }

        #endregion

        #region Поиск углов ребра

        /// <summary>
        /// Находит нижние углы ребра по контуру профиля.
        /// Ищет самую широкую горизонтальную линию на минимальном Y.
        /// </summary>
        private void FindRibBottomCorners(
            List<(double X, double Y)> profileCentered,
            out double point4_X, out double point4_Y,
            out double point3_X, out double point3_Y)
        {
            // Убираем дубликаты точек
            var uniquePoints = new List<(double X, double Y)>();
            for (int i = 0; i < profileCentered.Count; i++)
            {
                var p = profileCentered[i];
                if (uniquePoints.Count == 0 ||
                    Math.Abs(p.X - uniquePoints.Last().X) > 0.1 ||
                    Math.Abs(p.Y - uniquePoints.Last().Y) > 0.1)
                {
                    uniquePoints.Add(p);
                }
            }

            // Находим минимальный Y (низ профиля)
            double profMinY = uniquePoints.Min(p => p.Y);

            // Ищем горизонтальные линии на нижнем уровне
            var ribEdges = new List<(double X1, double X2, double Y)>();

            for (int k = 0; k < uniquePoints.Count; k++)
            {
                var curr = uniquePoints[k];
                var next = uniquePoints[(k + 1) % uniquePoints.Count];

                // Горизонтальная линия на нижнем уровне (с допуском)
                if (Math.Abs(curr.Y - profMinY) < 5.0 &&
                    Math.Abs(next.Y - profMinY) < 5.0 &&
                    Math.Abs(curr.Y - next.Y) < 1.0)
                {
                    double x1 = Math.Min(curr.X, next.X);
                    double x2 = Math.Max(curr.X, next.X);
                    double lineLen = x2 - x1;

                    if (lineLen > 10.0)  // Игнорируем очень короткие линии
                    {
                        ribEdges.Add((x1, x2, (curr.Y + next.Y) / 2.0));
                    }
                }
            }

            if (ribEdges.Count > 0)
            {
                // Берём самую широкую горизонтальную линию внизу - это низ ребра
                var widest = ribEdges.OrderByDescending(e => e.X2 - e.X1).First();
                point4_X = widest.X1;
                point3_X = widest.X2;
                point4_Y = widest.Y;
                point3_Y = widest.Y;
            }
            else
            {
                // Fallback - берём крайние точки на минимальном Y
                var bottomPoints = uniquePoints
                    .Where(p => Math.Abs(p.Y - profMinY) < 5.0)
                    .OrderBy(p => p.X)
                    .ToList();

                if (bottomPoints.Count >= 2)
                {
                    point4_X = bottomPoints.First().X;
                    point4_Y = bottomPoints.First().Y;
                    point3_X = bottomPoints.Last().X;
                    point3_Y = bottomPoints.Last().Y;
                }
                else
                {
                    // Крайний fallback
                    point4_X = uniquePoints.Min(p => p.X);
                    point3_X = uniquePoints.Max(p => p.X);
                    point4_Y = profMinY;
                    point3_Y = profMinY;
                }
            }
        }

        #endregion

        #region Центроид

        private void ComputeCentroid(List<(double X, double Y)> pts, out double cx, out double cy)
        {
            double signedArea = 0;
            cx = 0;
            cy = 0;
            int n = pts.Count;

            for (int j = 0; j < n; j++)
            {
                int k = (j + 1) % n;
                double cross = pts[j].X * pts[k].Y - pts[k].X * pts[j].Y;
                signedArea += cross;
                cx += (pts[j].X + pts[k].X) * cross;
                cy += (pts[j].Y + pts[k].Y) * cross;
            }

            signedArea /= 2.0;
            if (Math.Abs(signedArea) < 1e-10)
            {
                cx = 0;
                cy = 0;
                return;
            }

            cx /= (6.0 * signedArea);
            cy /= (6.0 * signedArea);
        }

        private bool NeedMirrorByOverhang(List<(double X, double Y)> pts, bool desiredRight, int beamIndex, int totalBeams)
        {
            // Для крайних балок определяем по позиции относительно центра
            // Первая балка — свес должен быть слева (desiredRight = false)
            // Последняя балка — свес должен быть справа (desiredRight = true)

            double minX = pts.Min(p => p.X);
            double maxX = pts.Max(p => p.X);

            double leftOverhang = -minX;  // насколько выступает влево от оси
            double rightOverhang = maxX;  // насколько выступает вправо от оси

            // Если разница свесов маленькая — сечение почти симметричное
            if (Math.Abs(rightOverhang - leftOverhang) < 50)
            {
                // Для симметричных сечений не зеркалируем
                return false;
            }

            // Определяем куда сейчас выступает сечение
            bool currentlyRight = rightOverhang > leftOverhang;

            // Зеркалируем если текущее направление не совпадает с желаемым
            return currentlyRight != desiredRight;
        }

        private double DetectZScale(List<double> zCoords, double ribWidthMm)
        {
            if (zCoords == null || zCoords.Count < 2) return 10.0;
            if (ribWidthMm <= 1e-6) return 10.0;

            double range = zCoords.Max() - zCoords.Min();

            double d1 = Math.Abs(range - ribWidthMm);
            double d10 = Math.Abs(range * 10.0 - ribWidthMm);

            return (d10 < d1) ? 10.0 : 1.0;
        }

        #endregion

        #region Контур

        private List<ArgoPoint2D> ApplyChangedPoints(Beam argoBeam)
        {
            // ВАЖНО.
            // ChangedPoints/ChangedPoints2 в АРГО описывают изменение контура в других
            // расчётных сечениях по X. Их нельзя накладывать на один постоянный профиль.
            // Если применить эти точки к базовому контуру, у N4/N2 появляются ложные
            // нижние «полки»/уступы: дублирующиеся точки исходного контура расходятся,
            // а арматура и хомуты визуально оказываются висящими в воздухе.
            //
            // Пока конвертер экспортирует одно постоянное custom-сечение на балку,
            // берём только базовый контур. Полноценная поддержка ChangedPoints должна
            // делаться отдельно через переменные сечения Paris или нарезку BeamPart.
            var contour = argoBeam.CrossSectionContour
                .Select(p => new ArgoPoint2D(p.Z, p.Y))
                .ToList();

            return NormalizeContour(contour);
        }

        private void ApplyPointChanges(
            List<ArgoPoint2D> contour,
            IReadOnlyList<int> indices,
            IReadOnlyList<ArgoPoint2D> points)
        {
            if (contour.Count == 0 || indices == null || points == null) return;

            int count = Math.Min(indices.Count, points.Count);
            for (int i = 0; i < count; i++)
            {
                int idx = indices[i];

                // По инструкции точки нумеруются с 1. Для устойчивости поддерживаем
                // и 0-based индексы, если такие встретятся в нестандартном файле.
                int zeroBased = idx >= 1 && idx <= contour.Count
                    ? idx - 1
                    : idx;

                if (zeroBased < 0 || zeroBased >= contour.Count)
                {
                    Console.WriteLine($"  WARNING: индекс изменённой точки {idx} вне контура из {contour.Count} точек");
                    continue;
                }

                contour[zeroBased] = new ArgoPoint2D(points[i].Z, points[i].Y);
            }
        }

        private List<ArgoPoint2D> NormalizeContour(List<ArgoPoint2D> contour)
        {
            var filtered = new List<ArgoPoint2D>();
            foreach (var p in contour)
            {
                if (filtered.Count == 0 ||
                    Math.Abs(p.Z - filtered.Last().Z) > 0.1 ||
                    Math.Abs(p.Y - filtered.Last().Y) > 0.1)
                {
                    filtered.Add(p);
                }
            }

            if (filtered.Count > 1)
            {
                var first = filtered.First();
                var last = filtered.Last();
                if (Math.Abs(first.Z - last.Z) < 0.1 && Math.Abs(first.Y - last.Y) < 0.1)
                    filtered.RemoveAt(filtered.Count - 1);
            }

            return filtered;
        }

        private List<(double X, double Y)> ConvertArgoContour(
            List<ArgoPoint2D> argoPoints, double beamAxisZ)
        {
            var result = new List<(double X, double Y)>();
            foreach (var p in argoPoints)
            {
                double x = (p.Z - beamAxisZ) * 10;
                double y = p.Y * 10;
                result.Add((x, y));
            }
            return result;
        }

        private bool IsProfilePoint(List<(double X, double Y)> pts, int k)
        {
            if (k == 0 || k == pts.Count - 1) return true;
            var a = pts[k - 1];
            var b = pts[k];
            var c = pts[k + 1];
            double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            return Math.Abs(cross) >= 0.5;
        }

        #endregion

        #region Локальная геометрия сечения без CrossSection.Net

        private sealed class LocalSectionProperties
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public double Yc { get; set; }
            public double Zc { get; set; }
            public double Perimeter { get; set; }
            public double Area { get; set; }
            public double Iyy { get; set; }
            public double Izz { get; set; }
            public double Iyz { get; set; }
            public double It { get; set; }
            public double Syy { get; set; }
            public double Szz { get; set; }
            public double Byy { get; set; }
            public double Bzz { get; set; }
            public double WyyPlus { get; set; }
            public double WyyMinus { get; set; }
            public double WzzPlus { get; set; }
            public double WzzMinus { get; set; }
            public double WeightFactor { get; set; } = 1.0;
            public List<ParisPoint2D> StressPoints { get; set; } = new();
        }

        private LocalSectionProperties BuildLocalSectionProperties(
            List<(double X, double Y)> points,
            int id,
            string name)
        {
            var section = new LocalSectionProperties
            {
                Id = id,
                Name = name,
                WeightFactor = 1.0
            };

            if (points == null || points.Count < 3)
                return section;

            int n = points.Count;
            double signedArea2 = 0.0;
            double cxNum = 0.0;
            double cyNum = 0.0;
            double ixx0 = 0.0;
            double iyy0 = 0.0;
            double ixy0 = 0.0;
            double perimeter = 0.0;

            for (int i = 0; i < n; i++)
            {
                var p1 = points[i];
                var p2 = points[(i + 1) % n];
                double cross = p1.X * p2.Y - p2.X * p1.Y;

                signedArea2 += cross;
                cxNum += (p1.X + p2.X) * cross;
                cyNum += (p1.Y + p2.Y) * cross;

                ixx0 += (p1.Y * p1.Y + p1.Y * p2.Y + p2.Y * p2.Y) * cross;
                iyy0 += (p1.X * p1.X + p1.X * p2.X + p2.X * p2.X) * cross;
                ixy0 += (2 * p1.X * p1.Y + p1.X * p2.Y + p2.X * p1.Y + 2 * p2.X * p2.Y) * cross;

                double dx = p2.X - p1.X;
                double dy = p2.Y - p1.Y;
                perimeter += Math.Sqrt(dx * dx + dy * dy);
            }

            double signedArea = signedArea2 / 2.0;
            double area = Math.Abs(signedArea);
            if (area < 1e-9)
            {
                section.Perimeter = Math.Round(perimeter, 5);
                section.StressPoints = BuildStressPoints(points);
                return section;
            }

            double cx = cxNum / (6.0 * signedArea);
            double cy = cyNum / (6.0 * signedArea);

            double ixxOrigin = ixx0 / 12.0;
            double iyyOrigin = iyy0 / 12.0;
            double ixyOrigin = ixy0 / 24.0;

            // Моменты относительно центра тяжести. Берём модуль, чтобы ориентация обхода
            // контура по/против часовой стрелки не давала отрицательные характеристики.
            double ixxC = Math.Abs(ixxOrigin - signedArea * cy * cy);
            double iyyC = Math.Abs(iyyOrigin - signedArea * cx * cx);
            double ixyC = Math.Abs(ixyOrigin - signedArea * cx * cy);

            double minX = points.Min(p => p.X);
            double maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y);
            double maxY = points.Max(p => p.Y);

            double yPlus = Math.Max(1e-9, maxY - cy);
            double yMinus = Math.Max(1e-9, cy - minY);
            double xPlus = Math.Max(1e-9, maxX - cx);
            double xMinus = Math.Max(1e-9, cx - minX);

            section.Yc = Math.Round(cx, 5);
            section.Zc = Math.Round(cy, 5);
            section.Perimeter = Math.Round(perimeter, 5);
            section.Area = Math.Round(area, 5);

            // Имена полей сохраняем как в старой связке CrossSection.Net:
            // Iyy получал ixx_c, Izz получал iyy_c.
            section.Iyy = Math.Round(ixxC, 7);
            section.Izz = Math.Round(iyyC, 7);
            section.Iyz = Math.Round(ixyC, 7);
            section.It = Math.Round(ixxC + iyyC, 7); // приближённо; точный torsion solver отключён
            section.Syy = 0;
            section.Szz = 0;
            section.Byy = Math.Round(maxX - minX, 5);
            section.Bzz = Math.Round(maxY - minY, 5);
            section.WyyPlus = Math.Round(ixxC / yPlus, 5);
            section.WyyMinus = Math.Round(ixxC / yMinus, 5);
            section.WzzPlus = Math.Round(iyyC / xPlus, 5);
            section.WzzMinus = Math.Round(iyyC / xMinus, 5);
            section.StressPoints = BuildStressPoints(points);

            return section;
        }

        private List<ParisPoint2D> BuildStressPoints(List<(double X, double Y)> points)
        {
            if (points == null || points.Count == 0)
                return new List<ParisPoint2D>();

            double minX = points.Min(p => p.X);
            double maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y);
            double maxY = points.Max(p => p.Y);
            double tolY = Math.Max(1.0, (maxY - minY) / 100.0);
            double tolX = Math.Max(1.0, (maxX - minX) / 100.0);

            var top = points.Where(p => Math.Abs(p.Y - maxY) <= tolY).ToList();
            var bottom = points.Where(p => Math.Abs(p.Y - minY) <= tolY).ToList();
            var left = points.Where(p => Math.Abs(p.X - minX) <= tolX).ToList();
            var right = points.Where(p => Math.Abs(p.X - maxX) <= tolX).ToList();

            (double X, double Y) topLeft = top.OrderBy(p => p.X).FirstOrDefault();
            (double X, double Y) topRight = top.OrderByDescending(p => p.X).FirstOrDefault();
            (double X, double Y) bottomRight = bottom.OrderByDescending(p => p.X).FirstOrDefault();
            (double X, double Y) bottomLeft = bottom.OrderBy(p => p.X).FirstOrDefault();

            // Если какой-то уровень не нашёлся, берём крайние точки по соответствующим координатам.
            if (top.Count == 0) topLeft = left.OrderByDescending(p => p.Y).First();
            if (top.Count == 0) topRight = right.OrderByDescending(p => p.Y).First();
            if (bottom.Count == 0) bottomRight = right.OrderBy(p => p.Y).First();
            if (bottom.Count == 0) bottomLeft = left.OrderBy(p => p.Y).First();

            return new List<ParisPoint2D>
            {
                new ParisPoint2D(topLeft.X, topLeft.Y),
                new ParisPoint2D(topRight.X, topRight.Y),
                new ParisPoint2D(bottomRight.X, bottomRight.Y),
                new ParisPoint2D(bottomLeft.X, bottomLeft.Y)
            };
        }

        #endregion

        #region Section → PrssmSection

        private PrssmSection ConvertSectionToPrssm(
            LocalSectionProperties section,
            Geometry._BaseShape shape,
            int beamIndex,
            double offsetY,
            double offsetZ)
        {
            string sectionName = $"{_sectionBaseName}_Б{beamIndex + 1}";

            var prssmSection = new PrssmSection
            {
                Id = section.Id,
                Name = sectionName,
                SectionType = 1,
                Yc = section.Yc,
                Zc = section.Zc,
                Perimeter = section.Perimeter,
                Area = section.Area,
                Iyy = section.Iyy,
                Izz = section.Izz,
                Iyz = section.Iyz,
                It = section.It,
                Syy = section.Syy,
                Szz = section.Szz,
                Byy = section.Byy,
                Bzz = section.Bzz,
                WyyPlus = section.WyyPlus,
                WyyMinus = section.WyyMinus,
                WzzPlus = section.WzzPlus,
                WzzMinus = section.WzzMinus,
                OffsetType = 11,
                OffsetY = Math.Round(offsetY, 2),
                OffsetZ = Math.Round(offsetZ, 2),
                WeightFactor = section.WeightFactor
            };

            var prssmShape = new PrssmShape
            {
                Id = 1,
                Name = "custom",
                CadType = 0,
                Beta = shape.Beta,
                IsReflectX = shape.IsReflectX,
                IsReflectY = shape.IsReflectY,
                Location = new PrssmPoint(shape.Location.X, shape.Location.Y),
                Area = Math.Round(shape.Area, 5),
                GC = new PrssmPoint(Math.Round(shape.GC.X, 2), Math.Round(shape.GC.Y, 2)),
                SelectedRegionType = (int)shape.SelectedRegionType
            };

            foreach (var region in shape.Profile)
            {
                var prssmRegion = new PrssmProfileRegion
                {
                    RegionType = (int)region.RegionType,
                    Points = new List<PrssmProfilePoint>()
                };

                foreach (var p in region.Points)
                {
                    prssmRegion.Points.Add(new PrssmProfilePoint
                    {
                        X = Math.Round(p.X, 2),
                        Y = Math.Round(p.Y, 2),
                        IsAnchor = p.IsAnchor,
                        IsProfilePoint = p.IsProfilePoint
                    });
                }

                prssmShape.Profile.Add(prssmRegion);
            }

            prssmSection.Shapes.Add(prssmShape);

            prssmSection.StressPoints = section.StressPoints
                .Select(p => new PrssmPoint(Math.Round(p.X, 2), Math.Round(p.Y, 2)))
                .ToList();

            return prssmSection;
        }

        #endregion

        #region Материал

        private PrssmMaterial ConvertConcreteMaterial(GlobalParameters gp)
        {
            var (name, youngModulus, stdType) = GetConcreteClass(gp.ConcreteStrength);
            return new PrssmMaterial
            {
                Id = _materialIdCounter++,
                Name = name,
                MaterialType = 0,
                StandartMaterialType = stdType,
                StandartType = 1,
                YoungModulus = youngModulus,
                PoissonRatio = 0.2,
                SpecificWeight = 2.45E-05,
                ThermalCoefficient = 1E-05
            };
        }

        private (string Name, double YoungModulus, int StdType) GetConcreteClass(double strength)
        {
            if (strength <= 7.5) return ("Б7.5", 16000, 30);
            if (strength <= 10) return ("Б10", 18000, 31);
            if (strength <= 12.5) return ("Б12.5", 21000, 32);
            if (strength <= 15) return ("Б15", 23000, 33);
            if (strength <= 17.5) return ("Б17.5", 25500, 34);
            if (strength <= 20) return ("Б20", 27000, 35);
            if (strength <= 22.5) return ("Б22.5", 28500, 36);
            if (strength <= 25) return ("Б25", 30000, 37);
            if (strength <= 27.5) return ("Б27.5", 31500, 38);
            if (strength <= 30) return ("Б30", 32500, 39);
            if (strength <= 35) return ("Б35", 34500, 40);
            if (strength <= 40) return ("Б40", 36000, 41);
            if (strength <= 45) return ("Б45", 37500, 42);
            if (strength <= 50) return ("Б50", 38000, 43);
            if (strength <= 55) return ("Б55", 39000, 44);
            return ("Б60", 40000, 45);
        }

        private PrssmMaterial ConvertReinforcementMaterial(double reinforcementType)
        {
            var (name, youngModulus, stdType) = GetReinforcementClass(reinforcementType);
            return new PrssmMaterial
            {
                Id = _materialIdCounter++,
                Name = name,
                MaterialType = 2,
                StandartMaterialType = stdType,
                StandartType = 1,
                YoungModulus = youngModulus,
                PoissonRatio = 0.3,
                SpecificWeight = 0.00007698,
                ThermalCoefficient = 0.000012
            };
        }

        private (string Name, double YoungModulus, int StdType) GetReinforcementClass(double type)
        {
            if (type == 0.0) return ("A240", 210000, 1);
            return ("A300", 210000, 2);
        }

        #endregion

        #region Продольная арматура

        private void ConvertLongitudinalReinforcement(
            Beam argoBeam, ArgoDocument argoDoc,
            PrssmBeam prssmBeam,
            List<ParisCadPoint2D> profilePoints,
            PrssmPoint bindingPointGlobal,
            ParisPoint2D llLocal,
            double profMinY, double ribTopY,
            double ribAxisXLocal,
            double ribWidth)
        {
            var detailed = argoDoc.DetailedReinforcement?.BeamDetails
                .FirstOrDefault(d => d.BeamNumber == argoBeam.Number);

            if (detailed == null || (detailed.TensileBars.Count == 0 && detailed.CompressedBars.Count == 0))
            {
                ConvertLongFromCalc(argoBeam, argoDoc, prssmBeam, llLocal, bindingPointGlobal,
                    profMinY, ribTopY, ribAxisXLocal, ribWidth);
                return;
            }

            double ribWidthMm = ribWidth;
            var supportExtra = (argoDoc.GlobalParams.FullLength - (argoDoc.GlobalParams.SupportAxis2 - argoDoc.GlobalParams.SupportAxis1)) / 2;
            //var bendsIndex = -1;
            // РАСТЯНУТЫЕ
            for (int j = 0; j < detailed.TensileBars.Count && j < argoBeam.TensileBars.Count; j++)
            {
                var dg = detailed.TensileBars[j];
                var cb = argoBeam.TensileBars[j];
                var bends = argoBeam.Bends.FirstOrDefault(x =>
                    Math.Abs(x.Area - cb.Area) < 0.01 &&
                    Math.Abs(x.LowerCoordinate - cb.XMin) < 0.1);

                if (dg.Count <= 0) continue;

                double yFirstAbs = 0;
                double stepElement = 0;

                if (dg.ZCoordinates.Count > 0)
                {
                    var sortedZ = dg.ZCoordinates.OrderBy(z => z).ToList();
                    double zScale = DetectZScale(sortedZ, ribWidthMm);

                    double firstX = ribAxisXLocal + sortedZ.First() * zScale;
                    double lastX = ribAxisXLocal + sortedZ.Last() * zScale;

                    yFirstAbs = firstX;
                    stepElement = (dg.Count > 1) ? (lastX - firstX) / (dg.Count - 1) : 0;
                }

                //double zAbs = profMinY + (cb.DeltaLower * 10);

                double yOffset = yFirstAbs - llLocal.X;
                double zOffset = cb.DeltaLower * 10 /*zAbs - profMinY*/;
                double xOffset = (supportExtra + cb.XMin) * 10;

                var bendSegment = new PrssmReinforcementSegment { IsFirst = true };
                if (bends != null)
                {
                    var _l = (bends.LowerCoordinate - bends.UpperCoordinate) * 10;
                    var _h = ribTopY - bends.DeltaUpper * 10 - profMinY - bends.DeltaLower * 10;
                    bendSegment.Length = Math.Sqrt(_l * _l + _h * _h);
                    bendSegment.Angle = -Math.Atan(_h / _l) * 180 / Math.PI;
                    bendSegment.Height = _h;
                    zOffset += _h;
                    xOffset = (supportExtra + bends.UpperCoordinate) * 10;
                }

                prssmBeam.ReinforcementLongitudinals.Add(new PrssmLongitudinalReinforcement
                {
                    Diameter = dg.Diameter,
                    ItemsAtRow = dg.Count,
                    Material = ConvertReinforcementMaterial(argoDoc.GlobalParams.TensileReinforcementType),
                    NAtItem = 1,
                    BindingPoint = bindingPointGlobal,
                    YOffset = Math.Round(yOffset, 2),
                    ZOffset = Math.Round(zOffset, 2),
                    StepElement = Math.Round(stepElement, 2),
                    OffsetFromStart = xOffset,
                    SegmentCount = bends == null ? 1 : 3,
                    Segments = new List<PrssmReinforcementSegment>
                    {
                        new PrssmReinforcementSegment { Length = (cb.XMax - cb.XMin) * 10, IsFirst = true }
                    }
                });
                if (bends != null)
                {
                    prssmBeam.ReinforcementLongitudinals.Last().Segments.Insert(0, bendSegment);
                    prssmBeam.ReinforcementLongitudinals.Last().Segments.Add(new PrssmReinforcementSegment() { Angle = -bendSegment.Angle, Length = bendSegment.Length, Height = bendSegment.Height });
                    //bendsIndex++;
                }
            }

            // СЖАТЫЕ
            for (int j = 0; j < detailed.CompressedBars.Count && j < argoBeam.CompressedBars.Count; j++)
            {
                var dg = detailed.CompressedBars[j];
                var cb = argoBeam.CompressedBars[j];
                if (dg.Count <= 0) continue;

                double yFirstAbs = 0;
                double stepElement = 0;

                if (dg.ZCoordinates.Count > 0)
                {
                    var sortedZ = dg.ZCoordinates.OrderBy(z => z).ToList();
                    double zScale = DetectZScale(sortedZ, ribWidthMm);

                    double firstX = ribAxisXLocal + sortedZ.First() * zScale;
                    double lastX = ribAxisXLocal + sortedZ.Last() * zScale;

                    yFirstAbs = firstX;
                    stepElement = (dg.Count > 1) ? (lastX - firstX) / (dg.Count - 1) : 0;
                }

                // Для сжатой арматуры - от верха РЕБРА (не всего профиля!)
                double zAbs = ribTopY - (cb.DeltaUpper * 10);

                double yOffset = yFirstAbs - llLocal.X;
                double zOffset = zAbs - llLocal.Y;

                prssmBeam.ReinforcementLongitudinals.Add(new PrssmLongitudinalReinforcement
                {
                    Diameter = dg.Diameter,
                    ItemsAtRow = dg.Count,
                    Material = ConvertReinforcementMaterial(argoDoc.GlobalParams.CompressedReinforcementType),
                    NAtItem = 1,
                    BindingPoint = bindingPointGlobal,
                    YOffset = Math.Round(yOffset, 2),
                    ZOffset = Math.Round(zOffset, 2),
                    StepElement = Math.Round(stepElement, 2),
                    OffsetFromStart = Math.Max(0, cb.XMin * 10),
                    SegmentCount = 1,
                    Segments = new List<PrssmReinforcementSegment>
                    {
                        new PrssmReinforcementSegment { Length = (cb.XMax - cb.XMin) * 10, IsFirst = true }
                    }
                });
            }
        }

        private void ConvertLongFromCalc(
            Beam argoBeam,
            ArgoDocument argoDoc,
            PrssmBeam prssmBeam,
            ParisPoint2D llLocal,
            PrssmPoint bindingPointGlobal,
            double profMinY,
            double ribTopY,
            double ribAxisXLocal,
            double ribWidth)
        {
            foreach (var cb in argoBeam.TensileBars)
            {
                if (cb.Area <= 0) continue;
                var (d, n) = EstDiamCount(cb.Area);

                double usableWidth = Math.Max(0, ribWidth - 2 * 25 - d);
                double stepEl = (n > 1) ? usableWidth / (n - 1) : 0;

                double firstX = ribAxisXLocal - usableWidth / 2.0;
                double zAbs = profMinY + (cb.DeltaLower * 10);

                prssmBeam.ReinforcementLongitudinals.Add(new PrssmLongitudinalReinforcement
                {
                    Diameter = d,
                    ItemsAtRow = n,
                    NAtItem = 1,
                    Material = ConvertReinforcementMaterial(argoDoc.GlobalParams.TensileReinforcementType),
                    BindingPoint = bindingPointGlobal,
                    YOffset = Math.Round(firstX - llLocal.X, 2),
                    ZOffset = Math.Round(zAbs - llLocal.Y, 2),
                    StepElement = Math.Round(stepEl, 2),
                    OffsetFromStart = Math.Max(0, cb.XMin * 10),
                    SegmentCount = 1,
                    Segments = new List<PrssmReinforcementSegment>
                    {
                        new PrssmReinforcementSegment { Length = (cb.XMax - cb.XMin) * 10, IsFirst = true }
                    }
                });
            }

            foreach (var cb in argoBeam.CompressedBars)
            {
                if (cb.Area <= 0) continue;
                var (d, n) = EstDiamCount(cb.Area);

                double usableWidth = Math.Max(0, ribWidth - 2 * 25 - d);
                double stepEl = (n > 1) ? usableWidth / (n - 1) : 0;

                double firstX = ribAxisXLocal - usableWidth / 2.0;
                // Для сжатой арматуры - от верха РЕБРА
                double zAbs = ribTopY - (cb.DeltaUpper * 10);

                prssmBeam.ReinforcementLongitudinals.Add(new PrssmLongitudinalReinforcement
                {
                    Diameter = d,
                    ItemsAtRow = n,
                    NAtItem = 1,
                    Material = ConvertReinforcementMaterial(argoDoc.GlobalParams.CompressedReinforcementType),
                    BindingPoint = bindingPointGlobal,
                    YOffset = Math.Round(firstX - llLocal.X, 2),
                    ZOffset = Math.Round(zAbs - llLocal.Y, 2),
                    StepElement = Math.Round(stepEl, 2),
                    OffsetFromStart = Math.Max(0, cb.XMin * 10),
                    SegmentCount = 1,
                    Segments = new List<PrssmReinforcementSegment>
                    {
                        new PrssmReinforcementSegment { Length = (cb.XMax - cb.XMin) * 10, IsFirst = true }
                    }
                });
            }
        }

        #endregion

        #region Поперечная арматура

        private void ConvertTransverseReinforcement(
    Beam argoBeam, ArgoDocument argoDoc, PrssmBeam prssmBeam,
    IEnumerable<ParisCadPoint2D> profilePoints,
    PrssmPoint bindingPoint,
    ParisPoint2D llLocal,
    double ribWidth,
    double profMinY,
    double ribTopY)
        {
            if (argoBeam.StirrupSections.Count == 0) return;

            // Высота ребра (от низа до верха ребра)
            double ribH = ribTopY - profMinY;

            const double coverSide = 25;
            const double coverBottom = 25;

            // В АРГО для хомутов EndX — это НЕ длина участка, а координата X конца участка
            // в сантиметрах от начала пролетного строения. Поэтому нельзя суммировать EndX
            // и центрировать блок хомутов: для N4_1200.57D это давало отрицательный старт
            // и хомуты вылезали слева за балку.
            double previousEndX_cm = 0.0;

            var detailed = argoDoc.DetailedReinforcement?.BeamDetails
                .FirstOrDefault(d => d.BeamNumber == argoBeam.Number);

            foreach (var ss in argoBeam.StirrupSections)
            {
                double sectionStartX_cm = previousEndX_cm;
                double sectionEndX_cm = ss.EndX;

                // Защита от нестандартного/битого ввода: координаты конца участков должны возрастать.
                if (sectionEndX_cm < sectionStartX_cm)
                    sectionEndX_cm = sectionStartX_cm;

                double sectionLength_cm = sectionEndX_cm - sectionStartX_cm;
                double offsetFromStart_mm = sectionStartX_cm * 10.0;
                double sectionLength_mm = sectionLength_cm * 10.0;

                previousEndX_cm = ss.EndX;

                if (ss.Area <= 0 || ss.Step <= 0 || sectionLength_mm <= 0)
                    continue;

                int longitudinalCountForBranches = detailed?.TensileBars
                    .Where(x => x.Count > 0)
                    .Select(x => x.Count)
                    .DefaultIfEmpty(2)
                    .Max() ?? 2;

                (double d, int branches) = EstStirrupDiam(ss.Area, longitudinalCountForBranches);

                double step_mm = ss.Step * 10.0;

                // Количество хомутов на данном участке. В АРГО шаг задан в см, EndX — абсолютная
                // координата конца участка. Берем только длину текущего участка.
                int cnt = Math.Max(1, (int)Math.Floor(sectionLength_mm / step_mm) + 1);

                double stirrupH = ribH - 2 * coverBottom;

                for (int i = 1; i <= branches / 2; i++)
                {
                    // Замкнутый прямоугольный хомут - 3 сегмента
                    prssmBeam.ReinforcementTransverses.Add(
                        new PrssmTransverseReinforcement
                        {
                            Diameter = d,
                            ItemsAtRow = cnt,
                            NAtItem = 1,
                            Material = ConvertReinforcementMaterial(argoDoc.GlobalParams.StirrupType),
                            IsClosed = true,
                            StepElement = step_mm,
                            OffsetFromStart = offsetFromStart_mm,
                            YOffset = coverSide * i,
                            ZOffset = stirrupH + coverBottom,
                            BindingPoint = bindingPoint,
                            SegmentCount = 3,
                            Segments = new List<PrssmReinforcementSegment>
                            {
                            // Левая сторона - вниз
                            new PrssmReinforcementSegment { Length = stirrupH, Angle = 270, IsFirst = true },
                            // Низ - горизонтально вправо
                            new PrssmReinforcementSegment { Length = ribWidth - 2 * coverSide * i, Angle = 0 },
                            // Правая сторона - вверх
                            new PrssmReinforcementSegment { Length = stirrupH, Angle = 90 },
                            }
                        });
                }
            }
        }

        private void ConvertPlateTopReinforcementAsTransverse(
            Beam argoBeam,
            ArgoDocument argoDoc,
            PrssmBeam prssmBeam,
            PrssmPoint bindingPoint,
            ParisPoint2D llLocal,
            double beamAxisZ_cm,
            double sectionGcX_mm,
            double sectionGcY_mm,
            bool mirrored,
            double beamLengthMm,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double ribTopY)
        {
            var slabReinf = argoBeam.SlabReinforcement;
            if (slabReinf == null || slabReinf.CalculatedBars.Count == 0)
                return;

            var contour = BuildPlateTopContour(
                slabReinf.CalculatedBars,
                beamAxisZ_cm,
                sectionGcX_mm,
                sectionGcY_mm,
                mirrored,
                concreteContour,
                ribTopY,
                argoBeam.Number > 1 && argoBeam.Number < argoDoc.GlobalParams.BeamCount);

            if (contour.Count < 3)
                return;

            double diameter;
            double stepMm;
            GetPlateBarDiameterAndStep(argoBeam, argoDoc, out diameter, out stepMm);

            if (diameter <= 0 || stepMm <= 0)
                return;

            int startIndex = FindPlateContourStartIndex(contour);
            var ordered = new List<(double X, double Y)>();
            for (int i = 0; i < contour.Count; i++)
                ordered.Add(contour[(startIndex + i) % contour.Count]);

            var start = ordered[0];
            var segments = new List<PrssmReinforcementSegment>();

            // Для сложного контура плитной арматуры добавляем ВСЕ стороны,
            // включая последнюю сторону к начальной точке. На прямоугольных
            // хомутах Paris нормально работает с IsClosed=true и автозамыканием,
            // но для ломаной верхней арматуры плиты автозамыкание на бортике
            // иногда визуально теряется. Явная последняя сторона устраняет
            // разрыв, а IsClosed=true оставляем, чтобы элемент считался
            // замкнутым контуром.
            for (int i = 0; i < ordered.Count; i++)
            {
                var a = ordered[i];
                var b = ordered[(i + 1) % ordered.Count];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1.0)
                    continue;

                segments.Add(new PrssmReinforcementSegment
                {
                    Length = Math.Round(len, 2),
                    Angle = Math.Round(NormalizeAngle(Math.Atan2(dy, dx) * 180.0 / Math.PI), 3),
                    IsFirst = segments.Count == 0
                });
            }

            if (segments.Count < 3)
                return;

            int itemsAtRow = Math.Max(1, (int)Math.Floor(beamLengthMm / stepMm) + 1);

            prssmBeam.ReinforcementTransverses.Add(new PrssmTransverseReinforcement
            {
                Name = "Верхняя арматура плиты",
                Diameter = diameter,
                ItemsAtRow = itemsAtRow,
                NAtItem = 1,
                Material = ConvertReinforcementMaterial(argoDoc.GlobalParams.SlabReinforcementType),
                IsClosed = true,
                StepElement = Math.Round(stepMm, 2),
                OffsetFromStart = 0,
                YOffset = Math.Round(start.X - llLocal.X, 2),
                ZOffset = Math.Round(start.Y - llLocal.Y, 2),
                BindingPoint = bindingPoint,
                SegmentCount = segments.Count,
                Segments = segments
            });
        }

        private List<(double X, double Y)> BuildPlateTopContour(
            IReadOnlyList<CalculatedBar> calculatedBars,
            double beamAxisZ_cm,
            double sectionGcX_mm,
            double sectionGcY_mm,
            bool mirrored,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double ribTopY,
            bool isInnerBeam)
        {
            var points = new List<(double X, double Y)>();

            if (concreteContour == null || concreteContour.Count < 3)
                return points;

            // Минимальный защитный слой для превращённой плитной арматуры.
            // Если точки АРГО лежат на грани бетона или чуть выходят наружу,
            // мы не оставляем их на поверхности, а переносим внутрь сечения.
            const double plateCoverMm = 30.0;

            // Берём верхнюю плитную арматуру не точками по отдельности, а целыми
            // расчетными стержнями. Внутренние балки часто имеют нижний V-образный
            // провал плитного стержня чуть ниже ribTopY - 250 мм. Если фильтровать
            // каждую точку отдельно, эта нижняя точка теряется, и контур превращается
            // в почти прямоугольный. Поэтому правило такое:
            //   1) стержень считается плитным, если хотя бы одна его точка лежит в
            //      верхней зоне;
            //   2) после этого сохраняем все точки этого стержня, кроме явно слишком
            //      низких, чтобы не захватить арматуру ребра.
            double minPlateY = ribTopY - 250.0;
            double hardMinPlateY = ribTopY - 450.0;

            // Для внутренних балок нижний V часто задан одним отдельным
            // расчетным стержнем плиты. Если затем строить общий convex/envelope,
            // промежуточные точки этого V могут исчезнуть. Поэтому дополнительно
            // сохраняем исходные полилинии стержней и для внутренних балок ниже
            // строим контур по двум ветвям: верхней и нижней фактической.
            var selectedRawPolylines = new List<List<(double X, double Y)>>();

            foreach (var bar in calculatedBars)
            {
                if (bar == null || bar.Area <= 0 || bar.BendPoints == null || bar.BendPoints.Count < 2)
                    continue;

                var mapped = new List<(double X, double Y)>();
                foreach (var p in bar.BendPoints)
                {
                    double x = (p.Z - beamAxisZ_cm) * 10.0;
                    double y = p.Y * 10.0;

                    if (mirrored)
                        x = -x;

                    x -= sectionGcX_mm;
                    y -= sectionGcY_mm;

                    if (double.IsFinite(x) && double.IsFinite(y))
                        mapped.Add((x, y));
                }

                if (mapped.Count < 2)
                    continue;

                // Если у стержня нет ни одной точки в верхней зоне плиты, это не та
                // арматура, которую сейчас переносим как верхнюю плитную поперечную.
                if (!mapped.Any(p => p.Y >= minPlateY))
                    continue;

                var keptRaw = mapped
                    .Where(p => p.Y >= hardMinPlateY)
                    .ToList();
                if (keptRaw.Count >= 2)
                    selectedRawPolylines.Add(keptRaw);

                foreach (var rawPt in mapped)
                {
                    // Мягкий нижний предел нужен именно для сохранения V-образного
                    // провала внутренних балок. Значение 450 мм достаточно для этих
                    // старых типовых сечений, но всё ещё отсеивает заведомо нижние
                    // элементы ребра.
                    if (rawPt.Y < hardMinPlateY)
                        continue;

                    var pt = rawPt;

                    // Точки АРГО иногда попадают прямо на грань или чуть за неё из-за
                    // округления. Снаружи бетонного контура такие точки нельзя оставлять:
                    // позже Paris нарисует арматуру в воздухе. Поэтому сначала возвращаем
                    // точку внутрь бетона, а затем выдерживаем защитный слой от границы.
                    pt = EnsureConcreteCover(pt, concreteContour, plateCoverMm);

                    if (PointInsideOrOnPolygon(pt, concreteContour, 1.0))
                        points.Add(pt);
                }
            }

            points = MergeClosePoints(points, 10.0);
            if (points.Count < 3)
                return new List<(double X, double Y)>();

            // Для внутренних балок сначала пробуем собрать контур из реальных
            // ветвей АРГО: верхняя горизонтальная ветвь + нижняя V-образная
            // полилиния. Это сохраняет промежуточные точки V, которые терялись
            // при построении общей огибающей. Если эвристика не сработала,
            // возвращаемся к старой огибающей.
            var hull = isInnerBeam
                ? BuildInnerPlateContourFromRawPolylines(selectedRawPolylines)
                : new List<(double X, double Y)>();

            if (hull.Count < 3)
                hull = BuildTopBottomEnvelopeContour(points);

            hull = SimplifyCollinear(hull, 2.0);

            // Главная правка: не разрешаем ребрам контура выходить за бетон.
            // Если прямой отрезок между двумя точками проходит вне сечения,
            // заменяем его маршрутом по ближайшей границе бетонного контура.
            var constrained = ConstrainClosedPolylineToConcrete(hull, concreteContour);

            // Если часть маршрута была проложена по границе бетона, уводим её
            // внутрь на защитный слой. Это убирает арматуру, лежащую прямо на
            // поверхности борта/плиты.
            constrained = ApplyConcreteCover(constrained, concreteContour, plateCoverMm);
            constrained = RegularizePlateRebarContour(constrained, concreteContour, plateCoverMm);
            constrained = MergeClosePoints(constrained, 5.0);
            constrained = SimplifyCollinear(constrained, 3.0);

            // Смещение отдельных точек внутрь бетона иногда делает соседний отрезок
            // чуть наружным на наклонной грани (типичный край N4_1500). После offset-а
            // ещё раз проверяем саму ломаную и, если нужно, возвращаем её на допустимый
            // маршрут по бетонному контуру.
            if (!ClosedPolylineInsideConcrete(constrained, concreteContour, 1.0))
            {
                constrained = ConstrainClosedPolylineToConcrete(constrained, concreteContour);
                constrained = ApplyConcreteCover(constrained, concreteContour, plateCoverMm);
                constrained = MergeClosePoints(constrained, 5.0);
                constrained = SimplifyCollinear(constrained, 3.0);
            }

            if (!ClosedPolylineInsideConcrete(constrained, concreteContour, 1.0))
            {
                // На острых/тонких участках 30 мм может быть слишком много.
                // Пробуем меньший защитный слой, но всё равно не оставляем контур
                // на самой поверхности бетона.
                constrained = ApplyConcreteCover(constrained, concreteContour, 20.0);
                constrained = RegularizePlateRebarContour(constrained, concreteContour, 20.0);
                constrained = MergeClosePoints(constrained, 5.0);
                constrained = SimplifyCollinear(constrained, 3.0);
            }

            // Геометрические доводки внутренней плитной арматуры делаем в самом конце.
            // Иначе запасной проход с уменьшенным защитным слоем заново перестраивает
            // контур и стирает горизонтальную полку/сдвиг начала скоса.
            if (isInnerBeam)
            {
                constrained = FlattenInnerPlateSideShelves(constrained);
                constrained = AlignInnerPlateShelvesToConcreteHaunch(constrained, concreteContour);
            }

            if (!ClosedPolylineInsideConcrete(constrained, concreteContour, 1.0))
            {
                Console.WriteLine("  WARNING: верхняя арматура плиты не прошла проверку по контуру бетона; группа пропущена");
                return new List<(double X, double Y)>();
            }

            return constrained;
        }

        private void GetPlateBarDiameterAndStep(
            Beam argoBeam,
            ArgoDocument argoDoc,
            out double diameter,
            out double stepMm)
        {
            diameter = 0;
            stepMm = 0;

            var detailed = argoDoc.DetailedReinforcement?.BeamDetails
                .FirstOrDefault(d => d.BeamNumber == argoBeam.Number);

            var detailedPlate = detailed?.PlateBars
                .Where(x => x.Diameter > 0 && x.Step > 0)
                .ToList();

            if (detailedPlate != null && detailedPlate.Count > 0)
            {
                // Если в АРГО несколько плитных групп, для единого очищенного контура
                // берём наиболее консервативную комбинацию: максимальный диаметр и
                // минимальный шаг.
                diameter = detailedPlate.Max(x => x.Diameter);
                stepMm = detailedPlate.Min(x => x.Step) * 10.0; // см -> мм
                return;
            }

            double areaCm2PerM = argoBeam.SlabReinforcement?.CalculatedBars
                .Where(x => x.Area > 0)
                .Select(x => x.Area)
                .DefaultIfEmpty(0)
                .Max() ?? 0;

            if (areaCm2PerM <= 0)
            {
                diameter = 10;
                stepMm = 200;
                return;
            }

            (diameter, stepMm) = EstimatePlateDiameterStep(areaCm2PerM);
        }

        private (double Diameter, double StepMm) EstimatePlateDiameterStep(double areaCm2PerM)
        {
            int[] diameters = { 6, 8, 10, 12, 14, 16 };
            int[] steps = { 100, 125, 150, 200, 250, 300 };

            double bestD = 10;
            double bestS = 200;
            double bestScore = double.PositiveInfinity;

            foreach (int d in diameters)
            {
                double barAreaMm2 = Math.PI * d * d / 4.0;
                foreach (int s in steps)
                {
                    double areaMm2PerM = barAreaMm2 * 1000.0 / s;
                    double areaCm2PerMCandidate = areaMm2PerM / 100.0;
                    double score = Math.Abs(areaCm2PerMCandidate - areaCm2PerM);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestD = d;
                        bestS = s;
                    }
                }
            }

            return (bestD, bestS);
        }

        private int FindPlateContourStartIndex(IReadOnlyList<(double X, double Y)> contour)
        {
            // Стартуем с верхней левой точки. Так контур обычно идёт вокруг верхней
            // плитной зоны предсказуемо, а YOffset/ZOffset проще проверить визуально.
            int index = 0;
            for (int i = 1; i < contour.Count; i++)
            {
                var p = contour[i];
                var best = contour[index];
                if (p.Y > best.Y + 1e-6 || (Math.Abs(p.Y - best.Y) < 1e-6 && p.X < best.X))
                    index = i;
            }
            return index;
        }

        /// <summary>
        /// У внутренних балок после увода точек от бетонной границы крайняя короткая
        /// полка иногда получает небольшой наклон (несколько миллиметров), хотя в АРГО
        /// это именно горизонтальный участок перед уходом в V.
        ///
        /// Здесь выравниваем только такие короткие приграничные полки:
        /// - один сосед почти вертикально замыкается на верхнюю ветвь;
        /// - второй сосед уходит длинной диагональю в V;
        /// - сам участок короткий и почти горизонтальный.
        /// </summary>
        private List<(double X, double Y)> FlattenInnerPlateSideShelves(List<(double X, double Y)> points)
        {
            if (points == null || points.Count < 4)
                return points;

            var result = new List<(double X, double Y)>(points);

            for (int i = 0; i < result.Count; i++)
            {
                int prevIndex = (i - 1 + result.Count) % result.Count;
                int nextIndex = (i + 1) % result.Count;

                var prev = result[prevIndex];
                var curr = result[i];
                var next = result[nextIndex];

                double leftDx = curr.X - prev.X;
                double leftDy = curr.Y - prev.Y;
                double rightDx = next.X - curr.X;
                double rightDy = next.Y - curr.Y;

                bool prevIsVertical = Math.Abs(leftDx) <= 8.0 && Math.Abs(leftDy) >= 40.0;
                bool currToNextIsShortShelf =
                    Math.Abs(rightDx) >= 20.0 &&
                    Math.Abs(rightDx) <= 140.0 &&
                    Math.Abs(rightDy) <= 12.0;
                bool nextThenFallsIntoV =
                    Distance(next, result[(nextIndex + 1) % result.Count]) >= 120.0 &&
                    result[(nextIndex + 1) % result.Count].Y < next.Y - 40.0;

                if (prevIsVertical && currToNextIsShortShelf && nextThenFallsIntoV)
                {
                    result[nextIndex] = (next.X, curr.Y);
                    continue;
                }

                bool nextIsVertical = Math.Abs(rightDx) <= 8.0 && Math.Abs(rightDy) >= 40.0;
                bool prevToCurrIsShortShelf =
                    Math.Abs(leftDx) >= 20.0 &&
                    Math.Abs(leftDx) <= 140.0 &&
                    Math.Abs(leftDy) <= 12.0;
                bool prevThenFallsIntoV =
                    Distance(prev, result[(prevIndex - 1 + result.Count) % result.Count]) >= 120.0 &&
                    result[(prevIndex - 1 + result.Count) % result.Count].Y < prev.Y - 40.0;

                if (nextIsVertical && prevToCurrIsShortShelf && prevThenFallsIntoV)
                {
                    result[prevIndex] = (prev.X, curr.Y);
                }
            }

            // Второй, более прямой проход: после offset-а от грани бетона крайняя
            // полка иногда остаётся почти горизонтальной, но один её конец выше на
            // несколько миллиметров. Для внутренних балок это именно технологическая
            // полка перед V, поэтому оба конца приводим к нижней из двух отметок.
            for (int i = 0; i < result.Count; i++)
            {
                int nextIndex = (i + 1) % result.Count;
                var a = result[i];
                var b = result[nextIndex];

                double dx = Math.Abs(b.X - a.X);
                double dy = Math.Abs(b.Y - a.Y);
                if (dx < 20.0 || dx > 140.0 || dy > 12.0)
                    continue;

                var before = result[(i - 1 + result.Count) % result.Count];
                var after = result[(nextIndex + 1) % result.Count];

                bool oneSideVertical =
                    (Math.Abs(a.X - before.X) <= 8.0 && Math.Abs(a.Y - before.Y) >= 40.0) ||
                    (Math.Abs(after.X - b.X) <= 8.0 && Math.Abs(after.Y - b.Y) >= 40.0);

                bool oneSideFallsIntoV =
                    (Distance(b, after) >= 120.0 && after.Y < b.Y - 40.0) ||
                    (Distance(before, a) >= 120.0 && before.Y < a.Y - 40.0);

                if (!oneSideVertical || !oneSideFallsIntoV)
                    continue;

                double shelfY = Math.Min(a.Y, b.Y);
                result[i] = (a.X, shelfY);
                result[nextIndex] = (b.X, shelfY);
            }

            return result;
        }

        /// <summary>
        /// У внутренних балок после выравнивания крайней полки остаётся ещё один
        /// геометрический нюанс: переход полка -> V может начинаться слишком рано.
        /// Тогда диагональ плитной арматуры получается круче/пологее бетонного вута.
        ///
        /// Здесь двигаем только точку конца короткой полки так, чтобы следующий
        /// участок к V шёл параллельно ближайшему наклонному участку бетонного
        /// контура. В результате полка продолжается чуть дальше, а скос повторяет
        /// направление самого вута.
        /// </summary>
        private List<(double X, double Y)> AlignInnerPlateShelvesToConcreteHaunch(
            List<(double X, double Y)> points,
            IReadOnlyList<(double X, double Y)> concreteContour)
        {
            if (points == null || points.Count < 4 || concreteContour == null || concreteContour.Count < 3)
                return points;

            var result = new List<(double X, double Y)>(points);
            var concreteSlopes = new List<((double X, double Y) A, (double X, double Y) B)>();

            for (int i = 0; i < concreteContour.Count; i++)
            {
                var a = concreteContour[i];
                var b = concreteContour[(i + 1) % concreteContour.Count];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                if (Math.Abs(dx) >= 40.0 && Math.Abs(dy) >= 40.0)
                    concreteSlopes.Add((a, b));
            }

            if (concreteSlopes.Count == 0)
                return result;

            for (int i = 0; i < result.Count; i++)
            {
                int nextIndex = (i + 1) % result.Count;
                var a = result[i];
                var b = result[nextIndex];

                double shelfDx = b.X - a.X;
                double shelfDy = b.Y - a.Y;
                if (Math.Abs(shelfDx) < 20.0 || Math.Abs(shelfDx) > 160.0 || Math.Abs(shelfDy) > 2.0)
                    continue;

                int prevIndex = (i - 1 + result.Count) % result.Count;
                int afterIndex = (nextIndex + 1) % result.Count;
                var prev = result[prevIndex];
                var after = result[afterIndex];

                bool aNearVerticalSide = Math.Abs(a.X - prev.X) <= 8.0 && Math.Abs(a.Y - prev.Y) >= 40.0;
                bool bLeadsIntoV = Distance(b, after) >= 120.0 && after.Y < b.Y - 40.0;

                bool bNearVerticalSide = Math.Abs(after.X - b.X) <= 8.0 && Math.Abs(after.Y - b.Y) >= 40.0;
                bool aLeadsIntoV = Distance(prev, a) >= 120.0 && prev.Y < a.Y - 40.0;

                if (!((aNearVerticalSide && bLeadsIntoV) || (bNearVerticalSide && aLeadsIntoV)))
                    continue;

                int sideIndex;
                int transitionIndex;
                int apexIndex;

                if (aNearVerticalSide && bLeadsIntoV)
                {
                    sideIndex = i;
                    transitionIndex = nextIndex;
                    apexIndex = afterIndex;
                }
                else
                {
                    sideIndex = nextIndex;
                    transitionIndex = i;
                    apexIndex = prevIndex;
                }

                var side = result[sideIndex];
                var transition = result[transitionIndex];
                var apex = result[apexIndex];

                var nearestSlope = concreteSlopes
                    .OrderBy(s =>
                    {
                        double midX = (s.A.X + s.B.X) / 2.0;
                        return Math.Abs(midX - side.X);
                    })
                    .First();

                double slopeDx = nearestSlope.B.X - nearestSlope.A.X;
                double slopeDy = nearestSlope.B.Y - nearestSlope.A.Y;
                double slopeAbs = Math.Abs(slopeDy / slopeDx);
                if (slopeAbs < 1e-6)
                    continue;

                double verticalDrop = Math.Abs(transition.Y - apex.Y);
                double desiredDx = verticalDrop / slopeAbs;
                double directionToApex = Math.Sign(apex.X - side.X);
                if (directionToApex == 0)
                    continue;

                double newX = apex.X - directionToApex * desiredDx;
                double minX = Math.Min(side.X, apex.X);
                double maxX = Math.Max(side.X, apex.X);
                if (newX <= minX + 5.0 || newX >= maxX - 5.0)
                    continue;

                result[transitionIndex] = (newX, transition.Y);
            }

            return result;
        }

        private List<(double X, double Y)> MergeClosePoints(List<(double X, double Y)> points, double tol)
        {
            var result = new List<(double X, double Y)>();
            foreach (var p in points)
            {
                bool exists = false;
                for (int i = 0; i < result.Count; i++)
                {
                    if (Distance(result[i], p) <= tol)
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    result.Add(p);
            }
            return result;
        }


        /// <summary>
        /// Нормализованная ветвь плитной арматуры во внутренней балке.
        /// </summary>
        private sealed class PlateBranchCandidate
        {
            public List<(double X, double Y)> Points { get; set; } = new();
            public double MinX { get; set; }
            public double MaxX { get; set; }
            public double MinY { get; set; }
            public double MaxY { get; set; }
            public double AvgY { get; set; }
        }

        /// <summary>
        /// Специальная сборка контура плитной арматуры для внутренних балок.
        ///
        /// В АРГО нижний V у внутренних балок обычно является отдельным расчетным
        /// стержнем с 3 точками. Общая огибающая может выкинуть его промежуточную
        /// точку. Здесь мы явно выбираем:
        ///   - верхнюю ветвь: самый высокий длинный почти горизонтальный стержень;
        ///   - нижнюю ветвь: стержень с минимальной Y, сохраняя все его точки.
        /// Потом добавляем нижние концевые анкеры у концов верхней ветви, если они
        /// есть в исходных данных. Это дает замкнутый контур без потери V.
        /// </summary>
        private List<(double X, double Y)> BuildInnerPlateContourFromRawPolylines(
            IReadOnlyList<List<(double X, double Y)>> polylines)
        {
            var result = new List<(double X, double Y)>();
            if (polylines == null || polylines.Count == 0)
                return result;

            var candidates = polylines
                .Where(pl => pl != null && pl.Count >= 2)
                .Select(pl => new PlateBranchCandidate
                {
                    Points = RemoveSequentialDuplicates(pl, 1.0),
                    MinX = pl.Min(p => p.X),
                    MaxX = pl.Max(p => p.X),
                    MinY = pl.Min(p => p.Y),
                    MaxY = pl.Max(p => p.Y),
                    AvgY = pl.Average(p => p.Y)
                })
                .Where(x => x.Points.Count >= 2 && x.MaxX - x.MinX > 20.0)
                .ToList();

            if (candidates.Count < 2)
                return result;

            // У N6 плитная арматура внутренних балок задаётся не одной длинной
            // верхней/нижней ветвью, а набором соседних горизонтальных кусков.
            // Если выбирать "самый длинный" исходный кусок буквально, мы берём один
            // фрагмент 130 мм и замыкаем его в крошечный прямоугольник. Для сборки
            // общего хомута сначала восстанавливаем виртуальные цельные горизонтали
            // по каждому уровню Y: от крайней левой до крайней правой точки.
            var horizontalGroups = new List<List<PlateBranchCandidate>>();
            foreach (var c in candidates.Where(x => NearlyHorizontal(x.Points, 8.0)).OrderBy(x => x.AvgY))
            {
                var group = horizontalGroups.FirstOrDefault(g => Math.Abs(g.Average(v => v.AvgY) - c.AvgY) <= 8.0);
                if (group == null)
                {
                    group = new List<PlateBranchCandidate>();
                    horizontalGroups.Add(group);
                }

                group.Add(c);
            }

            foreach (var group in horizontalGroups.Where(g => g.Count >= 2))
            {
                double minX = group.Min(g => g.MinX);
                double maxX = group.Max(g => g.MaxX);
                double y = group.Average(g => g.AvgY);

                if (maxX - minX <= group.Max(g => g.MaxX - g.MinX) + 20.0)
                    continue;

                var points = new List<(double X, double Y)> { (minX, y), (maxX, y) };
                candidates.Add(new PlateBranchCandidate
                {
                    Points = points,
                    MinX = minX,
                    MaxX = maxX,
                    MinY = y,
                    MaxY = y,
                    AvgY = y
                });
            }

            // Верхняя ветвь: самый высокий длинный стержень.
            // Для внутренних балок N4 это горизонталь по верху плиты, а не
            // диагональный доборный кусок.
            var top = candidates
                .OrderByDescending(x => x.MinY)
                .ThenByDescending(x => x.MaxX - x.MinX)
                .First();

            var topSorted = top.Points
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            var topLeft = topSorted.First();
            var topRight = topSorted.Last();
            double topY = Math.Min(topLeft.Y, topRight.Y);
            double topWidth = Math.Max(1.0, Math.Abs(topRight.X - topLeft.X));


            // ВАЖНО.
            // Предыдущие итерации пытались достраивать углы внутренней балки
            // ближайшими нижними точками у концов верхней ветви. В N4 эти точки
            // часто являются не углами контура, а короткими перехлестами/доборными
            // горизонтальными кусками. Из-за них появлялся боковой "выгрызок".
            //
            // Поэтому для внутренних балок больше не подмешиваем отдельные боковые
            // точки из общего облака. Нижнюю ветвь выбираем только как целый реальный
            // стержень АРГО:
            //   1) если есть полный нижний горизонтальный стержень почти на всю ширину
            //      верхней ветви — берём его (типично B3_0500);
            //   2) иначе берём фактическую V-ломаную с минимальной Y и сохраняем все
            //      её промежуточные точки (типично N4_1200).
            // Боковые стороны получаются простым замыканием верхней ветви на концы
            // выбранной нижней ветви. Это не трогает крайние балки и не ломает
            // высокую арматуру на бортиках.
            bool NearlyHorizontal(IReadOnlyList<(double X, double Y)> pts, double tol)
            {
                if (pts == null || pts.Count < 2) return false;
                double minY = pts.Min(p => p.Y);
                double maxY = pts.Max(p => p.Y);
                return Math.Abs(maxY - minY) <= tol;
            }

            var fullLower = candidates
                .Where(x => x.MaxY < topY - 5.0)
                .Where(x => x.MaxX - x.MinX >= topWidth * 0.70)
                .Where(x => NearlyHorizontal(x.Points, 8.0))
                .OrderBy(x => Math.Abs((x.MaxX - x.MinX) - topWidth))
                .ThenBy(x => x.MinY)
                .FirstOrDefault();

            var lowerHull = new List<(double X, double Y)>();

            if (fullLower != null)
            {
                lowerHull = fullLower.Points
                    .Where(p => p.X >= Math.Min(topLeft.X, topRight.X) - 80.0 &&
                                p.X <= Math.Max(topLeft.X, topRight.X) + 80.0)
                    .OrderBy(p => p.X)
                    .ThenBy(p => p.Y)
                    .ToList();
            }
            else
            {
                var vBranch = candidates
                    .Where(x => x.Points.Count >= 2)
                    .Where(x => x.MinY < topY - 20.0)
                    // Отсекаем короткие перехлесты у краев. Они как раз и давали
                    // ложные углы в N4. Реальная V-ветвь заметно длиннее.
                    .Where(x => x.MaxX - x.MinX >= topWidth * 0.35)
                    .OrderBy(x => x.MinY)
                    .ThenByDescending(x => x.MaxX - x.MinX)
                    .FirstOrDefault();

                if (vBranch != null)
                {
                    lowerHull = vBranch.Points
                        .Where(p => p.X >= Math.Min(topLeft.X, topRight.X) - 80.0 &&
                                    p.X <= Math.Max(topLeft.X, topRight.X) + 80.0)
                        .OrderBy(p => p.X)
                        .ThenBy(p => p.Y)
                        .ToList();
                }
            }

            // Fallback оставляем только на случай нестандартного файла, где нельзя
            // выбрать ни нижнюю горизонталь, ни V-ветвь как цельный стержень.
            if (lowerHull.Count < 2)
            {
                var lowerCandidates = candidates
                    .SelectMany(x => x.Points)
                    .Where(p => p.X >= topLeft.X - 60.0 && p.X <= topRight.X + 60.0)
                    .Where(p => p.Y < topY - 3.0)
                    .ToList();

                if (lowerCandidates.Count < 2)
                    return result;

                var leftSide = FindSideClosureFromLowerPoints(lowerCandidates, topLeft, searchMm: 30.0);
                var rightSide = FindSideClosureFromLowerPoints(lowerCandidates, topRight, searchMm: 30.0);
                lowerHull = BuildLowerEnvelope(lowerCandidates, leftSide, rightSide);
            }

            lowerHull = lowerHull
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();
            lowerHull = RemoveSequentialDuplicates(lowerHull, 1.0);

            if (lowerHull.Count < 2)
                return result;

            // У внутренних балок нижняя фактическая ветвь часто уже верхней:
            // её концы находятся ближе к оси ребра, чем концы верхней ветви.
            // Если замыкать topRight -> lowerRight и lowerLeft -> topLeft напрямую,
            // внешний угол "срезается" диагональю. Именно это визуально теряло
            // угол плитного стержня на внутренних балках.
            //
            // Для замкнутого стержня-хомута правильнее сохранить боковые стойки:
            // сначала опуститься вертикально под конец верхней ветви, затем уже
            // идти на выбранную нижнюю V/горизонтальную ветвь.
            //
            // Важная тонкость: высоту боковой стойки нельзя брать просто из конца
            // выбранной V-ветви. В АРГО у внутренних балок рядом с краем часто есть
            // отдельный короткий горизонтальный стержень, который задаёт реальную
            // отметку края плиты. Если проигнорировать его, угол сохраняется, но
            // край внутренней балки получается выше края соседней внешней балки.
            //
            // Поэтому:
            //   - форму нижней ветви по-прежнему берём цельным реальным стержнем;
            //   - отметку бокового края берём по ближайшей реальной нижней точке
            //     у конца верхней ветви;
            //   - X стойки всё равно фиксируем по верхнему концу, чтобы не вернуть
            //     старые косые "выгрызки".
            var sideLevelCandidates = candidates
                .SelectMany(x => x.Points)
                .Where(p => p.Y < topY - 3.0)
                .ToList();

            var innerLeftSide = FindSideClosureFromLowerPoints(sideLevelCandidates, topLeft, searchMm: 30.0);
            var innerRightSide = FindSideClosureFromLowerPoints(sideLevelCandidates, topRight, searchMm: 30.0);

            // Если крайняя короткая ветка у борта лежит ниже конца V-ветви,
            // сам конец V превращается не в полезный перелом, а в локальный
            // верхний "зубец": side -> высокий конец V -> провал V.
            //
            // Удалять этот конец совсем нельзя: тогда следующий сегмент может
            // срезать угол через пустоту, и ограничитель по бетону начнёт
            // прокладывать обходной маршрут по границе. Правильнее сохранить X
            // перехода к V, но опустить его на отметку крайней горизонтальной
            // ветки. Так получаем: вертикальная стойка -> короткая полка -> V.
            if (lowerHull.Count >= 3 &&
                innerLeftSide.Y < lowerHull[0].Y - 8.0 &&
                lowerHull[0].Y > Math.Max(innerLeftSide.Y, lowerHull[1].Y) + 8.0)
            {
                lowerHull[0] = (lowerHull[0].X, innerLeftSide.Y);
            }

            if (lowerHull.Count >= 3 &&
                innerRightSide.Y < lowerHull[lowerHull.Count - 1].Y - 8.0 &&
                lowerHull[lowerHull.Count - 1].Y > Math.Max(innerRightSide.Y, lowerHull[lowerHull.Count - 2].Y) + 8.0)
            {
                int last = lowerHull.Count - 1;
                lowerHull[last] = (lowerHull[last].X, innerRightSide.Y);
            }

            if (lowerHull.Count < 2)
                return result;

            var lowerLeft = lowerHull.First();
            var lowerRight = lowerHull.Last();

            // Итоговый контур: верх слева направо, правая вертикальная стойка,
            // нижняя фактическая ветвь справа налево, левая вертикальная стойка.
            result.Add(topLeft);
            if (Distance(topRight, topLeft) > 1.0)
                result.Add(topRight);

            if (Distance(result[result.Count - 1], innerRightSide) > 1.0)
                result.Add(innerRightSide);

            if (Distance(result[result.Count - 1], lowerRight) > 1.0)
                result.Add(lowerRight);

            for (int i = lowerHull.Count - 1; i >= 0; i--)
            {
                var p = lowerHull[i];
                if (Distance(result[result.Count - 1], p) > 1.0)
                    result.Add(p);
            }

            if (Distance(result[result.Count - 1], innerLeftSide) > 1.0)
                result.Add(innerLeftSide);

            result = RemoveSequentialDuplicates(result, 1.0);

            if (result.Count < 3)
                return new List<(double X, double Y)>();

            return result;
        }

        private (double X, double Y) FindSideClosureFromLowerPoints(
            IReadOnlyList<(double X, double Y)> lowerPoints,
            (double X, double Y) topPoint,
            double searchMm)
        {
            var near = lowerPoints
                .Where(p => Math.Abs(p.X - topPoint.X) <= searchMm && p.Y < topPoint.Y - 3.0)
                .OrderBy(p => Math.Abs(p.X - topPoint.X))
                // Берём самую верхнюю реальную нижнюю точку у края: она дает короткую
                // боковую сторону, как в АРГО, вместо диагонали до вершины V.
                .ThenByDescending(p => p.Y)
                .FirstOrDefault();

            if (!near.Equals(default((double X, double Y))))
                return (topPoint.X, near.Y);

            // Fallback: если точка не лежит прямо под верхним концом, берём ближайшую
            // по X, но X замыкания всё равно фиксируем по верхней точке.
            var nearest = lowerPoints
                .OrderBy(p => Math.Abs(p.X - topPoint.X))
                .ThenByDescending(p => p.Y)
                .FirstOrDefault();

            if (!nearest.Equals(default((double X, double Y))))
                return (topPoint.X, nearest.Y);

            return (topPoint.X, topPoint.Y - 100.0);
        }

        private List<(double X, double Y)> BuildLowerEnvelope(
            IReadOnlyList<(double X, double Y)> lowerPoints,
            (double X, double Y) leftSide,
            (double X, double Y) rightSide)
        {
            var pts = lowerPoints
                .Concat(new[] { leftSide, rightSide })
                .Where(p => p.X >= Math.Min(leftSide.X, rightSide.X) - 1.0 &&
                            p.X <= Math.Max(leftSide.X, rightSide.X) + 1.0)
                .GroupBy(p => Math.Round(p.X, 1))
                // Для каждого X оставляем нижнюю точку. Это убирает верхние
                // перехлёсты, но сохраняет настоящий V.
                .Select(g => g.OrderBy(p => p.Y).First())
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            if (pts.Count <= 2)
                return pts;

            double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
                => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

            var lower = new List<(double X, double Y)>();
            foreach (var p in pts)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            return RemoveSequentialDuplicates(lower, 1.0);
        }

        private bool HasClosePoint(IReadOnlyList<(double X, double Y)> points, (double X, double Y) p, double tol)
        {
            if (points == null) return false;
            return points.Any(x => Distance(x, p) <= tol);
        }

        private (double X, double Y) BuildVerticalSideClosurePoint(
            IReadOnlyList<(double X, double Y)> allPoints,
            IReadOnlyList<(double X, double Y)> bottomSorted,
            (double X, double Y) topPoint,
            (double X, double Y) fallback,
            double searchMm)
        {
            double y;

            // Самый стабильный вариант: если нижняя фактическая ветвь проходит
            // под этим X, берём Y на этой ветви интерполяцией. Так боковая
            // сторона замыкается вертикально, а нижний V остаётся фактическим.
            if (TryInterpolateYOnPolylineByX(bottomSorted, topPoint.X, out y))
                return (topPoint.X, y);

            // Если нижняя ветвь не перекрывает этот X, берём ближайшую нижнюю
            // исходную точку, но X всё равно фиксируем равным X верхнего конца.
            // Это принципиально: возвращать реальный X ближайшей точки нельзя,
            // иначе снова появится косая диагональ в углу.
            if (allPoints != null && allPoints.Count > 0)
            {
                var candidate = allPoints
                    .Where(p => Math.Abs(p.X - topPoint.X) <= searchMm && p.Y < topPoint.Y - 5.0)
                    .OrderBy(p => Math.Abs(p.X - topPoint.X))
                    .ThenBy(p => Math.Abs(p.Y - fallback.Y))
                    .FirstOrDefault();

                if (!candidate.Equals(default((double X, double Y))))
                    return (topPoint.X, candidate.Y);
            }

            return (topPoint.X, fallback.Y);
        }

        private bool TryInterpolateYOnPolylineByX(
            IReadOnlyList<(double X, double Y)> polylineSortedByX,
            double x,
            out double y)
        {
            y = 0;
            if (polylineSortedByX == null || polylineSortedByX.Count == 0)
                return false;

            for (int i = 0; i < polylineSortedByX.Count; i++)
            {
                var p = polylineSortedByX[i];
                if (Math.Abs(p.X - x) < 1e-6)
                {
                    y = p.Y;
                    return true;
                }
            }

            for (int i = 0; i < polylineSortedByX.Count - 1; i++)
            {
                var a = polylineSortedByX[i];
                var b = polylineSortedByX[i + 1];

                if ((a.X <= x && x <= b.X) || (b.X <= x && x <= a.X))
                {
                    if (Math.Abs(b.X - a.X) < 1e-6)
                    {
                        y = Math.Min(a.Y, b.Y);
                        return true;
                    }

                    double t = (x - a.X) / (b.X - a.X);
                    y = a.Y + t * (b.Y - a.Y);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Строит контур не как произвольный convex-hull, а как две понятные
        /// ветви: верхнюю и нижнюю огибающие. Для внутренней балки это важно:
        /// верхняя горизонтальная ветвь и нижний V-образный провал должны
        /// сохраниться как отдельные стороны замкнутого контура.
        /// </summary>
        private List<(double X, double Y)> BuildTopBottomEnvelopeContour(List<(double X, double Y)> points)
        {
            var pts = MergeClosePoints(points, 5.0)
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            if (pts.Count <= 3)
                return pts;

            double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
                => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

            var lower = new List<(double X, double Y)>();
            foreach (var p in pts)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<(double X, double Y)>();
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                var p = pts[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            // upper сейчас справа налево; разворачиваем, чтобы контур начинался
            // верхней ветвью слева направо, затем шёл по нижней ветви справа налево.
            upper.Reverse();
            lower.Reverse();

            var contour = new List<(double X, double Y)>();
            foreach (var p in upper)
                if (contour.Count == 0 || Distance(contour[contour.Count - 1], p) > 1.0)
                    contour.Add(p);

            foreach (var p in lower)
            {
                if (contour.Count > 0 && Distance(contour[contour.Count - 1], p) <= 1.0)
                    continue;
                if (contour.Count > 0 && Distance(contour[0], p) <= 1.0)
                    continue;
                contour.Add(p);
            }

            return RemoveSequentialDuplicates(contour, 1.0);
        }

        private List<(double X, double Y)> ConvexHull(List<(double X, double Y)> points)
        {
            var pts = points
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            if (pts.Count <= 1)
                return pts;

            var lower = new List<(double X, double Y)>();
            foreach (var p in pts)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<(double X, double Y)>();
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                var p = pts[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private List<(double X, double Y)> SimplifyCollinear(List<(double X, double Y)> points, double tol)
        {
            if (points.Count <= 3)
                return points;

            bool changed = true;
            var result = new List<(double X, double Y)>(points);

            while (changed && result.Count > 3)
            {
                changed = false;
                for (int i = 0; i < result.Count; i++)
                {
                    var a = result[(i - 1 + result.Count) % result.Count];
                    var b = result[i];
                    var c = result[(i + 1) % result.Count];

                    double ab = Distance(a, b);
                    double bc = Distance(b, c);
                    double ac = Distance(a, c);
                    if (ab < 1 || bc < 1)
                    {
                        result.RemoveAt(i);
                        changed = true;
                        break;
                    }

                    double area2 = Math.Abs(Cross(a, b, c));
                    double height = area2 / Math.Max(1e-9, ac);
                    if (height <= tol)
                    {
                        result.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            return result;
        }

        private List<(double X, double Y)> ConstrainClosedPolylineToConcrete(
            List<(double X, double Y)> polyline,
            IReadOnlyList<(double X, double Y)> concreteContour)
        {
            var result = new List<(double X, double Y)>();
            if (polyline == null || polyline.Count < 3)
                return result;

            result.Add(polyline[0]);

            for (int i = 0; i < polyline.Count; i++)
            {
                var a = polyline[i];
                var b = polyline[(i + 1) % polyline.Count];

                List<(double X, double Y)> edge;
                if (SegmentInsideConcrete(a, b, concreteContour, 1.0))
                {
                    edge = new List<(double X, double Y)> { a, b };
                }
                else
                {
                    edge = BuildBoundaryRouteInsideConcrete(a, b, concreteContour);
                }

                for (int k = 1; k < edge.Count; k++)
                {
                    var pt = edge[k];

                    // На последнем ребре не добавляем исходную первую точку: при
                    // IsClosed=true Paris замкнет контур сам. Промежуточные точки
                    // маршрута сохраняем, чтобы замыкание осталось корректным.
                    if (i == polyline.Count - 1 && Distance(pt, polyline[0]) < 1.0)
                        continue;

                    if (result.Count == 0 || Distance(result[result.Count - 1], pt) > 1.0)
                        result.Add(pt);
                }
            }

            return result;
        }

        private List<(double X, double Y)> BuildBoundaryRouteInsideConcrete(
            (double X, double Y) a,
            (double X, double Y) b,
            IReadOnlyList<(double X, double Y)> concreteContour)
        {
            var pa = GetClosestBoundaryPointInfo(a, concreteContour);
            var pb = GetClosestBoundaryPointInfo(b, concreteContour);

            var routeForward = BuildBoundaryRoute(a, b, pa, pb, concreteContour, forward: true);
            var routeBackward = BuildBoundaryRoute(a, b, pa, pb, concreteContour, forward: false);

            bool fOk = PolylineInsideConcrete(routeForward, concreteContour, 5.0);
            bool bOk = PolylineInsideConcrete(routeBackward, concreteContour, 5.0);

            if (fOk && !bOk) return routeForward;
            if (!fOk && bOk) return routeBackward;

            double lf = PolylineLength(routeForward);
            double lb = PolylineLength(routeBackward);
            return lf <= lb ? routeForward : routeBackward;
        }

        private List<(double X, double Y)> BuildBoundaryRoute(
            (double X, double Y) a,
            (double X, double Y) b,
            BoundaryPointInfo pa,
            BoundaryPointInfo pb,
            IReadOnlyList<(double X, double Y)> contour,
            bool forward)
        {
            int n = contour.Count;
            var route = new List<(double X, double Y)> { a };

            if (Distance(a, pa.Point) > 1.0)
                route.Add(pa.Point);

            if (forward)
            {
                int idx = (pa.SegmentIndex + 1) % n;
                int guard = 0;
                while (idx != (pb.SegmentIndex + 1) % n && guard < n + 2)
                {
                    route.Add(contour[idx]);
                    idx = (idx + 1) % n;
                    guard++;
                }
            }
            else
            {
                int idx = pa.SegmentIndex;
                int stop = pb.SegmentIndex;
                int guard = 0;
                while (idx != stop && guard < n + 2)
                {
                    route.Add(contour[idx]);
                    idx = (idx - 1 + n) % n;
                    guard++;
                }
            }

            if (Distance(route[route.Count - 1], pb.Point) > 1.0)
                route.Add(pb.Point);
            if (Distance(pb.Point, b) > 1.0)
                route.Add(b);

            return RemoveSequentialDuplicates(route, 1.0);
        }

        private List<(double X, double Y)> RemoveSequentialDuplicates(List<(double X, double Y)> points, double tol)
        {
            var result = new List<(double X, double Y)>();
            foreach (var p in points)
            {
                if (result.Count == 0 || Distance(result[result.Count - 1], p) > tol)
                    result.Add(p);
            }
            return result;
        }

        private List<(double X, double Y)> ApplyConcreteCover(
            IReadOnlyList<(double X, double Y)> points,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double coverMm)
        {
            var result = new List<(double X, double Y)>();
            if (points == null || points.Count == 0)
                return result;

            foreach (var p in points)
            {
                var q = EnsureConcreteCover(p, concreteContour, coverMm);
                if (result.Count == 0 || Distance(result[result.Count - 1], q) > 1.0)
                    result.Add(q);
            }

            return result;
        }


        /// <summary>
        /// Постобработка контура верхней плитной арматуры.
        ///
        /// После построения внешнего контура и увода от грани бетона возможны
        /// маленькие "зубцы" около бортов/вутов: это не реальные стержни, а
        /// следствие прокладки маршрута по границе бетонного контура. Также на
        /// внутренних балках иногда появляется лишняя V-образная нижняя точка.
        ///
        /// Удаляем только такие вершины, которые можно заменить хордой без выхода
        /// за бетон. Поэтому реальные переломы, где хорда пересекла бы пустоту или
        /// вышла за сечение, сохраняются.
        /// </summary>
        private List<(double X, double Y)> RegularizePlateRebarContour(
            IReadOnlyList<(double X, double Y)> points,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double coverMm)
        {
            var result = RemoveSequentialDuplicates(points?.ToList() ?? new List<(double X, double Y)>(), 1.0);
            if (result.Count <= 3 || concreteContour == null || concreteContour.Count < 3)
                return result;

            // 1) Убираем маленькие нижние зубцы. Это самые заметные артефакты на
            // крайних балках после offset-а от наклонного борта/вута.
            result = RemoveLocalPlateTeeth(
                result,
                concreteContour,
                coverMm,
                maxDepthMm: 90.0,
                maxSpanMm: 450.0,
                requireNearBoundary: true);

            // 2) Внутренние V-образные точки больше НЕ сглаживаем.
            // На внутренних балках это не зубец, а реальное опускание нижней
            // ветви плитной арматуры из АРГО. Предыдущий мягкий проход с
            // requireNearBoundary=false как раз съедал этот провал.

            // 3) После удаления только приграничных зубцов ещё раз выдерживаем минимальный отступ.
            result = ApplyConcreteCover(result, concreteContour, Math.Min(coverMm, 25.0));
            return RemoveSequentialDuplicates(result, 1.0);
        }

        private List<(double X, double Y)> RemoveLocalPlateTeeth(
            List<(double X, double Y)> points,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double coverMm,
            double maxDepthMm,
            double maxSpanMm,
            bool requireNearBoundary)
        {
            if (points.Count <= 3)
                return points;

            var result = new List<(double X, double Y)>(points);
            bool changed = true;
            int pass = 0;

            while (changed && result.Count > 3 && pass++ < 30)
            {
                changed = false;

                for (int i = 0; i < result.Count; i++)
                {
                    var a = result[(i - 1 + result.Count) % result.Count];
                    var b = result[i];
                    var c = result[(i + 1) % result.Count];

                    // Нас интересуют только нижние V-образные точки: b ниже
                    // соседей. Верхние углы бортов/плиты не трогаем.
                    double neighborLevel = Math.Min(a.Y, c.Y);
                    double depth = neighborLevel - b.Y;
                    if (depth <= 8.0 || depth > maxDepthMm)
                        continue;

                    double span = Distance(a, c);
                    if (span > maxSpanMm)
                        continue;

                    // Если вершина примыкает к реальной вертикальной боковой стойке,
                    // это не "зубец", а нужный внешний угол замкнутого плитного
                    // стержня. У внутренних балок такой угол как раз задаётся
                    // короткой крайней веткой АРГО; удалять его нельзя, иначе край
                    // снова поднимается относительно соседней внешней балки.
                    bool hasVerticalSide =
                        (Math.Abs(a.X - b.X) <= 5.0 && Math.Abs(a.Y - b.Y) > 8.0) ||
                        (Math.Abs(c.X - b.X) <= 5.0 && Math.Abs(c.Y - b.Y) > 8.0);
                    if (hasVerticalSide)
                        continue;

                    if (requireNearBoundary && DistanceToPolygonBoundary(b, concreteContour) > coverMm * 2.2)
                        continue;

                    // Удаляем вершину только когда хорда между соседями остаётся
                    // внутри бетонного контура. Это защищает реальные вуты/уступы.
                    if (!SegmentInsideConcrete(a, c, concreteContour, 1.0))
                        continue;

                    // И не разрешаем хорде лечь прямо на поверхность: если середина
                    // получилась слишком близко к границе, сначала попробуем не
                    // удалять эту вершину.
                    var mid = (X: (a.X + c.X) / 2.0, Y: (a.Y + c.Y) / 2.0);
                    if (DistanceToPolygonBoundary(mid, concreteContour) < Math.Min(coverMm * 0.35, 10.0))
                        continue;

                    result.RemoveAt(i);
                    changed = true;
                    break;
                }
            }

            return result;
        }

        private (double X, double Y) EnsureConcreteCover(
            (double X, double Y) p,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double coverMm)
        {
            if (concreteContour == null || concreteContour.Count < 3)
                return p;

            bool inside = PointInsideOrOnPolygon(p, concreteContour, 1.0);
            double dist = DistanceToPolygonBoundary(p, concreteContour);

            if (inside && dist >= coverMm)
                return p;

            var info = GetClosestBoundaryPointInfo(p, concreteContour);
            var boundaryPoint = info.Point;

            // Если исходная точка уже внутри, лучше двигаться от ближайшей грани
            // к самой точке: так сохраняется слой/линия АРГО. Если точка лежит на
            // грани или снаружи, используем нормаль внутрь полигона.
            var dir = (X: p.X - boundaryPoint.X, Y: p.Y - boundaryPoint.Y);
            double dirLen = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);

            var candidates = new List<(double X, double Y)>();
            if (inside && dirLen > 1e-6)
            {
                candidates.Add((boundaryPoint.X + dir.X / dirLen * coverMm,
                                boundaryPoint.Y + dir.Y / dirLen * coverMm));
            }

            var normalCandidate = MoveBoundaryPointInside(info, concreteContour, coverMm);
            candidates.Add(normalCandidate);

            // На всякий случай пробуем меньшие слои: лучше 20/10 мм внутри бетона,
            // чем точка на поверхности или снаружи.
            foreach (double c in new[] { Math.Min(20.0, coverMm), 10.0, 5.0 })
                candidates.Add(MoveBoundaryPointInside(info, concreteContour, c));

            foreach (var c in candidates)
            {
                if (PointInsideOrOnPolygon(c, concreteContour, 1.0))
                    return c;
            }

            // Последний fallback — ближайшая точка границы. Обычно сюда не попадаем,
            // но так не создаём NaN и не ломаем экспорт.
            return boundaryPoint;
        }

        private (double X, double Y) MoveBoundaryPointInside(
            BoundaryPointInfo info,
            IReadOnlyList<(double X, double Y)> contour,
            double distance)
        {
            int n = contour.Count;
            var a = contour[info.SegmentIndex];
            var b = contour[(info.SegmentIndex + 1) % n];

            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9)
                return info.Point;

            // Для CCW-контура внутренняя сторона слева от ребра, для CW — справа.
            double area = SignedPolygonArea(contour);
            double nx = area >= 0 ? -dy / len : dy / len;
            double ny = area >= 0 ? dx / len : -dx / len;

            var c1 = (X: info.Point.X + nx * distance, Y: info.Point.Y + ny * distance);
            if (PointInsideOrOnPolygon(c1, contour, 1.0))
                return c1;

            // Если ориентация/самопересечение контура дали неверную нормаль, пробуем
            // противоположную сторону.
            var c2 = (X: info.Point.X - nx * distance, Y: info.Point.Y - ny * distance);
            if (PointInsideOrOnPolygon(c2, contour, 1.0))
                return c2;

            return c1;
        }

        private double SignedPolygonArea(IReadOnlyList<(double X, double Y)> polygon)
        {
            double area = 0.0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2.0;
        }

        private bool ClosedPolylineInsideConcrete(
            IReadOnlyList<(double X, double Y)> polyline,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double tol)
        {
            if (polyline == null || polyline.Count < 3)
                return false;

            for (int i = 0; i < polyline.Count; i++)
            {
                var a = polyline[i];
                var b = polyline[(i + 1) % polyline.Count];
                if (!SegmentInsideConcrete(a, b, concreteContour, tol))
                    return false;
            }
            return true;
        }

        private bool PolylineInsideConcrete(
            IReadOnlyList<(double X, double Y)> polyline,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double tol)
        {
            if (polyline == null || polyline.Count < 2)
                return false;

            for (int i = 0; i < polyline.Count - 1; i++)
            {
                if (!SegmentInsideConcrete(polyline[i], polyline[i + 1], concreteContour, tol))
                    return false;
            }
            return true;
        }

        private bool SegmentInsideConcrete(
            (double X, double Y) a,
            (double X, double Y) b,
            IReadOnlyList<(double X, double Y)> concreteContour,
            double tol)
        {
            double len = Distance(a, b);
            int steps = Math.Max(2, (int)Math.Ceiling(len / 25.0));
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                var p = (X: a.X + (b.X - a.X) * t, Y: a.Y + (b.Y - a.Y) * t);
                if (!PointInsideOrOnPolygon(p, concreteContour, tol))
                    return false;
            }
            return true;
        }

        private bool PointInsideOrOnPolygon(
            (double X, double Y) p,
            IReadOnlyList<(double X, double Y)> polygon,
            double boundaryTol)
        {
            if (DistanceToPolygonBoundary(p, polygon) <= boundaryTol)
                return true;

            bool inside = false;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                bool intersect = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                                 (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / ((pj.Y - pi.Y) == 0 ? 1e-9 : (pj.Y - pi.Y)) + pi.X);
                if (intersect)
                    inside = !inside;
            }
            return inside;
        }

        private double DistanceToPolygonBoundary(
            (double X, double Y) p,
            IReadOnlyList<(double X, double Y)> polygon)
        {
            double best = double.PositiveInfinity;
            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % n];
                best = Math.Min(best, Distance(p, ClosestPointOnSegment(p, a, b)));
            }
            return best;
        }

        private (double X, double Y) ProjectPointToPolygonBoundary(
            (double X, double Y) p,
            IReadOnlyList<(double X, double Y)> polygon)
        {
            return GetClosestBoundaryPointInfo(p, polygon).Point;
        }

        private BoundaryPointInfo GetClosestBoundaryPointInfo(
            (double X, double Y) p,
            IReadOnlyList<(double X, double Y)> polygon)
        {
            double best = double.PositiveInfinity;
            var bestPoint = polygon[0];
            int bestIndex = 0;

            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % n];
                var q = ClosestPointOnSegment(p, a, b);
                double d = Distance(p, q);
                if (d < best)
                {
                    best = d;
                    bestPoint = q;
                    bestIndex = i;
                }
            }

            return new BoundaryPointInfo { Point = bestPoint, SegmentIndex = bestIndex };
        }

        private (double X, double Y) ClosestPointOnSegment(
            (double X, double Y) p,
            (double X, double Y) a,
            (double X, double Y) b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-9)
                return a;

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Max(0, Math.Min(1, t));
            return (a.X + dx * t, a.Y + dy * t);
        }

        private double PolylineLength(IReadOnlyList<(double X, double Y)> points)
        {
            double len = 0;
            for (int i = 0; i < points.Count - 1; i++)
                len += Distance(points[i], points[i + 1]);
            return len;
        }

        private struct BoundaryPointInfo
        {
            public (double X, double Y) Point;
            public int SegmentIndex;
        }

        private double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private double Distance((double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private double NormalizeAngle(double angle)
        {
            angle %= 360.0;
            if (angle < 0)
                angle += 360.0;
            return angle;
        }

        #endregion

        #region Утилиты

        private (double Diameter, int Count) EstDiamCount(double areaCm2)
        {
            int[] diameters = { 6, 8, 10, 12, 14, 16, 18, 20, 22, 25, 28, 32, 36, 40 };
            double areaMm2 = areaCm2 * 100;
            foreach (var d in diameters.Reverse())
            {
                double barArea = Math.PI * d * d / 4;
                int n = (int)Math.Round(areaMm2 / barArea);
                if (n >= 1 && n <= 10) return (d, n);
            }
            return (16, Math.Max(1, (int)Math.Round(areaMm2 / (Math.PI * 256 / 4))));
        }

        private (double Diametr, int Branches) EstStirrupDiam(double areaCm2, int reinfCount)
        {
            for (int i = 2; i < reinfCount; i += 2)
            {
                double areaMm2 = areaCm2 * 100 / i;
                foreach (var d in new[] { 8, 10, 12 })
                    if (Math.PI * d * d / 4 >= areaMm2 * 0.8) return (d, i);
            }
            return (10, 2);
        }

        #endregion
    }
}
