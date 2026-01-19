namespace AQMSDataUpdateLibrary.Constants
{
    public static class AQIConstants
    {
        // Data availability thresholds
        public const double MinDataCoveragePercent = 75.0;
        public const int RequiredEightHourDataPoints = 6;  // 75% of 8
        public const int Required24HourDataPoints = 18;    // 75% of 24

        // Pollutant names
        public const string PM25 = "PM2.5";
        public const string PM10 = "PM10";
        public const string O3 = "O₃";
        public const string SO2 = "SO₂";
        public const string NO2 = "NO₂";
        public const string CO = "CO";
        public const string NO = "NO";
        public const string NOX = "NOX";
        public const string AQI_INDEX = "AQI Index";

        // TypeID values
        public const int OneHourTypeID = 60;
        public const int TwentyFourHourTypeID = 1440;
        public const int MonthTypeID = 43200;
        public const int YearTypeID = 365;
    }

    public static class SQLQueries
    {
        // Optimized queries with proper indexing hints
        public const string GetActiveParameters = @"
            SELECT p.*, d.DriverName AS ParameterDriverName 
            FROM {0} p WITH (NOLOCK)
            INNER JOIN {1} d WITH (NOLOCK) ON p.DriverID = d.ID 
            WHERE p.ServerAvgInterval IS NOT NULL 
              AND p.DeviceID = @DeviceID
              AND d.DriverName != 'AQI Index'";

        public const string GetLatestInterval = @"
            SELECT TOP(1) [Interval] 
            FROM {0} WITH (NOLOCK)
            WHERE StationID = @StationID 
              AND DeviceID = @DeviceID 
              AND ParameterID = @ParameterID 
              AND TypeID = @TypeID 
            ORDER BY [Interval] DESC";

        public const string BulkGetIntervalCounts = @"
            WITH IntervalCounts AS (
                SELECT 
                    DATEADD({0}, DATEDIFF({0}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval,
                    COUNT(*) AS RecordCount
                FROM {1} sd WITH (NOLOCK, INDEX(IX_ParameterReadings_Station_Device_Parameter_Time))
                WHERE sd.StationID = @StationID 
                  AND sd.DeviceID = @DeviceID 
                  AND sd.ParameterID = @ParameterID
                  AND (@LastInterval IS NULL OR sd.CreatedTime > @LastInterval)
                GROUP BY DATEADD({0}, DATEDIFF({0}, 0, sd.CreatedTime) / @Interval * @Interval, 0)
            )
            SELECT Interval, RecordCount AS TotReccnt
            FROM IntervalCounts
            ORDER BY Interval ASC";

        public const string GetValidRecordCounts = @"
            SELECT 
                DATEADD({0}, DATEDIFF({0}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval,
                COUNT(*) AS cnt
            FROM {1} sd WITH (NOLOCK)
            INNER JOIN {2} f WITH (NOLOCK) ON f.ID = sd.LoggerFlags
            WHERE sd.StationID = @StationID 
              AND sd.DeviceID = @DeviceID 
              AND sd.ParameterID = @ParameterID
              AND f.Type != 'Validation'
              AND DATEADD({0}, DATEDIFF({0}, 0, sd.CreatedTime) / @Interval * @Interval, 0) = @IntervalValue
            GROUP BY DATEADD({0}, DATEDIFF({0}, 0, sd.CreatedTime) / @Interval * @Interval, 0)";

        public const string GetHighPriorityFlag = @"
            SELECT TOP(1) z.LoggerFlags
            FROM (
                SELECT 
                    a.LoggerFlags,
                    a.Priority,
                    COUNT(*) as NoOfRecords
                FROM (
                    SELECT 
                        DATEADD({0}, DATEDIFF({0}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval,
                        sd.LoggerFlags,
                        f.Priority
                    FROM {1} sd WITH (NOLOCK)
                    LEFT JOIN {2} f WITH (NOLOCK) ON sd.LoggerFlags = f.ID
                    WHERE sd.StationID = @StationID 
                      AND sd.DeviceID = @DeviceID 
                      AND sd.ParameterID = @ParameterID 
                      AND sd.LoggerFlags IS NOT NULL
                ) a
                WHERE a.Interval = @IntervalValue
                GROUP BY a.LoggerFlags, a.Priority
            ) z
            ORDER BY z.NoOfRecords DESC, z.Priority ASC";
    }
}
