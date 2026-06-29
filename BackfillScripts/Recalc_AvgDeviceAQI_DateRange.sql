/*======================================================================================
  Recalc_DeviceAQI_DateRange.sql
  --------------------------------------------------------------------------------------
  PURPOSE
    RE-CALCULATE the AQI Index value and the pollutant SubIndex values for ONE device
    over a given [@FromDate , @ToDate] range, using the SAME logic as the live service
    (AvgDataCalculationServer.cs) / Backfill_DeviceAverages.sql.

    Unlike the backfill script (which only gap-fills missing rows), this script
    OVERWRITES existing AQI rows + SubIndex values inside the range (UPSERT). Use it to
    correct/re-stamp AQI after pollutant averages have been changed or back-filled.

  CALCULATION RULES (as requested)
    * 1-H  (TypeID 60)  : full AQI -> piecewise breakpoints + 8h/24h rolling logic
                          (O3 8h+1h fallback, CO 8h, NO2 1h, PM10/PM2.5 24h,
                           SO2 1h if <=797 else 24h). AQI = MAX of non-null sub-indices.
    * 8-H / 24-H (and any other configured interval) : AQI = simple AVG of the 1-H AQI
                          values inside the bucket ; SubIndex = AVG of the 1-H SubIndex
                          values inside the bucket.

    The larger intervals are taken from the AQI-Index parameter's ServerAvgInterval
    (so 8-H = TypeID 480, 24-H = TypeID 1440 are handled automatically). 1-H is always
    processed first so the larger intervals can roll it up.

  HOW TO USE
    1. Set @DeviceID, @FromDate, @ToDate below.
    2. Keep @TestMode = 1 for a dry run (everything ROLLED BACK + counts printed).
       Set @TestMode = 0 to COMMIT.
    3. Run the whole file (it (re)creates two helper functions in GO batches).

  CONFIG (from AQMSDataUpdateService\App.config)
    AQIParameters = PM10,O₃,CO,PM2.5,SO₂,NO₂   (unicode subscripts)
======================================================================================*/


/*======================================================================================
  HELPER 1 : dbo.fn_PollutantAQI  -- identical to Backfill_DeviceAverages.sql
  Exact port of CalculatePollutantAQIValues (AvgDataCalculationServer.cs:2171).
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
  HELPER 2 : dbo.fn_RollingAvg  -- identical to Backfill_DeviceAverages.sql
  Rolling AVERAGE over (Interval - @Hours, Interval] on TypeID = @TypeID; gate >= @MinCount
  total rows; average only positive converted values. Returns NULL if gate fails.
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

    IF @cnt IS NULL OR @cnt < @MinCount RETURN NULL;
    IF @pos IS NULL OR @pos = 0          RETURN NULL;
    RETURN @sum / @pos;
END
GO


/*======================================================================================
  MAIN RECALC BATCH
======================================================================================*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

-----------------------------------------------------------------------------------------
-- PARAMETERS
-----------------------------------------------------------------------------------------
DECLARE @DeviceID        INT      = 0;                       -- <<<< SET DEVICE ID
DECLARE @FromDate        DATETIME = '2026-01-01 00:00:00';   -- <<<< RANGE START (inclusive)
DECLARE @ToDate          DATETIME = '2026-01-31 23:59:59';   -- <<<< RANGE END   (inclusive)
DECLARE @TestMode        BIT      = 1;                       -- 1 = ROLLBACK (dry run), 0 = COMMIT
DECLARE @defaultInterval INT      = 1;

IF @DeviceID = 0
BEGIN
    RAISERROR('Set @DeviceID before running.', 16, 1);
    RETURN;
END
IF @FromDate IS NULL OR @ToDate IS NULL OR @FromDate > @ToDate
BEGIN
    RAISERROR('Set a valid @FromDate / @ToDate range.', 16, 1);
    RETURN;
END

-- AQI pollutant driver names (unicode subscripts)
DECLARE @PM10 NVARCHAR(20) = N'PM10';
DECLARE @O3   NVARCHAR(20) = N'O₃';
DECLARE @CO   NVARCHAR(20) = N'CO';
DECLARE @PM25 NVARCHAR(20) = N'PM2.5';
DECLARE @SO2  NVARCHAR(20) = N'SO₂';
DECLARE @NO2  NVARCHAR(20) = N'NO₂';

DECLARE @cntAqiUpd INT = 0, @cntAqiIns INT = 0, @cntSubUpd INT = 0;

-- AQI-Index parameter for this device
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

IF @AqiParamId IS NULL
BEGIN
    RAISERROR('No AQI Index parameter configured for this device.', 16, 1);
    RETURN;
END

BEGIN TRY
BEGIN TRANSACTION;

-- reusable scalars
DECLARE @I DATETIME, @B DATETIME, @P INT;
DECLARE @vo3 FLOAT, @so2v FLOAT, @vno2 FLOAT;       -- only the hourly values actually used
DECLARE @pm10id INT, @o3id INT, @so2id INT, @no2id INT, @coid INT, @pm25id INT;
DECLARE @aqipm10 FLOAT, @aqio3 FLOAT, @aqiso2 FLOAT, @aqino2 FLOAT, @aqico FLOAT, @aqipm25 FLOAT;
DECLARE @avg8o3 FLOAT, @aqi8o3 FLOAT, @aqi1o3 FLOAT, @aqi FLOAT, @aqiavg FLOAT;

/*======================================================================================
  PART A : 1-H (TypeID 60)  -- full AQI calculation, UPSERT
======================================================================================*/

IF OBJECT_ID('tempdb..#h') IS NOT NULL DROP TABLE #h;
SELECT DISTINCT pa.Interval
INTO #h
FROM ParameterAverages pa
INNER JOIN DMN_Parameters dp     ON pa.ParameterID = dp.ID
INNER JOIN MST_Devices_Drivers d ON dp.DriverID    = d.ID
WHERE pa.DeviceID = @DeviceID AND pa.StationID = @AqiStationID AND pa.TypeID = 60
  AND d.DriverName IN (@PM10, @O3, @CO, @PM25, @SO2, @NO2) AND dp.shouldUseForAqi = 1
  AND pa.Interval >= @FromDate AND pa.Interval <= @ToDate;

DECLARE curH CURSOR LOCAL FAST_FORWARD FOR SELECT Interval FROM #h ORDER BY Interval;
OPEN curH; FETCH NEXT FROM curH INTO @I;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @vo3=NULL,@so2v=NULL,@vno2=NULL,
           @pm10id=NULL,@o3id=NULL,@so2id=NULL,@no2id=NULL,@coid=NULL,@pm25id=NULL,
           @aqipm10=NULL,@aqio3=NULL,@aqiso2=NULL,@aqino2=NULL,@aqico=NULL,@aqipm25=NULL,@aqi=NULL;

    -- converted hourly pollutant values (O3/SO2/NO2 needed for the formulas) + param ids
    SELECT
        @vo3   = MAX(CASE WHEN z.dn = @O3   THEN z.conv END),
        @so2v  = MAX(CASE WHEN z.dn = @SO2  THEN z.conv END),
        @vno2  = MAX(CASE WHEN z.dn = @NO2  THEN z.conv END),
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

    -- O3 : 8h rolling with 1h fallback
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

    -- UPSERT the AQI-Index 1-H row
    IF EXISTS (SELECT 1 FROM ParameterAverages
               WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@AqiParamId AND Interval=@I AND TypeID=60)
    BEGIN
        UPDATE ParameterAverages
        SET Parametervalue=@aqi, LoggerFlags=1, CreatedTime=GETDATE(), ParameterIDRef=@AqiParamRef
        WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@AqiParamId AND Interval=@I AND TypeID=60;
        SET @cntAqiUpd += 1;
    END
    ELSE
    BEGIN
        INSERT INTO ParameterAverages
            (StationID, DeviceID, ParameterIDRef, Parametervalue, Interval, CreatedTime, LoggerFlags, TypeID, ParameterID)
        VALUES (@AqiStationID, @DeviceID, @AqiParamRef, @aqi, @I, GETDATE(), 1, 60, @AqiParamId);
        SET @cntAqiIns += 1;
    END

    -- update SubIndex on each pollutant's hourly row
    IF @pm10id IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqipm10 WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@pm10id AND Interval=@I AND TypeID=60; SET @cntSubUpd+=@@ROWCOUNT; END
    IF @o3id   IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqio3   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@o3id   AND Interval=@I AND TypeID=60; SET @cntSubUpd+=@@ROWCOUNT; END
    IF @so2id  IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqiso2  WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@so2id  AND Interval=@I AND TypeID=60; SET @cntSubUpd+=@@ROWCOUNT; END
    IF @no2id  IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqino2  WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@no2id  AND Interval=@I AND TypeID=60; SET @cntSubUpd+=@@ROWCOUNT; END
    IF @coid   IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqico   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@coid   AND Interval=@I AND TypeID=60; SET @cntSubUpd+=@@ROWCOUNT; END
    IF @pm25id IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqipm25 WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@pm25id AND Interval=@I AND TypeID=60; SET @cntSubUpd+=@@ROWCOUNT; END

    FETCH NEXT FROM curH INTO @I;
END
CLOSE curH; DEALLOCATE curH;

PRINT '1-H AQI rows updated/inserted: ' + CAST(@cntAqiUpd AS VARCHAR(20)) + ' / ' + CAST(@cntAqiIns AS VARCHAR(20));


/*======================================================================================
  PART B : 8-H / 24-H (and any other configured larger interval)
           AQI = AVG of 1-H AQI in the bucket ; SubIndex = AVG of 1-H SubIndex in bucket.
           UPSERT. Runs after Part A so the 1-H AQI is freshly recalculated.
======================================================================================*/

IF OBJECT_ID('tempdb..#aqitok') IS NOT NULL DROP TABLE #aqitok;
SELECT
    ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS RID,
    CAST(LTRIM(RTRIM(LEFT(s.value, CHARINDEX('-', s.value) - 1))) AS INT)
        * CASE WHEN LTRIM(RTRIM(SUBSTRING(s.value, CHARINDEX('-', s.value)+1, 10))) = 'M' THEN 1 ELSE 60 END AS PtypeID
INTO #aqitok
FROM STRING_SPLIT(@AqiServerInterval, ',') s
WHERE CHARINDEX('-', s.value) > 0
  AND CAST(LTRIM(RTRIM(LEFT(s.value, CHARINDEX('-', s.value) - 1))) AS INT)
        * CASE WHEN LTRIM(RTRIM(SUBSTRING(s.value, CHARINDEX('-', s.value)+1, 10))) = 'M' THEN 1 ELSE 60 END <> 60;

DECLARE @aqRid INT = 1, @aqMax INT = (SELECT ISNULL(MAX(RID),0) FROM #aqitok);
DECLARE @cntBigUpd INT = 0, @cntBigIns INT = 0;

WHILE @aqRid <= @aqMax
BEGIN
    SELECT @P = PtypeID FROM #aqitok WHERE RID = @aqRid;
    IF @P IS NULL BEGIN SET @aqRid += 1; CONTINUE; END

    -- candidate buckets: derived from 1-H AQI rows whose interval falls in the range
    IF OBJECT_ID('tempdb..#bk') IS NOT NULL DROP TABLE #bk;
    SELECT DISTINCT DATEADD(MINUTE, DATEDIFF(MINUTE, 0, pa.Interval) / @P * @P, 0) AS Bucket
    INTO #bk
    FROM ParameterAverages pa
    WHERE pa.DeviceID = @DeviceID AND pa.StationID = @AqiStationID
      AND pa.ParameterID = @AqiParamId AND pa.TypeID = 60
      AND pa.Interval >= @FromDate AND pa.Interval <= @ToDate;

    DECLARE curB CURSOR LOCAL FAST_FORWARD FOR SELECT Bucket FROM #bk ORDER BY Bucket;
    OPEN curB; FETCH NEXT FROM curB INTO @B;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- AQI = AVG of the 1-H AQI values inside the bucket (full bucket window)
        SET @aqiavg = NULL;
        SELECT @aqiavg = AVG(pa.Parametervalue)
        FROM ParameterAverages pa
        WHERE pa.DeviceID = @DeviceID AND pa.StationID = @AqiStationID
          AND pa.ParameterID = @AqiParamId AND pa.TypeID = 60
          AND pa.Interval >= @B AND pa.Interval < DATEADD(MINUTE, @P, @B);

        -- UPSERT the AQI-Index larger-interval row
        IF EXISTS (SELECT 1 FROM ParameterAverages
                   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@AqiParamId AND Interval=@B AND TypeID=@P)
        BEGIN
            UPDATE ParameterAverages
            SET Parametervalue=@aqiavg, LoggerFlags=1, CreatedTime=GETDATE(), ParameterIDRef=@AqiParamRef
            WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@AqiParamId AND Interval=@B AND TypeID=@P;
            SET @cntBigUpd += 1;
        END
        ELSE
        BEGIN
            INSERT INTO ParameterAverages
                (StationID, DeviceID, ParameterIDRef, Parametervalue, Interval, CreatedTime, LoggerFlags, TypeID, ParameterID)
            VALUES (@AqiStationID, @DeviceID, @AqiParamRef, @aqiavg, @B, GETDATE(), 1, @P, @AqiParamId);
            SET @cntBigIns += 1;
        END

        -- per-pollutant SubIndex = AVG of 1-H SubIndex inside the bucket
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

        -- update SubIndex on the larger-interval pollutant rows (must already exist at TypeID = @P)
        IF @pm10id IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqipm10 WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@pm10id AND Interval=@B AND TypeID=@P; SET @cntSubUpd+=@@ROWCOUNT; END
        IF @o3id   IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqio3   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@o3id   AND Interval=@B AND TypeID=@P; SET @cntSubUpd+=@@ROWCOUNT; END
        IF @so2id  IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqiso2  WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@so2id  AND Interval=@B AND TypeID=@P; SET @cntSubUpd+=@@ROWCOUNT; END
        IF @no2id  IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqino2  WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@no2id  AND Interval=@B AND TypeID=@P; SET @cntSubUpd+=@@ROWCOUNT; END
        IF @coid   IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqico   WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@coid   AND Interval=@B AND TypeID=@P; SET @cntSubUpd+=@@ROWCOUNT; END
        IF @pm25id IS NOT NULL BEGIN UPDATE ParameterAverages SET SubIndex=@aqipm25 WHERE StationID=@AqiStationID AND DeviceID=@DeviceID AND ParameterID=@pm25id AND Interval=@B AND TypeID=@P; SET @cntSubUpd+=@@ROWCOUNT; END

        FETCH NEXT FROM curB INTO @B;
    END
    CLOSE curB; DEALLOCATE curB;

    SET @aqRid += 1;
END

PRINT 'Larger-interval (8-H/24-H/...) AQI rows updated/inserted: ' + CAST(@cntBigUpd AS VARCHAR(20)) + ' / ' + CAST(@cntBigIns AS VARCHAR(20));
PRINT 'Total SubIndex cells updated: ' + CAST(@cntSubUpd AS VARCHAR(20));


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

IF OBJECT_ID('tempdb..#h')      IS NOT NULL DROP TABLE #h;
IF OBJECT_ID('tempdb..#aqitok') IS NOT NULL DROP TABLE #aqitok;
IF OBJECT_ID('tempdb..#bk')     IS NOT NULL DROP TABLE #bk;
GO
