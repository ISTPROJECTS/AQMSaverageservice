using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using AQMSDataUpdateLibrary.Models;
using AQMSDataUpdateLibrary.Constants;
using log4net;

namespace AQMSDataUpdateLibrary.Repositories
{
    public interface IParameterRepository
    {
        Task<List<ParameterInfo>> GetActiveParametersAsync(int deviceId);
        Task<DateTime?> GetLatestIntervalAsync(int stationId, int deviceId, int parameterId, int typeId);
        Task<List<IntervalData>> GetIntervalCountsAsync(ParameterInfo param, DateTime? lastInterval, string interval, int intervalValue);
        Task<int> GetHighPriorityFlagAsync(ParameterInfo param, string interval, int intervalValue, DateTime intervalTime);
        Task<string> GetFormulaForParameterAsync(string parameterName);
    }

    public class ParameterRepository : IParameterRepository
    {
        private readonly string _connectionString;
        private readonly string _parameterTableName;
        private readonly string _driverTableName;
        private readonly string _averageTableName;
        private readonly string _readingTableName;
        private readonly string _flagTableName;
        private readonly ILog _log;

        // Cache for formulas to avoid repeated DB queries
        private static readonly ConcurrentDictionary<string, string> _formulaCache 
            = new ConcurrentDictionary<string, string>();

        public ParameterRepository(
            string connectionString,
            string parameterTableName,
            string driverTableName,
            string averageTableName,
            string readingTableName,
            string flagTableName)
        {
            _connectionString = connectionString;
            _parameterTableName = parameterTableName;
            _driverTableName = driverTableName;
            _averageTableName = averageTableName;
            _readingTableName = readingTableName;
            _flagTableName = flagTableName;
            _log = LogManager.GetLogger(typeof(ParameterRepository));
        }

        public async Task<List<ParameterInfo>> GetActiveParametersAsync(int deviceId)
        {
            var parameters = new List<ParameterInfo>();
            string query = string.Format(SQLQueries.GetActiveParameters, _parameterTableName, _driverTableName);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                    
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            parameters.Add(new ParameterInfo
                            {
                                ID = reader.GetInt32(reader.GetOrdinal("ID")),
                                StationID = reader.GetInt32(reader.GetOrdinal("StationID")),
                                DeviceID = reader.GetInt32(reader.GetOrdinal("DeviceID")),
                                ParameterID = reader.GetInt32(reader.GetOrdinal("ParameterID")),
                                ParameterDriverName = reader.GetString(reader.GetOrdinal("ParameterDriverName")),
                                ServerAvgInterval = reader.GetString(reader.GetOrdinal("ServerAvgInterval")),
                                DataSyncFrequency = reader.IsDBNull(reader.GetOrdinal("DataSyncFrequency")) 
                                    ? (int?)null 
                                    : reader.GetInt32(reader.GetOrdinal("DataSyncFrequency")),
                                IsCalculated = reader.IsDBNull(reader.GetOrdinal("IsCalculated")) 
                                    ? (bool?)null 
                                    : reader.GetBoolean(reader.GetOrdinal("IsCalculated"))
                            });
                        }
                    }
                }
            }

            return parameters;
        }

        public async Task<DateTime?> GetLatestIntervalAsync(int stationId, int deviceId, int parameterId, int typeId)
        {
            string query = string.Format(SQLQueries.GetLatestInterval, _averageTableName);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", stationId);
                    cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                    cmd.Parameters.AddWithValue("@ParameterID", parameterId);
                    cmd.Parameters.AddWithValue("@TypeID", typeId);

                    var result = await cmd.ExecuteScalarAsync();
                    return result == null || result == DBNull.Value ? (DateTime?)null : (DateTime)result;
                }
            }
        }

        public async Task<List<IntervalData>> GetIntervalCountsAsync(
            ParameterInfo param, 
            DateTime? lastInterval, 
            string interval, 
            int intervalValue)
        {
            var intervalData = new List<IntervalData>();
            string query = string.Format(SQLQueries.BulkGetIntervalCounts, interval, _readingTableName);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", param.StationID);
                    cmd.Parameters.AddWithValue("@DeviceID", param.DeviceID);
                    cmd.Parameters.AddWithValue("@ParameterID", param.ID);
                    cmd.Parameters.AddWithValue("@Interval", intervalValue);
                    cmd.Parameters.AddWithValue("@LastInterval", 
                        lastInterval.HasValue ? (object)lastInterval.Value : DBNull.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            intervalData.Add(new IntervalData
                            {
                                Interval = reader.GetDateTime(0),
                                TotalRecordCount = reader.GetInt32(1)
                            });
                        }
                    }
                }
            }

            return intervalData;
        }

        public async Task<int> GetHighPriorityFlagAsync(
            ParameterInfo param, 
            string interval, 
            int intervalValue, 
            DateTime intervalTime)
        {
            string query = string.Format(SQLQueries.GetHighPriorityFlag, 
                interval, _readingTableName, _flagTableName);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", param.StationID);
                    cmd.Parameters.AddWithValue("@DeviceID", param.DeviceID);
                    cmd.Parameters.AddWithValue("@ParameterID", param.ID);
                    cmd.Parameters.AddWithValue("@Interval", intervalValue);
                    cmd.Parameters.AddWithValue("@IntervalValue", intervalTime);

                    var result = await cmd.ExecuteScalarAsync();
                    return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
                }
            }
        }

        public async Task<string> GetFormulaForParameterAsync(string parameterName)
        {
            // Check cache first
            if (_formulaCache.TryGetValue(parameterName, out string cachedFormula))
            {
                return cachedFormula;
            }

            string query = "SELECT ConversionFactor FROM Parameter_Conversion WHERE Parameter = @Parameter";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@Parameter", parameterName);
                    var result = await cmd.ExecuteScalarAsync();
                    
                    string formula = result?.ToString() ?? string.Empty;
                    _formulaCache.TryAdd(parameterName, formula);
                    return formula;
                }
            }
        }
    }
}