using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrssmParser.Argo;
using PrssmParser.BearingCapacity;

namespace PrssmParser;

internal static class Program
{
    private enum InputFormat { Argo, Bdf }
    private sealed record Options(InputFormat Format, string Input, string Output, bool Interactive);
    private sealed record ReportEntry(string Input, string? Output, BdfDocument? Source, IReadOnlyList<string> Warnings, string? Error);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.WriteLine("PRSSM-Parser — конвертер балочных ПС в PARIS (.prssm)");
        if (args.Length == 1 && args[0] is "--help" or "-h") { Help(); return 0; }
        bool interactive = args.Length == 0;
        int code;
        try { code = Run(interactive ? AskOptions() : ParseOptions(args)); }
        catch (Exception error) { Console.Error.WriteLine($"Ошибка: {error.Message}"); code = 2; }
        if (interactive && !Console.IsInputRedirected)
        {
            Console.WriteLine("Нажмите Enter для выхода...");
            Console.ReadLine();
        }
        return code;
    }

    private static Options AskOptions()
    {
        Console.WriteLine("1 — АРГО-ЖБ: железнодорожные ПС (режим по умолчанию)");
        Console.WriteLine("2 — BearingCapacity: автомобильные балки (.bdf)");
        Console.Write("Тип входных данных [1]: ");
        string selection = Console.ReadLine()?.Trim() ?? "";
        var format = selection switch
        {
            "" or "1" => InputFormat.Argo, "2" => InputFormat.Bdf,
            _ => throw new ArgumentException("Выберите режим 1 или 2.")
        };
        string inputDefault = DefaultInput(format), outputDefault = DefaultOutput(format);
        Console.WriteLine($"Входная папка по умолчанию: {inputDefault}");
        Console.Write("Путь к файлу или папке [Enter — по умолчанию]: ");
        string input = ReadPath(inputDefault);
        Console.WriteLine($"Выходная папка по умолчанию: {outputDefault}");
        Console.Write("Папка результатов [Enter — по умолчанию]: ");
        return new(format, input, ReadPath(outputDefault), true);
    }

    private static string ReadPath(string fallback)
    {
        string value = Console.ReadLine()?.Trim().Trim('"') ?? "";
        return Path.GetFullPath(value.Length == 0 ? fallback : value);
    }

    private static Options ParseOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            string key = args[i];
            if (key is not ("--format" or "--input" or "--output") || i + 1 >= args.Length || !values.TryAdd(key, args[i + 1]))
                throw new ArgumentException("Ожидается --format argo|bdf --input <файл или папка> [--output <папка>]. Справка: --help.");
        }
        if (!values.TryGetValue("--format", out string? kind)) throw new ArgumentException("Укажите --format argo или --format bdf.");
        var format = kind.ToLowerInvariant() switch
        {
            "argo" => InputFormat.Argo, "bdf" => InputFormat.Bdf,
            _ => throw new ArgumentException($"Неизвестный формат: {kind}. Допустимы argo и bdf.")
        };
        return new(format, Path.GetFullPath(values.GetValueOrDefault("--input", DefaultInput(format))),
            Path.GetFullPath(values.GetValueOrDefault("--output", DefaultOutput(format))), false);
    }

    private static string DefaultInput(InputFormat format) => Path.Combine(AppContext.BaseDirectory, "Data", format == InputFormat.Argo ? "RAW" : "BDF");
    private static string DefaultOutput(InputFormat format) => format == InputFormat.Argo
        ? Path.Combine(AppContext.BaseDirectory, "Data", "PRSSM")
        : Path.Combine(AppContext.BaseDirectory, "Data", "PRSSM", "BDF");

    private static int Run(Options options)
    {
        bool single = File.Exists(options.Input);
        if (!single && !Directory.Exists(options.Input)) throw new DirectoryNotFoundException($"Входной путь не существует: {options.Input}");
        string sourceRoot = single ? Path.GetDirectoryName(options.Input)! : options.Input;
        var files = (single ? new[] { options.Input } : Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            .Where(f => Accepts(f, options.Format)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new ArgumentException($"Файлы выбранного формата не найдены: {options.Input}");
        Console.WriteLine($"Режим: {(options.Format == InputFormat.Argo ? "АРГО-ЖБ" : "BearingCapacity (.bdf)")}; файлов: {files.Length}");
        if (options.Format == InputFormat.Bdf)
            Console.WriteLine("Каждый .bdf экспортируется как одна типовая балка. Замечания и исходные параметры — в conversion-report.json.");
        else Console.WriteLine("Замечания по конвертации АРГО — в conversion-report.json.");
        if (options.Interactive && !single)
        {
            for (int i = 0; i < files.Length; i++) Console.WriteLine($"{i + 1}: {Path.GetRelativePath(sourceRoot, files[i])}");
            Console.Write("Номер файла [Enter — все файлы]: ");
            string selection = Console.ReadLine()?.Trim() ?? "";
            if (selection.Length != 0)
            {
                if (!int.TryParse(selection, out int number) || number < 1 || number > files.Length)
                    throw new ArgumentException("Номер файла вне списка.");
                files = new[] { files[number - 1] };
            }
        }
        Directory.CreateDirectory(options.Output);
        int success = 0, failed = 0, withWarnings = 0;
        var reports = new List<ReportEntry>();
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(sourceRoot, file);
            BdfDocument? source = null;
            try
            {
                PrssmDocument document;
                IReadOnlyList<string> warnings = Array.Empty<string>();
                if (options.Format == InputFormat.Bdf)
                {
                    source = new BdfParser().Parse(file);
                    var result = new BdfToPrssmConverter().Convert(source);
                    document = result.Document; warnings = result.Warnings;
                }
                else
                {
                    var result = ConvertArgo(file);
                    document = result.Document; warnings = result.Warnings;
                }
                string name = options.Format == InputFormat.Argo ? Path.GetFileName(file).Replace('.', '_') : Path.GetFileNameWithoutExtension(file);
                string target = Path.Combine(options.Output, Path.GetDirectoryName(relative) ?? "", name + ".prssm");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                SaveJson(target, document);
                Console.WriteLine($"✓ {relative}{(warnings.Count > 0 ? $" — замечаний: {warnings.Count}" : "")}");
                foreach (string warning in warnings)
                    Console.WriteLine($"  WARNING: {relative}: {warning}");
                success++;
                if (warnings.Count > 0) withWarnings++;
                reports.Add(new(relative, Path.GetRelativePath(options.Output, target), source, warnings, null));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"✗ {relative}: {error.Message}");
                failed++;
                reports.Add(new(relative, null, source, Array.Empty<string>(), error.Message));
            }
        }
        SaveJson(Path.Combine(options.Output, "conversion-report.json"), reports);
        Console.WriteLine($"Итого: {success} успешно, {failed} ошибок, {withWarnings} с замечаниями. Результаты: {options.Output}");
        return failed == 0 ? 0 : 1;
    }

    private static (PrssmDocument Document, IReadOnlyList<string> Warnings) ConvertArgo(string file)
    {
        var output = Console.Out;
        using var log = new StringWriter();
        try
        {
            Console.SetOut(log);
            var document = new ArgoToPrssmConverter().Convert(new ArgoParser().Parse(file));
            var warnings = log.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).Where(line => line.StartsWith("WARNING:", StringComparison.Ordinal))
                .Select(line => line["WARNING:".Length..].Trim()).Distinct().ToArray();
            return (document, warnings);
        }
        finally { Console.SetOut(output); }
    }

    private static void SaveJson<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Accepts(string path, InputFormat format)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (format == InputFormat.Bdf) return ext == ".bdf";
        if (new[] { ".bdf", ".json", ".prssm", ".txt", ".doc", ".docx", ".pdf", ".exe", ".dll", ".hex", ".bat", ".cmd", ".html", ".htm", ".js", ".css", ".lic", ".sys", ".aal", ".log", ".tmp", ".bak", ".xml", ".ini", ".cfg", ".gru" }.Contains(ext))
            return false;
        if (ext.Length is >= 3 and <= 4 && char.IsDigit(ext[1]) && char.IsDigit(ext[2])) return true;
        string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        return ext == ".dat" || (name.Length >= 2 && "ABNSI".Contains(name[0]) && char.IsDigit(name[1]));
    }

    private static void Help() => Console.WriteLine("""
        Запуск без аргументов: выбор формата, пути и одного/всех файлов.
        PRSSM-Parser --format argo|bdf [--input <файл или папка>] [--output <папка>]
        По умолчанию: Data/RAW для АРГО, Data/BDF для BearingCapacity рядом с программой.
        Выход: Data/PRSSM для АРГО, Data/PRSSM/BDF для BearingCapacity.
        Коды завершения: 0 — успешно, 1 — ошибки отдельных файлов, 2 — ошибка запуска.
        """);
}
