using GeometryLibrary;

namespace PrssmParser.Geometry
{
    /// <summary>
    /// Прямоугольник
    /// </summary>
    public class RectShape : _BaseShape
    {
        #region Properties
        public double B { get; set; }
        public double H { get; set; }

        public override IEnumerable<ParisRegion> Profile { get { return UpdatePosition(new ParisRegion[] { ParisRegion.RectWithCenter(-B / 2, -H / 2, B, H, true) }); } }
        #endregion

        public RectShape() { }

        public RectShape(RectShape element) : base(element)
        {
            H = element.H;
            B = element.B;
        }
    }
}
