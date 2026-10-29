using GeometryLibrary;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PrssmParser.Geometry
{
    /// <summary>
    /// Лёгкая совместимая заглушка Section без CrossSection.Net.
    /// Нужна только чтобы проект можно было собирать без ссылки на CrossSection.Net.
    /// Основной конвертер ArgoToPrssmConverter этот класс больше не использует.
    /// </summary>
    public class Section : INotifyPropertyChanged
    {
        private string name = string.Empty;
        private double yc;
        private double zc;
        private double area;
        private double perimeter;
        private double iyy;
        private double izz;
        private double iyz;
        private double it;
        private double syy;
        private double szz;
        private double byy;
        private double bzz;
        private double wyyPlus;
        private double wyyMinus;
        private double wzzPlus;
        private double wzzMinus;
        private double offsetY;
        private double offsetZ;

        public int Id { get; set; }
        public string Name { get => name; set { name = value; OnPropertyChanged(); } }
        public double Yc { get => yc; set { yc = value; OnPropertyChanged(); } }
        public double Zc { get => zc; set { zc = value; OnPropertyChanged(); } }
        public double Perimeter { get => perimeter; set { perimeter = value; OnPropertyChanged(); } }
        public double Area { get => area; set { area = value; OnPropertyChanged(); } }
        public double Iyy { get => iyy; set { iyy = value; OnPropertyChanged(); } }
        public double Izz { get => izz; set { izz = value; OnPropertyChanged(); } }
        public double Iyz { get => iyz; set { iyz = value; OnPropertyChanged(); } }
        public double It { get => it; set { it = value; OnPropertyChanged(); } }
        public double Syy { get => syy; set { syy = value; OnPropertyChanged(); } }
        public double Szz { get => szz; set { szz = value; OnPropertyChanged(); } }
        public double Byy { get => byy; set { byy = value; OnPropertyChanged(); } }
        public double Bzz { get => bzz; set { bzz = value; OnPropertyChanged(); } }
        public double WyyPlus { get => wyyPlus; set { wyyPlus = value; OnPropertyChanged(); } }
        public double WyyMinus { get => wyyMinus; set { wyyMinus = value; OnPropertyChanged(); } }
        public double WzzPlus { get => wzzPlus; set { wzzPlus = value; OnPropertyChanged(); } }
        public double WzzMinus { get => wzzMinus; set { wzzMinus = value; OnPropertyChanged(); } }
        public double OffsetY { get => offsetY; set { offsetY = value; OnPropertyChanged(); } }
        public double OffsetZ { get => offsetZ; set { offsetZ = value; OnPropertyChanged(); } }
        public double WeightFactor { get; set; } = 1.0;

        public List<_BaseShape> Shapes { get; set; } = new();
        public List<ParisPoint2D> StressPoints { get; set; } = new();

        /// <summary>
        /// Упрощённый расчёт по первому Body-региону первой формы.
        /// Для точного расчёта используйте старую версию Section.cs + CrossSection.Net.
        /// </summary>
        public void Solve()
        {
            var region = Shapes
                .SelectMany(s => s.Profile)
                .FirstOrDefault(r => r.RegionType == RegionType.Body);

            if (region == null || region.Points.Count() < 3)
                return;

            var points = region.Points.Select(p => (X: p.X, Y: p.Y)).ToList();
            CalculatePolygon(points);
        }

        private void CalculatePolygon(List<(double X, double Y)> points)
        {
            int n = points.Count;
            double signedArea2 = 0, cxNum = 0, cyNum = 0;
            double ixx0 = 0, iyy0 = 0, ixy0 = 0, per = 0;

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
                per += Math.Sqrt(dx * dx + dy * dy);
            }

            double signedArea = signedArea2 / 2.0;
            double a = Math.Abs(signedArea);
            if (a < 1e-9) return;

            double cx = cxNum / (6.0 * signedArea);
            double cy = cyNum / (6.0 * signedArea);
            double ixxC = Math.Abs(ixx0 / 12.0 - signedArea * cy * cy);
            double iyyC = Math.Abs(iyy0 / 12.0 - signedArea * cx * cx);
            double ixyC = Math.Abs(ixy0 / 24.0 - signedArea * cx * cy);

            double minX = points.Min(p => p.X);
            double maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y);
            double maxY = points.Max(p => p.Y);

            Yc = Math.Round(cx, 5);
            Zc = Math.Round(cy, 5);
            Area = Math.Round(a, 5);
            Perimeter = Math.Round(per, 5);
            Iyy = Math.Round(ixxC, 7);
            Izz = Math.Round(iyyC, 7);
            Iyz = Math.Round(ixyC, 7);
            It = Math.Round(ixxC + iyyC, 7);
            Byy = Math.Round(maxX - minX, 5);
            Bzz = Math.Round(maxY - minY, 5);
            WyyPlus = Math.Round(ixxC / Math.Max(1e-9, maxY - cy), 5);
            WyyMinus = Math.Round(ixxC / Math.Max(1e-9, cy - minY), 5);
            WzzPlus = Math.Round(iyyC / Math.Max(1e-9, maxX - cx), 5);
            WzzMinus = Math.Round(iyyC / Math.Max(1e-9, cx - minX), 5);

            StressPoints = new List<ParisPoint2D>
            {
                new ParisPoint2D(minX, maxY),
                new ParisPoint2D(maxX, maxY),
                new ParisPoint2D(maxX, minY),
                new ParisPoint2D(minX, minY)
            };
        }

        [field: NonSerialized]
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string prop = "")
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
