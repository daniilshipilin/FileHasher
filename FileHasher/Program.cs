using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace FileHasher
{
    class Program
    {
        #region Program configuration

        const string VERSION_INFO = "";

#if DEBUG
        const string BUILD_CONFIGURATION = " [Debug]";
#else
        const string BUILD_CONFIGURATION = "";
#endif

        #endregion

        #region Build/program info

        public static Version Ver { get; } = new AssemblyName(Assembly.GetExecutingAssembly().FullName).Version;
        public static string ProgramVersion { get; } = $"{Ver.Major}.{Ver.Minor}.{Ver.Build}";
        public static string ProgramBaseDirectory { get; } = AppDomain.CurrentDomain.BaseDirectory;
        public static string ProgramPath { get; } = Assembly.GetEntryAssembly().Location;
        public static string ProgramName { get; } = Assembly.GetExecutingAssembly().GetName().Name;
        public static string ProgramHeader { get; } = $"{ProgramName} v{ProgramVersion}{VERSION_INFO}{BUILD_CONFIGURATION}";

        #endregion

        enum MessageType
        {
            Default,
            FileModified,
            FileDeleted,
            FileNew,
            Exception
        }

        static string RootFolderPath { get; set; } = $"{ProgramBaseDirectory}Test";
        static string CsvFilePath => $"{ProgramBaseDirectory}Hashes.csv";
        static string LogFilePath => $"{ProgramBaseDirectory}Output.log";
        static string CsvFileLastHashPath => $"{ProgramBaseDirectory}last_hash";
        static string[] FileExtensions { get; set; } = new string[] { ".txt", ".log" };
        static string LogTimestampFormat => "yyyy-MM-dd HH:mm:ss.fff";
        static List<string> LogBufer { get; } = new List<string>();
        static bool WaitBeforeExit { get; set; } = false;
        static bool FolderCleanupRequired { get; set; } = false;

        static void Main(string[] args)
        {
            try
            {
                // parse args
                if (args.Length == 1 && args[0].Equals("/?"))
                {
                    Console.WriteLine(ProgramHeader);
                    Console.WriteLine("Program usage:");
                    Console.WriteLine($"  {ProgramName} [{nameof(RootFolderPath)}] [{nameof(FileExtensions)}] [-wait|-nowait] [-clean|-noclean]");
                    return;
                }

                ConsolePrint(ProgramHeader);

                if (args.Length >= 1) { RootFolderPath = Path.GetFullPath(args[0]); }

                if (args.Length >= 2) { FileExtensions = args[1].Split(','); }

                if (args.Length >= 3)
                {
                    if (args[2].Equals("-wait")) { WaitBeforeExit = true; }
                    else { WaitBeforeExit = false; }
                }

                if (args.Length >= 4)
                {
                    if (args[3].Equals("-clean")) { FolderCleanupRequired = true; }
                    else { FolderCleanupRequired = false; }
                }

                // print args
                ConsolePrint($"{nameof(RootFolderPath)}={RootFolderPath}");
                ConsolePrint($"{nameof(FileExtensions)}={string.Join(",", FileExtensions)}");
                ConsolePrint($"{nameof(WaitBeforeExit)}={WaitBeforeExit}");
                ConsolePrint($"{nameof(FolderCleanupRequired)}={FolderCleanupRequired}");

                if (!Directory.Exists(RootFolderPath))
                {
                    throw new DirectoryNotFoundException($"Directory '{RootFolderPath}' doesn't exist");
                }

                var csv = new List<CSVFile>();
                int filesModified = 0;
                int filesDeleted = 0;
                int filesNew = 0;

                if (File.Exists(CsvFilePath))
                {
                    // check last hash
                    if (File.Exists(CsvFileLastHashPath))
                    {
                        if (!CalculateSHA256(CsvFilePath).Equals(File.ReadAllText(CsvFileLastHashPath)))
                        {
                            throw new Exception($"'{CsvFilePath}' file hash mismatch");
                        }
                    }
                    else
                    {
                        File.WriteAllText(CsvFileLastHashPath, CalculateSHA256(CsvFilePath));
                    }

                    // read file contents
                    foreach (var line in File.ReadAllLines(CsvFilePath))
                    {
                        var split = line.Split(';');
                        csv.Add(new CSVFile()
                        {
                            FilePath = split[0],
                            LastWriteTimeUtc = long.Parse(split[1]),
                            FileSHA256 = split[2]
                        });
                    }

                    var filePaths = Directory.EnumerateFiles(RootFolderPath, "*.*", SearchOption.AllDirectories)
                                    .Where(s => FileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

                    // find files removed                    
                    for (int i = 0; i < csv.Count; i++)
                    {
                        bool fileRemoved = true;

                        foreach (var path in filePaths)
                        {
                            if (csv[i].FilePath.Equals(path))
                            {
                                fileRemoved = false;
                                long lastWrite = File.GetLastWriteTime(path).ToFileTimeUtc();

                                // update record, if file modification time is different
                                if (csv[i].LastWriteTimeUtc != lastWrite)
                                {
                                    csv[i].LastWriteTimeUtc = lastWrite;
                                    string tmpHash = CalculateSHA256(path);

                                    if (csv[i].FileSHA256.Equals(tmpHash))
                                    {
                                        // TODO: message about file last write time mismatch only
                                        ConsolePrint($"File '{path}' has last write date mismatch, but hashes are identical");
                                    }
                                    else
                                    {
                                        filesModified++;
                                        csv[i].FileSHA256 = tmpHash;
                                        ConsolePrint($"[MODIFIED]  '{path}' ({csv[i].FileSHA256Short})", MessageType.FileModified);
                                    }

                                }

                                break;
                            }
                        }

                        if (fileRemoved)
                        {
                            filesDeleted++;
                            ConsolePrint($"[DELETED]  '{csv[i].FilePath}'", MessageType.FileDeleted);
                            csv.RemoveAt(i);
                            i--; // decreasing index, because of previosly deleted element
                        }
                    }

                    // find files added
                    for (int i = 0; i < filePaths.Count; i++)
                    {
                        bool fileAdded = true;

                        foreach (var record in csv)
                        {
                            if (filePaths[i].Equals(record.FilePath))
                            {
                                fileAdded = false;
                                break;
                            }
                        }

                        if (fileAdded)
                        {
                            filesNew++;

                            csv.Add(new CSVFile()
                            {
                                FilePath = filePaths[i],
                                LastWriteTimeUtc = File.GetLastWriteTime(filePaths[i]).ToFileTimeUtc(),
                                FileSHA256 = CalculateSHA256(filePaths[i])
                            });

                            ConsolePrint($"[NEW]  '{filePaths[i]}' ({csv.Last().FileSHA256Short})", MessageType.FileNew);
                        }
                    }
                }
                else
                {
                    var filePaths = Directory.EnumerateFiles(RootFolderPath, "*.*", SearchOption.AllDirectories)
                                    .Where(s => FileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

                    if (filePaths.Count() == 0) { throw new Exception("No files to process"); }

                    // create csv records
                    foreach (var path in filePaths)
                    {
                        filesNew++;

                        csv.Add(new CSVFile()
                        {
                            FilePath = path,
                            LastWriteTimeUtc = File.GetLastWriteTime(path).ToFileTimeUtc(),
                            FileSHA256 = CalculateSHA256(path)
                        });

                        ConsolePrint($"[NEW]  '{path}' ({csv.Last().FileSHA256Short})", MessageType.FileNew);
                    }
                }

                if (filesModified > 0 || filesDeleted > 0 || filesNew > 0)
                {
                    ConsolePrint($"Updating '{CsvFilePath}'");

                    var csvSorted = csv.OrderBy(x => x.FilePath).ToList();
                    var result = new List<string>();

                    foreach (var record in csvSorted)
                    {
                        result.Add(record.GetRecordString());
                    }

                    // write csv records to the target file
                    File.WriteAllLines(CsvFilePath, result, new UTF8Encoding(false));

                    // calculate file hash
                    File.WriteAllText(CsvFileLastHashPath, CalculateSHA256(CsvFilePath));

                    if (filesModified > 0) { ConsolePrint($"Files modified: {filesModified}"); }
                    if (filesDeleted > 0) { ConsolePrint($"Files deleted: {filesDeleted}"); }
                    if (filesNew > 0) { ConsolePrint($"Files new: {filesNew}"); }

                    ConsolePrint($"Total records count: {result.Count}");
                }
                else
                {
                    ConsolePrint("Update not required");
                }

                if (FolderCleanupRequired)
                {
                    var filePaths = Directory.EnumerateFiles(RootFolderPath, "*.*", SearchOption.AllDirectories)
                                    .Where(s => !FileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

                    if (filePaths.Count == 0)
                    {
                        ConsolePrint("Folder cleanup not required");
                    }
                    else
                    {
                        int cleanupFilesDeleted = 0;

                        foreach (var path in filePaths)
                        {
                            cleanupFilesDeleted++;
                            File.Delete(path);
                            ConsolePrint($"File '{path}' deleted");
                        }

                        ConsolePrint($"Folder cleanup deleted files: {cleanupFilesDeleted}");
                    }
                }
            }
            catch (Exception ex)
            {
                ConsolePrint(ex.ToString(), MessageType.Exception);
            }

            try
            {
                // write log file
                File.AppendAllLines(LogFilePath, LogBufer, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            if (WaitBeforeExit)
            {
                Console.WriteLine("Press ENTER to exit");
                Console.ReadLine();
            }
        }

        //private static string CalculateSHA256(string filePath)
        //{
        //    byte[] result;

        //    using (var sr = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        //    {
        //        using (var sha256 = SHA256.Create()) { result = sha256.ComputeHash(sr); }
        //    }

        //    var sb = new StringBuilder();

        //    for (int i = 0; i < result.Length; i++)
        //    {
        //        sb.Append(result[i].ToString("x2"));
        //    }

        //    return (sb.ToString());
        //}

        private static string CalculateSHA256(string filePath)
        {
            byte[] result;

            using (var sha256 = SHA256.Create())
            {
                result = sha256.ComputeHash(File.ReadAllBytes(filePath));
            }

            return (BitConverter.ToString(result).Replace("-", string.Empty).ToLower());
        }

        private static void ConsolePrint(string message, MessageType messageType = MessageType.Default)
        {
            if (messageType != MessageType.Default)
            {
                if (messageType == MessageType.FileDeleted)
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else if (messageType == MessageType.FileModified)
                {
                    Console.BackgroundColor = ConsoleColor.DarkMagenta;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else if (messageType == MessageType.FileNew)
                {
                    Console.BackgroundColor = ConsoleColor.DarkGreen;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else if (messageType == MessageType.Exception)
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
            }

            Console.WriteLine(message);
            Console.ResetColor();
            LogBufer.Add($"{(DateTime.Now).ToString(LogTimestampFormat).PadRight(25)} {message}");
        }
    }
}
