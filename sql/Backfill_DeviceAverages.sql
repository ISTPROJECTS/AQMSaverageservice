/*======================================================================================
  Backfill_DeviceAverages.sql
  --------------------------------------------------------------------------------------
  Backfills missing averages / AQI / sub-indices / monthly / yearly rows for ONE device
  whose backlog 5-minute raw data was inserted into ParameterReadings AFTER the device
  already started sending fresh data.

  Why a script (and not just the service):
    The live service (AQMSDataUpdateLibrary.AvgDataCalculationService) is INCREMENTAL.
    For each parameter/interval it reads the newest Interval already in ParameterAverages
    (GetLatestInterval) and only looks at readings with CreatedTime > lastInterval. Once a
    device is live again, lastInterval is recent, so the older backlog buckets are skipped
    forever. This script instead backfills by "missing interval" (NOT EXISTS) so old buckets
    are filled regardless of newer data, while reproducing the EXACT calculation logic of
    the service.

  Logic mirrored from:
    AverageCalculationService.cs  (averaging, sub-index, monthly/yearly)
    AQICalculator.cs              (AQI breakpoints, rolling averages, unit conversion)
    ParameterRepository.cs / AQIConstants.cs (bucketing, flag selection, constants)
    BulkDataWriter.cs             (insert columns + NOT EXISTS dedupe pattern)

  Behavior decisions (confirmed):
    - Plain ad-hoc script (set @DeviceID below). No stored procedures created.
    - One combined script; stages run in dependency order 1 -> 2 -> 3 -> 4 -> 5.
    - Complete periods only (skips the in-progress hour / month / year), matching the service.

  SAFETY: runs inside a transaction. Set @CommitChanges = 1 to persist. Leave 0 for a
          dry-run (everything is rolled back so you can inspect row counts first).
======================================================================================*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

------------------------------------------------------------------------------------------
-- >>>>>>>>>>>>>>>>>>>>>>>>  SET THESE TWO VALUES  <<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<
------------------------------------------------------------------------------------------
DECLARE @DeviceID      INT = 0;     -- <<< target device id
DECLARE @CommitChanges BIT = 0;     -- 0 = dry-run (rollback), 1 = persist
------------------------------------------------------------------------------------------

DECLARE @Now DATETIME = GETDATE();

-- Constants (AQIConstants.cs)
DECLARE @OneHourTypeID INT = 60;
DECLARE @MonthTypeID   INT = 43200;
DECLARE @YearTypeID    INT = 365;

-- Config (App.config appSettings)
DECLARE @WindDirection NVARCHAR(100) = N'WD,WIND DIRECTION';  -- upper-cased once here
DECLARE @Rain          NVARCHAR(100) = N'RAIN';

/*--------------------------------------------------------------------------------------
  Stage 0 : AQI breakpoint table (transcribed verbatim from AQICalculator.cs:348-510)

  AQI = (AqiHigh-AqiLow)/(CHigh-CLow) * (value-CLow) + AqiLow      [LinearInterpolation]
        capped to CapTo (= 1500) on the top open ranges            [Math.Min(...,1500)]

  Matching rule replicates the C# if/else chain (first branch wins):
     SELECT TOP 1 ... WHERE PollutantType=@t AND @v >= Lo AND @v <= ISNULL(Hi,1e18)
     ORDER BY Hi ASC
  Values below a pollutant's lowest Lo match no row -> NULL (same as C# returning null).
--------------------------------------------------------------------------------------*/
IF OBJECT_ID('tempdb..#Breakpoints') IS NOT NULL DROP TABLE #Breakpoints;
CREATE TABLE #Breakpoints
(
    PollutantType VARCHAR(20) NOT NULL,
    Lo    FLOAT NOT NULL,   -- value-range lower bound (inclusive for TOP-1/ORDER BY Hi matching)
    Hi    FLOAT NULL,       -- value-range upper bound; NULL = open-ended top range
    CLow  FLOAT NOT NULL,
    CHigh FLOAT NOT NULL,
    AqiLow  FLOAT NOT NULL,
    AqiHigh FLOAT NOT NULL,
    CapTo FLOAT NULL         -- NULL = no cap; 1500 on top ranges that use Math.Min
);

INSERT INTO #Breakpoints (PollutantType, Lo, Hi, CLow, CHigh, AqiLow, AqiHigh, CapTo) VALUES
-- 8_O3  (CalculateO3_8HourAQI)
 ('8_O3',     0,    100.5,    0,   100,    0,   50, NULL),
 ('8_O3',   100.5,  120.5,  101,   120,   51,  100, NULL),
 ('8_O3',   120.5,  167.5,  121,   167,  101,  150, NULL),
 ('8_O3',   167.5,  206.5,  168,   206,  151,  200, NULL),
 ('8_O3',   206.5,  NULL,   207,   392,  201,  300, 1500),
-- 1_O3  (CalculateO3_1HourAQI)
 ('1_O3',   200,    322.5,  200,   322,  101,  150, NULL),
 ('1_O3',   322.5,  400.5,  323,   400,  151,  200, NULL),
 ('1_O3',   400.5,  792.5,  401,   792,  201,  300, NULL),
 ('1_O3',   792.5,  NULL,   793,  1184,  301,  500, 1500),
-- 8_CO  (CalculateCO_8HourAQI)
 ('8_CO',     0,     5.4,    0,    5.4,    0,   50, NULL),
 ('8_CO',     5.4,  10.4,    5.5,  10.4,   51,  100, NULL),
 ('8_CO',    10.4,  14.4,   10.5,  14.4,  101,  150, NULL),
 ('8_CO',    14.4,  17.9,   14.5,  17.9,  151,  200, NULL),
 ('8_CO',    17.9,  35.4,   18.0,  35.4,  201,  300, NULL),
 ('8_CO',    35.4,  NULL,   35.5,  58.4,  301,  500, 1500),
-- 1_SO2 (CalculateSO2_1HourAQI)
 ('1_SO2',    0,    92.5,    0,    92,    0,   50, NULL),
 ('1_SO2',   92.5, 350.5,   93,   350,   51,  100, NULL),
 ('1_SO2',  350.5, 485.5,  351,   485,  101,  150, NULL),
 ('1_SO2',  485.5,  NULL,  486,   797,  151,  200, NULL),
-- 24_SO2 (CalculateSO2_24HourAQI)
 ('24_SO2', 797,  1583.5,  798,  1583,  201,  300, NULL),
 ('24_SO2',1583.5, NULL,  1584,  2631,  301,  500, 1500),
-- 1_NO2 (CalculateNO2_1HourAQI)
 ('1_NO2',    0,   100.5,    0,   100,    0,   50, NULL),
 ('1_NO2',  100.5, 400.5,  101,   400,   51,  100, NULL),
 ('1_NO2',  400.5, 677.5,  401,   677,  101,  150, NULL),
 ('1_NO2',  677.5,1221.5,  678,  1221,  151,  200, NULL),
 ('1_NO2', 1221.5,2349.5, 1222,  2349,  201,  300, NULL),
 ('1_NO2', 2349.5, NULL,  2350,  3853,  301,  500, 1500),
-- 24_PM10 (CalculatePM10_24HourAQI)
 ('24_PM10',  0,    75.5,    0,    75,    0,   50, NULL),
 ('24_PM10', 75.5, 150.5,   76,   150,   51,  100, NULL),
 ('24_PM10',150.5, 250.5,  151,   250,  101,  150, NULL),
 ('24_PM10',250.5, 350.5,  251,   350,  151,  200, NULL),
 ('24_PM10',350.5, 420.5,  351,   420,  201,  300, NULL),
 ('24_PM10',420.5,  NULL,  421,   600,  301,  500, 1500),
-- 24_PM2.5 (CalculatePM25_24HourAQI)
 ('24_PM2.5', 0,    50.4,    0,   50.4,   0,   50, NULL),
 ('24_PM2.5',50.4,  60.4,   50.5, 60.4,   51,  100, NULL),
 ('24_PM2.5',60.4,  75.4,   60.5, 75.4,  101,  150, NULL),
 ('24_PM2.5',75.4, 150.4,   75.5,150.4,  151,  200, NULL),
 ('24_PM2.5',150.4,250.4,  150.5,250.4,  201,  300, NULL),
 ('24_PM2.5',250.4, NULL,  250.5,500.4,  301,  500, 1500);

-- Working temp tables reused by Stage 2
IF OBJECT_ID('tempdb..#pn')   IS NOT NULL DROP TABLE #pn;
IF OBJECT_ID('tempdb..#hist') IS NOT NULL DROP TABLE #hist;
CREATE TABLE #pn   (DriverName NVARCHAR(100), ParameterID INT, Val FLOAT);          -- current converted pollutant values @interval
CREATE TABLE #hist (DriverName NVARCHAR(100), conv FLOAT, Interval DATETIME);       -- 24h converted history for rolling windows

BEGIN TRY
BEGIN TRAN;

/*======================================================================================
  STAGE 1 : Raw parameter averages -> ParameterAverages
  Mirrors ProcessParameterAveragesAsync / ProcessIntervalAveragesAsync.
======================================================================================*/
PRINT '--- Stage 1: raw parameter averages ---';

DECLARE @s INT, @pid INT, @pref INT, @freq INT, @drv NVARCHAR(100),
        @ivalTxt NVARCHAR(20), @code CHAR(1), @ival INT,
        @itype VARCHAR(6), @typeId INT, @expected INT,
        @isWind BIT, @isRain BIT, @typeStr VARCHAR(20);

DECLARE @sql NVARCHAR(MAX);
DECLARE @s1tpl NVARCHAR(MAX) = N'
;WITH r AS (
    SELECT pr.Parametervalue AS v, pr.LoggerFlags AS lf, f.[Type] AS ftype, f.Priority AS prio,
           DATEADD({T}, DATEDIFF({T}, 0, pr.CreatedTime) / @ival * @ival, 0) AS bkt
    FROM ParameterReadings pr WITH (NOLOCK)
    LEFT JOIN DMN_Flags f WITH (NOLOCK) ON pr.LoggerFlags = f.ID
    WHERE pr.StationID = @s AND pr.DeviceID = @DeviceID AND pr.ParameterID = @pid
),
buckets AS (
    SELECT DISTINCT bkt FROM r
    WHERE @Now >= DATEADD({T}, @ival, bkt)               -- complete window only
),
flagpick AS (                                            -- GetHighPriorityFlag
    SELECT bkt, lf FROM (
        SELECT r.bkt, r.lf,
               ROW_NUMBER() OVER (PARTITION BY r.bkt ORDER BY COUNT(*) DESC, MIN(r.prio) ASC) rn
        FROM r WHERE r.lf IS NOT NULL
        GROUP BY r.bkt, r.lf
    ) z WHERE rn = 1
),
validcnt AS (                                            -- valid (non-Validation) reading count
    SELECT bkt, COUNT(*) AS vc FROM r WHERE r.ftype <> ''Validation'' GROUP BY bkt
)
INSERT INTO ParameterAverages
    (StationID, DeviceID, ParameterID, ParameterIDRef, Parametervalue, SubIndex, [Type], [Interval], LoggerFlags, TypeID, CreatedTime)
SELECT @s, @DeviceID, @pid, @pref,
       CASE WHEN (ISNULL(vc.vc,0) * 100.0 / @expected) >= 75 THEN va.aggval ELSE NULL END,
       NULL,
       @typeStr,
       b.bkt,
       NULLIF(fp.lf, 0),
       @typeId,
       @Now
FROM buckets b
LEFT JOIN flagpick fp ON fp.bkt = b.bkt
LEFT JOIN validcnt vc ON vc.bkt = b.bkt
CROSS APPLY (
    SELECT CASE
        WHEN @isWind = 1 THEN
            (SELECT CASE WHEN DEGREES(ATN2(AVG(SIN(RADIANS(rr.v))), AVG(COS(RADIANS(rr.v))))) < 0
                         THEN 360 + DEGREES(ATN2(AVG(SIN(RADIANS(rr.v))), AVG(COS(RADIANS(rr.v)))))
                         ELSE DEGREES(ATN2(AVG(SIN(RADIANS(rr.v))), AVG(COS(RADIANS(rr.v))))) END
             FROM r rr WHERE rr.bkt = b.bkt AND rr.ftype <> ''Validation'' AND (fp.lf IS NULL OR rr.lf = fp.lf))
        WHEN @isRain = 1 THEN
            (SELECT SUM(rr.v) FROM r rr WHERE rr.bkt = b.bkt AND rr.ftype <> ''Validation'' AND (fp.lf IS NULL OR rr.lf = fp.lf))
        ELSE
            (SELECT AVG(rr.v) FROM r rr WHERE rr.bkt = b.bkt AND rr.ftype <> ''Validation'' AND (fp.lf IS NULL OR rr.lf = fp.lf))
    END AS aggval
) va
WHERE NOT EXISTS (
    SELECT 1 FROM ParameterAverages t WITH (NOLOCK)
    WHERE t.StationID = @s AND t.DeviceID = @DeviceID AND t.ParameterID = @pid
      AND t.[Interval] = b.bkt AND t.TypeID = @typeId
);';

DECLARE c1 CURSOR LOCAL FAST_FORWARD FOR
    SELECT p.ID, p.StationID, p.ParameterID, ISNULL(p.DataSyncFrequency, 1), d.DriverName,
           LTRIM(RTRIM(LEFT(tok.value, CHARINDEX('-', tok.value) - 1)))      AS IVal,
           LTRIM(RTRIM(SUBSTRING(tok.value, CHARINDEX('-', tok.value) + 1, 10))) AS ICode
    FROM DMN_Parameters p WITH (NOLOCK)
    JOIN MST_Devices_Drivers d WITH (NOLOCK) ON p.DriverID = d.ID
    CROSS APPLY STRING_SPLIT(p.ServerAvgInterval, ',') tok
    WHERE p.DeviceID = @DeviceID
      AND p.ServerAvgInterval IS NOT NULL
      AND d.DriverName <> 'AQI Index'
      AND CHARINDEX('-', tok.value) > 0;

OPEN c1;
FETCH NEXT FROM c1 INTO @pid, @s, @pref, @freq, @drv, @ivalTxt, @code;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @ival   = TRY_CAST(@ivalTxt AS INT);
    SET @code   = UPPER(@code);
    IF @ival IS NOT NULL AND @code IN ('M','H')
    BEGIN
        SET @itype  = CASE WHEN @code = 'M' THEN 'MINUTE' ELSE 'HOUR' END;
        SET @typeId = CASE WHEN @code = 'M' THEN @ival ELSE @ival * 60 END;  -- = total minutes width

        IF @typeId >= @freq                                   -- skip interval < data sync freq
        BEGIN
            SET @expected = @typeId / @freq;                  -- expected reading count (int division, as in C#)
            IF @expected < 1 SET @expected = 1;
            SET @isWind  = CASE WHEN CHARINDEX(UPPER(@drv), @WindDirection) > 0 THEN 1 ELSE 0 END;
            SET @isRain  = CASE WHEN CHARINDEX(UPPER(@drv), @Rain) > 0 THEN 1 ELSE 0 END;
            SET @typeStr = CAST(@ival AS VARCHAR(10)) + @code;

            SET @sql = REPLACE(@s1tpl, '{T}', @itype);
            EXEC sp_executesql @sql,
                 N'@s INT, @DeviceID INT, @pid INT, @pref INT, @ival INT, @typeId INT,
                   @expected INT, @isWind BIT, @isRain BIT, @typeStr VARCHAR(20), @Now DATETIME',
                 @s=@s, @DeviceID=@DeviceID, @pid=@pid, @pref=@pref, @ival=@ival, @typeId=@typeId,
                 @expected=@expected, @isWind=@isWind, @isRain=@isRain, @typeStr=@typeStr, @Now=@Now;
        END
    END
    FETCH NEXT FROM c1 INTO @pid, @s, @pref, @freq, @drv, @ivalTxt, @code;
END
CLOSE c1; DEALLOCATE c1;
PRINT '    Stage 1 done.';

/*======================================================================================
  STAGE 2 : 1-hour AQI (TypeID = 60) + pollutant sub-indices
  Mirrors CalculateAQIForIntervalsAsync + AQICalculator.CalculateAQIAsync + UpdateSubIndices.
======================================================================================*/
PRINT '--- Stage 2: hourly AQI + sub-indices ---';

DECLARE @aqiId INT, @aqiRef INT, @aqiIntervals NVARCHAR(200), @doHourly BIT;
DECLARE @interval DATETIME;
DECLARE @cnt INT, @avg FLOAT;
DECLARE @id_pm25 INT, @id_pm10 INT, @id_o3 INT, @id_so2 INT, @id_no2 INT, @id_co INT;
DECLARE @v_pm25 FLOAT, @v_pm10 FLOAT, @v_o3 FLOAT, @v_so2 FLOAT, @v_no2 FLOAT, @v_co FLOAT;
DECLARE @si_pm25 FLOAT, @si_pm10 FLOAT, @si_o3 FLOAT, @si_so2 FLOAT, @si_no2 FLOAT, @si_co FLOAT;
DECLARE @o3_8aqi FLOAT, @o3_8raw FLOAT, @o3_8rawcnt INT, @o3_1aqi FLOAT, @aqi FLOAT;

DECLARE cAqi CURSOR LOCAL FAST_FORWARD FOR
    SELECT p.ID, p.ParameterID, p.StationID, p.ServerAvgInterval
    FROM DMN_Parameters p WITH (NOLOCK)
    JOIN MST_Devices_Drivers d WITH (NOLOCK) ON p.DriverID = d.ID
    WHERE p.DeviceID = @DeviceID AND d.DriverName = 'AQI Index' AND p.ServerAvgInterval IS NOT NULL;

OPEN cAqi;
FETCH NEXT FROM cAqi INTO @aqiId, @aqiRef, @s, @aqiIntervals;
WHILE @@FETCH_STATUS = 0
BEGIN
    -- run hourly AQI if any interval token resolves to TypeID = 60 (e.g. '1-H' or '60-M')
    SET @doHourly = CASE WHEN EXISTS (
        SELECT 1 FROM STRING_SPLIT(@aqiIntervals, ',') t
        WHERE CHARINDEX('-', t.value) > 0
          AND CASE WHEN UPPER(LTRIM(RTRIM(SUBSTRING(t.value, CHARINDEX('-', t.value)+1, 10)))) = 'M'
                   THEN TRY_CAST(LTRIM(RTRIM(LEFT(t.value, CHARINDEX('-', t.value)-1))) AS INT)
                   ELSE TRY_CAST(LTRIM(RTRIM(LEFT(t.value, CHARINDEX('-', t.value)-1))) AS INT) * 60 END = 60
    ) THEN 1 ELSE 0 END;

    IF @doHourly = 1
    BEGIN
        DECLARE cHr CURSOR LOCAL FAST_FORWARD FOR
            SELECT DISTINCT DATEADD(HOUR, DATEDIFF(HOUR, 0, pa.Interval), 0) AS hr
            FROM ParameterAverages pa WITH (NOLOCK)
            JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
            JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
            WHERE pa.StationID = @s AND pa.DeviceID = @DeviceID AND pa.TypeID = @OneHourTypeID
              AND dp.shouldUseForAqi = 1
              AND d.DriverName IN (N'PM2.5', N'PM10', N'O₃', N'SO₂', N'NO₂', N'CO')
              AND @Now >= DATEADD(HOUR, 1, DATEADD(HOUR, DATEDIFF(HOUR, 0, pa.Interval), 0))
              AND NOT EXISTS (
                  SELECT 1 FROM ParameterAverages a WITH (NOLOCK)
                  WHERE a.StationID = @s AND a.DeviceID = @DeviceID AND a.ParameterID = @aqiId
                    AND a.[Interval] = DATEADD(HOUR, DATEDIFF(HOUR, 0, pa.Interval), 0)
                    AND a.TypeID = @OneHourTypeID)
            ORDER BY hr;

        OPEN cHr;
        FETCH NEXT FROM cHr INTO @interval;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            -- current converted pollutant values at this hour (shouldUseForAqi = 1)
            DELETE FROM #pn;
            INSERT INTO #pn (DriverName, ParameterID, Val)
            SELECT d.DriverName, pa.ParameterID,
                   pa.Parametervalue * COALESCE(CASE WHEN u.UnitName <> pc.SecondaryUnit
                                                     THEN TRY_CAST(pc.ConversionFactor AS FLOAT) ELSE 1 END, 1)
            FROM ParameterAverages pa WITH (NOLOCK)
            JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
            JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
            JOIN ReportedUnits u WITH (NOLOCK) ON dp.UnitID = u.ID
            LEFT JOIN Parameter_Conversion pc WITH (NOLOCK) ON d.DriverName = pc.Parameter
            WHERE pa.StationID = @s AND pa.DeviceID = @DeviceID AND pa.Interval = @interval
              AND pa.TypeID = @OneHourTypeID AND dp.shouldUseForAqi = 1
              AND d.DriverName IN (N'PM2.5', N'PM10', N'O₃', N'SO₂', N'NO₂', N'CO');

            -- 24h converted history (no shouldUseForAqi filter; matches GetHistoricalReadings)
            DELETE FROM #hist;
            INSERT INTO #hist (DriverName, conv, Interval)
            SELECT d.DriverName,
                   pa.Parametervalue * COALESCE(CASE WHEN u.UnitName <> pc.SecondaryUnit
                                                     THEN TRY_CAST(pc.ConversionFactor AS FLOAT) ELSE 1 END, 1),
                   pa.Interval
            FROM ParameterAverages pa WITH (NOLOCK)
            JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
            JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
            JOIN ReportedUnits u WITH (NOLOCK) ON dp.UnitID = u.ID
            LEFT JOIN Parameter_Conversion pc WITH (NOLOCK) ON d.DriverName = pc.Parameter
            WHERE pa.StationID = @s AND pa.DeviceID = @DeviceID AND pa.TypeID = @OneHourTypeID
              AND d.DriverName IN (N'PM2.5', N'PM10', N'O₃', N'SO₂', N'NO₂', N'CO')
              AND pa.Interval > DATEADD(HOUR, -24, @interval) AND pa.Interval <= @interval
              AND pa.Parametervalue IS NOT NULL;

            SELECT @id_pm25 = ParameterID, @v_pm25 = Val FROM #pn WHERE DriverName = N'PM2.5';
            SELECT @id_pm10 = ParameterID, @v_pm10 = Val FROM #pn WHERE DriverName = N'PM10';
            SELECT @id_o3   = ParameterID, @v_o3   = Val FROM #pn WHERE DriverName = N'O₃';
            SELECT @id_so2  = ParameterID, @v_so2  = Val FROM #pn WHERE DriverName = N'SO₂';
            SELECT @id_no2  = ParameterID, @v_no2  = Val FROM #pn WHERE DriverName = N'NO₂';
            SELECT @id_co   = ParameterID, @v_co   = Val FROM #pn WHERE DriverName = N'CO';

            SET @si_pm25 = NULL; SET @si_pm10 = NULL; SET @si_o3 = NULL;
            SET @si_so2  = NULL; SET @si_no2  = NULL; SET @si_co = NULL;

            -- PM2.5 : 24h rolling (>=18 points), avg of values>0 -> 24_PM2.5
            SELECT @cnt = COUNT(*), @avg = AVG(CASE WHEN conv > 0 THEN conv END)
            FROM #hist WHERE DriverName = N'PM2.5';
            IF @cnt >= 18 AND @avg IS NOT NULL
                SET @si_pm25 = (SELECT TOP 1 CASE WHEN bp.CapTo IS NOT NULL
                        AND ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) > bp.CapTo
                        THEN bp.CapTo ELSE ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) END
                    FROM #Breakpoints bp WHERE bp.PollutantType = '24_PM2.5'
                      AND @avg >= bp.Lo AND @avg <= ISNULL(bp.Hi, 1e18) ORDER BY bp.Hi);

            -- PM10 : 24h rolling (>=18) -> 24_PM10
            SELECT @cnt = COUNT(*), @avg = AVG(CASE WHEN conv > 0 THEN conv END)
            FROM #hist WHERE DriverName = N'PM10';
            IF @cnt >= 18 AND @avg IS NOT NULL
                SET @si_pm10 = (SELECT TOP 1 CASE WHEN bp.CapTo IS NOT NULL
                        AND ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) > bp.CapTo
                        THEN bp.CapTo ELSE ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) END
                    FROM #Breakpoints bp WHERE bp.PollutantType = '24_PM10'
                      AND @avg >= bp.Lo AND @avg <= ISNULL(bp.Hi, 1e18) ORDER BY bp.Hi);

            -- CO : 8h rolling (>=6) -> 8_CO
            SELECT @cnt = COUNT(*), @avg = AVG(CASE WHEN conv > 0 THEN conv END)
            FROM #hist WHERE DriverName = N'CO' AND Interval > DATEADD(HOUR, -8, @interval);
            IF @cnt >= 6 AND @avg IS NOT NULL
                SET @si_co = (SELECT TOP 1 CASE WHEN bp.CapTo IS NOT NULL
                        AND ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) > bp.CapTo
                        THEN bp.CapTo ELSE ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) END
                    FROM #Breakpoints bp WHERE bp.PollutantType = '8_CO'
                      AND @avg >= bp.Lo AND @avg <= ISNULL(bp.Hi, 1e18) ORDER BY bp.Hi);

            -- O3 : special case (AQICalculator.CalculateO3AQIAsync)
            IF @v_o3 IS NOT NULL
            BEGIN
                -- 8h rolling subindex (>=6) -> 8_O3
                SELECT @cnt = COUNT(*), @avg = AVG(CASE WHEN conv > 0 THEN conv END)
                FROM #hist WHERE DriverName = N'O₃' AND Interval > DATEADD(HOUR, -8, @interval);
                SET @o3_8aqi = CASE WHEN @cnt >= 6 AND @avg IS NOT NULL THEN
                        (SELECT TOP 1 CASE WHEN bp.CapTo IS NOT NULL
                            AND ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) > bp.CapTo
                            THEN bp.CapTo ELSE ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) END
                         FROM #Breakpoints bp WHERE bp.PollutantType = '8_O3'
                           AND @avg >= bp.Lo AND @avg <= ISNULL(bp.Hi, 1e18) ORDER BY bp.Hi)
                    END;
                -- raw 8h average of all readings (no >0 filter, no count gate)
                SELECT @o3_8rawcnt = COUNT(*), @o3_8raw = AVG(conv)
                FROM #hist WHERE DriverName = N'O₃' AND Interval > DATEADD(HOUR, -8, @interval);
                -- 1h subindex of current value -> 1_O3
                SET @o3_1aqi = (SELECT TOP 1 CASE WHEN bp.CapTo IS NOT NULL
                        AND ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@v_o3-bp.CLow)+bp.AqiLow) > bp.CapTo
                        THEN bp.CapTo ELSE ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@v_o3-bp.CLow)+bp.AqiLow) END
                    FROM #Breakpoints bp WHERE bp.PollutantType = '1_O3'
                      AND @v_o3 >= bp.Lo AND @v_o3 <= ISNULL(bp.Hi, 1e18) ORDER BY bp.Hi);

                IF @v_o3 <= 200
                    SET @si_o3 = @o3_8aqi;
                ELSE IF @o3_8rawcnt > 0 AND @o3_8raw <= 392
                    SET @si_o3 = CASE WHEN ISNULL(@o3_8aqi,0) > ISNULL(@o3_1aqi,0) THEN ISNULL(@o3_8aqi,0) ELSE ISNULL(@o3_1aqi,0) END;
                ELSE
                    SET @si_o3 = @o3_1aqi;
            END

            -- SO2 : special case
            IF @v_so2 IS NOT NULL
            BEGIN
                IF @v_so2 <= 797
                    SET @si_so2 = (SELECT TOP 1 CASE WHEN bp.CapTo IS NOT NULL
                            AND ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@v_so2-bp.CLow)+bp.AqiLow) > bp.CapTo
                            THEN bp.CapTo ELSE ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@v_so2-bp.CLow)+bp.AqiLow) END
                        FROM #Breakpoints bp WHERE bp.PollutantType = '1_SO2'
                          AND @v_so2 >= bp.Lo AND @v_so2 <= ISNULL(bp.Hi, 1e18) ORDER BY bp.Hi);
                ELSE
                BEGIN
                    SELECT @cnt = COUNT(*), @avg = AVG(CASE WHEN conv > 0 THEN conv END)
                    FROM #hist WHERE DriverName = N'SO₂';
                    IF @cnt >= 18 AND @avg IS NOT NULL
                        SET @si_so2 = (SELECT TOP 1 CASE WHEN bp.CapTo IS NOT NULL
                                AND ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) > bp.CapTo
                                THEN bp.CapTo ELSE ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@avg-bp.CLow)+bp.AqiLow) END
                            FROM #Breakpoints bp WHERE bp.PollutantType = '24_SO2'
                              AND @avg >= bp.Lo AND @avg <= ISNULL(bp.Hi, 1e18) ORDER BY bp.Hi);
                END
            END

            -- NO2 : 1h of current value -> 1_NO2
            IF @v_no2 IS NOT NULL
                SET @si_no2 = (SELECT TOP 1 CASE WHEN bp.CapTo IS NOT NULL
                        AND ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@v_no2-bp.CLow)+bp.AqiLow) > bp.CapTo
                        THEN bp.CapTo ELSE ((bp.AqiHigh-bp.AqiLow)/(bp.CHigh-bp.CLow)*(@v_no2-bp.CLow)+bp.AqiLow) END
                    FROM #Breakpoints bp WHERE bp.PollutantType = '1_NO2'
                      AND @v_no2 >= bp.Lo AND @v_no2 <= ISNULL(bp.Hi, 1e18) ORDER BY bp.Hi);

            -- AQI = max of available sub-indices
            SET @aqi = (SELECT MAX(v) FROM (VALUES (@si_pm25),(@si_pm10),(@si_o3),(@si_so2),(@si_no2),(@si_co)) x(v));

            -- insert AQI row (LoggerFlags = 1, Type = NULL)
            IF NOT EXISTS (SELECT 1 FROM ParameterAverages
                           WHERE StationID = @s AND DeviceID = @DeviceID AND ParameterID = @aqiId
                             AND [Interval] = @interval AND TypeID = @OneHourTypeID)
                INSERT INTO ParameterAverages
                    (StationID, DeviceID, ParameterID, ParameterIDRef, Parametervalue, SubIndex, [Type], [Interval], LoggerFlags, TypeID, CreatedTime)
                VALUES (@s, @DeviceID, @aqiId, @aqiRef, @aqi, NULL, NULL, @interval, 1, @OneHourTypeID, @Now);

            -- back-fill sub-index onto each pollutant's 1H row (only when computed)
            IF @si_pm25 IS NOT NULL AND @id_pm25 IS NOT NULL
                UPDATE ParameterAverages SET SubIndex = @si_pm25
                WHERE StationID=@s AND DeviceID=@DeviceID AND ParameterID=@id_pm25 AND [Interval]=@interval AND TypeID=@OneHourTypeID;
            IF @si_pm10 IS NOT NULL AND @id_pm10 IS NOT NULL
                UPDATE ParameterAverages SET SubIndex = @si_pm10
                WHERE StationID=@s AND DeviceID=@DeviceID AND ParameterID=@id_pm10 AND [Interval]=@interval AND TypeID=@OneHourTypeID;
            IF @si_o3 IS NOT NULL AND @id_o3 IS NOT NULL
                UPDATE ParameterAverages SET SubIndex = @si_o3
                WHERE StationID=@s AND DeviceID=@DeviceID AND ParameterID=@id_o3 AND [Interval]=@interval AND TypeID=@OneHourTypeID;
            IF @si_so2 IS NOT NULL AND @id_so2 IS NOT NULL
                UPDATE ParameterAverages SET SubIndex = @si_so2
                WHERE StationID=@s AND DeviceID=@DeviceID AND ParameterID=@id_so2 AND [Interval]=@interval AND TypeID=@OneHourTypeID;
            IF @si_no2 IS NOT NULL AND @id_no2 IS NOT NULL
                UPDATE ParameterAverages SET SubIndex = @si_no2
                WHERE StationID=@s AND DeviceID=@DeviceID AND ParameterID=@id_no2 AND [Interval]=@interval AND TypeID=@OneHourTypeID;
            IF @si_co IS NOT NULL AND @id_co IS NOT NULL
                UPDATE ParameterAverages SET SubIndex = @si_co
                WHERE StationID=@s AND DeviceID=@DeviceID AND ParameterID=@id_co AND [Interval]=@interval AND TypeID=@OneHourTypeID;

            FETCH NEXT FROM cHr INTO @interval;
        END
        CLOSE cHr; DEALLOCATE cHr;
    END

    FETCH NEXT FROM cAqi INTO @aqiId, @aqiRef, @s, @aqiIntervals;
END
CLOSE cAqi; DEALLOCATE cAqi;
PRINT '    Stage 2 done.';

/*======================================================================================
  STAGE 3 : Multi-hour AQI averaging (TypeID <> 60, e.g. 8-H / 24-H)
  Mirrors AverageAQIFromHourlyDataAsync.
======================================================================================*/
PRINT '--- Stage 3: multi-hour AQI averaging ---';

DECLARE @s3aqi NVARCHAR(MAX) = N'
;WITH src AS (
    SELECT DATEADD({T}, DATEDIFF({T}, 0, pa.Interval) / @ival * @ival, 0) AS bkt, pa.Parametervalue AS v
    FROM ParameterAverages pa WITH (NOLOCK)
    JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
    JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
    WHERE pa.StationID = @s AND pa.DeviceID = @DeviceID AND pa.TypeID = 60 AND d.DriverName = N''AQI Index''
),
agg AS (SELECT bkt, AVG(v) AS av FROM src GROUP BY bkt)
INSERT INTO ParameterAverages
    (StationID, DeviceID, ParameterID, ParameterIDRef, Parametervalue, SubIndex, [Type], [Interval], LoggerFlags, TypeID, CreatedTime)
SELECT @s, @DeviceID, @aqiId, @aqiRef, agg.av, NULL, NULL, agg.bkt, 1, @typeId, @Now
FROM agg
WHERE agg.av IS NOT NULL
  AND @Now >= DATEADD({T}, @ival, agg.bkt)
  AND NOT EXISTS (SELECT 1 FROM ParameterAverages t WITH (NOLOCK)
                  WHERE t.StationID=@s AND t.DeviceID=@DeviceID AND t.ParameterID=@aqiId
                    AND t.[Interval]=agg.bkt AND t.TypeID=@typeId);';

DECLARE @s3sub NVARCHAR(MAX) = N'
UPDATE tgt SET SubIndex = s.asi
FROM ParameterAverages tgt
JOIN (
    SELECT pa.ParameterID AS pid,
           DATEADD({T}, DATEDIFF({T}, 0, pa.Interval) / @ival * @ival, 0) AS bkt,
           AVG(pa.SubIndex) AS asi
    FROM ParameterAverages pa WITH (NOLOCK)
    JOIN DMN_Parameters dp WITH (NOLOCK) ON pa.ParameterID = dp.ID
    JOIN MST_Devices_Drivers d WITH (NOLOCK) ON dp.DriverID = d.ID
    WHERE pa.StationID = @s AND pa.DeviceID = @DeviceID AND pa.TypeID = 60 AND dp.shouldUseForAqi = 1
      AND d.DriverName IN (N''PM2.5'', N''PM10'', N''O₃'', N''SO₂'', N''NO₂'', N''CO'') AND pa.SubIndex IS NOT NULL
    GROUP BY pa.ParameterID, DATEADD({T}, DATEDIFF({T}, 0, pa.Interval) / @ival * @ival, 0)
) s ON tgt.StationID=@s AND tgt.DeviceID=@DeviceID AND tgt.ParameterID=s.pid
   AND tgt.[Interval]=s.bkt AND tgt.TypeID=@typeId
WHERE @Now >= DATEADD({T}, @ival, s.bkt);';

DECLARE cAqi3 CURSOR LOCAL FAST_FORWARD FOR
    SELECT p.ID, p.ParameterID, p.StationID, p.ServerAvgInterval
    FROM DMN_Parameters p WITH (NOLOCK)
    JOIN MST_Devices_Drivers d WITH (NOLOCK) ON p.DriverID = d.ID
    WHERE p.DeviceID = @DeviceID AND d.DriverName = 'AQI Index' AND p.ServerAvgInterval IS NOT NULL;

OPEN cAqi3;
FETCH NEXT FROM cAqi3 INTO @aqiId, @aqiRef, @s, @aqiIntervals;
WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE cTok CURSOR LOCAL FAST_FORWARD FOR
        SELECT LTRIM(RTRIM(LEFT(t.value, CHARINDEX('-', t.value)-1))),
               UPPER(LTRIM(RTRIM(SUBSTRING(t.value, CHARINDEX('-', t.value)+1, 10))))
        FROM STRING_SPLIT(@aqiIntervals, ',') t
        WHERE CHARINDEX('-', t.value) > 0;

    OPEN cTok;
    FETCH NEXT FROM cTok INTO @ivalTxt, @code;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @ival = TRY_CAST(@ivalTxt AS INT);
        IF @ival IS NOT NULL AND @code IN ('M','H')
        BEGIN
            SET @itype  = CASE WHEN @code = 'M' THEN 'MINUTE' ELSE 'HOUR' END;
            SET @typeId = CASE WHEN @code = 'M' THEN @ival ELSE @ival * 60 END;

            IF @typeId <> @OneHourTypeID          -- 60 handled by Stage 2
            BEGIN
                SET @sql = REPLACE(@s3aqi, '{T}', @itype);
                EXEC sp_executesql @sql,
                     N'@s INT, @DeviceID INT, @aqiId INT, @aqiRef INT, @ival INT, @typeId INT, @Now DATETIME',
                     @s=@s, @DeviceID=@DeviceID, @aqiId=@aqiId, @aqiRef=@aqiRef, @ival=@ival, @typeId=@typeId, @Now=@Now;

                SET @sql = REPLACE(@s3sub, '{T}', @itype);
                EXEC sp_executesql @sql,
                     N'@s INT, @DeviceID INT, @ival INT, @typeId INT, @Now DATETIME',
                     @s=@s, @DeviceID=@DeviceID, @ival=@ival, @typeId=@typeId, @Now=@Now;
            END
        END
        FETCH NEXT FROM cTok INTO @ivalTxt, @code;
    END
    CLOSE cTok; DEALLOCATE cTok;

    FETCH NEXT FROM cAqi3 INTO @aqiId, @aqiRef, @s, @aqiIntervals;
END
CLOSE cAqi3; DEALLOCATE cAqi3;
PRINT '    Stage 3 done.';

/*======================================================================================
  STAGE 4 : Monthly aggregates -> ParameterAveragesMonth
  Mirrors GetMonthlyAggregatesAsync (hourly TypeID=60 -> month; complete months only).
======================================================================================*/
PRINT '--- Stage 4: monthly averages ---';
;WITH m AS (
    SELECT pa.StationID, pa.DeviceID, pa.ParameterID,
           DATEADD(MONTH, DATEDIFF(MONTH, 0, pa.Interval), 0) AS bkt,
           MAX(pa.ParameterIDRef) AS ref, AVG(pa.Parametervalue) AS av,
           AVG(pa.SubIndex) AS asi, MIN(pa.LoggerFlags) AS lf
    FROM ParameterAverages pa WITH (NOLOCK)
    WHERE pa.DeviceID = @DeviceID AND pa.TypeID = @OneHourTypeID AND pa.Parametervalue IS NOT NULL
      AND DATEADD(MONTH, DATEDIFF(MONTH, 0, pa.Interval), 0) < DATEFROMPARTS(YEAR(@Now), MONTH(@Now), 1)
    GROUP BY pa.StationID, pa.DeviceID, pa.ParameterID, DATEADD(MONTH, DATEDIFF(MONTH, 0, pa.Interval), 0)
)
INSERT INTO ParameterAveragesMonth
    (StationID, DeviceID, ParameterID, ParameterIDRef, Parametervalue, SubIndex, [Type], [Interval], LoggerFlags, TypeID, CreatedTime)
SELECT m.StationID, m.DeviceID, m.ParameterID, m.ref, m.av, m.asi, 'MONTH', m.bkt, m.lf, @MonthTypeID, @Now
FROM m
WHERE NOT EXISTS (SELECT 1 FROM ParameterAveragesMonth t WITH (NOLOCK)
                  WHERE t.StationID=m.StationID AND t.DeviceID=m.DeviceID AND t.ParameterID=m.ParameterID
                    AND t.[Interval]=m.bkt AND t.TypeID=@MonthTypeID);
PRINT '    Stage 4 done.';

/*======================================================================================
  STAGE 5 : Yearly aggregates -> ParameterAveragesYear
  Mirrors GetYearlyAggregatesAsync (hourly TypeID=60 -> year; complete years only).
======================================================================================*/
PRINT '--- Stage 5: yearly averages ---';
;WITH y AS (
    SELECT pa.StationID, pa.DeviceID, pa.ParameterID,
           DATEADD(YEAR, DATEDIFF(YEAR, 0, pa.Interval), 0) AS bkt,
           MAX(pa.ParameterIDRef) AS ref, AVG(pa.Parametervalue) AS av,
           AVG(pa.SubIndex) AS asi, MIN(pa.LoggerFlags) AS lf
    FROM ParameterAverages pa WITH (NOLOCK)
    WHERE pa.DeviceID = @DeviceID AND pa.TypeID = @OneHourTypeID AND pa.Parametervalue IS NOT NULL
      AND DATEADD(YEAR, DATEDIFF(YEAR, 0, pa.Interval), 0) < DATEFROMPARTS(YEAR(@Now), 1, 1)
    GROUP BY pa.StationID, pa.DeviceID, pa.ParameterID, DATEADD(YEAR, DATEDIFF(YEAR, 0, pa.Interval), 0)
)
INSERT INTO ParameterAveragesYear
    (StationID, DeviceID, ParameterID, ParameterIDRef, Parametervalue, SubIndex, [Type], [Interval], LoggerFlags, TypeID, CreatedTime)
SELECT y.StationID, y.DeviceID, y.ParameterID, y.ref, y.av, y.asi, 'YEAR', y.bkt, y.lf, @YearTypeID, @Now
FROM y
WHERE NOT EXISTS (SELECT 1 FROM ParameterAveragesYear t WITH (NOLOCK)
                  WHERE t.StationID=y.StationID AND t.DeviceID=y.DeviceID AND t.ParameterID=y.ParameterID
                    AND t.[Interval]=y.bkt AND t.TypeID=@YearTypeID);
PRINT '    Stage 5 done.';

/*--------------------------------------------------------------------------------------
  Commit / rollback
--------------------------------------------------------------------------------------*/
IF @CommitChanges = 1
BEGIN
    COMMIT TRAN;
    PRINT 'COMMITTED changes for DeviceID = ' + CAST(@DeviceID AS VARCHAR(10));
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT 'DRY-RUN (rolled back). Set @CommitChanges = 1 to persist. DeviceID = ' + CAST(@DeviceID AS VARCHAR(10));
END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    PRINT 'ERROR ' + CAST(ERROR_NUMBER() AS VARCHAR(10)) + ' at line ' + CAST(ERROR_LINE() AS VARCHAR(10)) + ': ' + ERROR_MESSAGE();
    THROW;
END CATCH;

-- cleanup
IF OBJECT_ID('tempdb..#Breakpoints') IS NOT NULL DROP TABLE #Breakpoints;
IF OBJECT_ID('tempdb..#pn')          IS NOT NULL DROP TABLE #pn;
IF OBJECT_ID('tempdb..#hist')        IS NOT NULL DROP TABLE #hist;
