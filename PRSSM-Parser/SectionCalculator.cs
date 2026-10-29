using System;
using System.Collections.Generic;
using System.Linq;
using ArgoPoint2D = PrssmParser.Argo.Point2D;

namespace PrssmParser
{
    public static class SectionCalculator
    {
        public class SectionProperties
        {
            public double Area { get; set; }
            public double Perimeter { get; set; }
            public double Xc { get; set; }
            public double Yc { get; set; }
            public double Iyy { get; set; }
            public double Izz { get; set; }
            public double Iyz { get; set; }
            public double It { get; set; }
            public double Syy { get; set; }
            public double Szz { get; set; }
            public double WyyPlus { get; set; }
            public double WyyMinus { get; set; }
            public double WzzPlus { get; set; }
            public double WzzMinus { get; set; }
            public double Byy { get; set; }
            public double Bzz { get; set; }
            public double XMin { get; set; }
            public double XMax { get; set; }
            public double YMin { get; set; }
            public double YMax { get; set; }
        }

        public static SectionProperties Calculate(List<(double X, double Y)> points)
        {
            if (points == null || points.Count < 3)
                throw new ArgumentException("Контур должен содержать минимум 3 точки");

            var result = new SectionProperties();
            int n = points.Count;

            result.XMin = points.Min(p => p.X);
            result.XMax = points.Max(p => p.X);
            result.YMin = points.Min(p => p.Y);
            result.YMax = points.Max(p => p.Y);

            result.Perimeter = CalculatePerimeter(points);

            double signedArea = CalculateSignedArea(points);
            result.Area = Math.Abs(signedArea);

            (result.Xc, result.Yc) = CalculateCentroid(points, signedArea);

            (result.Iyy, result.Izz, result.Iyz) = CalculateMomentsOfInertia(points, result.Xc, result.Yc);

            // Статические моменты — через разрезание на уровне ЦТ
            (result.Syy, result.Szz) = CalculateStaticMomentsPrecise(points, result.Xc, result.Yc);

            // Моменты сопротивления
            double distYPlus = result.YMax - result.Yc;
            double distYMinus = result.Yc - result.YMin;
            double distXPlus = result.XMax - result.Xc;
            double distXMinus = result.Xc - result.XMin;

            result.WyyPlus = distYPlus > 1e-10 ? result.Iyy / distYPlus : 0;
            result.WyyMinus = distYMinus > 1e-10 ? result.Iyy / distYMinus : 0;
            result.WzzPlus = distXPlus > 1e-10 ? result.Izz / distXPlus : 0;
            result.WzzMinus = distXMinus > 1e-10 ? result.Izz / distXMinus : 0;

            // Byy, Bzz — минимальные толщины сечения (ширина ребра)
            (result.Byy, result.Bzz) = CalculateMinThicknesses(points);

            // Момент инерции при кручении
            result.It = CalculateTorsionalInertia(result.Area, result.Perimeter, result.Iyy, result.Izz);

            return result;
        }

        private static double CalculatePerimeter(List<(double X, double Y)> points)
        {
            double perimeter = 0;
            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double dx = points[j].X - points[i].X;
                double dy = points[j].Y - points[i].Y;
                perimeter += Math.Sqrt(dx * dx + dy * dy);
            }
            return perimeter;
        }

        private static double CalculateSignedArea(List<(double X, double Y)> points)
        {
            double area = 0;
            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                area += points[i].X * points[j].Y;
                area -= points[j].X * points[i].Y;
            }
            return area / 2.0;
        }

        private static double CalculateArea(List<(double X, double Y)> points)
        {
            return Math.Abs(CalculateSignedArea(points));
        }

        private static (double Xc, double Yc) CalculateCentroid(List<(double X, double Y)> points, double signedArea)
        {
            double cx = 0, cy = 0;
            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double cross = points[i].X * points[j].Y - points[j].X * points[i].Y;
                cx += (points[i].X + points[j].X) * cross;
                cy += (points[i].Y + points[j].Y) * cross;
            }
            double area6 = signedArea * 6.0;
            if (Math.Abs(area6) < 1e-10)
                return (0, 0);
            return (cx / area6, cy / area6);
        }

        private static (double Iyy, double Izz, double Iyz) CalculateMomentsOfInertia(
            List<(double X, double Y)> points, double xc, double yc)
        {
            double iyy = 0, izz = 0, iyz = 0;
            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double x0 = points[i].X - xc;
                double y0 = points[i].Y - yc;
                double x1 = points[j].X - xc;
                double y1 = points[j].Y - yc;
                double cross = x0 * y1 - x1 * y0;
                iyy += cross * (y0 * y0 + y0 * y1 + y1 * y1);
                izz += cross * (x0 * x0 + x0 * x1 + x1 * x1);
                iyz += cross * (x0 * y1 + 2 * x0 * y0 + 2 * x1 * y1 + x1 * y0);
            }
            return (Math.Abs(iyy / 12.0), Math.Abs(izz / 12.0), iyz / 24.0);
        }

        /// <summary>
        /// Точный расчёт статического момента полусечения 
        /// через пересечение контура горизонтальной/вертикальной линией
        /// </summary>
        private static (double Syy, double Szz) CalculateStaticMomentsPrecise(
            List<(double X, double Y)> points, double xc, double yc)
        {
            // Syy — статический момент верхней половины относительно горизонтальной оси через ЦТ
            double syy = CalculateHalfStaticMomentY(points, yc);

            // Szz — статический момент правой половины относительно вертикальной оси через ЦТ
            double szz = CalculateHalfStaticMomentX(points, xc);

            return (Math.Abs(syy), Math.Abs(szz));
        }

        /// <summary>
        /// Статический момент части сечения выше yLevel относительно оси y=yLevel
        /// Метод: разбиваем на горизонтальные полоски
        /// </summary>
        private static double CalculateHalfStaticMomentY(List<(double X, double Y)> points, double yc)
        {
            // Собираем все Y-координаты точек + yc
            var yValues = points.Select(p => p.Y).Distinct().ToList();
            yValues.Add(yc);
            yValues = yValues.Where(y => y >= yc).OrderBy(y => y).ToList();

            double staticMoment = 0;

            for (int i = 0; i < yValues.Count - 1; i++)
            {
                double y1 = yValues[i];
                double y2 = yValues[i + 1];
                double yMid = (y1 + y2) / 2;

                // Ширина сечения на уровне yMid
                double width = GetWidthAtLevel(points, yMid);
                double stripArea = width * (y2 - y1);
                double stripCentroidDist = yMid - yc;

                staticMoment += stripArea * stripCentroidDist;
            }

            return staticMoment;
        }

        private static double CalculateHalfStaticMomentX(List<(double X, double Y)> points, double xc)
        {
            var xValues = points.Select(p => p.X).Distinct().ToList();
            xValues.Add(xc);
            xValues = xValues.Where(x => x >= xc).OrderBy(x => x).ToList();

            double staticMoment = 0;

            for (int i = 0; i < xValues.Count - 1; i++)
            {
                double x1 = xValues[i];
                double x2 = xValues[i + 1];
                double xMid = (x1 + x2) / 2;

                double height = GetHeightAtX(points, xMid);
                double stripArea = height * (x2 - x1);
                double stripCentroidDist = xMid - xc;

                staticMoment += stripArea * stripCentroidDist;
            }

            return staticMoment;
        }

        /// <summary>
        /// Ширина сечения (по X) на заданном уровне Y
        /// </summary>
        private static double GetWidthAtLevel(List<(double X, double Y)> points, double y)
        {
            var intersections = new List<double>();
            int n = points.Count;

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var p1 = points[i];
                var p2 = points[j];

                if ((p1.Y <= y && p2.Y > y) || (p2.Y <= y && p1.Y > y))
                {
                    double t = (y - p1.Y) / (p2.Y - p1.Y);
                    double x = p1.X + t * (p2.X - p1.X);
                    intersections.Add(x);
                }
                else if (Math.Abs(p1.Y - y) < 1e-10 && Math.Abs(p2.Y - y) < 1e-10)
                {
                    intersections.Add(p1.X);
                    intersections.Add(p2.X);
                }
            }

            if (intersections.Count < 2)
                return 0;

            intersections.Sort();
            return intersections.Last() - intersections.First();
        }

        /// <summary>
        /// Высота сечения (по Y) на заданном X
        /// </summary>
        private static double GetHeightAtX(List<(double X, double Y)> points, double x)
        {
            var intersections = new List<double>();
            int n = points.Count;

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var p1 = points[i];
                var p2 = points[j];

                if ((p1.X <= x && p2.X > x) || (p2.X <= x && p1.X > x))
                {
                    double t = (x - p1.X) / (p2.X - p1.X);
                    double y = p1.Y + t * (p2.Y - p1.Y);
                    intersections.Add(y);
                }
            }

            if (intersections.Count < 2)
                return 0;

            intersections.Sort();
            return intersections.Last() - intersections.First();
        }

        /// <summary>
        /// Минимальные толщины сечения:
        /// Byy — минимальная ширина (по X) при сканировании по Y
        /// Bzz — минимальная высота (по Y) при сканировании по X
        /// </summary>
        private static (double Byy, double Bzz) CalculateMinThicknesses(List<(double X, double Y)> points)
        {
            double yMin = points.Min(p => p.Y);
            double yMax = points.Max(p => p.Y);
            double xMin = points.Min(p => p.X);
            double xMax = points.Max(p => p.X);

            // Сканируем по Y для Byy (минимальная ширина)
            int steps = 100;
            double minWidth = double.MaxValue;
            for (int s = 1; s < steps; s++)
            {
                double y = yMin + (yMax - yMin) * s / steps;
                double w = GetWidthAtLevel(points, y);
                if (w > 0.1) // Игнорируем нулевые
                    minWidth = Math.Min(minWidth, w);
            }
            double byy = minWidth < double.MaxValue ? minWidth : (xMax - xMin);

            // Сканируем по X для Bzz (минимальная высота)  
            double minHeight = double.MaxValue;
            for (int s = 1; s < steps; s++)
            {
                double x = xMin + (xMax - xMin) * s / steps;
                double h = GetHeightAtX(points, x);
                if (h > 0.1)
                    minHeight = Math.Min(minHeight, h);
            }
            double bzz = minHeight < double.MaxValue ? minHeight : (yMax - yMin);

            return (byy, bzz);
        }

        private static double CalculateTorsionalInertia(double area, double perimeter, double iyy, double izz)
        {
            if (perimeter < 1e-10)
                return 0;

            double tAvg = area / perimeter;
            double it = 4.0 * area * area / perimeter * tAvg;
            double itMax = iyy + izz;
            return Math.Min(it, itMax);
        }

        public static List<(double X, double Y)> ConvertArgoContour(
                    List<ArgoPoint2D> argoPoints, double offsetZ = 0)
        {
            var result = new List<(double X, double Y)>();
            foreach (var p in argoPoints)
            {
                result.Add(((p.Z - offsetZ) * 10, p.Y * 10));
            }
            return result;
        }
    }
}
