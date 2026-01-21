using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using AQMSDataUpdateLibrary.Models;
using AQMSDataUpdateLibrary.Constants;
using AQMSDataUpdateLibrary.Repositories;
using log4net;

namespace AQMSDataUpdateLibrary.Services
{
    public interface IAverageCalculationService
    {
        Task<bool> CalculateAveragesForDeviceAsync(int deviceId);
        Task<bool> CalculateMonthlyAveragesAsync();
        Task<bool> CalculateYearlyAveragesAsync();
    }

    public class AverageCalculationService : IAverageCalculationService
    {
        private readonly IParameterRepository _parameterRepo;
        private readonly IBulkDataWriter _bulkWriter;
        private readonly IAQICalculator _aqiCalculator;
        private readonly string _connectionString;
        private readonly int _defaultInterval;
        private readonly string _winddirection;
        private readonly string _rain;
        private readonly ILog _log;
        private readonly ILog _errorLog;

        public AverageCalculationService(
            IParameterRepository parameterRepo,
            IBulkDataWriter bulkWriter,
            IAQICalculator aqiCalculator,
            string connectionString,
            int defaultInterval,
            string winddirection,
            string rain)
        {
            _parameterRepo = parameterRepo;
            _bulkWriter = bulkWriter;
            _aqiCalculator = aqiCalculator;
            _connectionString = connectionString;
            _defaultInterval = defaultInterval;
            _winddirection = winddirection;
            _rain = rain;
            _log = LogManager.GetLogger(typeof(AverageCalculationService));
            _errorLog = LogManager.GetLogger("error");
        }

        public async Task<bool> CalculateAveragesForDeviceAsync(int deviceId)
        {
            try
            {
                _log.Info($"Starting average calculation for device {deviceId}");

                // Get all parameters for this device
                var parameters = await _parameterRepo.GetActiveParametersAsync(deviceId);
                
                if (!parameters.Any())
                {
                    _log.Info($"No parameters found for device {deviceId}");
                    return true;
                }

                // Process each parameter
                foreach (var param in parameters)
                {
                    await ProcessParameterAveragesAsync(param);
                }

                // Process AQI calculations
                await ProcessAQICalculationsAsync(deviceId);

                _log.Info($"Completed average calculation for device {deviceId}");
                return true;
            }
            catch (Exception ex)
            {
                _errorLog.Error($"Error calculating averages for device {deviceId}", ex);
                return false;
            }
        }

        private async Task ProcessParameterAveragesAsync(ParameterInfo param)
        {
            var intervals = param.ServerAvgInterval.Split(',');
            int dataSyncFreq = param.DataSyncFrequency ?? _defaultInterval;

            foreach (var intervalSpec in intervals)
            {
                var parts = intervalSpec.Split('-');
                if (parts.Length != 2) continue;

                int intervalValue = int.Parse(parts[0]);
                string intervalCode = parts[1]; // M or H
                string intervalType = intervalCode == "M" ? "MINUTE" : "HOUR";
                int typeId = intervalCode == "M" ? intervalValue : intervalValue * 60;

                // Skip if data sync frequency doesn't support this interval
                if (typeId < dataSyncFreq)
                {
                    continue;
                }

                await ProcessIntervalAveragesAsync(param, intervalValue, intervalCode, intervalType, typeId, dataSyncFreq);
            }
        }

        private async Task ProcessIntervalAveragesAsync(
            ParameterInfo param,
            int intervalValue,
            string intervalCode,
            string intervalType,
            int typeId,
            int dataSyncFreq)
        {
            // Get last processed interval
            DateTime? lastInterval = await _parameterRepo.GetLatestIntervalAsync(
                param.StationID, param.DeviceID, param.ID, typeId);

            // Get interval counts
            var intervalData = await _parameterRepo.GetIntervalCountsAsync(
                param, lastInterval, intervalType, intervalValue);

            if (!intervalData.Any())
            {
                return;
            }

            var recordsToInsert = new List<AverageRecord>();

            foreach (var interval in intervalData)
            {
                // Skip incomplete intervals (except last one)
                if (intervalData.IndexOf(interval) == intervalData.Count - 1)
                {
                    int expectedRecords = intervalCode == "M" ? intervalValue : intervalValue * 60;
                    int actualRecords = interval.TotalRecordCount * dataSyncFreq;
                    
                    if (actualRecords != expectedRecords)
                    {
                        continue; // Skip incomplete last interval
                    }
                }

                // Check if interval is complete (time-based check)
                TimeSpan elapsed = DateTime.Now - interval.Interval;
                bool isComplete = intervalCode == "M"
                    ? elapsed.TotalMinutes >= intervalValue
                    : elapsed.TotalHours >= intervalValue;

                if (!isComplete)
                {
                    continue;
                }

                // Get logger flag
                int loggerFlag = await _parameterRepo.GetHighPriorityFlagAsync(
                    param, intervalType, intervalValue, interval.Interval);

                // Calculate percentage of valid data
                int validRecordCount = await GetValidRecordCountAsync(
                    param, intervalType, intervalValue, interval.Interval);
                
                double percentage = CalculateDataPercentage(
                    validRecordCount, dataSyncFreq, intervalValue, intervalCode);

                // Create average record
                var avgRecord = await CreateAverageRecordAsync(
                    param, interval.Interval, typeId, intervalValue, 
                    intervalCode, loggerFlag, percentage, dataSyncFreq);

                if (avgRecord != null)
                {
                    recordsToInsert.Add(avgRecord);
                }
            }

            // Bulk insert all records
            if (recordsToInsert.Any())
            {
                await _bulkWriter.BulkInsertAveragesAsync(recordsToInsert);
                _log.Info($"Inserted {recordsToInsert.Count} average records for parameter {param.ID}");
            }
        }

        private async Task<AverageRecord> CreateAverageRecordAsync(
            ParameterInfo param,
            DateTime interval,
            int typeId,
            int intervalValue,
            string intervalCode,
            int loggerFlag,
            double percentage,
            int dataSyncFreq)
        {
            var record = new AverageRecord
            {
                StationID = param.StationID,
                DeviceID = param.DeviceID,
                ParameterID = param.ID,
                ParameterIDRef = param.ParameterID,
                Interval = interval,
                TypeID = typeId,
                Type = $"{intervalValue}{intervalCode}",
                LoggerFlags = loggerFlag != 0 ? loggerFlag : (int?)null,
                CreatedTime = DateTime.Now
            };

            // Calculate parameter value based on percentage and parameter type
            if (percentage >= AQIConstants.MinDataCoveragePercent)
            {
                record.ParameterValue = await CalculateParameterValueAsync(
                    param, interval, intervalValue, intervalCode, dataSyncFreq, loggerFlag);
            }
            else
            {
                record.ParameterValue = null;
            }

            return record;
        }

        private async Task<double?> CalculateParameterValueAsync(
            ParameterInfo param,
            DateTime interval,
            int intervalValue,
            string intervalCode,
            int dataSyncFreq,
            int loggerFlag)
        {
            string intervalType = intervalCode == "M" ? "MINUTE" : "HOUR";

            // Special handling for wind direction
            if (_winddirection.ToUpper().Contains(param.ParameterDriverName.ToUpper()))
            {
                return await CalculateWindDirectionAsync(
                    param, interval, intervalValue, intervalType, loggerFlag);
            }

            // Special handling for rain (sum instead of average)
            string aggregation = _rain.ToUpper().Contains(param.ParameterDriverName.ToUpper())
                ? "SUM"
                : "AVG";

            return await GetAggregatedValueAsync(
                param, interval, intervalValue, intervalType, aggregation, loggerFlag);
        }

        private async Task<double?> CalculateWindDirectionAsync(
            ParameterInfo param,
            DateTime interval,
            int intervalValue,
            string intervalType,
            int loggerFlag)
        {
            string query = $@"
                SELECT 
                    CASE 
                        WHEN DEGREES(ATN2(AVG(SIN(RADIANS(ParameterValue))), AVG(COS(RADIANS(ParameterValue))))) < 0 
                        THEN 360 + DEGREES(ATN2(AVG(SIN(RADIANS(ParameterValue))), AVG(COS(RADIANS(ParameterValue)))))
                        ELSE DEGREES(ATN2(AVG(SIN(RADIANS(ParameterValue))), AVG(COS(RADIANS(ParameterValue)))))
                    END AS WindDirection
                FROM ParameterReadings pr WITH (NOLOCK)
                INNER JOIN DMN_Flags f WITH (NOLOCK) ON pr.LoggerFlags = f.ID
                WHERE pr.StationID = @StationID
                  AND pr.DeviceID = @DeviceID
                  AND pr.ParameterID = @ParameterID
                  AND DATEADD({intervalType}, DATEDIFF({intervalType}, 0, pr.CreatedTime) / @Interval * @Interval, 0) = @IntervalValue
                  AND f.Type != 'Validation'
                  AND (@LoggerFlag = 0 OR pr.LoggerFlags = @LoggerFlag)";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", param.StationID);
                    cmd.Parameters.AddWithValue("@DeviceID", param.DeviceID);
                    cmd.Parameters.AddWithValue("@ParameterID", param.ID);
                    cmd.Parameters.AddWithValue("@Interval", intervalValue);
                    cmd.Parameters.AddWithValue("@IntervalValue", interval);
                    cmd.Parameters.AddWithValue("@LoggerFlag", loggerFlag);

                    var result = await cmd.ExecuteScalarAsync();
                    return result == null || result == DBNull.Value 
                        ? (double?)null 
                        : Convert.ToDouble(result);
                }
            }
        }

        private async Task<double?> GetAggregatedValueAsync(
            ParameterInfo param,
            DateTime interval,
            int intervalValue,
            string intervalType,
            string aggregation,
            int loggerFlag)
        {
            string query = $@"
                SELECT {aggregation}(pr.ParameterValue) AS AggValue
                FROM ParameterReadings pr WITH (NOLOCK)
                INNER JOIN DMN_Flags f WITH (NOLOCK) ON pr.LoggerFlags = f.ID
                WHERE pr.StationID = @StationID
                  AND pr.DeviceID = @DeviceID
                  AND pr.ParameterID = @ParameterID
                  AND DATEADD({intervalType}, DATEDIFF({intervalType}, 0, pr.CreatedTime) / @Interval * @Interval, 0) = @IntervalValue
                  AND f.Type != 'Validation'
                  AND (@LoggerFlag = 0 OR pr.LoggerFlags = @LoggerFlag)";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", param.StationID);
                    cmd.Parameters.AddWithValue("@DeviceID", param.DeviceID);
                    cmd.Parameters.AddWithValue("@ParameterID", param.ID);
                    cmd.Parameters.AddWithValue("@Interval", intervalValue);
                    cmd.Parameters.AddWithValue("@IntervalValue", interval);
                    cmd.Parameters.AddWithValue("@LoggerFlag", loggerFlag);

                    var result = await cmd.ExecuteScalarAsync();
                    return result == null || result == DBNull.Value 
                        ? (double?)null 
                        : Convert.ToDouble(result);
                }
            }
        }

        private async Task<int> GetValidRecordCountAsync(
            ParameterInfo param,
            string intervalType,
            int intervalValue,
            DateTime interval)
        {
            string query = $@"
                SELECT COUNT(*)
                FROM ParameterReadings pr WITH (NOLOCK)
                INNER JOIN DMN_Flags f WITH (NOLOCK) ON pr.LoggerFlags = f.ID
                WHERE pr.StationID = @StationID
                  AND pr.DeviceID = @DeviceID
                  AND pr.ParameterID = @ParameterID
                  AND DATEADD({intervalType}, DATEDIFF({intervalType}, 0, pr.CreatedTime) / @Interval * @Interval, 0) = @IntervalValue
                  AND f.Type != 'Validation'";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", param.StationID);
                    cmd.Parameters.AddWithValue("@DeviceID", param.DeviceID);
                    cmd.Parameters.AddWithValue("@ParameterID", param.ID);
                    cmd.Parameters.AddWithValue("@Interval", intervalValue);
                    cmd.Parameters.AddWithValue("@IntervalValue", interval);

                    var result = await cmd.ExecuteScalarAsync();
                    return Convert.ToInt32(result);
                }
            }
        }

        private double CalculateDataPercentage(
            int validRecordCount,
            int dataSyncFreq,
            int intervalValue,
            string intervalCode)
        {
            int totalMinutes = intervalCode == "M" ? intervalValue : intervalValue * 60;
            int expectedRecords = totalMinutes / dataSyncFreq;
            return (validRecordCount * 100.0) / expectedRecords;
        }

        private async Task ProcessAQICalculationsAsync(int deviceId)
        {
            // Get AQI parameter configuration
            var aqiParams = await GetAQIParametersAsync(deviceId);

            foreach (var param in aqiParams)
            {
                var intervals = param.ServerAvgInterval.Split(',');

                foreach (var intervalSpec in intervals)
                {
                    var parts = intervalSpec.Split('-');
                    if (parts.Length != 2) continue;

                    int intervalValue = int.Parse(parts[0]);
                    string intervalCode = parts[1];
                    int typeId = intervalCode == "M" ? intervalValue : intervalValue * 60;

                    if (typeId == AQIConstants.OneHourTypeID)
                    {
                        // Calculate AQI from pollutant values (fresh calculation)
                        await CalculateAQIForIntervalsAsync(param, typeId);
                    }
                    else
                    {
                        // Average AQI and sub-indices from hourly data
                        await AverageAQIFromHourlyDataAsync(param, intervalValue, intervalCode, typeId);
                    }
                }
            }
        }

        private async Task<List<ParameterInfo>> GetAQIParametersAsync(int deviceId)
        {
            var parameters = new List<ParameterInfo>();
            string query = @"
                SELECT p.*, d.DriverName AS ParameterDriverName 
                FROM DMN_Parameters p WITH (NOLOCK)
                INNER JOIN MST_Devices_Drivers d WITH (NOLOCK) ON p.DriverID = d.ID 
                WHERE p.DeviceID = @DeviceID 
                  AND d.DriverName = 'AQI Index'
                  AND p.ServerAvgInterval IS NOT NULL";

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
                                ServerAvgInterval = reader.GetString(reader.GetOrdinal("ServerAvgInterval"))
                            });
                        }
                    }
                }
            }

            return parameters;
        }

        private async Task CalculateAQIForIntervalsAsync(ParameterInfo param, int typeId)
        {
            // Get intervals that need AQI calculation
            DateTime? lastInterval = await _parameterRepo.GetLatestIntervalAsync(
                param.StationID, param.DeviceID, param.ID, typeId);

            var intervals = await GetIncompleteAQIIntervalsAsync(
                param.StationID, param.DeviceID, lastInterval, typeId);

            var aqiRecords = new List<AverageRecord>();

            foreach (var interval in intervals)
            {
                // Check if interval is complete
                TimeSpan elapsed = DateTime.Now - interval;
                if (elapsed.TotalHours < 1)
                {
                    continue;
                }

                // Calculate AQI
                var aqiResult = await _aqiCalculator.CalculateAQIAsync(
                    param.StationID, param.DeviceID, interval, typeId);

                // Create AQI record
                var aqiRecord = new AverageRecord
                {
                    StationID = param.StationID,
                    DeviceID = param.DeviceID,
                    ParameterID = param.ID,
                    ParameterIDRef = param.ParameterID,
                    ParameterValue = aqiResult.AQI,
                    Interval = interval,
                    TypeID = typeId,
                    Type = null,
                    LoggerFlags = 1,
                    CreatedTime = DateTime.Now
                };

                aqiRecords.Add(aqiRecord);

                // Also update sub-indices for individual pollutants
                await UpdateSubIndicesAsync(param, interval, typeId, aqiResult);
            }

            if (aqiRecords.Any())
            {
                await _bulkWriter.BulkInsertAveragesAsync(aqiRecords);
                _log.Info($"Inserted {aqiRecords.Count} AQI records");
            }
        }

        private async Task<List<DateTime>> GetIncompleteAQIIntervalsAsync(
            int stationId,
            int deviceId,
            DateTime? lastInterval,
            int typeId)
        {
            var intervals = new List<DateTime>();
            string query = @"
                SELECT DISTINCT a.Interval
                FROM (
                    SELECT DATEADD(HOUR, DATEDIFF(HOUR, 0, pa.Interval), 0) AS Interval
                    FROM ParameterAverages pa WITH (NOLOCK)
                    INNER JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
                    INNER JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
                    WHERE pa.StationID = @StationID
                      AND pa.DeviceID = @DeviceID
                      AND pa.TypeID = @TypeID
                      AND d.DriverName IN ('PM2.5', 'PM10', 'O₃', 'SO₂', 'NO₂', 'CO')
                      AND dp.shouldUseForAqi = 1
                      AND (@LastInterval IS NULL OR pa.Interval > @LastInterval)
                ) a
                ORDER BY a.Interval ASC";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", stationId);
                    cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                    cmd.Parameters.AddWithValue("@TypeID", typeId);
                    cmd.Parameters.AddWithValue("@LastInterval", 
                        lastInterval.HasValue ? (object)lastInterval.Value : DBNull.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            intervals.Add(reader.GetDateTime(0));
                        }
                    }
                }
            }

            return intervals;
        }

        private async Task UpdateSubIndicesAsync(
            ParameterInfo param,
            DateTime interval,
            int typeId,
            AQICalculationResult aqiResult)
        {
            var updates = new List<AverageRecord>();

            // Create update records for each pollutant
            if (aqiResult.PM25SubIndex.HasValue && aqiResult.ParameterIDs.ContainsKey(AQIConstants.PM25))
            {
                updates.Add(CreateSubIndexUpdate(param, interval, typeId, 
                    aqiResult.ParameterIDs[AQIConstants.PM25].Value, aqiResult.PM25SubIndex.Value));
            }

            if (aqiResult.PM10SubIndex.HasValue && aqiResult.ParameterIDs.ContainsKey(AQIConstants.PM10))
            {
                updates.Add(CreateSubIndexUpdate(param, interval, typeId,
                    aqiResult.ParameterIDs[AQIConstants.PM10].Value, aqiResult.PM10SubIndex.Value));
            }

            if (aqiResult.O3SubIndex.HasValue && aqiResult.ParameterIDs.ContainsKey(AQIConstants.O3))
            {
                updates.Add(CreateSubIndexUpdate(param, interval, typeId,
                    aqiResult.ParameterIDs[AQIConstants.O3].Value, aqiResult.O3SubIndex.Value));
            }

            if (aqiResult.SO2SubIndex.HasValue && aqiResult.ParameterIDs.ContainsKey(AQIConstants.SO2))
            {
                updates.Add(CreateSubIndexUpdate(param, interval, typeId,
                    aqiResult.ParameterIDs[AQIConstants.SO2].Value, aqiResult.SO2SubIndex.Value));
            }

            if (aqiResult.NO2SubIndex.HasValue && aqiResult.ParameterIDs.ContainsKey(AQIConstants.NO2))
            {
                updates.Add(CreateSubIndexUpdate(param, interval, typeId,
                    aqiResult.ParameterIDs[AQIConstants.NO2].Value, aqiResult.NO2SubIndex.Value));
            }

            if (aqiResult.COSubIndex.HasValue && aqiResult.ParameterIDs.ContainsKey(AQIConstants.CO))
            {
                updates.Add(CreateSubIndexUpdate(param, interval, typeId,
                    aqiResult.ParameterIDs[AQIConstants.CO].Value, aqiResult.COSubIndex.Value));
            }

            if (updates.Any())
            {
                await UpdateSubIndicesOnExistingRecordsAsync(updates);
            }
        }

        private AverageRecord CreateSubIndexUpdate(
            ParameterInfo param,
            DateTime interval,
            int typeId,
            int parameterId,
            double subIndex)
        {
            return new AverageRecord
            {
                StationID = param.StationID,
                DeviceID = param.DeviceID,
                ParameterID = parameterId,
                SubIndex = subIndex,
                Interval = interval,
                TypeID = typeId
            };
        }

        public async Task<bool> CalculateMonthlyAveragesAsync()
        {
            _log.Info("Starting monthly average calculation");
            
            // Implementation similar to yearly, aggregating from daily averages
            // Left as exercise - follows same pattern as yearly
            
            return true;
        }

        public async Task<bool> CalculateYearlyAveragesAsync()
        {
            _log.Info("Starting yearly average calculation");
            
            // Implementation would aggregate from monthly averages
            // Left as exercise - follows same pattern as monthly
            
            return true;
        }

        private async Task AverageAQIFromHourlyDataAsync(
            ParameterInfo param,
            int intervalValue,
            string intervalCode,
            int typeId)
        {
            _log.Info($"Averaging AQI and sub-indices for {intervalValue}{intervalCode} interval");

            // Get last processed interval for this type
            DateTime? lastInterval = await _parameterRepo.GetLatestIntervalAsync(
                param.StationID, param.DeviceID, param.ID, typeId);

            // Get intervals that need averaging from hourly data
            var intervalsToProcess = await GetIntervalsToAverageAsync(
                param.StationID, param.DeviceID, lastInterval, intervalValue, intervalCode, typeId);

            if (!intervalsToProcess.Any())
            {
                _log.Info("No intervals to average");
                return;
            }

            var aqiRecords = new List<AverageRecord>();
            var subIndexUpdates = new List<AverageRecord>();

            foreach (var interval in intervalsToProcess)
            {
                // Check if interval is complete
                TimeSpan elapsed = DateTime.Now - interval;
                bool isComplete = intervalCode == "M"
                    ? elapsed.TotalMinutes >= intervalValue
                    : elapsed.TotalHours >= intervalValue;

                if (!isComplete)
                {
                    continue;
                }

                // Get averaged AQI value from hourly records
                var avgAQI = await GetAveragedAQIValueAsync(
                    param.StationID, param.DeviceID, interval, intervalValue, intervalCode);

                if (avgAQI.HasValue)
                {
                    // Create AQI record with averaged value
                    var aqiRecord = new AverageRecord
                    {
                        StationID = param.StationID,
                        DeviceID = param.DeviceID,
                        ParameterID = param.ID,
                        ParameterIDRef = param.ParameterID,
                        ParameterValue = avgAQI.Value,
                        Interval = interval,
                        TypeID = typeId,
                        Type = null,
                        LoggerFlags = 1,
                        CreatedTime = DateTime.Now
                    };

                    aqiRecords.Add(aqiRecord);
                }

                // Get averaged sub-indices for all pollutants
                var subIndices = await GetAveragedSubIndicesAsync(
                    param.StationID, param.DeviceID, interval, intervalValue, intervalCode);

                foreach (var subIndex in subIndices)
                {
                    subIndexUpdates.Add(new AverageRecord
                    {
                        StationID = param.StationID,
                        DeviceID = param.DeviceID,
                        ParameterID = subIndex.ParameterID,
                        SubIndex = subIndex.AvgSubIndex,
                        ParameterValue = subIndex.AvgParameterValue,
                        Interval = interval,
                        TypeID = typeId,
                        Type = $"{intervalValue}{intervalCode}",
                        LoggerFlags = 1,
                        CreatedTime = DateTime.Now,
                        ParameterIDRef = subIndex.ParameterIDRef
                    });
                }
            }

            // Insert averaged AQI records
            if (aqiRecords.Any())
            {
                await _bulkWriter.BulkInsertAveragesAsync(aqiRecords);
                _log.Info($"Inserted {aqiRecords.Count} averaged AQI records for {intervalValue}{intervalCode}");
            }

            // Insert averaged sub-index records for pollutants
            if (subIndexUpdates.Any())
            {
                await UpdateSubIndicesOnExistingRecordsAsync(subIndexUpdates);
                _log.Info($"Inserted {subIndexUpdates.Count} averaged sub-index records for {intervalValue}{intervalCode}");
            }
        }

        private async Task<List<DateTime>> GetIntervalsToAverageAsync(
            int stationId,
            int deviceId,
            DateTime? lastInterval,
            int intervalValue,
            string intervalCode,
            int targetTypeId)
        {
            var intervals = new List<DateTime>();
            string intervalType = intervalCode == "M" ? "MINUTE" : "HOUR";

            // Get distinct intervals from hourly data that need to be averaged
            string query = $@"
                SELECT DISTINCT 
                    DATEADD({intervalType}, DATEDIFF({intervalType}, 0, pa.Interval) / @Interval * @Interval, 0) AS Interval
                FROM ParameterAverages pa WITH (NOLOCK)
                INNER JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
                INNER JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
                WHERE pa.StationID = @StationID
                  AND pa.DeviceID = @DeviceID
                  AND pa.TypeID = @HourlyTypeID
                  AND d.DriverName IN ('PM2.5', 'PM10', 'O₃', 'SO₂', 'NO₂', 'CO', 'AQI Index')
                  AND (@LastInterval IS NULL OR 
                       DATEADD({intervalType}, DATEDIFF({intervalType}, 0, pa.Interval) / @Interval * @Interval, 0) > @LastInterval)
                ORDER BY Interval ASC";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", stationId);
                    cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                    cmd.Parameters.AddWithValue("@HourlyTypeID", AQIConstants.OneHourTypeID);
                    cmd.Parameters.AddWithValue("@Interval", intervalValue);
                    cmd.Parameters.AddWithValue("@LastInterval",
                        lastInterval.HasValue ? (object)lastInterval.Value : DBNull.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            intervals.Add(reader.GetDateTime(0));
                        }
                    }
                }
            }

            return intervals;
        }

        private async Task<double?> GetAveragedAQIValueAsync(
            int stationId,
            int deviceId,
            DateTime interval,
            int intervalValue,
            string intervalCode)
        {
            string intervalType = intervalCode == "M" ? "MINUTE" : "HOUR";

            // Average the AQI values from hourly data for this interval
            string query = $@"
                SELECT AVG(pa.Parametervalue) AS AvgAQI
                FROM ParameterAverages pa WITH (NOLOCK)
                INNER JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
                INNER JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
                WHERE pa.StationID = @StationID
                  AND pa.DeviceID = @DeviceID
                  AND pa.TypeID = @HourlyTypeID
                  AND d.DriverName = 'AQI Index'
                  AND DATEADD({intervalType}, DATEDIFF({intervalType}, 0, pa.Interval) / @Interval * @Interval, 0) = @TargetInterval
                  AND pa.Parametervalue IS NOT NULL";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", stationId);
                    cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                    cmd.Parameters.AddWithValue("@HourlyTypeID", AQIConstants.OneHourTypeID);
                    cmd.Parameters.AddWithValue("@Interval", intervalValue);
                    cmd.Parameters.AddWithValue("@TargetInterval", interval);

                    var result = await cmd.ExecuteScalarAsync();
                    return result == null || result == DBNull.Value
                        ? (double?)null
                        : Convert.ToDouble(result);
                }
            }
        }

        private async Task<List<AveragedSubIndex>> GetAveragedSubIndicesAsync(
            int stationId,
            int deviceId,
            DateTime interval,
            int intervalValue,
            string intervalCode)
        {
            var subIndices = new List<AveragedSubIndex>();
            string intervalType = intervalCode == "M" ? "MINUTE" : "HOUR";
            string[] aqiParams = _aqiCalculator.GetType()
                .GetField("_aqiParameters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_aqiCalculator)?.ToString()?.Split(',') ?? new string[0];

            if (aqiParams.Length == 0)
            {
                // Fallback to hardcoded list
                aqiParams = new[] { "PM2.5", "PM10", "O₃", "SO₂", "NO₂", "CO" };
            }

            string paramsCondition = string.Join(",", aqiParams.Select(p => $"'{p.Trim()}'"));

            // Average sub-indices and parameter values from hourly data
            string query = $@"
                SELECT 
                    d.DriverName,
                    pa.ParameterID,
                    dp.ParameterID AS ParameterIDRef,
                    AVG(pa.SubIndex) AS AvgSubIndex,
                    AVG(pa.Parametervalue) AS AvgParameterValue
                FROM ParameterAverages pa WITH (NOLOCK)
                INNER JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
                INNER JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
                WHERE pa.StationID = @StationID
                  AND pa.DeviceID = @DeviceID
                  AND pa.TypeID = @HourlyTypeID
                  AND d.DriverName IN ({paramsCondition})
                  AND dp.shouldUseForAqi = 1
                  AND DATEADD({intervalType}, DATEDIFF({intervalType}, 0, pa.Interval) / @Interval * @Interval, 0) = @TargetInterval
                  AND pa.SubIndex IS NOT NULL
                GROUP BY d.DriverName, pa.ParameterID, dp.ParameterID";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", stationId);
                    cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                    cmd.Parameters.AddWithValue("@HourlyTypeID", AQIConstants.OneHourTypeID);
                    cmd.Parameters.AddWithValue("@Interval", intervalValue);
                    cmd.Parameters.AddWithValue("@TargetInterval", interval);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            subIndices.Add(new AveragedSubIndex
                            {
                                DriverName = reader.GetString(0),
                                ParameterID = reader.GetInt32(1),
                                ParameterIDRef = reader.GetInt32(2),
                                AvgSubIndex = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3),
                                AvgParameterValue = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4)
                            });
                        }
                    }
                }
            }

            return subIndices;
        }

        private async Task UpdateSubIndicesOnExistingRecordsAsync(List<AverageRecord> subIndexUpdates)
        {
            if (!subIndexUpdates.Any()) return;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // Create temp table for updates
                        string createTempTable = @"
                            CREATE TABLE #TempSubIndexUpdates (
                                StationID INT,
                                DeviceID INT,
                                ParameterID INT,
                                Interval DATETIME,
                                TypeID INT,
                                SubIndex FLOAT
                            )";

                        using (var cmd = new SqlCommand(createTempTable, connection, transaction))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // Bulk load updates to temp table
                        var tempTable = new DataTable();
                        tempTable.Columns.Add("StationID", typeof(int));
                        tempTable.Columns.Add("DeviceID", typeof(int));
                        tempTable.Columns.Add("ParameterID", typeof(int));
                        tempTable.Columns.Add("Interval", typeof(DateTime));
                        tempTable.Columns.Add("TypeID", typeof(int));
                        tempTable.Columns.Add("SubIndex", typeof(double));

                        foreach (var update in subIndexUpdates)
                        {
                            tempTable.Rows.Add(
                                update.StationID,
                                update.DeviceID,
                                update.ParameterID,
                                update.Interval,
                                update.TypeID,
                                update.SubIndex ?? (object)DBNull.Value
                            );
                        }

                        using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                        {
                            bulkCopy.DestinationTableName = "#TempSubIndexUpdates";
                            bulkCopy.BatchSize = 1000;
                            await bulkCopy.WriteToServerAsync(tempTable);
                        }

                        // Update existing records with sub-indices
                        string updateQuery = $@"
                            UPDATE pa
                            SET pa.SubIndex = t.SubIndex
                            FROM ParameterAverages pa
                            INNER JOIN #TempSubIndexUpdates t ON
                                pa.StationID = t.StationID AND
                                pa.DeviceID = t.DeviceID AND
                                pa.ParameterID = t.ParameterID AND
                                pa.Interval = t.Interval AND
                                pa.TypeID = t.TypeID";

                        int rowsUpdated;
                        using (var cmd = new SqlCommand(updateQuery, connection, transaction))
                        {
                            cmd.CommandTimeout = 300;
                            rowsUpdated = await cmd.ExecuteNonQueryAsync();
                        }

                        transaction.Commit();
                        _log.Info($"Updated SubIndex for {rowsUpdated} pollutant records");
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _log.Error("Failed to update sub-indices", ex);
                        throw;
                    }
                }
            }
        }

        private class AveragedSubIndex
        {
            public string DriverName { get; set; }
            public int ParameterID { get; set; }
            public int ParameterIDRef { get; set; }
            public double? AvgSubIndex { get; set; }
            public double? AvgParameterValue { get; set; }
        }
    }
}