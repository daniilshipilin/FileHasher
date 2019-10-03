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
        readonly string _connectionString;

        public SQLiteDBAccess(string connectionStringsSection = "Default")
        {
            _connectionString = GetConnectionString(connectionStringsSection);
            CheckDBExists();
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

        public void Delete_FilePath(FilePathsDBModel model)
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "DELETE FROM FilePaths " +
                               "WHERE FilePathID = @FilePathID;";

                cnn.Execute(query, model);
            }
        }

        public int Select_CountRecords()
        {
            using (IDbConnection cnn = new SQLiteConnection(_connectionString))
            {
                string query = "SELECT COUNT(*) " +
                               "FROM FilePaths;";

                var output = cnn.ExecuteScalar<int>(query);

                return (output);
            }
        }

        #endregion
    }
}
