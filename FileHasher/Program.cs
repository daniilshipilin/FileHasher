using FileHasher.Models;
using FileHasher.SQL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace FileHasher
{
    static class Program
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
            FileRestored,
            Exception
        }

        const string LOG_TIMESTAMP_FORMAT = "yyyy-MM-dd HH:mm:ss.fff";
        const string HASH_ALGORITHM = "SHA256";

        static readonly string _logFilePath = $"{ProgramBaseDirectory}Output.log";
        static readonly List<string> _logBufer = new List<string>();

        static SQLiteDBAccess _sqlite;

        static string _rootFolderPath;
        static string[] _fileExtensions;
        static bool _waitBeforeExit = false;
        static bool _folderCleanupScheduled = false;
        static bool _dbUpdatePrompt = false;
        static bool _dbOptimizationScheduled = false;
        static bool _restoreFilesFromDB = false;

        static void Main(string[] args)
        {
            try
            {
                // parse args
                if ((args.Length == 1 && args[0].Equals("/?")) || args.Length < 2)
                {
                    Console.WriteLine(ProgramHeader);
                    Console.WriteLine("Program usage:");
                    Console.WriteLine($"  {ProgramName} \"Root folder path\" \"File extensions list (comma separated)\" [-wait] [-clean] [-prompt] [-optimize] [-restore]");
                    return;
                }

                ConsolePrint(ProgramHeader);

                if (args.Length >= 1) { _rootFolderPath = FormatPath(args[0]); }

                if (args.Length >= 2) { _fileExtensions = args[1].Split(','); }

                if (args.Length >= 3)
                {
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (args[i].Equals("-wait")) { _waitBeforeExit = true; }
                        else if (args[i].Equals("-clean")) { _folderCleanupScheduled = true; }
                        else if (args[i].Equals("-prompt")) { _dbUpdatePrompt = true; }
                        else if (args[i].Equals("-optimize")) { _dbOptimizationScheduled = true; }
                        else if (args[i].Equals("-restore")) { _restoreFilesFromDB = true; }
                    }
                }

                if (!Directory.Exists(_rootFolderPath))
                {
                    throw new DirectoryNotFoundException($"Directory '{_rootFolderPath}' doesn't exist");
                }

                // set root folder path in the FilePathsDBModel class
                FilePathsDBModel.SetRootFolderPath(_rootFolderPath);

                // print args
                ConsolePrint($"Root folder path = {_rootFolderPath}");
                ConsolePrint($"File extensions = {string.Join(",", _fileExtensions)}");
                ConsolePrint($"Restore files from DB = {_restoreFilesFromDB}");
                ConsolePrint($"DB update prompt = {_dbUpdatePrompt}");
                ConsolePrint($"Folder cleanup scheduled = {_folderCleanupScheduled}");
                ConsolePrint($"DB optimization scheduled = {_dbOptimizationScheduled}");
                ConsolePrint($"Wait before exit = {_waitBeforeExit}");

                _sqlite = new SQLiteDBAccess();

                if (_restoreFilesFromDB) { RestoreFilesFromDB(); }
                else { HashAndBackupFilesToDB(); }

                ConsolePrint($"Total file paths in DB: {_sqlite.Select_CountRecords_FilePaths()}");
                ConsolePrint($"DB revision: {_sqlite.Select_DatabaseRevision()}");

                // compact db
                if (_dbOptimizationScheduled)
                {
                    ConsolePrint("Optimizing db");
                    _sqlite.CompactDatabase();
                }

                // clean root folder
                if (_folderCleanupScheduled)
                {
                    var filePathsToDelete = Directory.EnumerateFiles(_rootFolderPath, "*.*", SearchOption.AllDirectories)
                                    .Where(s => !_fileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

                    if (filePathsToDelete.Count == 0)
                    {
                        ConsolePrint("Folder cleanup not required");
                    }
                    else
                    {
                        int cleanupFilesDeleted = 0;

                        foreach (var path in filePathsToDelete)
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
            finally
            {
                try
                {
                    // write log file
                    File.AppendAllLines(_logFilePath, _logBufer, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }

            if (_waitBeforeExit)
            {
                Console.WriteLine("Press ENTER to exit");
                Console.ReadLine();
            }
        }

        private static void RestoreFilesFromDB()
        {
            int filesRestored = 0;

            var dbFilePaths = _sqlite.Select_FilePaths();

            var filePaths = Directory.EnumerateFiles(_rootFolderPath, "*.*", SearchOption.AllDirectories)
                            .Where(s => _fileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

            for (int i = 0; i < dbFilePaths.Count; i++)
            {
                bool fileDeleted = true;

                foreach (var path in filePaths)
                {
                    if (dbFilePaths[i].FileFullPath.Equals(path))
                    {
                        fileDeleted = false;
                        long lastWrite = File.GetLastWriteTime(dbFilePaths[i].FileFullPath).ToFileTimeUtc();

                        if (dbFilePaths[i].LastWriteTimeUtc != lastWrite)
                        {
                            dbFilePaths[i].LastWriteTimeUtc = lastWrite;
                            string tmpHash = CalculateHash(File.ReadAllBytes(dbFilePaths[i].FileFullPath));

                            if (dbFilePaths[i].FileHash.Equals(tmpHash))
                            {
                                _sqlite.Update_LastWriteTimeUtc(dbFilePaths[i]);
                                ConsolePrint($"'{dbFilePaths[i].FileFullPath}' has different last write timestamp, but hashes are identical");
                            }
                            else
                            {
                                if (DisplayPromptYesNo($"'{dbFilePaths[i].FileFullPath}' was modified. Restore from db? (y/n):"))
                                {
                                    filesRestored++;
                                    RestoreFile(dbFilePaths[i].FileFullPath, _sqlite.Select_Blob(dbFilePaths[i]));

                                    // update last write timestamp
                                    dbFilePaths[i].LastWriteTimeUtc = File.GetLastWriteTime(dbFilePaths[i].FileFullPath).ToFileTimeUtc();
                                    _sqlite.Update_LastWriteTimeUtc(dbFilePaths[i]);

                                    ConsolePrint($"[RESTORED - MODIFIED]  '{dbFilePaths[i].FileFullPath}' ({dbFilePaths[i].FileHashShort})", MessageType.FileRestored);
                                }
                            }
                        }

                        break;
                    }
                }

                if (fileDeleted)
                {
                    if (DisplayPromptYesNo($"'{dbFilePaths[i].FileFullPath}' was deleted. Restore from db? (y/n):"))
                    {
                        filesRestored++;
                        RestoreFile(dbFilePaths[i].FileFullPath, _sqlite.Select_Blob(dbFilePaths[i]));

                        dbFilePaths[i].LastWriteTimeUtc = File.GetLastWriteTime(dbFilePaths[i].FileFullPath).ToFileTimeUtc();
                        _sqlite.Update_LastWriteTimeUtc(dbFilePaths[i]);

                        ConsolePrint($"[RESTORED - DELETED]  '{dbFilePaths[i].FileFullPath}' ({dbFilePaths[i].FileHashShort})", MessageType.FileRestored);
                    }
                }
            }

            if (filesRestored > 0)
            {
                ConsolePrint($"Files restored: {filesRestored}");
            }
            else { ConsolePrint("Restore not required"); }
        }

        private static void HashAndBackupFilesToDB()
        {
            int filesModified = 0;
            int filesDeleted = 0;
            int filesNew = 0;

            var dbFilePaths = _sqlite.Select_FilePaths();

            var filePaths = Directory.EnumerateFiles(_rootFolderPath, "*.*", SearchOption.AllDirectories)
                            .Where(s => _fileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

            // find deleted files
            for (int i = 0; i < dbFilePaths.Count; i++)
            {
                bool fileIsDeleted = true;

                foreach (var path in filePaths)
                {
                    if (dbFilePaths[i].FileFullPath.Equals(path))
                    {
                        fileIsDeleted = false;
                        long lastWrite = File.GetLastWriteTime(dbFilePaths[i].FileFullPath).ToFileTimeUtc();

                        // update record, if file modification time is different
                        if (dbFilePaths[i].LastWriteTimeUtc != lastWrite)
                        {
                            dbFilePaths[i].LastWriteTimeUtc = lastWrite;
                            string tmpHash = CalculateHash(File.ReadAllBytes(dbFilePaths[i].FileFullPath));

                            if (dbFilePaths[i].FileHash.Equals(tmpHash))
                            {
                                _sqlite.Update_LastWriteTimeUtc(dbFilePaths[i]);
                                ConsolePrint($"'{dbFilePaths[i].FileFullPath}' has different last write timestamp, but hashes are identical");
                            }
                            else
                            {
                                if (DisplayPromptYesNo($"'{dbFilePaths[i].FileFullPath}' was modified. Update db? (y/n):"))
                                {
                                    filesModified++;
                                    dbFilePaths[i].FileHash = tmpHash;

                                    if (!dbFilePaths[i].HashAlgorithm.Equals(HASH_ALGORITHM))
                                    {
                                        ConsolePrint($"'{dbFilePaths[i].FileFullPath}' was hashed using {dbFilePaths[i].HashAlgorithm} - update using {HASH_ALGORITHM}");
                                        dbFilePaths[i].HashAlgorithm = HASH_ALGORITHM;
                                    }
                                    _sqlite.Update_FilePath(dbFilePaths[i]);

                                    using (var blob = new BlobsDBModel() { FK_FilePathID = dbFilePaths[i].FilePathID, BlobData = File.ReadAllBytes(dbFilePaths[i].FileFullPath) })
                                    {
                                        // update file blob
                                        _sqlite.Update_Blob(blob);
                                    }

                                    ConsolePrint($"[MODIFIED]  '{dbFilePaths[i].FileFullPath}' ({dbFilePaths[i].FileHashShort})", MessageType.FileModified);
                                }
                            }
                        }

                        break;
                    }
                }

                if (fileIsDeleted)
                {
                    if (DisplayPromptYesNo($"'{dbFilePaths[i].FileFullPath}' was deleted. Update db? (y/n):"))
                    {
                        filesDeleted++;
                        _sqlite.Delete_FilePath(dbFilePaths[i]);

                        // delete file blob
                        _sqlite.Delete_Blob(dbFilePaths[i]);

                        ConsolePrint($"[DELETED]  '{dbFilePaths[i].FileFullPath}' ({dbFilePaths[i].FileHashShort})", MessageType.FileDeleted);
                        dbFilePaths.RemoveAt(i);
                        i--; // decreasing index, because of previosly deleted element
                    }
                }
            }

            // find new files
            for (int i = 0; i < filePaths.Count; i++)
            {
                bool fileIsNew = true;

                foreach (var record in dbFilePaths)
                {
                    if (filePaths[i].Equals(record.FileFullPath))
                    {
                        fileIsNew = false;
                        break;
                    }
                }

                if (fileIsNew)
                {
                    if (DisplayPromptYesNo($"'{filePaths[i]}' was added. Update db? (y/n):"))
                    {
                        filesNew++;

                        dbFilePaths.Add(new FilePathsDBModel()
                        {
                            FilePath = RemovePathRootFromPath(filePaths[i]),
                            LastWriteTimeUtc = File.GetLastWriteTime(filePaths[i]).ToFileTimeUtc(),
                            HashAlgorithm = HASH_ALGORITHM,
                            FileHash = CalculateHash(File.ReadAllBytes(filePaths[i]))
                        });

                        _sqlite.Insert_FilePath(dbFilePaths.Last());

                        // select FilePathID from last insert operation
                        int filePathID = _sqlite.Select_FilePathID(dbFilePaths.Last());

                        using (var blob = new BlobsDBModel() { FK_FilePathID = filePathID, BlobData = File.ReadAllBytes(dbFilePaths.Last().FileFullPath) })
                        {
                            // write file blob to the db
                            _sqlite.Insert_Blob(blob);
                        }

                        ConsolePrint($"[NEW]  '{dbFilePaths.Last().FileFullPath}' ({dbFilePaths.Last().FileHashShort})", MessageType.FileNew);
                    }
                }
            }

            if (filesModified > 0 || filesDeleted > 0 || filesNew > 0)
            {
                if (filesModified > 0) { ConsolePrint($"Files modified: {filesModified}"); }
                if (filesDeleted > 0) { ConsolePrint($"Files deleted: {filesDeleted}"); }
                if (filesNew > 0) { ConsolePrint($"Files new: {filesNew}"); }
            }
            else { ConsolePrint("Update not required"); }
        }

        private static string FormatPath(string path)
        {
            string[] split = Path.GetFullPath(path).Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();

            for (int i = 0; i < split.Length; i++)
            {
                sb.Append(split[i]);

                if (i < split.Length - 1 || i == 0)
                {
                    sb.Append(Path.DirectorySeparatorChar);
                }
            }

            return (sb.ToString());
        }

        private static string RemovePathRootFromPath(string path)
        {
            // full path string 'C:\Music\Chipzel\Super Hexagon\01 - Courtesy.mp3'
            // path root string 'C:\Music'
            // return string 'Chipzel\Super Hexagon\01 - Courtesy.mp3'
            return (path.Substring(_rootFolderPath.Length + 1));
        }

        private static bool DisplayPromptYesNo(string message)
        {
            bool result = true;

            if (_dbUpdatePrompt)
            {
                Console.Write(message);

                if (Console.ReadKey(true).Key == ConsoleKey.Y)
                {
                    result = true;
                    Console.WriteLine(" Yes");
                }
                else
                {
                    result = false;
                    Console.WriteLine(" No");
                }
            }

            return (result);
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
                else if (messageType == MessageType.FileRestored)
                {
                    Console.BackgroundColor = ConsoleColor.Cyan;
                    Console.ForegroundColor = ConsoleColor.Black;
                }
                else if (messageType == MessageType.Exception)
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
            }

            Console.WriteLine(message);
            Console.ResetColor();
            _logBufer.Add($"{DateTime.Now.ToString(LOG_TIMESTAMP_FORMAT).PadRight(25)} {message}");
        }

        private static void RestoreFile(string path, byte[] bytes)
        {
            string targetDir = Path.GetDirectoryName(path);

            if (!Directory.Exists(targetDir)) { Directory.CreateDirectory(targetDir); }

            File.WriteAllBytes(path, bytes);
        }

        private static string CalculateHash(string filePath)
        {
            using (var sr = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                using (var hash = SHA256.Create())
                {
                    return (BitConverter.ToString(hash.ComputeHash(sr)).Replace("-", string.Empty).ToLower());
                }
            }
        }

        private static string CalculateHash(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                return (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty).ToLower());
            }
        }
    }
}
