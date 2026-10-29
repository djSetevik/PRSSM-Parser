using GeometryLibrary;

namespace PrssmParser.Geometry
{
    /// <summary>
    /// Круг
    /// </summary>
    public class CircleShape : _BaseShape
    {
        #region Properties
        public double D { get; set; }

        public override IEnumerable<ParisRegion> Profile
        {
            get
            {
                var region = ParisRegion.Arc(D / 2, 0, 90, true, true) + ParisRegion.Arc(D / 2, 90, 180, true, true) + ParisRegion.Arc(D / 2, 180, 270, true, true) + ParisRegion.Arc(D / 2, 270, 360, true, true);
                return UpdatePosition(new ParisRegion[] { region });
            }
        }
        #endregion


        public CircleShape() { }

        public CircleShape(CircleShape element) : base(element) { D = element.D; }
    }
}
