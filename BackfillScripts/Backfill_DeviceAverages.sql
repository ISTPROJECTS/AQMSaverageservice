/*======================================================================================
  Backfill_DeviceAverages.sql
  --------------------------------------------------------------------------------------
  PURPOSE
    Backfill ParameterAverages / ParameterAveragesMonth / ParameterAveragesYear (incl.
    AQI + SubIndex) for ONE device whose backlog 5-minute raw data was inserted into
    ParameterReadings *after* the device already had newer averages.

  WHY THIS IS NEEDED
    The live service (AvgDataCalculationServer.cs) resumes every stage from the LATEST
    interval present in the averages tables (GetLatestIntervalRecordFromAvgTable +
    "WHERE a.Interval > @intervalValue"). Backlog rows carry OLDER timestamps, so they
    fall behind that watermark and are never averaged.

    This script instead GAP-FILLS: it processes every reading bucket and inserts only
    where no average row exists yet (NOT EXISTS). All value math, thresholds and AQI
    breakpoints are ported 1:1 from AvgDataCalculationServer.cs. Safe / idempotent to
    re-run (re-running inserts 0 new rows).

  HOW TO USE
    1. Set @DeviceID below.
    2. Keep @TestMode = 1 for the first run  -> everything is ROLLED BACK so you can
       inspect counts safely. Set @TestMode = 0 to actually COMMIT.
    3. Run the whole file (it contains a few GO batches for the helper functions).

  CONFIG ASSUMPTIONS (from AQMSDataUpdateService\App.config)
    defaultInterval = 1 ; AQIParameters = PM10,O3,CO,PM2.5,SO2,NO2 (unicode subscripts)
    winddirection   = WD,Wind Direction ; rain = RAIN
    Table names: ParameterReadings, ParameterAverages, ParameterAveragesMonth,
                 ParameterAveragesYear, DMN_Parameters, MST_Devices_Drivers, DMN_Flags,
                 Parameter_Conversion, ReportedUnits
  --------------------------------------------------------------------------------------
  NOTE ON COVERAGE
    - Calculated parameters (NOX from NO/NO2) are intentionally NOT handled.
    - Interval set is driven by each parameter's DMN_Parameters.ServerAvgInterval.
======================================================================================*/


/*======================================================================================
  HELPER 1 : dbo.fn_PollutantAQI
  Exact port of CalculatePollutantAQIValues (AvgDataCalculationServer.cs:2171).
  Piecewise linear AQI breakpoints per pollutant "category". Returns NULL for NULL input.
  The ">1500 -> 1500" cap is applied ONLY in the top band of the categories that have it
  in the C# (1_O3, 8_CO, 24_SO2, 1_NO2, 24_PM10, 24_PM2.5).
======================================================================================*/
GO
CREATE OR ALTER FUNCTION dbo.fn_PollutantAQI (@v FLOAT, @cat VARCHAR(20))
RETURNS FLOAT
AS
BEGIN
    IF @v IS NULL RETURN NULL;
    DECLARE @r FLOAT = 0.0;

    IF @cat = '8_O3'
    BEGIN
        IF      @v >= 0    AND @v <= 100.5 SET @r = (50.0 - 0)    / (100.0 - 0)   * (@v - 0)     + 0;
        ELSE IF @v > 100.5 AND @v <= 120.5 SET @r = (100.0 - 51.0)/ (120.0 - 101.0)* (@v - 101.0)+ 51;
        ELSE IF @v > 120.5 AND @v <= 167.5 SET @r = (150.0 - 101.0)/(167.0 - 121.0)* (@v - 121.0)+ 101;
        ELSE IF @v > 167.5 AND @v <= 206.5 SET @r = (200.0 - 151.0)/(206.0 - 168.0)* (@v - 168.0)+ 151;
        ELSE IF @v > 206.5                 SET @r = (300.0 - 201.0)/(392.0 - 207.0)* (@v - 207.0)+ 201;
    END
    ELSE IF @cat = '1_O3'
    BEGIN
        IF      @v >= 200  AND @v <= 322.5 SET @r = (150.0 - 101.0)/(322.0 - 200.0)* (@v - 200.0)+ 101;
        ELSE IF @v > 322.5 AND @v <= 400.5 SET @r = (200.0 - 151.0)/(400.0 - 323.0)* (@v - 323.0)+ 151;
        ELSE IF @v > 400.5 AND @v <= 792.5 SET @r = (300.0 - 201.0)/(792.0 - 401.0)* (@v - 401.0)+ 201;
        ELSE IF @v > 792.5
        BEGIN
            SET @r = (500.0 - 301.0)/(1184.0 - 793.0)* (@v - 793.0)+ 301;
            IF @r > 1500 SET @r = 1500;
        END
    END
    ELSE IF @cat = '8_CO'
    BEGIN
        IF      @v >= 0.0  AND @v <= 5.4  SET @r = (50.0 - 0)    / (5.4 - 0.0)  * (@v - 0.0)  + 0;
        ELSE IF @v > 5.4   AND @v <= 10.4 SET @r = (100.0 - 51.0)/ (10.4 - 5.5) * (@v - 5.5)  + 51;
        ELSE IF @v > 10.4  AND @v <= 14.4 SET @r = (150.0 - 101.0)/(14.4 - 10.5)* (@v - 10.5) + 101;
        ELSE IF @v > 14.4  AND @v <= 17.9 SET @r = (200.0 - 151.0)/(17.9 - 14.5)* (@v - 14.5) + 151;
        ELSE IF @v > 17.9  AND @v <= 35.4 SET @r = (300.0 - 201.0)/(35.4 - 18.0)* (@v - 18.0) + 201;
        ELSE IF @v > 35.4
        BEGIN
            SET @r = (500.0 - 301.0)/(58.4 - 35.5)* (@v - 35.5) + 301;
            IF @r > 1500 SET @r = 1500;
        END
    END
    ELSE IF @cat = '1_SO2'
    BEGIN
        IF      @v >= 0    AND @v <= 92.5  SET @r = (50.0 - 0.0)  / (92.0 - 0.0)  * (@v - 0)   + 0;
        ELSE IF @v > 92.5  AND @v <= 350.5 SET @r = (100.0 - 51.0)/ (350.0 - 93.0)* (@v - 93)  + 51;
        ELSE IF @v > 350.5 AND @v <= 485.5 SET @r = (150.0 - 101.0)/(485.0 - 351.0)*(@v - 351) + 101;
        ELSE IF @v > 485.5                 SET @r = (200.0 - 151.0)/(797.0 - 486.0)*(@v - 486) + 151;
    END
    ELSE IF @cat = '24_SO2'
    BEGIN
        IF      @v > 797   AND @v <= 1583.5 SET @r = (300.0 - 201.0)/(1583.0 - 798.0)* (@v - 798) + 201;
        ELSE IF @v > 1583.5
        BEGIN
            SET @r = (500.0 - 301.0)/(2631.0 - 1584.0)* (@v - 1584) + 301;
            IF @r > 1500 SET @r = 1500;
        END
    END
    ELSE IF @cat = '1_NO2'
    BEGIN
        IF      @v >= 0     AND @v <= 100.5  SET @r = (50.0 - 0)    / (100.0 - 0)    * (@v - 0)    + 0;
        ELSE IF @v > 100.5  AND @v <= 400.5  SET @r = (100.0 - 51.0)/ (400.0 - 101.0)* (@v - 101)  + 51;
        ELSE IF @v > 400.5  AND @v <= 677.5  SET @r = (150.0 - 101.0)/(677.0 - 401.0)* (@v - 401)  + 101;
        ELSE IF @v > 677.5  AND @v <= 1221.5 SET @r = (200.0 - 151.0)/(1221.0 - 678.0)*(@v - 678)  + 151;
        ELSE IF @v > 1221.5 AND @v <= 2349.5 SET @r = (300.0 - 201.0)/(2349.0 - 1222.0)*(@v - 1222)+ 201;
        ELSE IF @v > 2349.5
        BEGIN
            SET @r = (500.0 - 301.0)/(3853.0 - 2350.0)* (@v - 2350) + 301;
            IF @r > 1500 SET @r = 1500;
        END
    END
    ELSE IF @cat = '24_PM10'
    BEGIN
        IF      @v >= 0     AND @v <= 75.5  SET @r = (50.0 - 0)    / (75.0 - 0)    * (@v - 0)   + 0;
        ELSE IF @v > 75.5   AND @v <= 150.5 SET @r = (100.0 - 51.0)/ (150.0 - 76.0)* (@v - 76)  + 51;
        ELSE IF @v > 150.5  AND @v <= 250.5 SET @r = (150.0 - 101.0)/(250.0 - 151.0)*(@v - 151) + 101;
        ELSE IF @v > 250.5  AND @v <= 350.5 SET @r = (200.0 - 151.0)/(350.0 - 251.0)*(@v - 251) + 151;
        ELSE IF @v > 350.5  AND @v <= 420.5 SET @r = (300.0 - 201.0)/(420.0 - 351.0)*(@v - 351) + 201;
        ELSE IF @v > 420.5
        BEGIN
            SET @r = (500.0 - 301.0)/(600.0 - 421.0)* (@v - 421) + 301;
            IF @r > 1500 SET @r = 1500;
        END
    END
    ELSE IF @cat = '24_PM2.5'
    BEGIN
        IF      @v >= 0.0   AND @v <= 50.4  SET @r = (50.0 - 0)    / (50.4 - 0.0)  * (@v - 0.0)  + 0;
        ELSE IF @v > 50.4   AND @v <= 60.4  SET @r = (100.0 - 51.0)/ (60.4 - 50.5) * (@v - 50.5) + 51;
        ELSE IF @v > 60.4   AND @v <= 75.4  SET @r = (150.0 - 101.0)/(75.4 - 60.5) * (@v - 60.5) + 101;
        ELSE IF @v > 75.4   AND @v <= 150.4 SET @r = (200.0 - 151.0)/(150.4 - 75.5)* (@v - 75.5) + 151;
        ELSE IF @v > 150.4  AND @v <= 250.4 SET @r = (300.0 - 201.0)/(250.4 - 150.5)*(@v - 150.5)+ 201;
        ELSE IF @v > 250.4
        BEGIN
            SET @r = (500.0 - 301.0)/(500.4 - 250.4)* (@v - 250.4)+ 301;
            IF @r > 1500 SET @r = 1500;
        END
    END

    RETURN @r;
END
GO


/*======================================================================================
  HELPER 2 : dbo.fn_RollingAvg
  Port of CalculateEightHoursRollingAverageAQI (cs:2410) and
  CalculateTwentryFourHoursRollingAverage (cs:2510) -- the rolling AVERAGE part only.
    * Window  : (Interval - @Hours, Interval]  on TypeID = @TypeID (hourly = 60).
    * Gate    : total rows in window must be >= @MinCount (6 for 8h, 18 for 24h).
    * Average : ONLY over rows whose converted value > 0 ; divide by that positive count.
    * Returns : the rolling average value (NULL if gate fails or no positive rows).
                AQI is obtained by wrapping the result in dbo.fn_PollutantAQI(..).
  Conversion mirrors the service:
      ParameterValue * COALESCE(CASE WHEN UnitName<>SecondaryUnit
                                     THEN TRY_CAST(ConversionFactor AS FLOAT) ELSE 1 END,1)
======================================================================================*/
GO
CREATE OR ALTER FUNCTION dbo.fn_RollingAvg
(
    @StationID INT,
    @DeviceID  INT,
    @I         DATETIME,
    @Driver    NVARCHAR(100),
    @TypeID    INT,
    @Hours     INT,
    @MinCount  INT
)
RETURNS FLOAT
AS
BEGIN
    DECLARE @cnt INT, @pos INT, @sum FLOAT;

    SELECT @cnt = COUNT(*),
           @pos = SUM(CASE WHEN x.conv > 0 THEN 1 ELSE 0 END),
           @sum = SUM(CASE WHEN x.conv > 0 THEN x.conv ELSE 0 END)
    FROM (
        SELECT pa.ParameterValue * COALESCE(CASE WHEN u.UnitName <> pc.SecondaryUnit
                                                 THEN TRY_CAST(pc.ConversionFactor AS FLOAT)
                                                 ELSE 1 END, 1) AS conv
        FROM ParameterAverages pa
        INNER JOIN DMN_Parameters       dp ON pa.ParameterID = dp.ID
        INNER JOIN MST_Devices_Drivers  d  ON dp.DriverID    = d.ID
        INNER JOIN ReportedUnits        u  ON dp.UnitID      = u.ID
        LEFT  JOIN Parameter_Conversion pc ON d.DriverName   = pc.Parameter
        WHERE pa.StationID = @StationID
          AND pa.DeviceID  = @DeviceID
          AND pa.Interval <= @I
          AND pa.Interval  > DATEADD(HOUR, -@Hours, @I)
          AND pa.TypeID    = @TypeID
          AND d.DriverName = @Driver
    ) x;

    IF @cnt IS NULL OR @cnt < @MinCount RETURN NULL;   -- 75% availability gate
    IF @pos IS NULL OR @pos = 0          RETURN NULL;   -- no positive values
    RETURN @sum / @pos;
END
GO


/*======================================================================================
  MAIN BACKFILL BATCH
======================================================================================*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

-----------------------------------------------------------------------------------------
-- PARAMETERS
-----------------------------------------------------------------------------------------
DECLARE @DeviceID        INT = 0;     -- <<<<<<<<<<<<<< SET THE DEVICE ID HERE
DECLARE @TestMode        BIT = 1;     -- 1 = ROLLBACK (dry run) , 0 = COMMIT
DECLARE @defaultInterval INT = 1;     -- App.config defaultInterval

IF @DeviceID = 0
BEGIN
    RAISERROR('Set @DeviceID before running.', 16, 1);
    RETURN;
END

-- AQI pollutant driver names (unicode subscripts, matching MST_Devices_Drivers.DriverName)
DECLARE @PM10 NVARCHAR(20) = N'PM10';
DECLARE @O3   NVARCHAR(20) = N'O₃';
DECLARE @CO   NVARCHAR(20) = N'CO';
DECLARE @PM25 NVARCHAR(20) = N'PM2.5';
DECLARE @SO2  NVARCHAR(20) = N'SO₂';
DECLARE @NO2  NVARCHAR(20) = N'NO₂';

DECLARE @cntStage1 INT = 0, @cntAqiHr INT = 0, @cntAqiNh INT = 0;
DECLARE @cntMonthIns INT = 0, @cntMonthUpd INT = 0, @cntYearIns INT = 0, @cntYearUpd INT = 0;

BEGIN TRY
BEGIN TRANSACTION;

/*======================================================================================
  STAGE 1 : REGULAR AVERAGES (hourly / daily / any configured interval)
  Port of InsertParameterAvgData (cs:1562) + InsertDataIntoAvgTable (cs:512).
  Driven by each parameter's ServerAvgInterval tokens (e.g. "1-H,24-H").
  Bucketing uses MINUTE datepart with N = interval-minutes; this is mathematically
  identical to the C# HOUR/MINUTE bucketing  ( floor(floor(M/60)/k) = floor(M/(60k)) ).
======================================================================================*/

IF OBJECT_ID('tempdb..#tok') IS NOT NULL DROP TABLE #tok;

SELECT
    ROW_NUMBER() OVER (ORDER BY p.ID) AS RID,
    p.ID                                  AS ParameterID,
    p.StationID                           AS StationID,
    d.DriverName                          AS DriverName,
    COALESCE(p.DataSyncFrequency, @defaultInterval) AS intSrv,
    LTRIM(RTRIM(LEFT(s.value, CHARINDEX('-', s.value) - 1)))      AS nStr,
    LTRIM(RTRIM(SUBSTRING(s.value, CHARINDEX('-', s.value) + 1, 10))) AS unitCode
INTO #tok
FROM DMN_Parameters p
INNER JOIN MST_Devices_Drivers d ON p.DriverID = d.ID
CROSS APPLY STRING_SPLIT(p.ServerAvgInterval, ',') s
WHERE p.DeviceID = @DeviceID
  AND d.DriverName <> N'AQI Index'
  AND p.ServerAvgInterval IS NOT NULL
  AND CHARINDEX('-', s.value) > 0;

DECLARE @rid INT = 1, @maxRid INT = (SELECT ISNULL(MAX(RID),0) FROM #tok);
DECLARE @ParameterID INT, @StationID INT, @DriverName NVARCHAR(100), @intSrv INT;
DECLARE @nStr NVARCHAR(10), @unitCode NVARCHAR(10);
DECLARE @N INT;          -- interval length in MINUTES (= PtypeID = TypeID for regular avg)
DECLARE @IsRain BIT, @IsWind BIT;

WHILE @rid <= @maxRid
BEGIN
    SELECT @ParameterID = ParameterID, @StationID = StationID, @DriverName = DriverName,
           @intSrv = intSrv, @nStr = nStr, @unitCode = unitCode
    FROM #tok WHERE RID = @rid;

    IF @ParameterID IS NULL BEGIN SET @rid += 1; CONTINUE; END

    SET @N = CAST(@nStr AS INT) * CASE WHEN @unitCode = 'M' THEN 1 ELSE 60 END;

    -- skip if interval is smaller than the raw data frequency (PtypeID < intServerInterval)
    IF @N < @intSrv BEGIN SET @rid += 1; CONTINUE; END

    -- rain / wind-direction membership (App.config: rain="RAIN", winddirection="WD,Wind Direction")
    SET @IsRain = CASE WHEN 'RAIN'               LIKE '%' + UPPER(@DriverName) + '%' THEN 1 ELSE 0 END;
    SET @IsWind = CASE WHEN 'WD,WIND DIRECTION'  LIKE '%' + UPPER(@DriverName) + '%' THEN 1 ELSE 0 END;

    ;WITH Reads AS (
        SELECT sd.Parametervalue, sd.LoggerFlags, sd.ParameterIDRef,
               DATEADD(MINUTE, DATEDIFF(MINUTE, 0, sd.CreatedTime) / @N * @N, 0) AS Bucket
        FROM ParameterReadings sd
        WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID
    ),
    Buckets AS (   -- candidate buckets
        SELECT Bucket, MAX(ParameterIDRef) AS ParameterIDRef
        FROM Reads GROUP BY Bucket
    ),
    PriRank AS (   -- dominant logger flag per bucket: most records, tie -> lowest priority
        SELECT r.Bucket, r.LoggerFlags,
               ROW_NUMBER() OVER (PARTITION BY r.Bucket ORDER BY COUNT(*) DESC, MIN(f.Priority) ASC) AS rn
        FROM Reads r LEFT JOIN DMN_Flags f ON r.LoggerFlags = f.ID
        WHERE r.LoggerFlags IS NOT NULL
        GROUP BY r.Bucket, r.LoggerFlags
    ),
    Priority AS (
        SELECT Bucket, LoggerFlags AS PriorityFlag FROM PriRank WHERE rn = 1
    ),
    ValidCnt AS (  -- count of NON-validation flagged records (for the 75% rule)
        SELECT r.Bucket, COUNT(*) AS ValidCnt
        FROM Reads r INNER JOIN DMN_Flags f ON r.LoggerFlags = f.ID AND f.Type <> 'Validation'
        GROUP BY r.Bucket
    ),
    AggByFlag AS ( -- aggregates over (bucket, flag) for non-validation rows -> used when PriorityFlag<>0
        SELECT r.Bucket, r.LoggerFlags,
               AVG(r.Parametervalue) AS AvgVal,
               SUM(r.Parametervalue) AS SumVal,
               DEGREES(ATN2(AVG(SIN(RADIANS(r.Parametervalue))),
                            AVG(COS(RADIANS(r.Parametervalue))))) AS WindRaw
        FROM Reads r INNER JOIN DMN_Flags f ON r.LoggerFlags = f.ID AND f.Type <> 'Validation'
        GROUP BY r.Bucket, r.LoggerFlags
    ),
    AggAll AS (    -- aggregate over ALL rows in bucket -> used when PriorityFlag = 0 (no flagged rows)
        SELECT r.Bucket, AVG(r.Parametervalue) AS AvgVal, SUM(r.Parametervalue) AS SumVal
        FROM Reads r GROUP BY r.Bucket
    )
    INSERT INTO ParameterAverages
        (StationID, DeviceID, ParameterID, Parametervalue, Type, Interval, LoggerFlags, TypeID, CreatedTime, ParameterIDRef)
    SELECT
        @StationID, @DeviceID, @ParameterID,
        CASE
            WHEN pr.PriorityFlag IS NULL THEN          -- branch 4: no flagged records
                 CASE WHEN ap.pct >= 75
                      THEN CASE WHEN @IsRain = 1 THEN aa.SumVal ELSE aa.AvgVal END
                      ELSE NULL END
            ELSE                                        -- branches 1/2/3: PriorityFlag <> 0
                 CASE WHEN ap.pct >= 75 THEN
                          CASE WHEN @IsWind = 1
                                    THEN CASE WHEN abf.WindRaw < 0 THEN 360 + abf.WindRaw ELSE abf.WindRaw END
                               WHEN @IsRain = 1 THEN abf.SumVal
                               ELSE abf.AvgVal END
                      ELSE NULL END                     -- pct < 75 -> NULL value (row still inserted)
        END                                            AS Parametervalue,
        @nStr + @unitCode                              AS Type,
        bk.Bucket                                      AS Interval,
        pr.PriorityFlag                                AS LoggerFlags,   -- NULL when branch 4
        @N                                             AS TypeID,
        GETDATE()                                      AS CreatedTime,
        bk.ParameterIDRef                              AS ParameterIDRef
    FROM Buckets bk
    LEFT JOIN Priority  pr  ON pr.Bucket  = bk.Bucket
    LEFT JOIN ValidCnt  vc  ON vc.Bucket  = bk.Bucket
    LEFT JOIN AggByFlag abf ON abf.Bucket = bk.Bucket AND abf.LoggerFlags = pr.PriorityFlag
    LEFT JOIN AggAll    aa  ON aa.Bucket  = bk.Bucket
    CROSS APPLY (VALUES ( (ISNULL(vc.ValidCnt,0) * @intSrv * 100) / @N )) ap(pct)  -- integer math, as in C#
    WHERE DATEADD(MINUTE, @N, bk.Bucket) <= GETDATE()        -- only completed intervals
      AND NOT EXISTS (
            SELECT 1 FROM ParameterAverages b
            WHERE b.StationID = @StationID AND b.DeviceID = @DeviceID
              AND b.ParameterID = @ParameterID AND b.Interval = bk.Bucket AND b.TypeID = @N);

    SET @cntStage1 += @@ROWCOUNT;
    SET @rid += 1;
END

PRINT 'Stage 1 (regular averages) rows inserted: ' + CAST(@cntStage1 AS VARCHAR(20));


/*======================================================================================
  STAGE 2 : AQI + SUBINDEX
  Port of InsertAQIParameterAvgData (cs:1779), InsertAQI (cs:2029),
  UpdateSubindex (cs:2061), plus the O3 / SO2 conditional paths.
  Runs AFTER Stage 1 so pollutant hourly rows exist.
======================================================================================*/

DECLARE @AqiParamId INT, @AqiParamRef INT, @AqiStationID INT, @AqiServerInterval NVARCHAR(200), @AqiSrv INT;

SELECT TOP (1)
       @AqiParamId        = p.ID,
       @AqiParamRef       = p.ParameterID,
       @AqiStationID      = p.StationID,
       @AqiServerInterval = p.ServerAvgInterval,
       @AqiSrv            = COALESCE(p.DataSyncFrequency, @defaultInterval)
FROM DMN_Parameters p
INNER JOIN MST_Devices_Drivers d ON p.DriverID = d.ID
WHERE p.DeviceID = @DeviceID AND d.DriverName = N'AQI Index' AND p.ServerAvgInterval IS NOT NULL;

IF @AqiParamId IS NOT NULL
BEGIN
    -- explode AQI ServerAvgInterval tokens
    IF OBJECT_ID('tempdb..#aqitok') IS NOT NULL DROP TABLE #aqitok;
    SELECT
        ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS RID,
        CAST(LTRIM(RTRIM(LEFT(s.value, CHARINDEX('-', s.value) - 1))) AS INT)
            * CASE WHEN LTRIM(RTRIM(SUBSTRING(s.value, CHARINDEX('-', s.value)+1, 10))) = 'M' THEN 1 ELSE 60 END AS PtypeID
    INTO #aqitok
    FROM STRING_SPLIT(@AqiServerInterval, ',') s
    WHERE CHARINDEX('-', s.value) > 0;

    DECLARE @aqRid INT = 1, @aqMax INT = (SELECT ISNULL(MAX(RID),0) FROM #aqitok);
    DECLARE @P INT;     -- AQI interval in minutes

    -- scalars reused across loops
    DECLARE @I DATETIME, @B DATETIME;
    DECLARE @vpm10 FLOAT, @vo3 FLOAT, @so2v FLOAT, @vno2 FLOAT, @vco FLOAT, @vpm25 FLOAT;
    DECLARE @pm10id INT, @o3id INT, @so2id INT, @no2id INT, @coid INT, @pm25id INT;
    DECLARE @aqipm10 FLOAT, @aqio3 FLOAT, @aqiso2 FLOAT, @aqino2 FLOAT, @aqico FLOAT, @aqipm25 FLOAT;
    DECLARE @avg8o3 FLOAT, @aqi8o3 FLOAT, @aqi1o3 FLOAT, @aqi FLOAT, @aqiavg FLOAT;

    WHILE @aqRid <= @aqMax
    BEGIN
        SELECT @P = PtypeID FROM #aqitok WHERE RID = @aqRid;

        IF @P IS NULL OR @P < @AqiSrv BEGIN SET @aqRid += 1; CONTINUE; END

        /*----------------------------------------------------------------------------
          HOURLY AQI BRANCH (PtypeID = 60)
        ----------------------------------------------------------------------------*/
        IF @P = 60
        BEGIN
            IF OBJECT_ID('tempdb..#aqih') IS NOT NULL DROP TABLE #aqih;
            SELECT DISTINCT pa.Interval
            INTO #aqih
            FROM ParameterAverages pa
            INNER JOIN DMN_Parameters dp      ON pa.ParameterID = dp.ID
            INNER JOIN MST_Devices_Drivers d  ON dp.DriverID    = d.ID
            WHERE pa.DeviceID = @DeviceID AND pa.StationID = @AqiStationID AND pa.TypeID = 60
              AND d.DriverName IN (@PM10, @O3, @CO, @PM25, @SO2, @NO2) AND dp.shouldUseForAqi = 1
              AND DATEADD(MINUTE, 60, pa.Interval) <= GETDATE()
              AND NOT EXISTS (SELECT 1 FROM ParameterAverages b
                              WHERE b.DeviceID = @DeviceID AND b.StationID = @AqiStationID
                                AND b.ParameterID = @AqiParamId AND b.Interval = pa.Interval AND b.TypeID = 60);

            DECLARE curH CURSOR LOCAL FAST_FORWARD FOR SELECT Interval FROM #aqih ORDER BY Interval;
            OPEN curH; FETCH NEXT FROM curH INTO @I;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                -- reset
                SELECT @vpm10=NULL,@vo3=NULL,@so2v=NULL,@vno2=NULL,@vco=NULL,@vpm25=NULL,
                       @pm10id=NULL,@o3id=NULL,@so2id=NULL,@no2id=NULL,@coid=NULL,@pm25id=NULL,
                       @aqipm10=NULL,@aqio3=NULL,@aqiso2=NULL,@aqino2=NULL,@aqico=NULL,@aqipm25=NULL,@aqi=NULL;

                -- converted hourly pollutant values + their parameter ids (cs:182)
                SELECT
                    @vpm10 = MAX(CASE WHEN z.dn = @PM10 THEN z.conv END),
                    @vo3   = MAX(CASE WHEN z.dn = @O3   THEN z.conv END),
                    @so2v  = MAX(CASE WHEN z.dn = @SO2  THEN z.conv END),
                    @vno2  = MAX(CASE WHEN z.dn = @NO2  THEN z.conv END),
                    @vco   = MAX(CASE WHEN z.dn = @CO   THEN z.conv END),
                    @vpm25 = MAX(CASE WHEN z.dn = @PM25 THEN z.conv END),
                    @pm10id = MAX(CASE WHEN z.dn = @PM10 THEN z.pid END),
                    @o3id   = MAX(CASE WHEN z.dn = @O3   THEN z.pid END),
                    @so2id  = MAX(CASE WHEN z.dn = @SO2  THEN z.pid END),
                    @no2id  = MAX(CASE WHEN z.dn = @NO2  THEN z.pid END),
                    @coid   = MAX(CASE WHEN z.dn = @CO   THEN z.pid END),
                    @pm25id = MAX(CASE WHEN z.dn = @PM25 THEN z.pid END)
                FROM (
                    SELECT pa.ParameterID AS pid, d.DriverName AS dn,
                           pa.ParameterValue * COALESCE(CASE WHEN u.UnitName <> pc.SecondaryUnit
                                                             THEN TRY_CAST(pc.ConversionFactor AS FLOAT)
                                                             ELSE 1 END, 1) AS conv
                    FROM ParameterAverages pa
                    INNER JOIN DMN_Parameters dp      ON pa.ParameterID = dp.ID
                    INNER JOIN MST_Devices_Drivers d  ON dp.DriverID    = d.ID
                    INNER JOIN ReportedUnits u        ON dp.UnitID      = u.ID
                    LEFT  JOIN Parameter_Conversion pc ON d.DriverName  = pc.Parameter
                    WHERE pa.DeviceID = @DeviceID AND pa.StationID = @AqiStationID
                      AND pa.Interval = @I AND pa.TypeID = 60
                      AND d.DriverName IN (@PM10, @O3, @CO, @PM25, @SO2, @NO2) AND dp.shouldUseForAqi = 1
                ) z;

                -- O3 : 8h rolling, with 1h fallback (cs:1871)
                SET @avg8o3 = dbo.fn_RollingAvg(@AqiStationID, @DeviceID, @I, @O3, 60, 8, 6);
                SET @aqi8o3 = dbo.fn_PollutantAQI(@avg8o3, '8_O3');
                IF @vo3 <= 200
                    SET @aqio3 = @aqi8o3;
                ELSE IF @vo3 > 200 AND @avg8o3 <= 392
                BEGIN
                    SET @aqi1o3 = dbo.fn_PollutantAQI(@vo3, '1_O3');
                    SET @aqio3  = CASE WHEN @aqi8o3 > @aqi1o3 THEN @aqi8o3 ELSE @aqi1o3 END;
                END
                ELSE IF @avg8o3 > 392
                    SET @aqio3 = dbo.fn_PollutantAQI(@vo3, '1_O3');

                -- CO : 8h rolling
                SET @aqico = dbo.fn_PollutantAQI(dbo.fn_RollingAvg(@AqiStationID, @DeviceID, @I, @CO, 60, 8, 6), '8_CO');

                -- NO2 : 1h
                SET @aqino2 = dbo.fn_PollutantAQI(@vno2, '1_NO2');

                -- PM10 / PM2.5 : 24h rolling
                SET @aqipm10 = dbo.fn_PollutantAQI(dbo.fn_RollingAvg(@AqiStationID, @DeviceID, @I, @PM10, 60, 24, 18), '24_PM10');
                SET @aqipm25 = dbo.fn_PollutantAQI(dbo.fn_RollingAvg(@AqiStationID, @DeviceID, @I, @PM25, 60, 24, 18), '24_PM2.5');

                -- SO2 : 1h if <=797 else 24h rolling
                IF @so2v <= 797
                    SET @aqiso2 = dbo.fn_PollutantAQI(@so2v, '1_SO2');
                ELSE
                    SET @aqiso2 = dbo.fn_PollutantAQI(dbo.fn_RollingAvg(@AqiStationID, @DeviceID, @I, @SO2, 60, 24, 18), '24_SO2');

                -- AQI = MAX of non-null sub-indices
                SELECT @aqi = MAX(v)
                FROM (VALUES (@aqipm10),(@aqio3),(@aqiso2),(@aqino2),(@aqico),(@aqipm25)) t(v);

                -- InsertAQI (cs:2029) -- guarded by #aqih NOT EXISTS
                INSERT INTO ParameterAverages
                    (StationID, DeviceID, ParameterIDRef, Parametervalue, Interval, CreatedTime, LoggerFlags, TypeID, ParameterID)
                VALUES (@AqiStationID, @DeviceID, @AqiParamRef, @aqi, @I, GETDATE(), 1, 60, @AqiParamId);
                SET @cntAqiHr += 1;

                -- UpdateSubindex (cs:2061) on each pollutant's existing hourly row
                IF @pm10id IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqipm10 WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@pm10id AND Interval=@I AND TypeID=60;
                IF @o3id   IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqio3   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@o3id   AND Interval=@I AND TypeID=60;
                IF @so2id  IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqiso2  WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@so2id  AND Interval=@I AND TypeID=60;
                IF @no2id  IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqino2  WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@no2id  AND Interval=@I AND TypeID=60;
                IF @coid   IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqico   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@coid   AND Interval=@I AND TypeID=60;
                IF @pm25id IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqipm25 WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@pm25id AND Interval=@I AND TypeID=60;

                FETCH NEXT FROM curH INTO @I;
            END
            CLOSE curH; DEALLOCATE curH;
        END
        /*----------------------------------------------------------------------------
          NON-HOURLY AQI BRANCH (e.g. 8-H / 24-H)
          Port of GetRecordCountForEachIntervalAQI1 (cs:215) + GetSubindexAvgValue (cs:246).
          AQI value  = AVG of the hourly (TypeID 60) AQI rows inside the bucket.
          SubIndex   = AVG(SubIndex) of hourly pollutant rows inside the bucket.
          NOTE: source rows are filtered to TypeID=60 (the hourly AQI/pollutant rows).
                The C# omits a TypeID filter; restricting to 60 avoids double-counting
                already-aggregated rows and matches the intended "roll up hourly" behaviour.
        ----------------------------------------------------------------------------*/
        ELSE
        BEGIN
            IF OBJECT_ID('tempdb..#aqibk') IS NOT NULL DROP TABLE #aqibk;
            SELECT DISTINCT DATEADD(MINUTE, DATEDIFF(MINUTE, 0, pa.Interval) / @P * @P, 0) AS Bucket
            INTO #aqibk
            FROM ParameterAverages pa
            WHERE pa.DeviceID = @DeviceID AND pa.StationID = @AqiStationID
              AND pa.ParameterID = @AqiParamId AND pa.TypeID = 60
              AND DATEADD(MINUTE, @P, DATEADD(MINUTE, DATEDIFF(MINUTE, 0, pa.Interval) / @P * @P, 0)) <= GETDATE()
              AND NOT EXISTS (SELECT 1 FROM ParameterAverages b
                              WHERE b.DeviceID = @DeviceID AND b.StationID = @AqiStationID
                                AND b.ParameterID = @AqiParamId
                                AND b.Interval = DATEADD(MINUTE, DATEDIFF(MINUTE, 0, pa.Interval) / @P * @P, 0)
                                AND b.TypeID = @P);

            DECLARE curB CURSOR LOCAL FAST_FORWARD FOR SELECT Bucket FROM #aqibk ORDER BY Bucket;
            OPEN curB; FETCH NEXT FROM curB INTO @B;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @aqiavg = NULL;
                SELECT @aqiavg = AVG(pa.Parametervalue)
                FROM ParameterAverages pa
                WHERE pa.DeviceID = @DeviceID AND pa.StationID = @AqiStationID
                  AND pa.ParameterID = @AqiParamId AND pa.TypeID = 60
                  AND pa.Interval >= @B AND pa.Interval < DATEADD(MINUTE, @P, @B);

                INSERT INTO ParameterAverages
                    (StationID, DeviceID, ParameterIDRef, Parametervalue, Interval, CreatedTime, LoggerFlags, TypeID, ParameterID)
                VALUES (@AqiStationID, @DeviceID, @AqiParamRef, @aqiavg, @B, GETDATE(), 1, @P, @AqiParamId);
                SET @cntAqiNh += 1;

                -- per-pollutant average sub-index over the bucket + that pollutant's param id
                SELECT @aqipm10=NULL,@aqio3=NULL,@aqiso2=NULL,@aqino2=NULL,@aqico=NULL,@aqipm25=NULL,
                       @pm10id=NULL,@o3id=NULL,@so2id=NULL,@no2id=NULL,@coid=NULL,@pm25id=NULL;
                SELECT
                    @aqipm10 = AVG(CASE WHEN d.DriverName=@PM10 THEN pa.SubIndex END),
                    @aqio3   = AVG(CASE WHEN d.DriverName=@O3   THEN pa.SubIndex END),
                    @aqiso2  = AVG(CASE WHEN d.DriverName=@SO2  THEN pa.SubIndex END),
                    @aqino2  = AVG(CASE WHEN d.DriverName=@NO2  THEN pa.SubIndex END),
                    @aqico   = AVG(CASE WHEN d.DriverName=@CO   THEN pa.SubIndex END),
                    @aqipm25 = AVG(CASE WHEN d.DriverName=@PM25 THEN pa.SubIndex END),
                    @pm10id  = MAX(CASE WHEN d.DriverName=@PM10 THEN pa.ParameterID END),
                    @o3id    = MAX(CASE WHEN d.DriverName=@O3   THEN pa.ParameterID END),
                    @so2id   = MAX(CASE WHEN d.DriverName=@SO2  THEN pa.ParameterID END),
                    @no2id   = MAX(CASE WHEN d.DriverName=@NO2  THEN pa.ParameterID END),
                    @coid    = MAX(CASE WHEN d.DriverName=@CO   THEN pa.ParameterID END),
                    @pm25id  = MAX(CASE WHEN d.DriverName=@PM25 THEN pa.ParameterID END)
                FROM ParameterAverages pa
                INNER JOIN DMN_Parameters dp     ON pa.ParameterID = dp.ID
                INNER JOIN MST_Devices_Drivers d ON dp.DriverID    = d.ID
                WHERE pa.DeviceID = @DeviceID AND pa.StationID = @AqiStationID AND pa.TypeID = 60
                  AND d.DriverName IN (@PM10,@O3,@CO,@PM25,@SO2,@NO2) AND dp.shouldUseForAqi = 1
                  AND pa.Interval >= @B AND pa.Interval < DATEADD(MINUTE, @P, @B);

                -- update the larger-interval pollutant rows (TypeID = @P) for this bucket
                IF @pm10id IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqipm10 WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@pm10id AND Interval=@B AND TypeID=@P;
                IF @o3id   IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqio3   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@o3id   AND Interval=@B AND TypeID=@P;
                IF @so2id  IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqiso2  WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@so2id  AND Interval=@B AND TypeID=@P;
                IF @no2id  IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqino2  WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@no2id  AND Interval=@B AND TypeID=@P;
                IF @coid   IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqico   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@coid   AND Interval=@B AND TypeID=@P;
                IF @pm25id IS NOT NULL UPDATE ParameterAverages SET SubIndex=@aqipm25 WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@pm25id AND Interval=@B AND TypeID=@P;

                FETCH NEXT FROM curB INTO @B;
            END
            CLOSE curB; DEALLOCATE curB;
        END

        SET @aqRid += 1;
    END
END
ELSE
    PRINT 'No AQI Index parameter configured for this device - Stage 2 skipped.';

PRINT 'Stage 2 AQI hourly rows inserted: '     + CAST(@cntAqiHr AS VARCHAR(20));
PRINT 'Stage 2 AQI non-hourly rows inserted: ' + CAST(@cntAqiNh AS VARCHAR(20));


/*======================================================================================
  STAGE 3 : MONTH AVERAGES (TypeID 43200)
  Port of InsertDataIntoAvgTableMonth (cs:632). Source = daily rows (TypeID 1440)
  with LoggerFlags = 1 (priorityLoggerflag passed as 1) and flag Type <> 'Validation'.
  UPDATE existing month rows, then INSERT the missing ones. Scoped to @DeviceID.
======================================================================================*/

UPDATE m
SET m.Parametervalue = src.AvgValue,
    m.SubIndex       = src.SubIdx,
    m.CreatedTime    = GETDATE()
FROM ParameterAveragesMonth m
INNER JOIN (
    SELECT sd.StationID, sd.DeviceID, sd.ParameterID,
           CAST(DATEADD(MONTH, DATEDIFF(MONTH, 0, sd.Interval), 0) AS DATETIME) AS MonthStart,
           AVG(sd.SubIndex)       AS SubIdx,
           AVG(sd.Parametervalue) AS AvgValue
    FROM ParameterAverages sd
    INNER JOIN DMN_Flags f ON sd.LoggerFlags = f.ID AND f.Type <> 'Validation'
    WHERE sd.DeviceID = @DeviceID AND f.ID = 1 AND sd.TypeID = 1440
    GROUP BY sd.StationID, sd.DeviceID, sd.ParameterID,
             CAST(DATEADD(MONTH, DATEDIFF(MONTH, 0, sd.Interval), 0) AS DATETIME)
) src
  ON  m.StationID   = src.StationID
  AND m.DeviceID    = src.DeviceID
  AND m.ParameterID = src.ParameterID
  AND m.Interval    = src.MonthStart
  AND m.TypeID      = 43200;
SET @cntMonthUpd = @@ROWCOUNT;

INSERT INTO ParameterAveragesMonth
    (StationID, DeviceID, ParameterID, Parametervalue, SubIndex, Type, Interval, LoggerFlags, TypeID, CreatedTime, ParameterIDRef)
SELECT a.StationID, a.DeviceID, a.ParameterID, a.Parametervalue, a.SubIndex,
       '43200-MO', a.Interval, 1, 43200, GETDATE(), a.ParameterIDRef
FROM (
    SELECT sd.StationID, sd.DeviceID, sd.ParameterID, sd.ParameterIDRef,
           CAST(DATEADD(MONTH, DATEDIFF(MONTH, 0, sd.Interval), 0) AS DATETIME) AS Interval,
           AVG(sd.Parametervalue) AS Parametervalue,
           AVG(sd.SubIndex)       AS SubIndex
    FROM ParameterAverages sd
    INNER JOIN DMN_Flags f ON sd.LoggerFlags = f.ID AND f.Type <> 'Validation'
    WHERE sd.DeviceID = @DeviceID AND f.ID = 1 AND sd.TypeID = 1440
    GROUP BY sd.StationID, sd.DeviceID, sd.ParameterID, sd.ParameterIDRef,
             CAST(DATEADD(MONTH, DATEDIFF(MONTH, 0, sd.Interval), 0) AS DATETIME)
) a
WHERE NOT EXISTS (
    SELECT 1 FROM ParameterAveragesMonth b
    WHERE b.StationID = a.StationID AND b.DeviceID = a.DeviceID
      AND b.ParameterID = a.ParameterID AND b.Interval = a.Interval AND b.TypeID = 43200);
SET @cntMonthIns = @@ROWCOUNT;

PRINT 'Stage 3 month rows updated/inserted: ' + CAST(@cntMonthUpd AS VARCHAR(20)) + ' / ' + CAST(@cntMonthIns AS VARCHAR(20));


/*======================================================================================
  STAGE 4 : YEAR AVERAGES (TypeID 365)
  Port of InsertDataIntoAvgTableYear (cs:770). Source = month rows (TypeID 43200)
  with LoggerFlags = 1 and flag Type <> 'Validation'. UPDATE then INSERT missing.
======================================================================================*/

UPDATE y
SET y.Parametervalue = src.AvgValue,
    y.SubIndex       = src.SubIdx,
    y.CreatedTime    = GETDATE()
FROM ParameterAveragesYear y
INNER JOIN (
    SELECT sd.StationID, sd.DeviceID, sd.ParameterID,
           CAST(DATEADD(YEAR, DATEDIFF(YEAR, 0, sd.Interval), 0) AS DATETIME) AS YearStart,
           AVG(sd.SubIndex)       AS SubIdx,
           AVG(sd.Parametervalue) AS AvgValue
    FROM ParameterAveragesMonth sd
    INNER JOIN DMN_Flags f ON sd.LoggerFlags = f.ID AND f.Type <> 'Validation'
    WHERE sd.DeviceID = @DeviceID AND f.ID = 1 AND sd.TypeID = 43200
    GROUP BY sd.StationID, sd.DeviceID, sd.ParameterID,
             CAST(DATEADD(YEAR, DATEDIFF(YEAR, 0, sd.Interval), 0) AS DATETIME)
) src
  ON  y.StationID   = src.StationID
  AND y.DeviceID    = src.DeviceID
  AND y.ParameterID = src.ParameterID
  AND y.Interval    = src.YearStart
  AND y.TypeID      = 365;
SET @cntYearUpd = @@ROWCOUNT;

INSERT INTO ParameterAveragesYear
    (StationID, DeviceID, ParameterID, Parametervalue, SubIndex, Type, Interval, LoggerFlags, TypeID, CreatedTime, ParameterIDRef)
SELECT a.StationID, a.DeviceID, a.ParameterID, a.Parametervalue, a.SubIndex,
       '365-Y', a.Interval, 1, 365, GETDATE(), a.ParameterIDRef
FROM (
    SELECT sd.StationID, sd.DeviceID, sd.ParameterID, sd.ParameterIDRef,
           CAST(DATEADD(YEAR, DATEDIFF(YEAR, 0, sd.Interval), 0) AS DATETIME) AS Interval,
           AVG(sd.Parametervalue) AS Parametervalue,
           AVG(sd.SubIndex)       AS SubIndex
    FROM ParameterAveragesMonth sd
    INNER JOIN DMN_Flags f ON sd.LoggerFlags = f.ID AND f.Type <> 'Validation'
    WHERE sd.DeviceID = @DeviceID AND f.ID = 1 AND sd.TypeID = 43200
    GROUP BY sd.StationID, sd.DeviceID, sd.ParameterID, sd.ParameterIDRef,
             CAST(DATEADD(YEAR, DATEDIFF(YEAR, 0, sd.Interval), 0) AS DATETIME)
) a
WHERE NOT EXISTS (
    SELECT 1 FROM ParameterAveragesYear b
    WHERE b.StationID = a.StationID AND b.DeviceID = a.DeviceID
      AND b.ParameterID = a.ParameterID AND b.Interval = a.Interval AND b.TypeID = 365);
SET @cntYearIns = @@ROWCOUNT;

PRINT 'Stage 4 year rows updated/inserted: ' + CAST(@cntYearUpd AS VARCHAR(20)) + ' / ' + CAST(@cntYearIns AS VARCHAR(20));


-----------------------------------------------------------------------------------------
-- COMMIT / ROLLBACK
-----------------------------------------------------------------------------------------
IF @TestMode = 1
BEGIN
    ROLLBACK TRANSACTION;
    PRINT '*** TEST MODE = 1 : all changes ROLLED BACK. Set @TestMode = 0 to COMMIT. ***';
END
ELSE
BEGIN
    COMMIT TRANSACTION;
    PRINT '*** COMMITTED. ***';
END

END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    PRINT 'ERROR: ' + ERROR_MESSAGE() + ' (line ' + CAST(ERROR_LINE() AS VARCHAR(10)) + ')';
    THROW;
END CATCH

-- cleanup temp tables
IF OBJECT_ID('tempdb..#tok')    IS NOT NULL DROP TABLE #tok;
IF OBJECT_ID('tempdb..#aqitok') IS NOT NULL DROP TABLE #aqitok;
IF OBJECT_ID('tempdb..#aqih')   IS NOT NULL DROP TABLE #aqih;
IF OBJECT_ID('tempdb..#aqibk')  IS NOT NULL DROP TABLE #aqibk;

/*--------------------------------------------------------------------------------------
  Optional: drop the helper functions if you don't want them left in the database.
  (Leave them if you intend to re-run the backfill for other devices.)
--------------------------------------------------------------------------------------*/
-- DROP FUNCTION IF EXISTS dbo.fn_RollingAvg;
-- DROP FUNCTION IF EXISTS dbo.fn_PollutantAQI;
GO
