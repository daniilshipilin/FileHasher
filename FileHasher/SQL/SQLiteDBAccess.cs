using Dapper;
using FileHasher.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;

namespace FileHasher.SQL
{
    public class SQLiteDBAccess
    {
        public const int DATABASE_VERSION = 3;

        readonly string _connectionString;

        public SQLiteDBAccess(string connectionStringsSection = "Default")
        {
            _connectionString = GetConnectionString(connectionStringsSection);
            CheckDBExists();
            CheckDBVersion();
        }

        private void CheckDBExists()
        {
            var builder = new SQLiteConnectionStringBuilder(_connectionString);
            string dbFilePath = Path.GetFullPath(builder.DataSource);

            if (!File.Exists(dbFilePath))
            {
                if (!Directory.Exists(Path.GetDirectoryName(dbFilePath)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dbFilePath));
                }

                // create new default db
                File.WriteAllBytes(dbFilePath, Properties.Resources.DefaultDBFile);
            }
        }

        private void CheckDBVersion()
        {
            var settings = Select_Settings();
            bool checkPassed = false;

            foreach (var setting in settings)
            {
                if (setting.SettingKey.Equals("DatabaseVersion"))
                {
                    checkPassed = (int.Parse(setting.SettingValue) == DATABASE_VERSION);
                    break;
                }
            }

            if (!checkPassed)
            {
                throw new Exception($"DB version check failed. Currently db supported version is '{DATABASE_VERSION}'.");
            }
        }

        private string GetConnectionString(string name)
        {
            string connectionString;

            try
            {
                connectionString = ConfigurationManager.ConnectionStrings[name].ConnectionString;
            }
            catch (NullReferenceException)
            {
                throw new Exception("Requested connection string name doesn't exist in App.config");
            }

            return (connectionString);
        }

        #region DB queries

        public List<SettingsDBModel> Select_Settings()
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "SELECT * " +
                               "FROM Settings;";

                var output = cnn.Query<SettingsDBModel>(query);

                return (output.ToList());
            }
        }

        public List<FilePathsDBModel> Select_FilePaths()
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "SELECT * " +
                               "FROM FilePaths " +
                               "ORDER BY FilePath ASC;";

                var output = cnn.Query<FilePathsDBModel>(query);

                return (output.ToList());
            }
        }

        public int Select_FilePathID(FilePathsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "SELECT FilePathID " +
                               "FROM FilePaths " +
                               "WHERE FilePath = @FilePath;";

                int output = cnn.ExecuteScalar<int>(query, model);

                return (output);
            }
        }

        public byte[] Select_Blob(FilePathsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "SELECT BlobData " +
                               "FROM Blobs " +
                               "WHERE FK_FilePathID = @FilePathID;";

                var output = cnn.QuerySingle<byte[]>(query, model);

                return (output);
            }
        }

        public void Insert_FilePath(FilePathsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "INSERT INTO FilePaths (FilePath, LastWriteTimeUtc, HashAlgorithm, FileHash) " +
                               "VALUES (@FilePath, @LastWriteTimeUtc, @HashAlgorithm, @FileHash);";

                cnn.Execute(query, model);
            }
        }

        public void Update_FilePath(FilePathsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "UPDATE FilePaths " +
                               "SET FilePath = @FilePath, LastWriteTimeUtc = @LastWriteTimeUtc, HashAlgorithm = @HashAlgorithm, FileHash = @FileHash " +
                               "WHERE FilePathID = @FilePathID;";

                cnn.Execute(query, model);
            }
        }

        public void Update_LastWriteTimeUtc(FilePathsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "UPDATE FilePaths " +
                               "SET LastWriteTimeUtc = @LastWriteTimeUtc " +
                               "WHERE FilePathID = @FilePathID;";

                cnn.Execute(query, model);
            }
        }

        public void Delete_FilePath(FilePathsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "DELETE FROM FilePaths " +
                               "WHERE FilePathID = @FilePathID;";

                cnn.Execute(query, model);
            }
        }

        public void Insert_Blob(BlobsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "INSERT INTO Blobs (FK_FilePathID, BlobData) " +
                               "VALUES (@FK_FilePathID, @BlobData);";

                cnn.Execute(query, model);
            }
        }

        public void Update_Blob(BlobsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "UPDATE Blobs " +
                               "SET BlobData = @BlobData " +
                               "WHERE FK_FilePathID = @FK_FilePathID;";

                cnn.Execute(query, model);
            }
        }

        public void Delete_Blob(FilePathsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "DELETE FROM Blobs " +
                               "WHERE FK_FilePathID = @FilePathID;";

                cnn.Execute(query, model);
            }
        }

        public int Select_CountRecords_FilePaths()
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "SELECT COUNT(*) " +
                               "FROM FilePaths;";

                int output = cnn.ExecuteScalar<int>(query);

                return (output);
            }
        }

        public void CompactDatabase()
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "VACUUM main;";

                cnn.Execute(query);
            }
        }

        #endregion
    }
}
