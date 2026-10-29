using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArgoParser
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  КОНВЕРТЕР АРГО → PRSSM                                      ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            // Папки данных находятся рядом с исполняемым файлом.
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string rawFolder = Path.Combine(baseDir, "Data", "RAW");
            string prssmFolder = Path.Combine(baseDir, "Data", "PRSSM");

            Console.WriteLine($"Папка RAW: {rawFolder}");

            // Создаём папки если не существуют
            if (!Directory.Exists(rawFolder))
            {
                Directory.CreateDirectory(rawFolder);
                Console.WriteLine($"Создана папка: {rawFolder}");
                Console.WriteLine("Поместите файлы АРГО в эту папку и запустите программу снова.");
                WaitForExit();
                return;
            }

            if (!Directory.Exists(prssmFolder))
            {
                Directory.CreateDirectory(prssmFolder);
            }

            // Ищем все файлы АРГО в папке RAW
            var files = Directory.GetFiles(rawFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => IsArgoFile(f))
                .OrderBy(f => f)
                .ToArray();

            if (files.Length == 0)
            {
                Console.WriteLine($"Файлы АРГО не найдены в папке: {rawFolder}");
                WaitForExit();
                return;
            }

            Console.WriteLine($"Папка RAW: {rawFolder}");
            Console.WriteLine($"Папка PRSSM: {prssmFolder}");           
            int _counter = 1;
            foreach (var file in files)
            {
                var name = file.Split("\\");
                Console.WriteLine($"{_counter}: {name[name.Length-1]}");
                _counter++;
            }
            Console.WriteLine($"Найдено файлов АРГО: {files.Length}\n");
            Console.WriteLine(new string('═', 70));

            Console.WriteLine("Укажите порядковый номер файла для конвертации. Для конвертации всех файлов в папке RAW нажмите Enter");
            int success = 0, failed = 0;
            var errors = new List<string>(); 
            
            string input = Console.ReadLine();
            if (int.TryParse(input, out int number))
            {
                for (int i = 0; i < files.Length; i++)
                    if (number == i + 1)
                    { 
                        ReadFile(files[i], rawFolder, prssmFolder, ref success, ref errors, ref failed);
                        break;
                    }
            }
            else
            {
                foreach (var file in files)
                    ReadFile(file, rawFolder, prssmFolder, ref success, ref errors, ref failed);
            }
            Console.WriteLine(new string('═', 70));
            Console.WriteLine($"\nИТОГО: {success} успешно, {failed} ошибок из {files.Length} файлов");
            Console.WriteLine($"Результаты в: {prssmFolder}");

            if (errors.Count > 0 && errors.Count <= 15)
            {
                Console.WriteLine("\nОШИБКИ:");
                foreach (var err in errors)
                    Console.WriteLine($"  • {err}");
            }
            else if (errors.Count > 15)
            {
                Console.WriteLine($"\nОШИБКИ ({errors.Count} шт., показаны первые 15):");
                foreach (var err in errors.Take(15))
                    Console.WriteLine($"  • {err}");
            }

            WaitForExit();
        }

        private static void ReadFile(string file, string rawFolder, string prssmFolder, ref int success, ref List<string> errors, ref int failed)
        {
            string relativePath = Path.GetRelativePath(rawFolder, file);
            string fileName = Path.GetFileName(file);

            Console.Write($"  {relativePath,-50} ");

            try
            {
                // 1) Парсим файл АРГО
                var parser = new ArgoParser();
                var doc = parser.Parse(file);

                // 2) Конвертируем в PRSSM
                var converter = new ArgoToPrssmConverter();
                var prssmDoc = converter.Convert(doc);

                // 3) Определяем выходной путь (сохраняем структуру подпапок)
                string relativeDir = Path.GetDirectoryName(relativePath) ?? "";
                string outputDir = string.IsNullOrEmpty(relativeDir)
                    ? prssmFolder
                    : Path.Combine(prssmFolder, relativeDir);

                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                string outName = fileName.Replace(".", "_") + ".prssm";
                string outPath = Path.Combine(outputDir, outName);

                // 4) Сохраняем JSON
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                string json = JsonSerializer.Serialize(prssmDoc, options);
                File.WriteAllText(outPath, json);

                Console.WriteLine($"✓");
                success++;
            }
            catch (Exception ex)
            {
                string errMsg = ex.Message.Length > 30 ? ex.Message.Substring(0, 30) + "..." : ex.Message;
                Console.WriteLine($"✗ {errMsg}");
                errors.Add($"{relativePath}: {ex.Message}");
                failed++;
            }
        }

        static void WaitForExit()
        {
            Console.WriteLine("\nНажмите Enter для выхода...");
            Console.ReadLine();
        }

        static bool IsArgoFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();

            // Пропускаем служебные расширения
            var skipExtensions = new[] {
                ".json", ".prssm", ".txt", ".doc", ".docx", ".pdf", ".exe", ".dll",
                ".hex", ".bat", ".cmd", ".html", ".htm", ".js", ".css", ".lic", ".sys",
                ".aal", ".log", ".tmp", ".bak", ".xml", ".ini", ".cfg", ".gru"
            };

            if (skipExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase)))
                return false;

            // Расширения .01-.99 или с буквой (.02p, .05d)
            if (ext.Length >= 2 && ext.Length <= 4)
            {
                string extPart = ext.Substring(1);
                if (extPart.Length >= 2 && char.IsDigit(extPart[0]) && char.IsDigit(extPart[1]))
                    return true;
            }

            // Имя файла начинается с A0-A9, B0-B9, N0-N9, S0-S9, I0-I9
            if (name.Length >= 2 && "ABNSI".Contains(name[0]) && char.IsDigit(name[1]))
                return true;

            // .dat файлы могут быть АРГО
            if (ext == ".dat")
                return true;

            return false;
        }
    }
}
