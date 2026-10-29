using System.Drawing;

namespace PrssmParser.BearingCapacity;

public static class BdfGeometry
{
    /// <summary>Reproduces ais7GbBeamType1/2.Profile, including PointF rounding.</summary>
    public static IReadOnlyList<PointF> BuildProfile(BdfBeam b)
    {
        if (b.Type is not (1 or 2)) throw new NotSupportedException($"Тип балки {b.Type} не поддерживается.");
        bool right = b.ConsoleSide == "Справа", left = b.ConsoleSide == "Слева";
        double rightEdge = b.TopWidth / 2 + (right ? b.ConsoleLength : 0);
        double leftEdge = -b.TopWidth / 2 - (left ? b.ConsoleLength : 0);
        double haunchZ = -(b.HaunchHeight == 0 ? b.TopThickness : b.HaunchHeight);
        var points = new List<PointF>
        {
            P(0, 0), P(right && b.ConsoleLength < 0 ? 0 : b.TopWidth / 2, 0),
            P(rightEdge, 0), P(rightEdge, -b.TopThickness)
        };
        if (b.Type == 1)
        {
            double web = (b.HaunchWidth == 0 ? b.WebWidth : b.HaunchWidth) / 2;
            points.AddRange(Haunch(b.TopHaunch, b.TopX, b.TopY,
                P(right && b.ConsoleLength < 0 ? 0 : b.TopWidth / 2, -b.TopThickness), P(web, haunchZ), P(b.WebWidth / 2, -b.Height)));
            points.AddRange(Haunch(b.TopHaunch, ReverseX(b.TopHaunch, b.TopX, b.TopY), b.TopX,
                P(-b.WebWidth / 2, -b.Height), P(-web, haunchZ), P(left && b.ConsoleLength < 0 ? 0 : -b.TopWidth / 2, -b.TopThickness)));
        }
        else
        {
            points.AddRange(Haunch(b.TopHaunch, b.TopX, b.TopY,
                P(b.TopWidth / 2, -b.TopThickness), P(b.WebWidth / 2, haunchZ), P(b.WebWidth / 2, -b.Height / 2)));
            points.AddRange(Haunch(b.BottomHaunch, ReverseX(b.BottomHaunch, b.BottomX, b.BottomY), b.BottomX,
                P(b.WebWidth / 2, -b.Height / 2), P(b.WebWidth / 2, -b.Height + b.BottomThickness), P(b.BottomWidth / 2, -b.Height + b.BottomThickness)));
            points.Add(P(b.BottomEdgeWidth / 2, -b.Height)); points.Add(P(-b.BottomEdgeWidth / 2, -b.Height));
            points.AddRange(Haunch(b.BottomHaunch, b.BottomX, b.BottomY,
                P(-b.BottomWidth / 2, -b.Height + b.BottomThickness), P(-b.WebWidth / 2, -b.Height + b.BottomThickness), P(-b.WebWidth / 2, -b.Height / 2)));
            points.AddRange(Haunch(b.TopHaunch, ReverseX(b.TopHaunch, b.TopX, b.TopY), b.TopX,
                P(-b.WebWidth / 2, -b.Height / 2), P(-b.WebWidth / 2, haunchZ), P(-b.TopWidth / 2, -b.TopThickness)));
        }
        points.Add(P(leftEdge, -b.TopThickness)); points.Add(P(leftEdge, 0));
        points.Add(P(left && b.ConsoleLength < 0 ? 0 : -b.TopWidth / 2, 0));
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))
            throw new FormatException("Параметры вута образуют некорректный контур .bdf.");
        return points;
    }

    public static double TorsionalInertia(BdfBeam b)
    {
        double left = (b.TopWidth - b.WebWidth) / 2 + (b.ConsoleSide == "Слева" ? b.ConsoleLength : 0);
        double right = (b.TopWidth - b.WebWidth) / 2 + (b.ConsoleSide == "Справа" ? b.ConsoleLength : 0);
        double result = ThinRectangle(left, (b.TopThickness + b.HaunchHeight) / 2) + ThinRectangle(right, (b.TopThickness + b.HaunchHeight) / 2);
        if (b.Type == 1) return result + ThinRectangle(b.Height, b.WebWidth);
        double width = Math.Max(b.BottomThickness, (b.BottomWidth + b.BottomEdgeWidth) / 2);
        double height = Math.Min(b.BottomThickness, (b.BottomWidth + b.BottomEdgeWidth) / 2);
        double halfWidth = width / 2, halfHeight = height / 2;
        double rectangle = halfWidth * Math.Pow(halfHeight, 3) *
            (5.333333492279053 - 3.36 * halfHeight / halfWidth * (1 - Math.Pow(halfHeight, 4) / (12 * Math.Pow(halfWidth, 4))));
        return result + ThinRectangle(b.Height - b.BottomThickness, (b.WebWidth + b.HaunchWidth) / 2)
            + rectangle;
    }

    private static double ThinRectangle(double a, double b)
    {
        double max = Math.Max(a, b), min = Math.Min(a, b);
        return min <= 0 ? 0 : (max / min - 0.63) * Math.Pow(min, 4) / 3;
    }
    private static double ReverseX(string type, double x, double y) => type == "Линейный" ? y : x;
    private static PointF P(double x, double y) => new((float)x, (float)y);

    private static IEnumerable<PointF> Haunch(string type, double x, double y, PointF a, PointF corner, PointF c)
    {
        if (type == "Отсутствует") return new[] { a, corner, c };
        PointF Along(PointF end, double length) => P(corner.X + length * (end.X - corner.X) / Distance(end, corner),
            corner.Y + length * (end.Y - corner.Y) / Distance(end, corner));
        if (type == "Линейный") return new[] { a, Along(a, x), Along(c, y), c };
        if (type != "Радиус") throw new NotSupportedException($"Неизвестный тип вута: {type}.");
        if (x <= 0) return new[] { a, corner, c };
        var u = new PointF(a.X - corner.X, a.Y - corner.Y);
        var v = new PointF(c.X - corner.X, c.Y - corner.Y);
        double cosine = (u.X * v.X + u.Y * v.Y) / Distance(a, corner) / Distance(c, corner);
        double tangent = x * Math.Tan(Math.PI / 2 - Math.Acos(cosine) / 2);
        var start = Along(a, tangent); var end = Along(c, tangent);
        // Keep single-precision divisions from the original PointF implementation.
        double k1 = (corner.Y - a.Y) / (corner.X - a.X);
        double q1 = a.Y - a.X / (corner.X - a.X) * (corner.Y - a.Y);
        double offset1 = x / Math.Cos(Math.Atan(k1)) * Math.Sign(start.X - end.X);
        double k2 = (corner.Y - c.Y) / (corner.X - c.X);
        double q2 = c.Y - c.X / (corner.X - c.X) * (corner.Y - c.Y);
        double offset2 = x / Math.Cos(Math.Atan(k2)) * Math.Sign(start.X - end.X);
        double centerX = (q2 - offset2 - q1 + offset1) / (k1 - k2);
        double centerY = k1 * centerX + q1 - offset1;
        if (double.IsInfinity(k2)) { centerX = corner.X + x * Math.Sign(start.X - end.X); centerY = k1 * centerX + q1 - offset1; }
        if (double.IsInfinity(k1)) { centerX = corner.X - x * Math.Sign(start.X - end.X); centerY = k2 * centerX + q2 - offset2; }
        double from = Math.Atan2(start.Y - centerY, start.X - centerX), to = Math.Atan2(end.Y - centerY, end.X - centerX);
        if (to < from) to += 2 * Math.PI;
        var points = new List<PointF> { a, start };
        double step = (to - from) / 5;
        if (!(step > 0) || !double.IsFinite(step)) throw new FormatException("Вырожденное скругление в .bdf.");
        for (double angle = from; angle <= to; angle += step) points.Add(P(x * Math.Cos(angle) + centerX, x * Math.Sin(angle) + centerY));
        points.Add(end); points.Add(c);
        return points;
    }

    private static double Distance(PointF a, PointF b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
