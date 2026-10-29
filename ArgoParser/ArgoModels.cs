using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ArgoParser
{
    public class ArgoFileCode
    {
        [JsonPropertyName("ИмяФайла")]
        public string OriginalFileName { get; set; }

        [JsonPropertyName("КодНагрузки")]
        public char LoadCode { get; set; }

        [JsonPropertyName("НазваниеНагрузки")]
        public string LoadName { get; set; }

        [JsonPropertyName("ГодНагрузки")]
        public int? LoadYear { get; set; }

        [JsonPropertyName("КоличествоРёбер")]
        public int MainRibCount { get; set; }

        [JsonPropertyName("ПлитноеБезКонсолей")]
        public bool IsPlateWithoutConsoles => MainRibCount == 0;

        [JsonPropertyName("ДлинаПролёта")]
        public int SpanLength { get; set; }

        [JsonPropertyName("ПорядковыйНомер")]
        public int SerialNumber { get; set; }

        [JsonPropertyName("СуффиксТипа")]
        public char? TypeSuffix { get; set; }

        [JsonPropertyName("ОписаниеТипа")]
        public string TypeDescription { get; set; }

        public static ArgoFileCode Parse(string fileName)
        {
            var code = new ArgoFileCode { OriginalFileName = fileName };

            try
            {
                fileName = System.IO.Path.GetFileName(fileName);

                code.LoadCode = char.ToUpper(fileName[0]);
                switch (code.LoadCode)
                {
                    case 'A': code.LoadName = "1907"; code.LoadYear = 1907; break;
                    case 'B': code.LoadName = "1925"; code.LoadYear = 1925; break;
                    case 'N': code.LoadName = "1931"; code.LoadYear = 1931; break;
                    case 'S': code.LoadName = "1962"; code.LoadYear = 1962; break;
                    case 'I': code.LoadName = "Индивидуальное"; break;
                    default: code.LoadName = "Неизвестно"; break;
                }

                if (char.IsDigit(fileName[1]))
                    code.MainRibCount = fileName[1] - '0';

                int underscoreIdx = fileName.IndexOf('_');
                int dotIdx = fileName.IndexOf('.');
                if (underscoreIdx > 0 && dotIdx > underscoreIdx)
                {
                    string spanStr = fileName.Substring(underscoreIdx + 1, dotIdx - underscoreIdx - 1);
                    int.TryParse(spanStr, out int span);
                    code.SpanLength = span;
                }

                if (dotIdx > 0 && dotIdx < fileName.Length - 1)
                {
                    string afterDot = fileName.Substring(dotIdx + 1);
                    string numPart = "";
                    string letterPart = "";
                    foreach (char c in afterDot)
                    {
                        if (char.IsDigit(c)) numPart += c;
                        else if (char.IsLetter(c)) letterPart += c;
                    }

                    if (numPart.Length >= 2)
                    {
                        if (int.TryParse(numPart.Substring(0, 2), out int serial))
                            code.SerialNumber = serial;
                    }
                    else if (numPart.Length > 0)
                    {
                        if (int.TryParse(numPart, out int serial))
                            code.SerialNumber = serial;
                    }

                    if (letterPart.Length > 0)
                    {
                        code.TypeSuffix = char.ToLower(letterPart[letterPart.Length - 1]);
                        code.TypeDescription = GetTypeDescription(code.TypeSuffix.Value, code.MainRibCount);
                    }
                }
            }
            catch { }

            return code;
        }

        private static string GetTypeDescription(char suffix, int ribCount)
        {
            if (ribCount == 0)
            {
                switch (char.ToLower(suffix))
                {
                    case 'a': case 'b': case 'c': return "1 блок";
                    case 'd': case 'e': case 'f': return "2 блока";
                    case 'g': case 'h': case 'i': return "3 блока";
                    case 'j': case 'k': case 'l': return "4 блока";
                }
            }

            switch (char.ToLower(suffix))
            {
                case 'k': return "Короткие консоли";
                case 'd': return "Длинные консоли";
                case 'l': return "Левая длинная";
                case 'r': return "Правая длинная";
                case 'p': return "Симметричное";
                case 's': return "Стандартное";
                case 'z': return "Особое";
            }

            return suffix.ToString().ToUpper();
        }
    }

    public class ArgoDocument
    {
        [JsonPropertyName("ИсходныйФайл")]
        public string SourceFileName { get; set; }

        [JsonPropertyName("ШифрФайла")]
        public ArgoFileCode FileCode { get; set; }

        [JsonPropertyName("Заголовок")]
        public Header Header { get; set; }

        [JsonPropertyName("Параметры")]
        public GlobalParameters GlobalParams { get; set; }

        [JsonPropertyName("Балки")]
        public List<Beam> Beams { get; set; } = new List<Beam>();

        [JsonPropertyName("ЭкземпляровПечати")]
        public int PrintCopies { get; set; }

        [JsonPropertyName("Детализация")]
        public DetailedReinforcement DetailedReinforcement { get; set; }

        [JsonPropertyName("ИзмененияДиаметровСтержней")]
        public List<ChangedBarDiameter> ChangedBarDiameters { get; set; } = new List<ChangedBarDiameter>();
    }

    public class Header
    {
        [JsonPropertyName("СтрокКомментариев")]
        public int CommentLineCount { get; set; }

        [JsonPropertyName("Комментарии")]
        public List<string> Comments { get; set; } = new List<string>();
    }

    public class GlobalParameters
    {
        [JsonPropertyName("УровеньПечати")]
        public double PrintLevel { get; set; }

        [JsonPropertyName("ПрочностьБетона")]
        public double ConcreteStrength { get; set; }

        [JsonPropertyName("ТипРастянутойАрматуры")]
        public double TensileReinforcementType { get; set; }

        [JsonPropertyName("ТипСжатойАрматуры")]
        public double CompressedReinforcementType { get; set; }

        [JsonPropertyName("ТипАрматурыПлиты")]
        public double SlabReinforcementType { get; set; }

        [JsonPropertyName("ТипХомутов")]
        public double StirrupType { get; set; }

        [JsonPropertyName("КоординатаОсиОпорнойЧасти1")]
        public double SupportAxis1 { get; set; }

        [JsonPropertyName("КоординатаОсиОпорнойЧасти2")]
        public double SupportAxis2 { get; set; }

        [JsonPropertyName("КоординатаВнутреннейГраниБалансираОЧ1")]
        public double InnerSupport1 { get; set; }

        [JsonPropertyName("КоординатаВнутреннейГраниБалансираОЧ2")]
        public double InnerSupport2 { get; set; }

        [JsonPropertyName("ПолнаяДлинаПС")]
        public double FullLength { get; set; }

        [JsonPropertyName("КоличествоБалок")]
        public int BeamCount { get; set; }

        [JsonPropertyName("КоординатыОсейБалок")]
        public List<double> BeamCoordinates { get; set; } = new List<double>();

        [JsonPropertyName("ВидБалласта")]
        public double BallastType { get; set; }

        [JsonPropertyName("ТипШпал")]
        public double SleeperType { get; set; }

        [JsonPropertyName("КоординатыОсиПути")]
        public List<double> TrackAxisZ { get; set; } = new List<double>();

        [JsonPropertyName("ПризнакНаличияДиафрагм")]
        public double DiaphragmPresence { get; set; }

        [JsonPropertyName("КонтурБалластнойПризмы")]
        public List<Point2D> BallastContour { get; set; } = new List<Point2D>();
    }

    public class Point2D
    {
        [JsonPropertyName("Z")]
        public double Z { get; set; }

        [JsonPropertyName("Y")]
        public double Y { get; set; }

        public Point2D() { }

        public Point2D(double z, double y)
        {
            Z = z;
            Y = y;
        }
    }

    public class Beam
    {
        [JsonPropertyName("Номер")]
        public int Number { get; set; }

        [JsonPropertyName("НагрузкаТротуарИнтенсивность")]
        public double SidewalkLoadIntensity { get; set; }

        [JsonPropertyName("НагрузкаТротуарКоордината")]
        public double SidewalkLoadCoordinate { get; set; }

        [JsonPropertyName("НагрузкаОгражденияИнтенсивность")]
        public double FenceLoadIntensity { get; set; }

        [JsonPropertyName("НагрузкаОгражденияКоордината")]
        public double FenceLoadCoordinate { get; set; }

        [JsonPropertyName("АрмированиеПлиты")]
        public SlabReinforcement SlabReinforcement { get; set; }

        [JsonPropertyName("СосредоточенныеСилы")]
        public List<ConcentratedForce> ConcentratedForces { get; set; } = new List<ConcentratedForce>();

        [JsonPropertyName("КоличествоРасчетныхСечений")]
        public int SectionCount { get; set; }

        [JsonPropertyName("КоординатыРасчетныхСечений")]
        public List<double> SectionCoordinates { get; set; } = new List<double>();

        [JsonPropertyName("ТочкиСопряженияПлитыСБалкой")]
        public int[] SlabBeamJunction { get; set; } = new int[2];

        [JsonPropertyName("ТочкиСопряженияПлитыСВутом")]
        public int[] SlabVuteJunction { get; set; } = new int[2];

        [JsonPropertyName("ТочкиСопряженияБортаСПлитой")]
        public int[] BorderSlabJunction { get; set; } = new int[2];

        [JsonPropertyName("ТочкиСопряженияБортаСПлитой2")]
        public int[] BorderSlabJunction2 { get; set; } = new int[2];

        [JsonPropertyName("ПризнакПродольногоРазрезаСоСтороныМеньшейZ")]
        public double LongitudinalCutLower { get; set; }

        [JsonPropertyName("ПризнакПродольногоРазрезаСоСтороныБольшейZ")]
        public double LongitudinalCutUpper { get; set; }

        [JsonPropertyName("КонтурПоперечногоСечения")]
        public List<Point2D> CrossSectionContour { get; set; } = new List<Point2D>();

        [JsonPropertyName("КоличествоИзмененныхТочек")]
        public int ChangedPointsCount { get; set; }

        [JsonPropertyName("ИндексыИзмененныхТочек")]
        public List<int> ChangedPointIndices { get; set; } = new List<int>();

        [JsonPropertyName("ИзмененныеТочки")]
        public List<Point2D> ChangedPoints { get; set; } = new List<Point2D>();

        [JsonPropertyName("ПродольнаяСимметрияИзмененияКонтура")]
        public bool HasLongitudinalContourSymmetry { get; set; } = true;

        [JsonPropertyName("ГруппыИзмененияКонтура")]
        public List<ChangedContourGroup> ChangedContourGroups { get; set; } = new List<ChangedContourGroup>();

        [JsonPropertyName("Отгибы")]
        public List<BendReinforcement> Bends { get; set; } = new List<BendReinforcement>();

        [JsonPropertyName("СекцииХомутов")]
        public List<StirrupSection> StirrupSections { get; set; } = new List<StirrupSection>();

        [JsonPropertyName("РастянутыеСтержни")]
        public List<TensileBar> TensileBars { get; set; } = new List<TensileBar>();

        [JsonPropertyName("СжатыеСтержни")]
        public List<CompressedBar> CompressedBars { get; set; } = new List<CompressedBar>();

        public int ChangedPointsCount2 { get; set; }
        public List<int> ChangedPointIndices2 { get; set; } = new List<int>();
        public List<Point2D> ChangedPoints2 { get; set; } = new List<Point2D>();

        [JsonPropertyName("АсимметрияАрмироманияОтгибов")]
        public double BendSymmetryFlag { get; set; }
    }

    public class ChangedContourGroup
    {
        [JsonPropertyName("ИндексыТочек")]
        public List<int> PointIndices { get; set; } = new List<int>();

        [JsonPropertyName("КоординатыТочек")]
        public List<Point2D> Points { get; set; } = new List<Point2D>();
    }

    public class SlabReinforcement
    {
        [JsonPropertyName("КоличествоРасчетныхСтержней")]
        public int CalculatedBarsCount { get; set; }

        [JsonPropertyName("РасчетныеСтержни")]
        public List<CalculatedBar> CalculatedBars { get; set; } = new List<CalculatedBar>();
    }

    public class CalculatedBar
    {
        [JsonPropertyName("КоличествоТочекПерегиба")]
        public int BendPointsCount { get; set; }

        [JsonPropertyName("Площадь")]
        public double Area { get; set; }

        [JsonPropertyName("ТочкиПерегиба")]
        public List<Point2D> BendPoints { get; set; } = new List<Point2D>();
    }

    public class ConcentratedForce
    {
        [JsonPropertyName("X")]
        public double X { get; set; }

        [JsonPropertyName("Значение")]
        public double Value { get; set; }
    }

    public class BendReinforcement
    {
        [JsonPropertyName("Площадь")]
        public double Area { get; set; }

        [JsonPropertyName("КоординатаВерхнегоКонца")]
        public double UpperCoordinate { get; set; }

        [JsonPropertyName("КоординатаНижнегоКонца")]
        public double LowerCoordinate { get; set; }

        [JsonPropertyName("DeltaВерхнее")]
        public double DeltaUpper { get; set; }

        [JsonPropertyName("DeltaНижнее")]
        public double DeltaLower { get; set; }
    }

    public class StirrupSection
    {
        [JsonPropertyName("КоординатаКонцаУчастка")]
        public double EndX { get; set; }

        [JsonPropertyName("ПлощадьХомута")]
        public double Area { get; set; }

        [JsonPropertyName("ШагХомутов")]
        public double Step { get; set; }
    }

    public class TensileBar
    {
        [JsonPropertyName("XMin")]
        public double XMin { get; set; }

        [JsonPropertyName("XMax")]
        public double XMax { get; set; }

        [JsonPropertyName("DeltaНижнее")]
        public double DeltaLower { get; set; }

        [JsonPropertyName("Площадь")]
        public double Area { get; set; }
    }

    public class CompressedBar
    {
        [JsonPropertyName("XMin")]
        public double XMin { get; set; }

        [JsonPropertyName("XMax")]
        public double XMax { get; set; }

        [JsonPropertyName("DeltaВерхнее")]
        public double DeltaUpper { get; set; }

        [JsonPropertyName("Площадь")]
        public double Area { get; set; }
    }

    public class DetailedReinforcement
    {
        [JsonPropertyName("ДетализацияБалок")]
        public List<BeamDetailed> BeamDetails { get; set; } = new List<BeamDetailed>();
    }

    public class BeamDetailed
    {
        [JsonPropertyName("НомерБалки")]
        public int BeamNumber { get; set; }

        [JsonPropertyName("РастянутыеСтержни")]
        public List<DetailedBarGroup> TensileBars { get; set; } = new List<DetailedBarGroup>();

        [JsonPropertyName("СжатыеСтержни")]
        public List<DetailedBarGroup> CompressedBars { get; set; } = new List<DetailedBarGroup>();

        [JsonPropertyName("АрматураПлиты")]
        public List<PlateBarInfo> PlateBars { get; set; } = new List<PlateBarInfo>();

        [JsonPropertyName("ФизическаяАрматураПлиты")]
        public List<DetailedSlabBar> SlabBars { get; set; } = new List<DetailedSlabBar>();

        [JsonPropertyName("Отгибы")]
        public List<DetailedBendGroup> Bends { get; set; } = new List<DetailedBendGroup>();

        [JsonPropertyName("Хомуты")]
        public List<DetailedStirrupSection> Stirrups { get; set; } = new List<DetailedStirrupSection>();
    }

    public class DetailedBarGroup
    {
        [JsonPropertyName("Количество")]
        public int Count { get; set; }

        [JsonPropertyName("Диаметр")]
        public double Diameter { get; set; }

        [JsonPropertyName("КоординатыZ")]
        public List<double> ZCoordinates { get; set; } = new List<double>();

        [JsonPropertyName("КоординатаY")]
        public double YCoordinate { get; set; }
    }

    public class PlateBarInfo
    {
        [JsonPropertyName("Диаметр")]
        public double Diameter { get; set; }

        [JsonPropertyName("Шаг")]
        public double Step { get; set; }
    }

    public class DetailedSlabBar
    {
        [JsonPropertyName("КоличествоНаМетр")]
        public int BarsPerMeter { get; set; }

        [JsonPropertyName("Диаметр")]
        public double Diameter { get; set; }
    }

    public class ChangedBarDiameter
    {
        [JsonPropertyName("НомерБалки")]
        public int BeamNumber { get; set; }

        [JsonPropertyName("НомерСтержня")]
        public int BarNumber { get; set; }

        [JsonPropertyName("РасстояниеДоОсиБалки")]
        public double DistanceFromBeamAxis { get; set; }

        [JsonPropertyName("Диаметр")]
        public double Diameter { get; set; }
    }

    public class DetailedBendGroup
    {
        [JsonPropertyName("Количество")]
        public int Count { get; set; }

        [JsonPropertyName("Диаметр")]
        public double Diameter { get; set; }

        [JsonPropertyName("Координаты")]
        public List<BendCoordinates> Coordinates { get; set; } = new List<BendCoordinates>();
    }

    public class BendCoordinates
    {
        [JsonPropertyName("Y1")]
        public double Y1 { get; set; }

        [JsonPropertyName("Z1")]
        public double Z1 { get; set; }

        [JsonPropertyName("Y2")]
        public double Y2 { get; set; }

        [JsonPropertyName("Z2")]
        public double Z2 { get; set; }
    }

    public class DetailedStirrupSection
    {
        [JsonPropertyName("Количество")]
        public int Count { get; set; }

        [JsonPropertyName("Диаметр")]
        public double Diameter { get; set; }

        [JsonPropertyName("Шаг")]
        public double Step { get; set; }

        [JsonPropertyName("КоординатыX")]
        public List<double> XCoordinates { get; set; } = new List<double>();
    }
}
