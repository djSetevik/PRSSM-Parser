// Geometry of the native PARIS RCI/RCT shape; keep in sync with FEModel/Property/Shapes.
using GeometryLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using UnitLibrary.Units;
using UnitLibrary;

namespace PrssmParser.Geometry
{
    public class RCTShape : _BaseShape
    {
        #region Properties
        [UnitType(UnitType.Length)]
        public double H { get; set; }
        [UnitType(UnitType.Length)]
        public double B1 { get; set; }
        [UnitType(UnitType.Length)]
        public double TW { get; set; }
        [UnitType(UnitType.Length)]
        public double TF1 { get; set; }
        [UnitType(UnitType.Length)]
        public double TW_ { get; set; }
        [UnitType(UnitType.Length)]
        public double TF1_ { get; set; }
        [UnitType(UnitType.Length)]
        public double K1 { get; set; }
        [UnitType(UnitType.Length)]
        public double K2 { get; set; }
        [UnitType(UnitType.Length)]
        public double A1 { get; set; }
        [UnitType(UnitType.Length)]
        public double A2 { get; set; }
        [UnitType(UnitType.Length)]
        public double R1 { get; set; }

        public override IEnumerable<ParisRegion> Profile
        {
            get
            {
                var region = new ParisRegion();
                if (R1 == 0.0)
                {
                    region = new ParisRegion(new ParisCadPoint2D[] { new ParisCadPoint2D(-B1 / 2 - K1, H, true) }) +
                    ParisRegion.LineWithCenter(new ParisCadPoint2D(-B1 / 2, H, true), new ParisCadPoint2D(B1 / 2, H, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(B1 / 2 + K2, H, true), new ParisCadPoint2D(B1 / 2 + K2, H - TF1, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(B1 / 2, H - TF1, true), new ParisCadPoint2D(TW_ / 2 + A1, H - TF1_, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(TW_ / 2, H - TF1_ - A2, true), new ParisCadPoint2D(TW / 2, 0, true)) +
                    ParisRegion.LineWithCenter(new ParisCadPoint2D(TW / 2, 0), new ParisCadPoint2D(-TW / 2, 0, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(-TW_ / 2, H - TF1_ - A2, true), new ParisCadPoint2D(-TW_ / 2 - A1, H - TF1_, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(-B1 / 2, H - TF1, true), new ParisCadPoint2D(-B1 / 2 - K1, H - TF1, true));
                }
                else
                {
                    var topWallPoint = new ParisPoint2D(TW_ * 0.5, H - TF1_);
                    var topShelfPoint = new ParisPoint2D(B1 / 2, H - TF1);
                    var bottomWallPoint = new ParisPoint2D(TW / 2, 0);

                    var abc1 = Geometry2DHelper.LineEquastion(topWallPoint, topShelfPoint);
                    var abc2 = Geometry2DHelper.LineEquastion(topWallPoint, bottomWallPoint);

                    var paralel1 = Geometry2DHelper.ParallelLine(abc1, R1);
                    var paralel2 = Geometry2DHelper.ParallelLine(abc2, -R1);

                    var arcCenter = Geometry2DHelper.LinesIntersectionPoint(paralel1, paralel2);
                    var norm1 = Geometry2DHelper.NormLine(abc1, arcCenter);
                    var norm2 = Geometry2DHelper.NormLine(abc2, arcCenter);
                    var touchPoint1 = Geometry2DHelper.LinesIntersectionPoint(norm1, abc1);
                    var touchPoint2 = Geometry2DHelper.LinesIntersectionPoint(norm2, abc2);

                    double angleStart = Geometry2DHelper.AngleBtwVectors(arcCenter, new ParisPoint2D(touchPoint1.X - 1, touchPoint1.Y), touchPoint1) * 180 / Math.PI;
                    double angleStop = Geometry2DHelper.AngleBtwVectors(arcCenter, new ParisPoint2D(touchPoint2.X - 1, touchPoint2.Y), touchPoint2) * 180 / Math.PI;

                    region = new ParisRegion(new ParisCadPoint2D[] { new ParisCadPoint2D(-B1 / 2 - K1, H, true) }) +
                    ParisRegion.LineWithCenter(new ParisCadPoint2D(-B1 / 2, H, true), new ParisCadPoint2D(B1 / 2, H, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(B1 / 2 + K2, H, true), new ParisCadPoint2D(B1 / 2 + K2, H - TF1, true)) +
                    new ParisRegion(new ParisCadPoint2D[] { new ParisCadPoint2D(B1 / 2, H - TF1, true) }) +
                    (ParisRegion.Arc(R1, angleStart, angleStop, true, true) + new ParisVector2D(arcCenter.X, arcCenter.Y)) +
                    ParisRegion.LineWithCenter(new ParisCadPoint2D(TW / 2, 0, true), new ParisCadPoint2D(-TW / 2, 0, true)) +
                    (ParisRegion.Arc(R1, 180 - angleStop, 180 - angleStart, true, true) + new ParisVector2D(-arcCenter.X, arcCenter.Y)) +
                    ParisRegion.Line(new ParisCadPoint2D(-B1 / 2, H - TF1, true), new ParisCadPoint2D(-B1 / 2 - K1, H - TF1, true));
                }


                region.Points = region.Points.Distinct();

                return UpdatePosition(new ParisRegion[] { region });
            }
        }
        #endregion

        public RCTShape() { }

        public RCTShape(RCTShape element) : base(element)
        {
            H = element.H;
            B1 = element.B1;
            TF1 = element.TF1;
            TW = element.TW;
            TW_ = element.TW_;
            TF1_ = element.TF1_;
            K1 = element.K1;
            K2 = element.K2;
            A1 = element.A1;
            A2 = element.A2;
            R1 = element.R1;
        }
    }
}
