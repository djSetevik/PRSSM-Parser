// Geometry of the native PARIS RCI/RCT shape; keep in sync with FEModel/Property/Shapes.
using GeometryLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using UnitLibrary.Units;
using UnitLibrary;

namespace PrssmParser.Geometry
{
    public class RCIShape : _BaseShape
    {
        #region Properties
        [UnitType(UnitType.Length)]
        public double B1 { get; set; }
        [UnitType(UnitType.Length)]
        public double H { get; set; }
        [UnitType(UnitType.Length)]
        public double TW { get; set; }
        [UnitType(UnitType.Length)]
        public double TF1 { get; set; }
        [UnitType(UnitType.Length)]
        public double B2 { get; set; }
        [UnitType(UnitType.Length)]
        public double TF2 { get; set; }
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
        public double A3 { get; set; }
        [UnitType(UnitType.Length)]
        public double A4 { get; set; }
        [UnitType(UnitType.Length)]
        public double A5 { get; set; }
        [UnitType(UnitType.Length)]
        public double R1 { get; set; }
        [UnitType(UnitType.Length)]
        public double R2 { get; set; }

        public override IEnumerable<ParisRegion> Profile { get {
                if (TW_ == 0)
                    TW_ = TW;
                if (TF1_ == 0)
                    TF1_ = TF1;

                ParisRegion region1 = new ParisRegion(), region2 = new ParisRegion();
                if (R1 == 0)
                {
                    region1 = ParisRegion.Line(new ParisCadPoint2D(-B1 / 2 - K1, H, true), new ParisCadPoint2D(-B1 / 2, H, true, false)) +
                    ParisRegion.LineWithCenter(new ParisCadPoint2D(-B1 / 2, H, true), new ParisCadPoint2D(B1 / 2, H, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(B1 / 2 + K2, H, true), new ParisCadPoint2D(B1 / 2 + K2, H - TF1, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(B1 / 2, H - TF1, true), new ParisCadPoint2D(TW_ / 2 + A1, H - TF1_, true)) +
                    new ParisRegion(new ParisCadPoint2D[] { new ParisCadPoint2D(TW_ / 2, H - TF1_ - A2, true) });
                }
                else
                {
                    var topWallPoint = new ParisPoint2D(TW_ * 0.5, H - TF1_);
                    var topShelfPoint = new ParisPoint2D(B1 / 2, H - TF1);
                    var bottomWallPoint = new ParisPoint2D(TW / 2, TF2 + A4);

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

                    region1 = ParisRegion.Line(new ParisCadPoint2D(-B1 / 2 - K1, H, true), new ParisCadPoint2D(-B1 / 2, H, true, false)) +
                    ParisRegion.LineWithCenter(new ParisCadPoint2D(-B1 / 2, H, true), new ParisCadPoint2D(B1 / 2, H, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(B1 / 2 + K2, H, true), new ParisCadPoint2D(B1 / 2 + K2, H - TF1, true)) +
                    new ParisRegion(new ParisCadPoint2D[] { new ParisCadPoint2D(B1 / 2, H - TF1, true) }) +
                    (ParisRegion.Arc(R1, angleStart, angleStop, true, true) + new ParisVector2D(arcCenter.X, arcCenter.Y));

                }


                if (R2 == 0)
                {
                    region2 = ParisRegion.Line(new ParisCadPoint2D(TW / 2, TF2 + A4, true), new ParisCadPoint2D(B2 / 2 + A5, TF2, true)) +
                    ParisRegion.LineWithCenter(new ParisCadPoint2D(B2 / 2, 0, true), new ParisCadPoint2D(-B2 / 2, 0, true)) +
                    ParisRegion.Line(new ParisCadPoint2D(-B2 / 2 - A5, TF2, true), new ParisCadPoint2D(-TW / 2, TF2 + A4, true));
                }
                else
                {
                    var topWallPoint = new ParisPoint2D(TW_ * 0.5, H - TF1_);
                    var bottomShelfPoint = new ParisPoint2D(B2 / 2 + A5, TF2);
                    var bottomWallPoint = new ParisPoint2D(TW / 2, TF2 + A4);

                    var abc1 = Geometry2DHelper.LineEquastion(bottomWallPoint, bottomShelfPoint);
                    var abc2 = Geometry2DHelper.LineEquastion(bottomWallPoint, topWallPoint);

                    var paralel1 = Geometry2DHelper.ParallelLine(abc1, -R2);
                    var paralel2 = Geometry2DHelper.ParallelLine(abc2, R2);

                    var arcCenter = Geometry2DHelper.LinesIntersectionPoint(paralel1, paralel2);
                    var norm1 = Geometry2DHelper.NormLine(abc1, arcCenter);
                    var norm2 = Geometry2DHelper.NormLine(abc2, arcCenter);
                    var touchPoint1 = Geometry2DHelper.LinesIntersectionPoint(norm1, abc1);
                    var touchPoint2 = Geometry2DHelper.LinesIntersectionPoint(norm2, abc2);

                    double angleStart = Geometry2DHelper.AngleBtwVectors(arcCenter, new ParisPoint2D(touchPoint1.X - 1, touchPoint1.Y), touchPoint1) * 180 / Math.PI;
                    double angleStop = Geometry2DHelper.AngleBtwVectors(arcCenter, new ParisPoint2D(touchPoint2.X - 1, touchPoint2.Y), touchPoint2) * 180 / Math.PI;

                    region2 = (ParisRegion.Arc(R2, -angleStop, -angleStart, false, true) + new ParisVector2D(arcCenter.X, arcCenter.Y)) +
                    new ParisRegion(new ParisCadPoint2D[] { new ParisCadPoint2D(B2 / 2 + A5, TF2, true) }) +
                    ParisRegion.LineWithCenter(new ParisCadPoint2D(B2 / 2, 0, true), new ParisCadPoint2D(-B2 / 2, 0, true)) +
                    new ParisRegion(new ParisCadPoint2D[] { new ParisCadPoint2D(-B2 / 2 - A5, TF2, true) }) +
                     (ParisRegion.Arc(R2, angleStart + 180, angleStop + 180, false, true) + new ParisVector2D(-arcCenter.X, arcCenter.Y));

                }

                var reflectRegion = new ParisRegion(region1.Points.Skip(7).Reverse().Concat(new ParisCadPoint2D[] { new ParisCadPoint2D(B1 / 2 + K1, H - TF1, true) }));
                reflectRegion.Reflect(0, 0, 0, 1);

                var region = region1 + region2 + reflectRegion;

                region.Points = region.Points.Distinct();

                return UpdatePosition(new ParisRegion[] { region });
            }
        }
        #endregion

        public RCIShape() { }

        public RCIShape(RCIShape element) : base(element)
        {
            H = element.H;
            B1 = element.B1;
            B2 = element.B2;
            TF1 = element.TF1;
            TF2 = element.TF2;
            TW = element.TW;
            TW_ = element.TW_;
            TF1_ = element.TF1_;
            K1 = element.K1;
            K2 = element.K2;
            A1 = element.A1;
            A2 = element.A2;
            A3 = element.A3;
            A4 = element.A4;
            A5 = element.A5;
            R1 = element.R1;
            R2 = element.R2;
        }
    }
}
