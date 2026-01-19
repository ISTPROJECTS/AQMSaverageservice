using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using AQMSDataUpdateLibrary.Models;
using AQMSDataUpdateLibrary.Constants;
using log4net;

namespace AQMSDataUpdateLibrary.Services
{
    public interface IAQICalculator
    {
        Task<AQICalculationResult> CalculateAQIAsync(
            int stationId,
            int deviceId,
            DateTime interval,
            int typeId);
        double? CalculatePollutantAQI(double? pollutantValue, string pollutantType);
    }

    public class AQICalculator : IAQICalculator
    {
        private readonly string _connectionString;
        private readonly string _averageTableName;
        private readonly string _parameterTableName;
        private readonly string _driverTableName;
        private readonly string _flagTableName;
        private readonly string _aqiParameters;
        private readonly ILog _log;

        public AQICalculator(
            string connectionString,
            string averageTableName,
            string parameterTableName,
            string driverTableName,
            string flagTableName,
            string aqiParameters)
        {
            _connectionString = connectionString;
            _averageTableName = averageTableName;
            _parameterTableName = parameterTableName;
            _driverTableName = driverTableName;
            _flagTableName = flagTableName;
            _aqiParameters = aqiParameters;
            _log = LogManager.GetLogger(typeof(AQICalculator));
        }

        public async Task<AQICalculationResult> CalculateAQIAsync(
            int stationId,
            int deviceId,
            DateTime interval,
            int typeId)
        {
            var result = new AQICalculationResult
            {
                ParameterIDs = new Dictionary<string, int?>()
            };

            try
            {
                // Get pollutant values for the interval
                var pollutantValues = await GetPollutantValuesAsync(stationId, deviceId, interval, typeId);

                if (!pollutantValues.Any())
                {
                    return result;
                }

                // Calculate sub-indices
                result.PM25SubIndex = await CalculateRollingAverageAQIAsync(
                    stationId, deviceId, interval, AQIConstants.PM25, typeId, 24);
                result.PM10SubIndex = await CalculateRollingAverageAQIAsync(
                    stationId, deviceId, interval, AQIConstants.PM10, typeId, 24);
                result.COSubIndex = await CalculateRollingAverageAQIAsync(
                    stationId, deviceId, interval, AQIConstants.CO, typeId, 8);

                // O3 calculation (special case)
                var o3Value = GetPollutantValue(pollutantValues, AQIConstants.O3);
                if (o3Value.HasValue)
                {
                    result.O3SubIndex = await CalculateO3AQIAsync(
                        stationId, deviceId, interval, typeId, o3Value.Value);
                }

                // SO2 calculation (special case)
                var so2Value = GetPollutantValue(pollutantValues, AQIConstants.SO2);
                if (so2Value.HasValue)
                {
                    if (so2Value <= 797)
                    {
                        result.SO2SubIndex = CalculatePollutantAQI(so2Value, "1_SO2");
                    }
                    else
                    {
                        result.SO2SubIndex = await CalculateRollingAverageAQIAsync(
                            stationId, deviceId, interval, AQIConstants.SO2, typeId, 24);
                    }
                }

                // NO2 calculation
                var no2Value = GetPollutantValue(pollutantValues, AQIConstants.NO2);
                result.NO2SubIndex = CalculatePollutantAQI(no2Value, "1_NO2");

                // Store parameter IDs
                foreach (var pv in pollutantValues)
                {
                    result.ParameterIDs[pv.DriverName] = pv.ParameterID;
                }

                // Calculate overall AQI (max of all sub-indices)
                result.AQI = new[]
                {
                    result.PM25SubIndex,
                    result.PM10SubIndex,
                    result.O3SubIndex,
                    result.SO2SubIndex,
                    result.NO2SubIndex,
                    result.COSubIndex
                }
                .Where(x => x.HasValue)
                .DefaultIfEmpty()
                .Max();
            }
            catch (Exception ex)
            {
                _log.Error($"Error calculating AQI for Station {stationId}, Device {deviceId}", ex);
                throw;
            }

            return result;
        }

        private async Task<List<PollutantValue>> GetPollutantValuesAsync(
            int stationId,
            int deviceId,
            DateTime interval,
            int typeId)
        {
            var values = new List<PollutantValue>();
            string[] aqiParams = _aqiParameters.Split(',');
            string paramsCondition = string.Join(",", aqiParams.Select(d => $"'{d.Trim()}'"));

            string query = $@"
                SELECT 
                    d.DriverName,
                    pa.ParameterValue * COALESCE(
                        CASE WHEN u.UnitName <> pc.SecondaryUnit 
                             THEN TRY_CAST(pc.ConversionFactor AS FLOAT)
                             ELSE 1 END, 1) AS ConvertedParameterValue,
                    pa.ParameterID
                FROM {_averageTableName} pa WITH (NOLOCK)
                INNER JOIN {_parameterTableName} dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
                INNER JOIN {_driverTableName} d WITH (NOLOCK) ON dp.DriverID = d.ID
                INNER JOIN ReportedUnits u WITH (NOLOCK) ON dp.UnitID = u.ID
                LEFT JOIN Parameter_Conversion pc WITH (NOLOCK) ON d.DriverName = pc.Parameter
                WHERE pa.StationID = @StationID 
                  AND pa.DeviceID = @DeviceID 
                  AND pa.Interval = @Interval 
                  AND pa.TypeID = @TypeID 
                  AND d.DriverName IN ({paramsCondition})
                  AND dp.shouldUseForAqi = 1";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", stationId);
                    cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                    cmd.Parameters.AddWithValue("@Interval", interval);
                    cmd.Parameters.AddWithValue("@TypeID", typeId);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            values.Add(new PollutantValue
                            {
                                DriverName = reader.GetString(0),
                                Value = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1),
                                ParameterID = reader.GetInt32(2)
                            });
                        }
                    }
                }
            }

            return values;
        }

        private async Task<double?> CalculateRollingAverageAQIAsync(
            int stationId,
            int deviceId,
            DateTime interval,
            string pollutant,
            int typeId,
            int hours)
        {
            var readings = await GetHistoricalReadingsAsync(
                stationId, deviceId, interval, pollutant, typeId, hours);

            // Check data availability (75% rule)
            int requiredCount = hours == 8
                ? AQIConstants.RequiredEightHourDataPoints
                : AQIConstants.Required24HourDataPoints;

            if (readings.Count < requiredCount)
            {
                return null;
            }

            // Calculate average
            var validReadings = readings.Where(r => r > 0).ToList();
            if (!validReadings.Any())
            {
                return null;
            }

            double average = validReadings.Average();

            // Determine pollutant type for AQI calculation
            string pollutantType = hours == 8
                ? $"8_{pollutant}"
                : $"24_{pollutant}";

            return CalculatePollutantAQI(average, pollutantType);
        }

        private async Task<double?> CalculateO3AQIAsync(
            int stationId,
            int deviceId,
            DateTime interval,
            int typeId,
            double o3Value)
        {
            if (o3Value <= 200)
            {
                // Use 8-hour average
                return await CalculateRollingAverageAQIAsync(
                    stationId, deviceId, interval, AQIConstants.O3, typeId, 8);
            }
            else
            {
                // Get 8-hour average
                var eightHourAQI = await CalculateRollingAverageAQIAsync(
                    stationId, deviceId, interval, AQIConstants.O3, typeId, 8);

                var eightHourReadings = await GetHistoricalReadingsAsync(
                    stationId, deviceId, interval, AQIConstants.O3, typeId, 8);

                double? eightHourAvg = eightHourReadings.Any()
                    ? eightHourReadings.Average()
                    : (double?)null;

                if (eightHourAvg <= 392)
                {
                    // Compare 8-hour and 1-hour
                    var oneHourAQI = CalculatePollutantAQI(o3Value, "1_O3");
                    return Math.Max(eightHourAQI ?? 0, oneHourAQI ?? 0);
                }
                else
                {
                    // Use 1-hour only
                    return CalculatePollutantAQI(o3Value, "1_O3");
                }
            }
        }

        private async Task<List<double>> GetHistoricalReadingsAsync(
            int stationId,
            int deviceId,
            DateTime interval,
            string pollutant,
            int typeId,
            int hours)
        {
            var readings = new List<double>();
            DateTime startInterval = interval.AddHours(-hours);

            string query = $@"
                SELECT pa.ParameterValue * COALESCE(
                    CASE WHEN u.UnitName <> pc.SecondaryUnit 
                         THEN TRY_CAST(pc.ConversionFactor AS FLOAT)
                         ELSE 1 END, 1) AS ConvertedValue
                FROM {_averageTableName} pa WITH (NOLOCK)
                INNER JOIN {_parameterTableName} dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
                INNER JOIN {_driverTableName} d WITH (NOLOCK) ON dp.DriverID = d.ID
                INNER JOIN ReportedUnits u WITH (NOLOCK) ON dp.UnitID = u.ID
                LEFT JOIN Parameter_Conversion pc WITH (NOLOCK) ON d.DriverName = pc.Parameter
                WHERE pa.StationID = @StationID 
                  AND pa.DeviceID = @DeviceID 
                  AND pa.Interval > @StartInterval 
                  AND pa.Interval <= @EndInterval 
                  AND pa.TypeID = @TypeID 
                  AND d.DriverName = @Pollutant
                ORDER BY pa.Interval DESC";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@StationID", stationId);
                    cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                    cmd.Parameters.AddWithValue("@StartInterval", startInterval);
                    cmd.Parameters.AddWithValue("@EndInterval", interval);
                    cmd.Parameters.AddWithValue("@TypeID", typeId);
                    cmd.Parameters.AddWithValue("@Pollutant", pollutant);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            if (!reader.IsDBNull(0))
                            {
                                readings.Add(reader.GetDouble(0));
                            }
                        }
                    }
                }
            }

            return readings;
        }

        public double? CalculatePollutantAQI(double? pollutantValue, string pollutantType)
        {
            if (!pollutantValue.HasValue)
                return null;

            double value = pollutantValue.Value;

            if (pollutantType == "8_O3") return CalculateO3_8HourAQI(value);
            if (pollutantType == "1_O3") return CalculateO3_1HourAQI(value);
            if (pollutantType == "8_CO") return CalculateCO_8HourAQI(value);
            if (pollutantType == "1_SO2") return CalculateSO2_1HourAQI(value);
            if (pollutantType == "24_SO2") return CalculateSO2_24HourAQI(value);
            if (pollutantType == "1_NO2") return CalculateNO2_1HourAQI(value);
            if (pollutantType == "24_PM10") return CalculatePM10_24HourAQI(value);
            if (pollutantType == "24_PM2.5") return CalculatePM25_24HourAQI(value);

            return null;
        }


        private double? CalculateO3_8HourAQI(double value)
        {
            if (value >= 0 && value <= 100.5)
                return LinearInterpolation(value, 0, 100.0, 0, 50);

            if (value > 100.5 && value <= 120.5)
                return LinearInterpolation(value, 101.0, 120.0, 51, 100);

            if (value > 120.5 && value <= 167.5)
                return LinearInterpolation(value, 121.0, 167.0, 101, 150);

            if (value > 167.5 && value <= 206.5)
                return LinearInterpolation(value, 168.0, 206.0, 151, 200);

            if (value > 206.5)
                return Math.Min(LinearInterpolation(value, 207.0, 392.0, 201, 300), 1500);

            return null;
        }


        private double? CalculateO3_1HourAQI(double value)
        {
            if (value >= 200 && value <= 322.5)
                return LinearInterpolation(value, 200.0, 322.0, 101, 150);

            if (value > 322.5 && value <= 400.5)
                return LinearInterpolation(value, 323.0, 400.0, 151, 200);

            if (value > 400.5 && value <= 792.5)
                return LinearInterpolation(value, 401.0, 792.0, 201, 300);

            if (value > 792.5)
                return Math.Min(LinearInterpolation(value, 793.0, 1184.0, 301, 500), 1500);

            return null;
        }


        private double? CalculateCO_8HourAQI(double value)
        {
            if (value >= 0 && value <= 5.4)
                return LinearInterpolation(value, 0.0, 5.4, 0, 50);

            if (value > 5.4 && value <= 10.4)
                return LinearInterpolation(value, 5.5, 10.4, 51, 100);

            if (value > 10.4 && value <= 14.4)
                return LinearInterpolation(value, 10.5, 14.4, 101, 150);

            if (value > 14.4 && value <= 17.9)
                return LinearInterpolation(value, 14.5, 17.9, 151, 200);

            if (value > 17.9 && value <= 35.4)
                return LinearInterpolation(value, 18.0, 35.4, 201, 300);

            if (value > 35.4)
                return Math.Min(LinearInterpolation(value, 35.5, 58.4, 301, 500), 1500);

            return null;
        }


        private double? CalculateSO2_1HourAQI(double value)
        {
            if (value >= 0 && value <= 92.5)
                return LinearInterpolation(value, 0.0, 92.0, 0, 50);

            if (value > 92.5 && value <= 350.5)
                return LinearInterpolation(value, 93.0, 350.0, 51, 100);

            if (value > 350.5 && value <= 485.5)
                return LinearInterpolation(value, 351.0, 485.0, 101, 150);

            if (value > 485.5)
                return LinearInterpolation(value, 486.0, 797.0, 151, 200);

            return null;
        }


        private double? CalculateSO2_24HourAQI(double value)
        {
            if (value > 797 && value <= 1583.5)
                return LinearInterpolation(value, 798.0, 1583.0, 201, 300);

            if (value > 1583.5)
                return Math.Min(LinearInterpolation(value, 1584.0, 2631.0, 301, 500), 1500);

            return null;
        }


        private double? CalculateNO2_1HourAQI(double value)
        {
            if (value >= 0 && value <= 100.5)
                return LinearInterpolation(value, 0.0, 100.0, 0, 50);

            if (value > 100.5 && value <= 400.5)
                return LinearInterpolation(value, 101.0, 400.0, 51, 100);

            if (value > 400.5 && value <= 677.5)
                return LinearInterpolation(value, 401.0, 677.0, 101, 150);

            if (value > 677.5 && value <= 1221.5)
                return LinearInterpolation(value, 678.0, 1221.0, 151, 200);

            if (value > 1221.5 && value <= 2349.5)
                return LinearInterpolation(value, 1222.0, 2349.0, 201, 300);

            if (value > 2349.5)
                return Math.Min(LinearInterpolation(value, 2350.0, 3853.0, 301, 500), 1500);

            return null;
        }


        private double? CalculatePM10_24HourAQI(double value)
        {
            if (value >= 0 && value <= 75.5)
                return LinearInterpolation(value, 0.0, 75.0, 0, 50);

            if (value > 75.5 && value <= 150.5)
                return LinearInterpolation(value, 76.0, 150.0, 51, 100);

            if (value > 150.5 && value <= 250.5)
                return LinearInterpolation(value, 151.0, 250.0, 101, 150);

            if (value > 250.5 && value <= 350.5)
                return LinearInterpolation(value, 251.0, 350.0, 151, 200);

            if (value > 350.5 && value <= 420.5)
                return LinearInterpolation(value, 351.0, 420.0, 201, 300);

            if (value > 420.5)
                return Math.Min(LinearInterpolation(value, 421.0, 600.0, 301, 500), 1500);

            return null;
        }


        private double? CalculatePM25_24HourAQI(double value)
        {
            if (value >= 0 && value <= 50.4)
                return LinearInterpolation(value, 0.0, 50.4, 0, 50);

            if (value > 50.4 && value <= 60.4)
                return LinearInterpolation(value, 50.5, 60.4, 51, 100);

            if (value > 60.4 && value <= 75.4)
                return LinearInterpolation(value, 60.5, 75.4, 101, 150);

            if (value > 75.4 && value <= 150.4)
                return LinearInterpolation(value, 75.5, 150.4, 151, 200);

            if (value > 150.4 && value <= 250.4)
                return LinearInterpolation(value, 150.5, 250.4, 201, 300);

            if (value > 250.4)
                return Math.Min(LinearInterpolation(value, 250.5, 500.4, 301, 500), 1500);

            return null;
        }


        private double LinearInterpolation(double value, double cLow, double cHigh, double aqiLow, double aqiHigh)
        {
            return ((aqiHigh - aqiLow) / (cHigh - cLow)) * (value - cLow) + aqiLow;
        }

        private double? GetPollutantValue(List<PollutantValue> values, string pollutant)
        {
            return values.FirstOrDefault(v => v.DriverName == pollutant)?.Value;
        }

        private class PollutantValue
        {
            public string DriverName { get; set; }
            public double? Value { get; set; }
            public int ParameterID { get; set; }
        }
    }
}