using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using AQMSDataUpdateLibrary.Models;
using AQMSDataUpdateLibrary.Constants;
using AQMSDataUpdateLibrary.Repositories;
using log4net;

namespace AQMSDataUpdateLibrary.Repositories
{

    public interface IBulkDataWriter
    {
        Task BulkInsertAveragesAsync(List<AverageRecord> records);
        Task BulkInsertAveragesToTableAsync(List<AverageRecord> records, string destinationTableName);
        Task BulkUpdateAveragesAsync(List<AverageRecord> records);
        Task BulkInsertParameterReadingsAsync(List<ParameterReading> readings);
    }

    public class BulkDataWriter : IBulkDataWriter
    {
        private readonly string _connectionString;
        private readonly string _averageTableName;
        private readonly string _readingTableName;
        private readonly ILog _log;
        private const int BatchSize = 1000;

        public BulkDataWriter(string connectionString, string averageTableName, string readingTableName)
        {
            _connectionString = connectionString;
            _averageTableName = averageTableName;
            _readingTableName = readingTableName;
            _log = LogManager.GetLogger(typeof(BulkDataWriter));
        }

        public async Task BulkInsertAveragesAsync(List<AverageRecord> records)
        {
            if (records == null || records.Count == 0) return;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // Create staging table
                        string createStagingTable = $@"
                    CREATE TABLE #StagingAverages (
                        StationID INT,
                        DeviceID INT,
                        ParameterID INT,
                        ParameterIDRef INT,
                        Parametervalue FLOAT,
                        SubIndex FLOAT,
                        Type VARCHAR(50),
                        Interval DATETIME,
                        LoggerFlags INT,
                        TypeID INT,
                        CreatedTime DATETIME
                    )";

                        using (var cmd = new SqlCommand(createStagingTable, connection, transaction))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // Bulk insert into staging
                        var dataTable = ConvertToAverageDataTable(records);
                        using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                        {
                            bulkCopy.DestinationTableName = "#StagingAverages";
                            bulkCopy.BatchSize = BatchSize;
                            await bulkCopy.WriteToServerAsync(dataTable);
                        }

                        // INSERT with WHERE NOT EXISTS (same as original code logic)
                        string insertQuery = $@"
                    INSERT INTO {_averageTableName} (
                        StationID, DeviceID, ParameterID, ParameterIDRef,
                        Parametervalue, SubIndex, Type, Interval,
                        LoggerFlags, TypeID, CreatedTime
                    )
                    SELECT 
                        s.StationID, s.DeviceID, s.ParameterID, s.ParameterIDRef,
                        s.Parametervalue, s.SubIndex, s.Type, s.Interval,
                        s.LoggerFlags, s.TypeID, s.CreatedTime
                    FROM #StagingAverages s
                    LEFT JOIN {_averageTableName} t ON
                        s.StationID = t.StationID AND
                        s.DeviceID = t.DeviceID AND
                        s.ParameterID = t.ParameterID AND
                        s.Interval = t.Interval AND
                        s.TypeID = t.TypeID
                    WHERE t.ID IS NULL";  // Same pattern as original code!

                        int rowsInserted;
                        using (var cmd = new SqlCommand(insertQuery, connection, transaction))
                        {
                            cmd.CommandTimeout = 300;
                            rowsInserted = await cmd.ExecuteNonQueryAsync();
                        }

                        transaction.Commit();

                        int duplicates = records.Count - rowsInserted;
                        if (duplicates > 0)
                        {
                            _log.Info($"Bulk inserted {rowsInserted} new records, skipped {duplicates} existing records");
                        }
                        else
                        {
                            _log.Info($"Bulk inserted {rowsInserted} average records");
                        }
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _log.Error("Bulk insert with NOT EXISTS failed", ex);
                        throw;
                    }
                }
            }
        }

        public async Task BulkInsertAveragesToTableAsync(List<AverageRecord> records, string destinationTableName)
        {
            if (records == null || records.Count == 0) return;
            if (string.IsNullOrEmpty(destinationTableName)) return;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        string createStagingTable = $@"
                            CREATE TABLE #StagingAverages (
                                StationID INT,
                                DeviceID INT,
                                ParameterID INT,
                                ParameterIDRef INT,
                                Parametervalue FLOAT,
                                SubIndex FLOAT,
                                Type VARCHAR(50),
                                Interval DATETIME,
                                LoggerFlags INT,
                                TypeID INT,
                                CreatedTime DATETIME
                            )";

                        using (var cmd = new SqlCommand(createStagingTable, connection, transaction))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }

                        var dataTable = ConvertToAverageDataTable(records);
                        using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                        {
                            bulkCopy.DestinationTableName = "#StagingAverages";
                            bulkCopy.BatchSize = BatchSize;
                            await bulkCopy.WriteToServerAsync(dataTable);
                        }

                        string insertQuery = $@"
                            INSERT INTO {destinationTableName} (
                                StationID, DeviceID, ParameterID, ParameterIDRef,
                                Parametervalue, SubIndex, Type, Interval,
                                LoggerFlags, TypeID, CreatedTime
                            )
                            SELECT 
                                s.StationID, s.DeviceID, s.ParameterID, s.ParameterIDRef,
                                s.Parametervalue, s.SubIndex, s.Type, s.Interval,
                                s.LoggerFlags, s.TypeID, s.CreatedTime
                            FROM #StagingAverages s
                            LEFT JOIN {destinationTableName} t ON
                                s.StationID = t.StationID AND
                                s.DeviceID = t.DeviceID AND
                                s.ParameterID = t.ParameterID AND
                                s.Interval = t.Interval AND
                                s.TypeID = t.TypeID
                            WHERE t.ID IS NULL";

                        int rowsInserted;
                        using (var cmd = new SqlCommand(insertQuery, connection, transaction))
                        {
                            cmd.CommandTimeout = 300;
                            rowsInserted = await cmd.ExecuteNonQueryAsync();
                        }

                        transaction.Commit();
                        _log.Info($"Bulk inserted {rowsInserted} records into {destinationTableName}");
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _log.Error($"Bulk insert to {destinationTableName} failed", ex);
                        throw;
                    }
                }
            }
        }

        public async Task BulkUpdateAveragesAsync(List<AverageRecord> records)
        {
            if (records == null || records.Count == 0) return;

            // For updates, we'll use a temp table approach
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // Create temp table
                        string createTempTable = $@"
                            CREATE TABLE #TempAverages (
                                StationID INT,
                                DeviceID INT,
                                ParameterID INT,
                                Parametervalue FLOAT,
                                SubIndex FLOAT,
                                LoggerFlags INT,
                                Interval DATETIME,
                                TypeID INT
                            )";

                        using (var cmd = new SqlCommand(createTempTable, connection, transaction))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // Bulk insert into temp table
                        var dataTable = ConvertToUpdateDataTable(records);
                        using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                        {
                            bulkCopy.DestinationTableName = "#TempAverages";
                            bulkCopy.BatchSize = BatchSize;
                            await bulkCopy.WriteToServerAsync(dataTable);
                        }

                        // Update from temp table
                        string updateQuery = $@"
                            UPDATE t
                            SET t.Parametervalue = s.Parametervalue,
                                t.SubIndex = s.SubIndex,
                                t.LoggerFlags = s.LoggerFlags,
                                t.CreatedTime = GETDATE()
                            FROM {_averageTableName} t
                            INNER JOIN #TempAverages s ON 
                                t.StationID = s.StationID AND
                                t.DeviceID = s.DeviceID AND
                                t.ParameterID = s.ParameterID AND
                                t.Interval = s.Interval AND
                                t.TypeID = s.TypeID";

                        using (var cmd = new SqlCommand(updateQuery, connection, transaction))
                        {
                            int rowsAffected = await cmd.ExecuteNonQueryAsync();
                            _log.Info($"Bulk updated {rowsAffected} average records");
                        }

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _log.Error("Bulk update failed", ex);
                        throw;
                    }
                }
            }
        }

        public async Task BulkInsertParameterReadingsAsync(List<ParameterReading> readings)
        {
            if (readings == null || readings.Count == 0) return;

            var dataTable = ConvertToReadingsDataTable(readings);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                        {
                            bulkCopy.DestinationTableName = _readingTableName;
                            bulkCopy.BatchSize = BatchSize;
                            bulkCopy.BulkCopyTimeout = 300;

                            // Map columns
                            bulkCopy.ColumnMappings.Add("StationID", "StationID");
                            bulkCopy.ColumnMappings.Add("DeviceID", "DeviceID");
                            bulkCopy.ColumnMappings.Add("ParameterID", "ParameterID");
                            bulkCopy.ColumnMappings.Add("ParameterIDRef", "ParameterIDRef");
                            bulkCopy.ColumnMappings.Add("Parametervalue", "Parametervalue");
                            bulkCopy.ColumnMappings.Add("LoggerFlags", "LoggerFlags");
                            bulkCopy.ColumnMappings.Add("CreatedTime", "CreatedTime");

                            await bulkCopy.WriteToServerAsync(dataTable);
                        }

                        transaction.Commit();
                        _log.Info($"Bulk inserted {readings.Count} parameter readings");
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _log.Error("Bulk insert readings failed", ex);
                        throw;
                    }
                }
            }
        }

        private DataTable ConvertToAverageDataTable(List<AverageRecord> records)
        {
            var dt = new DataTable();
            dt.Columns.Add("StationID", typeof(int));
            dt.Columns.Add("DeviceID", typeof(int));
            dt.Columns.Add("ParameterID", typeof(int));
            dt.Columns.Add("ParameterIDRef", typeof(int));
            dt.Columns.Add("Parametervalue", typeof(double));
            dt.Columns.Add("SubIndex", typeof(double));
            dt.Columns.Add("Type", typeof(string));
            dt.Columns.Add("Interval", typeof(DateTime));
            dt.Columns.Add("LoggerFlags", typeof(int));
            dt.Columns.Add("TypeID", typeof(int));
            dt.Columns.Add("CreatedTime", typeof(DateTime));

            foreach (var record in records)
            {
                dt.Rows.Add(
                    record.StationID,
                    record.DeviceID,
                    record.ParameterID,
                    record.ParameterIDRef ?? (object)DBNull.Value,
                    record.ParameterValue ?? (object)DBNull.Value,
                    record.SubIndex ?? (object)DBNull.Value,
                    record.Type,
                    record.Interval,
                    record.LoggerFlags ?? (object)DBNull.Value,
                    record.TypeID,
                    record.CreatedTime
                );
            }

            return dt;
        }

        private DataTable ConvertToUpdateDataTable(List<AverageRecord> records)
        {
            var dt = new DataTable();
            dt.Columns.Add("StationID", typeof(int));
            dt.Columns.Add("DeviceID", typeof(int));
            dt.Columns.Add("ParameterID", typeof(int));
            dt.Columns.Add("Parametervalue", typeof(double));
            dt.Columns.Add("SubIndex", typeof(double));
            dt.Columns.Add("LoggerFlags", typeof(int));
            dt.Columns.Add("Interval", typeof(DateTime));
            dt.Columns.Add("TypeID", typeof(int));

            foreach (var record in records)
            {
                dt.Rows.Add(
                    record.StationID,
                    record.DeviceID,
                    record.ParameterID,
                    record.ParameterValue ?? (object)DBNull.Value,
                    record.SubIndex ?? (object)DBNull.Value,
                    record.LoggerFlags ?? (object)DBNull.Value,
                    record.Interval,
                    record.TypeID
                );
            }

            return dt;
        }

        private DataTable ConvertToReadingsDataTable(List<ParameterReading> readings)
        {
            var dt = new DataTable();
            dt.Columns.Add("StationID", typeof(int));
            dt.Columns.Add("DeviceID", typeof(int));
            dt.Columns.Add("ParameterID", typeof(int));
            dt.Columns.Add("ParameterIDRef", typeof(int));
            dt.Columns.Add("Parametervalue", typeof(double));
            dt.Columns.Add("LoggerFlags", typeof(int));
            dt.Columns.Add("CreatedTime", typeof(DateTime));

            foreach (var reading in readings)
            {
                dt.Rows.Add(
                    reading.StationID,
                    reading.DeviceID,
                    reading.ParameterID,
                    reading.ParameterIDRef ?? (object)DBNull.Value,
                    reading.ParameterValue ?? (object)DBNull.Value,
                    reading.LoggerFlags ?? (object)DBNull.Value,
                    reading.CreatedTime
                );
            }

            return dt;
        }
    }
}