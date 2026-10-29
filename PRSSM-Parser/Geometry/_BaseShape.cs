using GeometryLibrary;
using System.ComponentModel;

namespace PrssmParser.Geometry
{
    /// <summary>
    /// Базовый класс контура сечения
    /// </summary>
    public class _BaseShape
    {
        public _BaseShape() { }

        public _BaseShape(_BaseShape element)
        {
            Id = element.Id;
            Area = element.Area;
            GC = new ParisCadPoint2D(element.GC);
            Location = new ParisCadPoint2D(element.Location);
            Name = element.Name;
            Beta = element.Beta;
            IsReflectX = element.IsReflectX;
            IsReflectY = element.IsReflectY;
            SelectedRegionType = element.SelectedRegionType;
            Profile = element.Profile.Select(x => new ParisRegion(x.Points) { RegionType = x.RegionType }).ToList();
        }


        #region Properties
        /// <summary>
        /// Уникальный идентификатор объекта
        /// </summary>
        [Browsable(false)]
        public int Id { get; set; }

        /// <summary>
        /// Признак выделенности объекта
        /// </summary>
        [Browsable(false)]
        public bool IsSelected { get; set; }

        /// <summary>
        /// Профиль для рисования объекта
        /// </summary>
        [Browsable(false)]
        public virtual IEnumerable<ParisRegion> Profile { get; set; } = new ParisRegion[0];

        /// <summary>
        /// Площадь
        /// </summary>
        [Browsable(false)]
        public double Area { get; set; }

        /// <summary>
        /// Центр тяжести
        /// </summary>
        [Browsable(false)]
        public ParisCadPoint2D GC { get; set; } = new ParisCadPoint2D();

        /// <summary>
        /// Положение элемента в реальной системе координат
        /// </summary>
        [ReadOnly(true)]
        public ParisCadPoint2D Location { get; set; } = new ParisCadPoint2D();

        /// <summary>
        /// Название элемента
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Угол поворта
        /// </summary>
        public double Beta { get; set; }

        /// <summary>
        /// Отражение относительно OX
        /// </summary>
        public bool IsReflectX { get; set; }

        /// <summary>
        /// Отражение относительно OY
        /// </summary>
        public bool IsReflectY { get; set; }

        /// <summary>
        /// Тип контура
        /// </summary>
        public RegionType SelectedRegionType { get; set; } = RegionType.Body;
        #endregion


        #region Methods
        public IEnumerable<ParisPoint2D> GetBoundingPoints(bool isMain = false)
        {
            var boundingPoints = new ParisPoint2D[4];
            var profiles = !isMain ? Profile.Where(x => x.RegionType != RegionType.Hole) : Profile.Where(x => x.RegionType == RegionType.Body).ToArray();

            var center = GetCenterOfGrav(profiles);

            for (int i = 0; i < boundingPoints.Length; i++)
                boundingPoints[i] = new ParisPoint2D(center.X, center.Y);

            foreach (var prof in profiles)
            {
                var _center = prof.GetCentroid();
                var pointArray = prof.Points.OrderByDescending(x => (x - _center).Length()).ToArray();
                foreach (var _p in pointArray)
                {
                    var p = new ParisPoint2D(_p.X + Location.X, _p.Y + Location.Y);
                    if (p.X <= boundingPoints[0].X && p.Y >= boundingPoints[0].Y)
                        boundingPoints[0] = new ParisPoint2D(p.X, p.Y);
                    if (p.X >= boundingPoints[1].X && p.Y >= boundingPoints[1].Y)
                        boundingPoints[1] = new ParisPoint2D(p.X, p.Y);
                    if (p.X <= boundingPoints[2].X && p.Y <= boundingPoints[2].Y)
                        boundingPoints[2] = new ParisPoint2D(p.X, p.Y);
                    if (p.X >= boundingPoints[3].X && p.Y <= boundingPoints[3].Y)
                        boundingPoints[3] = new ParisPoint2D(p.X, p.Y);
                }
            }
            return boundingPoints;
        }

        /// <summary>
        /// Обновлление центра тяжести
        /// </summary>
        public virtual void UpdateCenterOfGrav()
        {
            double area = 0;
            double sx = 0;
            double sy = 0;
            int ind = 0;
            foreach (var i in Profile)
            {
                var a = i.GetArea();
                var c = i.GetCentroid();
                sx += a * c.X;
                sy += a * c.Y;
                area += a;
                ind += 1;
            }
            Area = area;
            GC = Math.Abs(Area) < 0.0001 ?
                new ParisCadPoint2D() :
                new ParisCadPoint2D(Math.Round(sx / Area, 2), Math.Round(sy / Area, 2));
        }

        /// <summary>
        /// Определение центра тяжести сечения
        /// </summary>
        /// <param name="regions">Список контуров</param>
        /// <returns>Точка центра тяжести</returns>
        internal ParisCadPoint2D GetCenterOfGrav(IEnumerable<ParisRegion> regions)
        {
            double area = 0;
            double sx = 0;
            double sy = 0;
            foreach (var i in regions)
            {
                var a = i.GetArea();
                var c = i.GetCentroid();
                sx += a * c.X;
                sy += a * c.Y;
                area += a;
            }
            return Math.Abs(area) < 0.0001 ?
                new ParisCadPoint2D() :
                new ParisCadPoint2D(Math.Round(sx / area, 2), Math.Round(sy / area, 2));
        }

        internal double[] GetAxisMomens()
        {
            double area = 0;
            double sx = 0;
            double sy = 0;
            double ix = 0;
            double iy = 0;

            foreach (var i in Profile)
            {
                var a = i.GetArea();
                var c = i.GetCentroid();
                sx += a * c.X;
                sy += a * c.Y;
                ix += GetIx(i.Points);
                iy += GetIy(i.Points);
                area += a;
            }
            var gc = Math.Abs(area) < 0.0001 ?
                new ParisCadPoint2D() :
                new ParisCadPoint2D(Math.Round(sx / area, 2), Math.Round(sy / area, 2));
            return new double[2] { ix - area * gc.Y * gc.Y, iy - area * gc.X * gc.X };

        }

        public double GetIx(IEnumerable<ParisPoint2D> points)
        {
            double ix = 0;
            List<double> x = points.Select(x => x.X).ToList();
            List<double> y = points.Select(x => x.Y).ToList();
            x.Add(x[0]);
            y.Add(y[0]);

            for (int i = 0; i < x.Count - 1; i++)
                ix += (y[i] * y[i] + y[i] * y[i + 1] + y[i + 1] * y[i + 1]) * (x[i] * y[i + 1] - x[i + 1] * y[i]);
            return Math.Abs(ix / 12);
        }

        public double GetIy(IEnumerable<ParisPoint2D> points)
        {
            double iy = 0;
            List<double> x = points.Select(x => x.X).ToList();
            List<double> y = points.Select(x => x.Y).ToList();
            x.Add(x[0]);
            y.Add(y[0]);

            for (int i = 0; i < x.Count - 1; i++)
                iy += (x[i] * x[i] + x[i] * x[i + 1] + x[i + 1] * x[i + 1]) * (x[i] * y[i + 1] - x[i + 1] * y[i]);

            return Math.Abs(iy / 12);
        }

        /// <summary>
        /// Обновление положения элемента
        /// </summary>
        /// <param name="regions">Список контуров</param>
        /// <returns>Список контуров с учетом фактического положения</returns>
        internal IEnumerable<ParisRegion> UpdatePosition(IEnumerable<ParisRegion> regions)
        {
            var center = GetCenterOfGrav(regions);
            foreach (var p in regions)
                p.Rotate(center.X, center.Y, Beta);
            if (IsReflectX)
                foreach (var p in regions)
                    p.Reflect(center.X, center.Y, center.X + 1, center.Y);
            if (IsReflectY)
                foreach (var p in regions)
                    p.Reflect(center.X, center.Y, center.X, center.Y + 1);
            foreach (var p in regions)
                p.Shift(-center.X, -center.Y);
            return regions;
        }

        /// <summary>
        /// Переместить элемент
        /// </summary>
        /// <param name="v">Вектор смещения</param>
        public void Move(ParisVector2D v) => this.Location += v;

        public void Rotate(double x, double y, double angle)
        {
            foreach (var p in Profile)
                p.Rotate(x, y, angle);
        }

        /// <summary>
        /// Определение границ объекта длля вписывания объекта в контролл
        /// </summary>
        /// <returns>Прямоугольная область</returns>
        public ParisRect Bounds()
        {
            var rect = new ParisRect();
            foreach (var prof in Profile)
                rect.Union(new ParisRect(new ParisPoint2D(prof.Points.Min(x => x.X) + Location.X, prof.Points.Max(x => x.Y) + Location.Y), new ParisPoint2D(prof.Points.Max(x => x.X) + Location.X, prof.Points.Min(x => x.Y) + Location.Y)));
            return rect;
        }

        #endregion
    }
}
