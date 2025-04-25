using AQMSDataUpdateLibrary.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using log4net;
namespace AQMSDataUpdateLibrary
{
    //Average Service for Server application
    public class AvgDataCalculationServer
    {
        // string filePath;
        string parameterTableName;
        string averageTableName;
        string averageTableNameMonth;
        string averageTableNameYear;
        string readingTableName;
        string flagTableName;
        string logTableName;
        string driverTableName;
        string winddirection;
        string defaultInterval;
        string rain;
        string AQIParameters;
        NameValueCollection _appSettingsSection;
        private static readonly ILog Log = LogManager.GetLogger(typeof(AvgDataCalculationServer));
        private static readonly ILog ErrorLog = LogManager.GetLogger("error");

        public AvgDataCalculationServer(NameValueCollection appSettingsSection)
        {
            _appSettingsSection = appSettingsSection;
            parameterTableName = _appSettingsSection["parameterTableName"];
            driverTableName = _appSettingsSection["driverTableName"];
            averageTableName = _appSettingsSection["AverageTableName"];
            averageTableNameMonth = _appSettingsSection["AverageTableNameMonth"];
            averageTableNameYear = _appSettingsSection["AverageTableNameYear"];
            readingTableName = _appSettingsSection["ReadingTableName"];
            flagTableName = _appSettingsSection["FlagTableName"];
            logTableName = _appSettingsSection["LogTableName"];
            winddirection = _appSettingsSection["winddirection"];
            rain = _appSettingsSection["rain"];
            defaultInterval = _appSettingsSection["defaultInterval"];
            AQIParameters = _appSettingsSection["AQIParameters"];
        }

        private DateTime? GetLatestIntervalRecordFromAvgTable(SqlCommand cmd, DataRow row, int pTypeId)
        {
            DateTime? formatInterval = null;
            cmd.CommandText = $"SELECT TOP(1) [Interval] FROM {averageTableName} WHERE StationID = @StationID AND DeviceID = @DeviceID AND ParameterID = @ParameterID AND TypeID = @TypeID ORDER BY [Interval] DESC";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            cmd.Parameters.AddWithValue("@TypeID", pTypeId);
            Log.Info("Lastest InterVal Query From Average Table:" + cmd.CommandText);
            using (var sqlReader = cmd.ExecuteReader())
            {
                if (sqlReader.Read())
                {
                    formatInterval = sqlReader.GetDateTime(sqlReader.GetOrdinal("Interval"));
                }
            }
            Log.Info("Last Interval in Avg Table:" + formatInterval.ToString());

            return formatInterval;
        }

        private DateTime? GetLatestIntervalRecordFromParameterTable(SqlCommand cmd, DataRow row)
        {
            DateTime? formatInterval = null;
            cmd.CommandText = $"SELECT TOP(1) [CreatedTime] FROM {readingTableName} WHERE StationID = @StationID AND DeviceID = @DeviceID AND ParameterID = @ParameterID  ORDER BY [CreatedTime] DESC";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            Log.Info("Lastest InterVal Query From Parameter Table:" + cmd.CommandText);
            using (var sqlReader = cmd.ExecuteReader())
            {
                if (sqlReader.Read())
                {
                    formatInterval = sqlReader.GetDateTime(sqlReader.GetOrdinal("CreatedTime"));
                }
            }
            Log.Info("Last Interval in Parameter Table:" + formatInterval.ToString());
            return formatInterval;
        }

        private DataTable GetRecordCountForEachInterval(SqlCommand cmd, DataRow row, DateTime? formatInterval, string interval, string intervalType)
        {
            DataTable dtCount = new DataTable();
            try
            {
                cmd.CommandText = $"Select a.Interval,COUNT(a.Interval) as TotReccnt from (SELECT dateadd({interval}, datediff({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) Interval FROM {readingTableName}  sd where sd.StationID = @StationID and sd.DeviceID = @DeviceID and sd.ParameterID = @ParameterID ) a where a.Interval > @intervalValue group by a.Interval order by a.Interval asc";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
                cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
                cmd.Parameters.AddWithValue("@Interval", intervalType);
                Log.Info("Query To fetch the number of records for each Interval:" + cmd.CommandText);
                if (formatInterval == null)
                    cmd.Parameters.AddWithValue("@intervalValue", string.Empty);
                else
                {
                    cmd.Parameters.AddWithValue("@intervalValue", formatInterval);
                    Log.Info("Format InterVal Value : " + formatInterval.ToString());
                }
                using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                {
                    adapter.Fill(dtCount);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error in GetRecordCountForEachInterval : " + ex);
            }
            return dtCount;
        }

        private DataTable GetRecordCountForEachIntervalAQI1(SqlCommand cmd, DataRow row, DateTime? formatInterval, string interval, string intervalType, int PtypeID)
        {
            DataTable dtCount = new DataTable();
            try
            {
                cmd.CommandText = $"Select a.Interval,COUNT(a.Interval) as TotReccnt,AVG(a.Parametervalue) as Parameteravg from (SELECT dateadd({interval}, datediff({interval}, 0, sd.Interval) / @Interval * @Interval, 0) Interval,Parametervalue FROM {averageTableName}  sd where sd.StationID = @StationID and sd.DeviceID = @DeviceID and sd.ParameterID = @ParameterID ) a where a.Interval > @intervalValue group by a.Interval order by a.Interval asc";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
                cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
                cmd.Parameters.AddWithValue("@Interval", intervalType);
                Log.Info("Query To fetch the number of records for each Interval:" + cmd.CommandText);
                if (formatInterval == null)
                    cmd.Parameters.AddWithValue("@intervalValue", string.Empty);
                else
                {
                    cmd.Parameters.AddWithValue("@intervalValue", formatInterval);
                    Log.Info("GetRecordCountForEachIntervalAQI1 Format InterVal Value : " + formatInterval.ToString());
                }
                using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                {
                    adapter.Fill(dtCount);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error in GetRecordCountForEachIntervalAQI1 : " + ex);
            }
            return dtCount;
        }

        private DataTable GetRecordCountForEachIntervalAQI(SqlCommand cmd, DataRow row, DateTime? formatInterval, string interval, string intervalType, int TypeID)
        {
            DataTable dtCount = new DataTable();
            try
            {
                string[] AQIparameters = AQIParameters.Split(',');
                string AQIParametersCondition = string.Join(",", AQIparameters.Select(d => $"'{d}'"));
                cmd.CommandText = $@"
        SELECT a.Interval, a.StationID, COUNT(a.Interval) AS TotReccnt 
        FROM (
            SELECT DISTINCT 
                dateadd({interval}, datediff({interval}, 0, pa.Interval) / @Interval * @Interval, 0) AS Interval,pa.StationID
            FROM {averageTableName} pa 
            INNER JOIN {parameterTableName} dp ON pa.ParameterID = dp.ID 
            INNER JOIN {driverTableName} d ON dp.DriverID = d.ID 
            WHERE pa.StationID = @StationID AND pa.TypeID = @TypeID
                AND d.DriverName IN ({AQIParametersCondition})
        ) a 
        WHERE a.Interval > @intervalValue
        GROUP BY a.Interval,a.StationID
        ORDER BY a.Interval ASC";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@TypeID", TypeID);
                cmd.Parameters.AddWithValue("@Interval", intervalType);

                Log.Info("Query To fetch the number of records for each Interval: " + cmd.CommandText);

                if (formatInterval == null)
                {
                    cmd.Parameters.AddWithValue("@intervalValue", string.Empty); // Using DBNull.Value instead of an empty string for nullable parameter
                }
                else
                {
                    cmd.Parameters.AddWithValue("@intervalValue", formatInterval);
                    Log.Info("GetRecordCountForEachIntervalAQI Format Interval Value: " + formatInterval.ToString());
                }


                using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                {
                    adapter.Fill(dtCount);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error in GetRecordCountForEachIntervalAQI: " + ex);
            }
            return dtCount;
        }

        private DataTable GetParametervaluesForEachIntervalAQI(SqlCommand cmd, DataRow row, int TypeID)
        {
            DataTable dtCount = new DataTable();
            try
            {
                string[] AQIparameters = AQIParameters.Split(',');
                string AQIParametersCondition = string.Join(",", AQIparameters.Select(d => $"'{d}'"));
                cmd.CommandText = $@"
       SELECT d.DriverName, pa.ParameterValue * COALESCE(CASE WHEN u.UnitName <> pc.SecondaryUnit THEN TRY_CAST(pc.ConversionFactor AS FLOAT)
            ELSE 1 END, 1) AS ConvertedParameterValue,  pa.Interval, u.UnitName AS ReportedUnit FROM  
    {averageTableName} pa INNER JOIN {parameterTableName} dp ON pa.ParameterID = dp.ID INNER JOIN {driverTableName} d ON dp.DriverID = d.ID 
    INNER JOIN ReportedUnits u ON dp.UnitID = u.ID LEFT JOIN Parameter_Conversion pc ON d.DriverName = pc.Parameter 
    WHERE  pa.StationID = @StationID AND pa.Interval = @Interval AND pa.TypeID = @TypeID AND d.DriverName IN ({AQIParametersCondition})";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@Interval", row["Interval"]);
                cmd.Parameters.AddWithValue("@TypeID", TypeID);

                Log.Info("Query To fetch the number of records for each Interval: " + cmd.CommandText);

                using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                {
                    adapter.Fill(dtCount);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error in GetRecordCountForEachIntervalAQI: " + ex);
            }
            return dtCount;
        }


        private int GetHighPriorityLoggerFlag(SqlCommand cmd, DataRow row, string intervalType, string interval, object intravel)
        {
            //cmd.CommandText = $@"SELECT TOP(1) z.LoggerFlags, z.Priority
            //            FROM (
            //                SELECT a.Interval, a.LoggerFlags, a.Priority, count(*) as NoOfRecords
            //                FROM (
            //                    SELECT DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval, LoggerFlags, Priority, Type
            //                    FROM {readingTableName} sd
            //                    LEFT JOIN {flagTableName} f ON sd.LoggerFlags = f.ID
            //                    WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID AND sd.LoggerFlags IS NOT NULL
            //                ) a
            //                WHERE a.Interval = @intervalValue 
            //            ) z
            //            ORDER BY z.Priority ";

            cmd.CommandText = $@"SELECT TOP(1) z.LoggerFlags, z.Priority FROM(
                             SELECT a.Interval, a.LoggerFlags, a.Priority, count(*) as NoOfRecords
                             FROM(
                                 SELECT DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) /  @Interval *  @Interval, 0) AS Interval, LoggerFlags, Priority, Type
                                 FROM {readingTableName} sd  LEFT JOIN {flagTableName} f ON  sd.LoggerFlags = f.ID
                                 WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID AND sd.LoggerFlags IS NOT NULL
                             ) a WHERE a.Interval = @intervalValue 
                             group by a.Interval, a.LoggerFlags, a.Priority   --and a.Type != 'Validation'
                         ) z
                         ORDER BY z.NoOfRecords desc, z.Priority asc";

            Log.Info("Query to Fetch the High Priority Logger Flag: " + cmd.CommandText);
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            cmd.Parameters.AddWithValue("@Interval", intervalType);
            cmd.Parameters.AddWithValue("@intervalValue", intravel);

            int priorityLoggerflag = 0;
            using (var reader = cmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    priorityLoggerflag = reader.GetInt32(reader.GetOrdinal("LoggerFlags"));
                }
            }
            Log.Info("High Priority Flag Value :" + priorityLoggerflag.ToString());
            return priorityLoggerflag;
        }

        private int GetHighPriorityLoggerFlagForUpdateRecords(SqlCommand cmd, DataRow row, string intervalType, string interval, object intravel)
        {
            //cmd.CommandText = $@"SELECT TOP(1) z.LoggerFlags, z.Priority
            //             FROM (
            //                 SELECT a.Interval, a.LoggerFlags, a.Priority
            //                 FROM (
            //                     SELECT DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval, LoggerFlags, Priority ,Type
            //                     FROM {readingTableName} sd
            //                     LEFT JOIN {flagTableName} f ON  sd.LoggerFlags = f.ID
            //                     WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID AND sd.LoggerFlags IS NOT NULL
            //                 ) a
            //                 WHERE a.Interval = DATEADD({interval}, DATEDIFF({interval}, 0, @intValue) / @Interval * @Interval, 0) and f.Type != 'Validation' 
            //             ) z
            //             ORDER BY z.Priority";

            cmd.CommandText = $@"SELECT TOP(1) z.LoggerFlags, z.Priority FROM(
                             SELECT a.Interval, a.LoggerFlags, a.Priority, count(*) as NoOfRecords
                             FROM(
                                 SELECT DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) /  @Interval *  @Interval, 0) AS Interval, LoggerFlags, Priority, Type
                                 FROM {readingTableName} sd
                                 LEFT JOIN {flagTableName} f ON  sd.LoggerFlags = f.ID
                                 WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID AND sd.LoggerFlags IS NOT NULL
                             ) a WHERE a.Interval = DATEADD({interval}, DATEDIFF({interval}, 0, @intValue) / @Interval  * @Interval , 0)
                             group by a.Interval, a.LoggerFlags, a.Priority   --and a.Type != 'Validation'
                         ) z
                         ORDER BY z.NoOfRecords desc, z.Priority asc";

            Log.Info("Query to Fetch the High Priority Logger Flag In Update mode: " + cmd.CommandText);

            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            cmd.Parameters.AddWithValue("@Interval", intervalType);
            cmd.Parameters.AddWithValue("@intValue", intravel);

            int priorityLoggerflag = 0;
            using (var reader = cmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    priorityLoggerflag = reader.GetInt32(reader.GetOrdinal("LoggerFlags"));
                }
            }
            Log.Info("Priority Flag Value: " + cmd.CommandText);
            return priorityLoggerflag;
        }
        private DataTable GetValidRecordsCountForUpdationeachInterval(SqlCommand cmd, DataRow row, string intervalType, string interval, object intravel)
        {
            cmd.CommandText = $"Select a.Interval,COUNT(a.Interval) as cnt from (SELECT dateadd({interval}, datediff({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) Interval, LoggerFlags FROM {readingTableName} sd join {flagTableName} f on f.Type != 'Validation' and sd.LoggerFlags = f.ID where sd.StationID = @StationID and sd.DeviceID = @DeviceID and sd.ParameterID = @ParameterID ) a where a.Interval = dateadd({interval}, datediff({interval}, 0, @intValue) / @Interval * @Interval, 0) group by a.Interval order by a.Interval desc";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            cmd.Parameters.AddWithValue("@Interval", intervalType);
            cmd.Parameters.AddWithValue("@intValue", intravel);
            Log.Info("Query to Fetch Valid records count for updation of each interval: " + cmd.CommandText);
            DataTable udt = new DataTable();
            using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
            {
                adapter.Fill(udt);
            }
            return udt;
        }
        private DataTable GetValidRecordsCountForeachInterval(SqlCommand cmd, DataRow row, string intervalType, string interval, object intravel)
        {
            cmd.CommandText = $@"SELECT a.Interval, COUNT(a.Interval) AS cnt
                        FROM (
                            SELECT DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval, LoggerFlags
                            FROM {readingTableName} sd
                            JOIN {flagTableName} f ON f.Type != 'Validation' AND sd.LoggerFlags = f.ID
                            WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID
                        ) a
                        WHERE a.Interval = @intervalValue
                        GROUP BY a.Interval
                        ORDER BY a.Interval DESC";
            Log.Info("Query to Fetch the valid records count for each interval in insertion mode: " + cmd.CommandText);
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            cmd.Parameters.AddWithValue("@Interval", intervalType);
            cmd.Parameters.AddWithValue("@intervalValue", intravel);

            DataTable dt = new DataTable();
            using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
            {
                adapter.Fill(dt);
            }
            return dt;
        }
        private void UpdateDataIntoAvgTable(SqlCommand cmd, int percentage, DataRow row, string interval, int priorityLoggerflag, string intervalCode, string intervalValue, object intraval)
        {
            string paramValue = "NULL";
            int intMinuteMultiplier = intervalCode == "M" ? 1 : 60;
            int typeIdValue = int.Parse(intervalValue) * intMinuteMultiplier;
            if (percentage >= 75)
            {
                if (rain.ToUpper().Contains(row["ParameterDriverName"].ToString().ToUpper()))
                    paramValue = "SUM(sd.Parametervalue)";
                else
                    paramValue = "AVG(sd.Parametervalue)";
            }
            if (percentage >= 75)
            {
                if (winddirection.ToUpper().Contains(row["ParameterDriverName"].ToString().ToUpper())) //for Wind Direction
                {

                    cmd.CommandText = $"UPDATE b SET b.Parametervalue = c.degreefinal,b.LoggerFlags = {priorityLoggerflag}" +
                                     $" FROM (select DATEADD({interval}, DATEDIFF({interval}, 0, CreatedTime) / @Interval * @Interval, 0) AS Interval,StationID, DeviceID, ParameterID, " +
                                     $"avg(a.cospv) as cospv,avg(a.sinpv) as sinpv, ATN2(avg(a.sinpv),avg(a.cospv)) as atanval,degrees(ATN2(avg(a.sinpv),avg(a.cospv))) as degreeval ," +
                                     $"case when degrees(ATN2(avg(a.sinpv),avg(a.cospv))) < 0  then 360+degrees(ATN2(avg(a.sinpv),avg(a.cospv))) else degrees(ATN2(avg(a.sinpv),avg(a.cospv))) end as degreefinal," +
                                    $"CAST(@Interval AS NVARCHAR(10)) + @IntervalType AS Type, {priorityLoggerflag} AS LoggerFlags,{typeIdValue} AS TypeID, GETDATE() AS date" +
                                    $" from (Select CreatedTime,StationID, DeviceID, ParameterID,parametervalue,ParameterIDRef, RADIANS(ParameterValue) radiansPV, COS(RADIANS(ParameterValue)) CosPV,SIN(RADIANS(ParameterValue)) AS SinPV " +
                                    $"from {readingTableName} sd JOIN {flagTableName} f ON sd.LoggerFlags = f.ID and f.Type != 'Validation' " +
                                    $"  WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID " +
                                     $"   ) a  GROUP BY StationID, DeviceID, ParameterID,DATEADD({interval}, DATEDIFF({interval}, 0, CreatedTime) / @Interval * @Interval, 0)" +
                                    $") c JOIN {averageTableName} b ON c.Interval = b.Interval AND c.StationID = b.StationID AND c.DeviceID = b.DeviceID AND c.ParameterID = b.ParameterID AND b.TypeID = c.TypeID " +
                                   $" WHERE b.TypeID = {typeIdValue} AND c.Interval = dateadd({interval}, datediff({interval}, 0,@intValue) / @Interval *@Interval, 0)  ";
                }
                else
                {
                    cmd.CommandText = $"UPDATE b SET b.Parametervalue = a.Parametervalue,b.LoggerFlags = {priorityLoggerflag}" +
                                      $" FROM(SELECT StationID, DeviceID, ParameterID,dateadd({interval}, datediff({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) Interval," +
                                      $"{paramValue} Parametervalue,{typeIdValue} TypeID,GETDATE() date FROM {readingTableName} sd JOIN {flagTableName}  f ON sd.LoggerFlags = f.ID and f.Type != 'Validation' " +
                                      $" WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID GROUP BY StationID, DeviceID, ParameterID, dateadd({interval}, datediff({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0)" +
                                      $" ) AS a JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID AND a.ParameterID = b.ParameterID " +
                                      $" AND b.TypeID = a.TypeID WHERE b.TypeID = {typeIdValue} AND a.Interval = dateadd({interval}, datediff({interval}, 0,@intValue) / @Interval *@Interval, 0)  ";
                }
            }
            else
            {
                cmd.CommandText = $"UPDATE b  SET b.Parametervalue = {paramValue},b.LoggerFlags = {priorityLoggerflag}" +
                                 $" FROM(SELECT StationID, DeviceID, ParameterID,dateadd({interval}, datediff({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) Interval," +
                                 $"{paramValue} Parametervalue,{typeIdValue} TypeID,GETDATE() date FROM {readingTableName} sd JOIN {flagTableName}  f ON sd.LoggerFlags = f.ID  " +
                                 $" WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID GROUP BY StationID, DeviceID, ParameterID, dateadd({interval}, datediff({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0)" +
                                 $" ) AS a JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID AND a.ParameterID = b.ParameterID " +
                                 $" AND b.TypeID = a.TypeID WHERE b.TypeID = {typeIdValue} AND a.Interval =  dateadd({interval}, datediff({interval}, 0,@intValue) / @Interval *@Interval, 0) ";
            }
            Log.Info("Update Query to Update parameter value in average table: " + cmd.CommandText);
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            cmd.Parameters.AddWithValue("@Interval", intervalValue);
            cmd.Parameters.AddWithValue("@IntervalType", intervalCode);
            cmd.Parameters.AddWithValue("@intValue", intraval);
            int i = cmd.ExecuteNonQuery();
        }
        private void InsertDataIntoAvgTable(SqlCommand cmd, int percentage, DataRow row, string interval, int priorityLoggerflag, string intervalCode, string intervalValue, object intraval)
        {
            try
            {
                string paramValue = "NULL";
                //if percentage is greater than 75 insert average of values, otherwise insert null
                if (percentage >= 75)
                    paramValue = "AVG(sd.Parametervalue)";
                int intMinuteMultiplier = intervalCode == "M" ? 1 : 60;
                int typeIdValue = int.Parse(intervalValue) * intMinuteMultiplier;

                if (rain.ToUpper().Contains(row["ParameterDriverName"].ToString().ToUpper()))
                {
                    if (percentage >= 75)
                        paramValue = "SUM(sd.Parametervalue)";
                }
                if (priorityLoggerflag != 0)
                {

                    if (percentage >= 75) //Valid records gater than or equal to 75%
                    {
                        if (winddirection.ToUpper().Contains(row["ParameterDriverName"].ToString().ToUpper())) //for Wind Direction
                        {

                            cmd.CommandText = $@"INSERT INTO {averageTableName} (StationID, DeviceID, ParameterID, Parametervalue, Type, Interval, LoggerFlags, TypeID, CreatedTime,ParameterIDRef)
                                    SELECT c.StationID, c.DeviceID, c.ParameterID, c.degreefinal, c.Type, c.Interval, c.LoggerFlags, c.TypeID, c.date,c.ParameterIDRef
                                    FROM (
                                    select DATEADD({interval}, DATEDIFF({interval}, 0, CreatedTime) / @Interval * @Interval, 0) AS Interval,StationID, DeviceID, ParameterID,ParameterIDRef, 
                                    avg(a.cospv) as cospv,avg(a.sinpv) as sinpv, ATN2(avg(a.sinpv),avg(a.cospv)) as atanval,
                                    degrees(ATN2(avg(a.sinpv),avg(a.cospv))) as degreeval ,
                                    case when degrees(ATN2(avg(a.sinpv),avg(a.cospv))) < 0 
                                    then 360+degrees(ATN2(avg(a.sinpv),avg(a.cospv))) 
                                    else degrees(ATN2(avg(a.sinpv),avg(a.cospv))) end as degreefinal,
                                    CAST(@Interval AS NVARCHAR(10)) + @IntervalType AS Type, {priorityLoggerflag} AS LoggerFlags,
                                    {typeIdValue} AS TypeID, GETDATE() AS date
                                        from
                                        (Select CreatedTime,StationID, DeviceID, ParameterID,parametervalue,ParameterIDRef, RADIANS(ParameterValue) radiansPV, COS(RADIANS(ParameterValue)) CosPV,SIN(RADIANS(ParameterValue)) AS SinPV 
                                        from {readingTableName} sd
                                        left JOIN {flagTableName} f ON sd.LoggerFlags = f.ID and f.Type != 'Validation' 
                                        WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID and f.ID=@priorityLoggerflag 
                                        ) a  GROUP BY StationID, DeviceID, ParameterID,ParameterIDRef, DATEADD({interval}, DATEDIFF({interval}, 0, CreatedTime) / @Interval * @Interval, 0)
                                    ) c
                                    LEFT JOIN {averageTableName} b ON c.Interval = b.Interval AND c.StationID = b.StationID AND c.DeviceID = b.DeviceID AND c.ParameterID = b.ParameterID AND b.TypeID = c.TypeID
                                    WHERE b.id IS NULL AND c.Interval = @intervalValue";
                        }
                        else
                        {
                            //Valid records gater than or equal to 75%
                            cmd.CommandText = $@"INSERT INTO {averageTableName} (StationID, DeviceID, ParameterID, Parametervalue, Type, Interval, LoggerFlags, TypeID, CreatedTime,ParameterIDRef)
                                    SELECT a.StationID, a.DeviceID, a.ParameterID, a.Parametervalue, a.Type, a.Interval, a.LoggerFlags, a.TypeID, a.date,a.ParameterIDRef
                                    FROM (
                                        SELECT StationID, DeviceID, ParameterID,ParameterIDRef, DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval,
                                               {paramValue} AS Parametervalue, CAST(@Interval AS NVARCHAR(10)) + @IntervalType AS Type, {priorityLoggerflag} AS LoggerFlags,
                                              {typeIdValue} AS TypeID, GETDATE() AS date
                                        FROM {readingTableName} sd
                                        JOIN {flagTableName} f ON sd.LoggerFlags = f.ID and f.Type != 'Validation'
                                        WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID and f.ID=@priorityLoggerflag
                                        GROUP BY StationID, DeviceID, ParameterID,ParameterIDRef, DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0), LoggerFlags
                                    ) a
                                    LEFT JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID AND a.ParameterID = b.ParameterID AND b.TypeID = a.TypeID
                                    WHERE b.id IS NULL AND a.Interval = @intervalValue";
                        }
                    }
                    else
                    {
                        //Valid records less than 75%
                        cmd.CommandText = $@"INSERT INTO {averageTableName} (StationID, DeviceID, ParameterID, Parametervalue, Type, Interval, LoggerFlags, TypeID, CreatedTime,ParameterIDRef)
                                    SELECT a.StationID, a.DeviceID, a.ParameterID, a.Parametervalue, a.Type, a.Interval, a.LoggerFlags, a.TypeID, a.date,a.ParameterIDRef
                                    FROM (
                                        SELECT StationID, DeviceID, ParameterID,ParameterIDRef, DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval,
                                               {paramValue} AS Parametervalue, CAST(@Interval AS NVARCHAR(10)) + @IntervalType AS Type, {priorityLoggerflag} AS LoggerFlags,
                                              {typeIdValue} AS TypeID, GETDATE() AS date
                                        FROM {readingTableName} sd
                                        JOIN {flagTableName} f ON sd.LoggerFlags = f.ID
                                        WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID and f.ID=@priorityLoggerflag
                                        GROUP BY StationID, DeviceID, ParameterID,ParameterIDRef, DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0)
                                    ) a
                                    LEFT JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID AND a.ParameterID = b.ParameterID AND b.TypeID = a.TypeID
                                    WHERE b.id IS NULL AND a.Interval = @intervalValue";
                    }
                }
                else
                {
                    cmd.CommandText = $@"INSERT INTO {averageTableName} (StationID, DeviceID, ParameterID, Parametervalue, Type, Interval, LoggerFlags, TypeID, CreatedTime,ParameterIDRef)
                                    SELECT a.StationID, a.DeviceID, a.ParameterID, a.Parametervalue, a.Type, a.Interval, a.LoggerFlags, a.TypeID, a.date,a.ParameterIDRef
                                    FROM (
                                        SELECT StationID, DeviceID, ParameterID,ParameterIDRef, DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) AS Interval,
                                               {paramValue} AS Parametervalue, CAST(@Interval AS NVARCHAR(10)) + @IntervalType AS Type, null AS LoggerFlags,
                                              {typeIdValue} AS TypeID, GETDATE() AS date
                                        FROM {readingTableName} sd
                                        left JOIN {flagTableName} f ON sd.LoggerFlags = f.ID and f.Type != 'Validation'
                                        WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID 
                                        GROUP BY StationID, DeviceID, ParameterID,ParameterIDRef, DATEADD({interval}, DATEDIFF({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0), LoggerFlags
                                    ) a
                                    LEFT JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID AND a.ParameterID = b.ParameterID AND b.TypeID = a.TypeID
                                    WHERE b.id IS NULL AND a.Interval = @intervalValue";
                }

                Log.Info(string.Format("Query to Insert Parameter values into average table2: StationID={0},deviceid={1},ParameterID={2},Interval={3},IntervalType={4},intervalValue={5},priorityLoggerflag={6}", Convert.ToInt32(row["StationID"]), Convert.ToInt32(row["DeviceId"]), Convert.ToInt32(row["ID"]), intervalValue, intervalCode, intraval, priorityLoggerflag));

                Log.Info("Query to Insert Parameter values into average table1: " + cmd.CommandText);
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
                cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
                cmd.Parameters.AddWithValue("@Interval", intervalValue);
                cmd.Parameters.AddWithValue("@IntervalType", intervalCode);
                cmd.Parameters.AddWithValue("@intervalValue", intraval);
                if (priorityLoggerflag != 0)
                    cmd.Parameters.AddWithValue("@priorityLoggerflag", priorityLoggerflag);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Log.Error("InsertDataIntoAvgTable: ", ex);

            }
        }

        private void InsertDataIntoAvgTableMonth(SqlCommand cmd, DataRow row, string interval, int priorityLoggerflag, string intervalCode, string intervalValue, string sqlConnectionString)
        {
           
            try
            {
                int typeIdValue = 43200;

                // ---------- UPDATE Query ----------
                cmd.CommandText = $@"
        -- Update existing monthly average
        UPDATE {averageTableNameMonth}
        SET 
            Parametervalue = monthlyAvg.AvgValue,
            CreatedTime = GETDATE()
        FROM (
            SELECT 
                sd.StationID,
                sd.DeviceID,
                sd.ParameterID,
                CAST(DATEADD(MONTH, DATEDIFF(MONTH, 0, sd.Interval), 0) AS DATETIME) AS MonthStart,
                AVG(sd.Parametervalue) AS AvgValue
            FROM {averageTableName} sd
            JOIN {flagTableName} f ON sd.LoggerFlags = f.ID AND f.Type != 'Validation'
            WHERE 
                sd.StationID = @StationID
                AND sd.DeviceID = @DeviceID
                AND sd.ParameterID = @ParameterID
                AND f.ID = @priorityLoggerflag
                AND sd.TypeID = 1440
            GROUP BY 
                sd.StationID,
                sd.DeviceID,
                sd.ParameterID,
                CAST(DATEADD(MONTH, DATEDIFF(MONTH, 0, sd.Interval), 0) AS DATETIME)
        ) monthlyAvg
        WHERE 
            {averageTableNameMonth}.StationID = monthlyAvg.StationID AND
            {averageTableNameMonth}.DeviceID = monthlyAvg.DeviceID AND
            {averageTableNameMonth}.ParameterID = monthlyAvg.ParameterID AND
            {averageTableNameMonth}.Interval = monthlyAvg.MonthStart AND
            {averageTableNameMonth}.TypeID = {typeIdValue};
    ";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
                cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
                if (priorityLoggerflag != 0)
                    cmd.Parameters.AddWithValue("@priorityLoggerflag", priorityLoggerflag);

                if (cmd.Connection.State != ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }
                cmd.ExecuteNonQuery();

                // ---------- INSERT Query ----------
                cmd.CommandText = $@"
        INSERT INTO {averageTableNameMonth} (
            StationID, DeviceID, ParameterID, Parametervalue, Type, Interval, LoggerFlags, TypeID, CreatedTime, ParameterIDRef
        )
        SELECT 
            a.StationID,
            a.DeviceID,
            a.ParameterID,
            a.Parametervalue,
            a.Type,
            a.Interval,
            a.LoggerFlags,
            a.TypeID,
            GETDATE(),
            a.ParameterIDRef
        FROM (
            SELECT 
                sd.StationID,
                sd.DeviceID,
                sd.ParameterID,
                sd.ParameterIDRef,
                CAST(DATEADD(MONTH, DATEDIFF(MONTH, 0, sd.Interval), 0) AS DATETIME) AS Interval,
                AVG(sd.Parametervalue) AS Parametervalue,
                CAST(@Interval AS NVARCHAR(10)) + @IntervalType AS Type,
                {priorityLoggerflag} AS LoggerFlags,
                {typeIdValue} AS TypeID
            FROM {averageTableName} sd
            JOIN {flagTableName} f ON sd.LoggerFlags = f.ID AND f.Type != 'Validation'
            WHERE 
                sd.StationID = @StationID
                AND sd.DeviceID = @DeviceID
                AND sd.ParameterID = @ParameterID
                AND f.ID = @priorityLoggerflag
                AND sd.TypeID = 1440
            GROUP BY 
                sd.StationID,
                sd.DeviceID,
                sd.ParameterID,
                sd.ParameterIDRef,
                CAST(DATEADD(MONTH, DATEDIFF(MONTH, 0, sd.Interval), 0) AS DATETIME)
        ) a
        WHERE NOT EXISTS (
            SELECT 1 FROM {averageTableNameMonth} b 
            WHERE 
                b.StationID = a.StationID AND 
                b.DeviceID = a.DeviceID AND 
                b.ParameterID = a.ParameterID AND 
                b.Interval = a.Interval AND 
                b.TypeID = a.TypeID
        );
    ";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
                cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
                cmd.Parameters.AddWithValue("@Interval", intervalValue);
                cmd.Parameters.AddWithValue("@IntervalType", intervalCode);
                if (priorityLoggerflag != 0)
                    cmd.Parameters.AddWithValue("@priorityLoggerflag", priorityLoggerflag);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                SqlConnection ConObj = new SqlConnection(sqlConnectionString);
                WriteToLogTable(ConObj, ex.Message, "Exception");
                Log.Error("InsertDataIntoAvgTable: ", ex);
                if (ConObj != null)
                {
                    ConObj.Close();
                    ConObj.Dispose();
                }
            }

        }

        private void InsertDataIntoAvgTableYear(SqlCommand cmd, DataRow row, string interval, int priorityLoggerflag, string intervalCode, string intervalValue, string sqlConnectionString)
        {
             try
            {
                int typeIdValue = 365;

                // ---------- UPDATE Query ----------
                cmd.CommandText = $@"
        -- Update existing monthly average
        UPDATE {averageTableNameYear}
        SET 
            Parametervalue = monthlyAvg.AvgValue,
            CreatedTime = GETDATE()
        FROM (
            SELECT 
                sd.StationID,
                sd.DeviceID,
                sd.ParameterID,
                CAST(DATEADD(YEAR, DATEDIFF(YEAR, 0, sd.Interval), 0) AS DATETIME) AS MonthStart,
                AVG(sd.Parametervalue) AS AvgValue
            FROM {averageTableNameMonth} sd
            JOIN {flagTableName} f ON sd.LoggerFlags = f.ID AND f.Type != 'Validation'
            WHERE 
                sd.StationID = @StationID
                AND sd.DeviceID = @DeviceID
                AND sd.ParameterID = @ParameterID
                AND f.ID = @priorityLoggerflag
                AND sd.TypeID = 43200
            GROUP BY 
                sd.StationID,
                sd.DeviceID,
                sd.ParameterID,
                CAST(DATEADD(YEAR, DATEDIFF(YEAR, 0, sd.Interval), 0) AS DATETIME)
        ) monthlyAvg
        WHERE 
            {averageTableNameYear}.StationID = monthlyAvg.StationID AND
            {averageTableNameYear}.DeviceID = monthlyAvg.DeviceID AND
            {averageTableNameYear}.ParameterID = monthlyAvg.ParameterID AND
            {averageTableNameYear}.Interval = monthlyAvg.MonthStart AND
            {averageTableNameYear}.TypeID = {typeIdValue};
    ";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
                cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
                if (priorityLoggerflag != 0)
                    cmd.Parameters.AddWithValue("@priorityLoggerflag", priorityLoggerflag);

                if (cmd.Connection.State != ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }
                cmd.ExecuteNonQuery();

                // ---------- INSERT Query ----------
                cmd.CommandText = $@"
        INSERT INTO {averageTableNameYear} (
            StationID, DeviceID, ParameterID, Parametervalue, Type, Interval, LoggerFlags, TypeID, CreatedTime, ParameterIDRef
        )
        SELECT 
            a.StationID,
            a.DeviceID,
            a.ParameterID,
            a.Parametervalue,
            a.Type,
            a.Interval,
            a.LoggerFlags,
            a.TypeID,
            GETDATE(),
            a.ParameterIDRef
        FROM (
            SELECT 
                sd.StationID,
                sd.DeviceID,
                sd.ParameterID,
                sd.ParameterIDRef,
                CAST(DATEADD(YEAR, DATEDIFF(YEAR, 0, sd.Interval), 0) AS DATETIME) AS Interval,
                AVG(sd.Parametervalue) AS Parametervalue,
                CAST(@Interval AS NVARCHAR(10)) + @IntervalType AS Type,
                {priorityLoggerflag} AS LoggerFlags,
                {typeIdValue} AS TypeID
            FROM {averageTableNameMonth} sd
            JOIN {flagTableName} f ON sd.LoggerFlags = f.ID AND f.Type != 'Validation'
            WHERE 
                sd.StationID = @StationID
                AND sd.DeviceID = @DeviceID
                AND sd.ParameterID = @ParameterID
                AND f.ID = @priorityLoggerflag
                AND sd.TypeID = 43200
            GROUP BY 
                sd.StationID,
                sd.DeviceID,
                sd.ParameterID,
                sd.ParameterIDRef,
                CAST(DATEADD(YEAR, DATEDIFF(YEAR, 0, sd.Interval), 0) AS DATETIME)
        ) a
        WHERE NOT EXISTS (
            SELECT 1 FROM {averageTableNameYear} b 
            WHERE 
                b.StationID = a.StationID AND 
                b.DeviceID = a.DeviceID AND 
                b.ParameterID = a.ParameterID AND 
                b.Interval = a.Interval AND 
                b.TypeID = a.TypeID
        );
    ";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
                cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
                cmd.Parameters.AddWithValue("@Interval", intervalValue);
                cmd.Parameters.AddWithValue("@IntervalType", intervalCode);
                if (priorityLoggerflag != 0)
                    cmd.Parameters.AddWithValue("@priorityLoggerflag", priorityLoggerflag);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                SqlConnection ConObj = new SqlConnection(sqlConnectionString);
                WriteToLogTable(ConObj, ex.Message, "Exception");
                Log.Error("InsertDataIntoAvgTable: ", ex);
                if (ConObj != null)
                {
                    ConObj.Close();
                    ConObj.Dispose();
                }
            }

        }

        private DataTable GetUpdatedRecordsFromReadingsTable(SqlCommand cmd, DataRow row, DateTime? FormatInterval)
        {
            cmd.CommandText = $"select CreatedTime from {readingTableName} where StationID = @StationID and DeviceID = @DeviceID and ParameterID = @ParameterID and UpdateStatus = 1 and CreatedTime<= @FormatInterval order by CreatedTime desc";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            cmd.Parameters.AddWithValue("@FormatInterval", FormatInterval);
            DataTable dtupdatecnt = new DataTable();
            using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
            {
                adapter.Fill(dtupdatecnt);
            }
            return dtupdatecnt;
        }
        private DataTable GetUpdatedRecordsFromReadingsTable(SqlConnection conObj)
        {
            string commandText = $"select a.StationID,a.DeviceID,a.ParameterID as ID,a.ParameterID, a.CreatedTime, b.ServerAvgInterval,b.ParameterName,b.DataSyncFrequency,c.DriverName AS ParameterDriverName from {readingTableName} a inner join {parameterTableName} b on a.StationId=b.StationId and a.DeviceId=b.DeviceId and a.ParameterID=b.ID inner join {driverTableName} c ON b.DriverID = c.ID  where a.UpdateStatus = 1 order by CreatedTime desc";
            Log.Info("Query to get the updated records from reading table: " + commandText);
            SqlCommand cmd = new SqlCommand(commandText, conObj);
            cmd.Parameters.Clear();
            DataTable dtupdatecnt = new DataTable();
            using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
            {
                adapter.Fill(dtupdatecnt);
            }
            return dtupdatecnt;
        }

        //This method is used to get the any calculated parameters configured in dmn_parameters
        private DataTable GetCalculatedParameters(SqlConnection con)
        {
            string query = $"SELECT p.*, d.DriverName AS ParameterDriverName FROM {parameterTableName} p inner join {driverTableName} d ON p.DriverID = d.ID WHERE p.IsCalculated = 1";
            Log.Info("Query to Fetch the Calculated Parameters: " + query);
            DataTable dt = new DataTable();
            using (SqlDataAdapter adapter = new SqlDataAdapter(query, con))
            {
                adapter.Fill(dt);
            }
            return dt;
        }
        //This method is used to get the conversion values for the calculated parameter name
        private string GetFormulaForParameter(SqlConnection con, string parameterName)
        {
            string query = "SELECT ConversionFactor FROM Parameter_Conversion WHERE Parameter = @Parameter";
            Log.Info("Query to Fetch the formulat for  Calculated Parameters: " + query);
            using (SqlCommand cmd = new SqlCommand(query, con))
            {
                cmd.Parameters.AddWithValue("@Parameter", parameterName);
                using (var sqlReader = cmd.ExecuteReader())
                {
                    if (sqlReader.Read())
                    {
                        string conversionFactor = sqlReader.GetString(sqlReader.GetOrdinal("ConversionFactor"));
                        Log.Info("Conversion Factor for " + parameterName + " : " + conversionFactor);
                        return conversionFactor;
                    }
                }
            }
            return string.Empty;
        }
        //This method is used to get the base parameter values for the selected calculated parameter to calculate the parameter values
        private DataTable GetParameterReadingsForSelectedPollutant(SqlConnection con, object stationId, object deviceId, object parameterId, DateTime? lastIntervalTime)
        {
            string query = "SELECT * FROM ParameterReadings WHERE StationID =@StationID and DeviceID=@DeviceID and ParameterIDRef=@ParameterID";
            if (lastIntervalTime != null)
                query = "SELECT * FROM ParameterReadings WHERE StationID =@StationID and DeviceID=@DeviceID and ParameterIDRef=@ParameterID and CreatedTime > @lastInterval";

            Log.Info("Query to fetch the parameterreadings for selected pollutant " + query);

            DataTable dt = new DataTable();
            using (SqlCommand cmd = new SqlCommand(query, con))
            {
                cmd.Parameters.AddWithValue("@StationID", stationId);
                cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                cmd.Parameters.AddWithValue("@ParameterID", parameterId);
                if (lastIntervalTime != null)
                    cmd.Parameters.AddWithValue("@lastInterval", lastIntervalTime);
                using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                {
                    adapter.Fill(dt);
                }
            }

            return dt;
        }
        DataTable GetParameterReadingsOfNoAndNo2(SqlConnection con, object stationId, object deviceId, DateTime? lastInterval)
        {
            DataTable dt = new DataTable();
            string query = $@"SELECT createdtime,MAX(CASE WHEN d.DriverName = 'no' THEN a.ParameterValue END) AS [no],MAX(CASE WHEN d.DriverName = 'no2' THEN a.ParameterValue END) AS no2,
                          MIN(a.LoggerFlags) AS loggerflags,a.StationID, a.DeviceId FROM {readingTableName} a INNER JOIN {parameterTableName} b ON a.ParameterID = b.ID INNER JOIN {driverTableName} d ON b.DriverID = d.ID
                           WHERE a.StationID = b.StationID AND a.DeviceID = b.DeviceID and a.ParameterID = b.ID and a.StationID = @StationID and b.DeviceID = @DeviceID and (b.IsCalculated != 1 OR b.IsCalculated IS NULL) and d.DriverName in ('NO', 'NO2')
                           group by CreatedTime,a.StationID,a.DeviceID order by CreatedTime desc";
            if (lastInterval != null)
            {
                query = $@"SELECT createdtime,MAX(CASE WHEN d.DriverName = 'no' THEN a.ParameterValue END) AS [no],MAX(CASE WHEN d.DriverName = 'no2' THEN a.ParameterValue END) AS no2,
                          MIN(a.LoggerFlags) AS loggerflags,a.StationID, a.DeviceId FROM {readingTableName} a INNER JOIN {parameterTableName} b ON a.ParameterID = b.ID INNER JOIN {driverTableName} d ON b.DriverID = d.ID
                           WHERE a.StationID = b.StationID AND a.DeviceID = b.DeviceID and a.ParameterID = b.ID and a.StationID = @StationID and b.DeviceID = @DeviceID and (b.IsCalculated != 1 OR b.IsCalculated IS NULL) and a.CreatedTime > @lastInterval  and d.DriverName in ('NO', 'NO2')
                           group by CreatedTime,a.StationID,a.DeviceID order by CreatedTime desc";
            }
            Log.Info("Query to fetch the parameterreadings of NO and NO2 " + query);
            using (SqlCommand cmd = new SqlCommand(query, con))
            {
                cmd.Parameters.AddWithValue("@StationID", stationId);
                cmd.Parameters.AddWithValue("@DeviceID", deviceId);
                Log.Info("Input Parameters : Station ID :" + stationId + " Device ID :" + deviceId);
                if (lastInterval != null)
                {
                    Log.Info("Interval Value :" + lastInterval);
                    cmd.Parameters.AddWithValue("@lastInterval", lastInterval);
                }
                using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                {
                    adapter.Fill(dt);
                }
            }

            return dt;

        }
        public bool InsertParameterSamplingsForCalculatedParameters(SqlConnection con)
        {
            try
            {
                DataTable dt = GetCalculatedParameters(con);
                foreach (DataRow paramrow in dt.Rows)
                {
                    DateTime? lastInterval = GetLatestIntervalRecordFromParameterTable(new SqlCommand(string.Empty, con), paramrow);


                    string parameterName = paramrow["ParameterDriverName"].ToString().Split('_')[0];
                    string conversionFactor = GetFormulaForParameter(con, parameterName);
                    if (conversionFactor == null)
                        continue;
                    if (parameterName.ToUpper() == "NOX")
                    {
                        //Get parameter readings for No and No2 for nox calculation
                        DataTable dtParamReadings = GetParameterReadingsOfNoAndNo2(con, paramrow["StationID"], paramrow["DeviceID"], lastInterval);
                        if (dtParamReadings.Rows.Count == 0)
                            continue;
                        double noConversionFactor = Convert.ToDouble(GetFormulaForParameter(con, "NO"));
                        double no2ConversionFactor = Convert.ToDouble(GetFormulaForParameter(con, "NO2"));
                        List<Parametersamplings> list = dtParamReadings.AsEnumerable()
                       .Select(row => new Parametersamplings
                       {

                           StationID = (int)paramrow["StationID"],
                           DeviceID = (int)paramrow["DeviceID"],
                           ParameterID = Convert.ToInt32(paramrow["ID"]),
                           Parametervalue = row.Field<double?>("NO") != null && row.Field<double?>("NO2") != null
                                           ? noConversionFactor * row.Field<double>("NO") + no2ConversionFactor * row.Field<double>("NO2")
                                           : (double?)null,
                           LoggerFlags = row.Field<int?>("LoggerFlags"),
                           IsLocked = null,
                           AqsMethod = null,
                           Interval = row.Field<DateTime>("CreatedTime"),
                           CreatedBy = null,
                           ModifyOn = null,
                           ModifyBy = null,
                           //UpdateStatus = row.Field<double?>("UpdateStatus"),
                           ParameterIDRef = (int)paramrow["ParameterID"],
                           StationGUID = string.Empty
                       }).ToList();
                        //bool isExists = CheckRecordsAlreadyExists(con, paramrow["StationID"], paramrow["DeviceID"], paramrow["ID"], list);
                        //if (!isExists)
                        //InsertParameterSamplings(list, con);
                        InsertParameterSamplings1(dtParamReadings, paramrow, con);

                    }
                    else
                    {
                        DataTable dtParamReadings = GetParameterReadingsForSelectedPollutant(con, paramrow["StationID"], paramrow["DeviceID"], paramrow["ParameterID"], lastInterval);
                        if (dtParamReadings.Rows.Count == 0)
                            continue;

                        List<Parametersamplings> list = dtParamReadings.AsEnumerable()
                        .Select(row => new Parametersamplings
                        {
                            StationID = row.Field<int>("StationID"),
                            DeviceID = row.Field<int>("DeviceID"),
                            ParameterID = Convert.ToInt32(paramrow["ID"]), //row.Field<int>("ID"),
                            Parametervalue = row.Field<double?>("Parametervalue") != null
                                            ? row.Field<double>("Parametervalue") * Convert.ToDouble(conversionFactor)
                                            : (double?)null,
                            LoggerFlags = row.Field<int?>("LoggerFlags"),
                            IsLocked = row.Field<bool?>("IsLocked"),
                            AqsMethod = row.Field<string>("AqsMethod"),
                            Interval = row.Field<DateTime>("CreatedTime"),
                            CreatedBy = row.Field<int?>("CreatedBy"),
                            ModifyOn = row.Field<DateTime?>("ModifyOn"),
                            ModifyBy = row.Field<int?>("ModifyBy"),
                            //UpdateStatus = row.Field<double?>("UpdateStatus"),
                            ParameterIDRef = row.Field<int>("ParameterIDRef"),
                            StationGUID = string.Empty
                        }).ToList();
                        //bool isExists = CheckRecordsAlreadyExists(con, paramrow["StationID"], paramrow["DeviceID"], paramrow["ID"], list);
                        //if (!isExists)
                        //if(list.Count > 0)
                        InsertParameterSamplings(list, con);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("error check");
                throw ex;
            }
            return true;
        }
        //this method is used to check parameter reading already exists in the central db
        bool CheckIfParameterReadingsExists(int stationId, int deviceID, int parameterId, DateTime interval, SqlConnection con)
        {
            string query = "SELECT ID FROM ParameterReadings WHERE StationId = @StationId and DeviceId=@DeviceId and ParameterId=@ParameterId and CreatedTime=@CreatedTime";
            Log.Info("Query to check parameter reading already inserted for given station id , device id , parameter id and interval:" + query);

            using (SqlCommand cmd = new SqlCommand(query, con))
            {
                cmd.Parameters.AddWithValue("@StationId", stationId);
                cmd.Parameters.AddWithValue("@DeviceId", deviceID);
                cmd.Parameters.AddWithValue("@ParameterId", parameterId);
                cmd.Parameters.AddWithValue("@CreatedTime", interval);
                Log.Info("Station Id: " + stationId + " ,Device ID: " + deviceID + " , Parameter ID: " + parameterId + " , Interval:" + interval);
                object result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    return true;
                }
                return false;
            }
        }
        //This method is used to insert parameter samplings into the central db
        public bool InsertParameterSamplings1(DataTable parameterSamplings, DataRow row1, SqlConnection con)
        {
            Log logObj = new Log();
            string insertQuery = "INSERT INTO [ParameterReadings] (StationID, DeviceID, ParameterID, Parametervalue, LoggerFlags, IsLocked, AqsMethod, " +
                                     "CreatedTime, CreatedBy, ModifyOn, ModifyBy, Alarm, ParameterIDRef, ParametervalueOrginal,LoggerFlagsOriginal) " +
                                     "VALUES (@StationID, @DeviceID, @ParameterID, @Parametervalue, @LoggerFlags, @IsLocked, @AqsMethod, " +
                                     "@CreatedTime, @CreatedBy, @ModifyOn, @ModifyBy, @Alarm, @ParameterIDRef,@ParametervalueOrginal,@LoggerFlagsOriginal)";

            Log.Info("Query to insert calculated parameter values into parameter readings table :" + insertQuery);

            using (SqlCommand cmd = new SqlCommand(insertQuery, con))
            {
                foreach (DataRow paramSampling in parameterSamplings.Rows)
                {
                    int stationId = (int)paramSampling["StationID"];
                    int parameterId = (int)row1["ID"];
                    double noConversionFactor = Convert.ToDouble(GetFormulaForParameter(con, "NO"));
                    double no2ConversionFactor = Convert.ToDouble(GetFormulaForParameter(con, "NO2"));
                    if (!CheckIfParameterReadingsExists(stationId, (int)paramSampling["DeviceID"], parameterId, (DateTime)paramSampling["CreatedTime"], con))
                    {
                        Log.Info("Reading Not Exists");
                        //var no = (double)paramSampling["NO"];
                        //  var no2 = (double)paramSampling["NO2"];



                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@StationID", stationId);
                        cmd.Parameters.AddWithValue("@DeviceID", (int)paramSampling["DeviceID"]);
                        cmd.Parameters.AddWithValue("@ParameterID", parameterId);
                        if (!paramSampling.IsNull("NO") && !paramSampling.IsNull("NO2"))
                        {
                            cmd.Parameters.AddWithValue("@Parametervalue", noConversionFactor * (double)paramSampling["NO"] + no2ConversionFactor * (double)paramSampling["NO2"]);
                        }
                        else
                        {
                            Log.Info(" stationID : " + stationId + " No value : " + paramSampling.Field<double?>("NO") ?? 0.0 + " No2 value : " + paramSampling.Field<double?>("NO2") ?? 0.0 + " Interval :" + (DateTime)paramSampling["CreatedTime"]);
                            //logObj.writeLog(" stationID : "+ stationId + " No value : " + (double)paramSampling["NO"] + " No2 value : " + (double)paramSampling["NO2"] +" Interval :"+ (DateTime)paramSampling["CreatedTime"], filePath);
                            cmd.Parameters.AddWithValue("@Parametervalue", DBNull.Value);

                        }

                        cmd.Parameters.AddWithValue("@LoggerFlags", paramSampling["LoggerFlags"] ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@IsLocked", DBNull.Value);
                        cmd.Parameters.AddWithValue("@AqsMethod", DBNull.Value);
                        cmd.Parameters.AddWithValue("@CreatedTime", paramSampling["CreatedTime"] ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@CreatedBy", DBNull.Value);
                        cmd.Parameters.AddWithValue("@ModifyOn", DBNull.Value);
                        cmd.Parameters.AddWithValue("@ModifyBy", DBNull.Value);
                        cmd.Parameters.AddWithValue("@Alarm", DBNull.Value);
                        cmd.Parameters.AddWithValue("@ParameterIDRef", row1["ParameterID"]);
                        if (!paramSampling.IsNull("NO") && !paramSampling.IsNull("NO2"))
                        {
                            cmd.Parameters.AddWithValue("@ParametervalueOrginal", noConversionFactor * (double)paramSampling["NO"] + no2ConversionFactor * (double)paramSampling["NO2"]);
                        }
                        else
                        {
                            // logObj.writeLog("No value : " + (double)paramSampling["NO"] + " No2 value : " + (double)paramSampling["NO2"] + " Interval :" + (DateTime)paramSampling["Interval"], filePath);
                            cmd.Parameters.AddWithValue("@ParametervalueOrginal", DBNull.Value);

                        }
                        cmd.Parameters.AddWithValue("@LoggerFlagsOriginal", paramSampling["LoggerFlags"] ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            return true;
        }

        public bool InsertParameterSamplings(List<Parametersamplings> parameterSamplings, SqlConnection con)
        {
            string insertQuery = "INSERT INTO [ParameterReadings] (StationID, DeviceID, ParameterID, Parametervalue, LoggerFlags, IsLocked, AqsMethod, " +
                                     "CreatedTime, CreatedBy, ModifyOn, ModifyBy, Alarm, ParameterIDRef, ParametervalueOrginal,LoggerFlagsOriginal) " +
                                     "VALUES (@StationID, @DeviceID, @ParameterID, @Parametervalue, @LoggerFlags, @IsLocked, @AqsMethod, " +
                                     "@CreatedTime, @CreatedBy, @ModifyOn, @ModifyBy, @Alarm, @ParameterIDRef,@ParametervalueOrginal,@LoggerFlagsOriginal)";

            Log.Info("Query to insert parameter sampling values into parameter readings table :" + insertQuery);

            using (SqlCommand cmd = new SqlCommand(insertQuery, con))
            {
                foreach (var paramSampling in parameterSamplings)
                {
                    int stationId = paramSampling.StationID;
                    int parameterId = paramSampling.ParameterID;
                    if (!CheckIfParameterReadingsExists(stationId, paramSampling.DeviceID, parameterId, paramSampling.Interval, con))
                    {
                        Log.Info("Parameter value not there");
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@StationID", stationId);
                        cmd.Parameters.AddWithValue("@DeviceID", paramSampling.DeviceID);
                        cmd.Parameters.AddWithValue("@ParameterID", parameterId);
                        cmd.Parameters.AddWithValue("@Parametervalue", (object)paramSampling.Parametervalue ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@LoggerFlags", (object)paramSampling.LoggerFlags ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@IsLocked", (object)paramSampling.IsLocked ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@AqsMethod", (object)paramSampling.AqsMethod ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@CreatedTime", (object)paramSampling.Interval ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@CreatedBy", (object)paramSampling.CreatedBy ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ModifyOn", (object)paramSampling.ModifyOn ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ModifyBy", (object)paramSampling.ModifyBy ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Alarm", (object)paramSampling.Alarm ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ParameterIDRef", (object)paramSampling.ParameterIDRef);
                        cmd.Parameters.AddWithValue("@ParametervalueOrginal", (object)paramSampling.Parametervalue ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@LoggerFlagsOriginal", (object)paramSampling.LoggerFlags ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                        Log.Info("Station " + stationId + " Device " + paramSampling.DeviceID + " Parameter " + parameterId + " inserted successfully.");
                    }
                }
            }

            return true;
        }
        //This method is used to check calculated parameters already inserted in the table, in order to avoid duplicates.
        bool CheckRecordsAlreadyExists(SqlConnection con, object stationId, object deviceId, object parameterid, List<Parametersamplings> list)
        {
            DataTable dtParamReadings = GetParameterReadingsForSelectedPollutant(con, stationId, deviceId, parameterid, null);
            List<Parametersamplings> list1 = dtParamReadings.AsEnumerable()
                       .Select(row => new Parametersamplings
                       {
                           StationID = row.Field<int>("StationID"),
                           DeviceID = row.Field<int>("DeviceID"),
                           ParameterID = row.Field<int>("ParameterID"),
                           Interval = row.Field<DateTime>("CreatedTime"),
                       }).ToList();

            List<Parametersamplings> list2 = list.Select(item => new Parametersamplings
            {
                StationID = item.StationID,
                DeviceID = item.DeviceID,
                ParameterID = item.ParameterID,
                Interval = item.Interval,
            }).ToList();
            bool containsAll = list2.All(item => list1.Any(i => i.StationID == item.StationID && i.DeviceID == item.DeviceID && i.ParameterID == item.ParameterID && i.Interval == item.Interval));
            return containsAll;
        }
        void CheckAndUpdateCalculatedParameters(SqlConnection conObj, int percentage, DataRow row, string interval, int priorityLoggerflag, string intervalCode, string intervalValue, object intraval)
        {
            DataTable dt = new DataTable();
            using (SqlDataAdapter adap = new SqlDataAdapter($"Select P.*, d.DriverName AS ParameterDriverName FROM {parameterTableName} p inner join {driverTableName} d ON p.DriverID = d.ID where P.StationId=@StationID and P.DeviceId=@DeviceID and P.ParameterId=@ParameterID", conObj))
            {
                adap.SelectCommand.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
                adap.SelectCommand.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
                adap.SelectCommand.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ParameterID"]));
                adap.Fill(dt);
            }
            if (dt.Rows.Count > 1) //means caluclated parameter configured for this station ,device
            {
                string parameterName = row["ParameterDriverName"].ToString();
                string conversionFactor = GetFormulaForParameter(conObj, parameterName);
                string paramValue = "NULL";
                int intMinuteMultiplier = intervalCode == "M" ? 1 : 60;
                int typeIdValue = int.Parse(intervalValue) * intMinuteMultiplier;
                if (parameterName.ToUpper() == "NOX")
                {
                    double noConversionFactor = Convert.ToDouble(GetFormulaForParameter(conObj, "NO"));
                    double no2ConversionFactor = Convert.ToDouble(GetFormulaForParameter(conObj, "NO2"));
                    using (SqlCommand cmd = new SqlCommand(string.Empty, conObj))
                    {
                        cmd.CommandText = $"UPDATE b SET b.Parametervalue = ({noConversionFactor} * a.no +{no2ConversionFactor} * a.no2),b.LoggerFlags = {priorityLoggerflag} From ( " +
                                          $"SELECT dateadd({interval}, datediff({interval}, 0, a.CreatedTime) / @Interval * @Interval, 0) Interval, avg(CASE WHEN d.DriverName = 'no' THEN a.ParameterValue END) AS[no], avg(CASE WHEN d.DriverName = 'no2' THEN a.ParameterValue END) AS no2," +
                                          $"MIN(a.LoggerFlags) AS loggerflags, a.StationID, a.DeviceId, {typeIdValue} TypeID FROM {readingTableName} a INNER JOIN {parameterTableName} b ON a.ParameterID = b.ID INNER JOIN {driverTableName} d ON b.DriverID = d.ID" +
                                          $" WHERE a.StationID = b.StationID AND a.DeviceID = b.DeviceID and a.ParameterID = b.ID and a.StationID = @StationID and b.DeviceID = @DeviceID and (b.iscalculated != 1 OR b.iscalculated IS NULL) and d.DriverName in ('NO', 'NO2') " +
                                          $" group by dateadd({interval}, datediff({interval}, 0, a.CreatedTime) / @Interval * @Interval, 0), a.StationID, a.DeviceID ) AS a " +
                                          $" JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID AND b.TypeID = a.TypeID WHERE b.TypeID = {typeIdValue} AND a.Interval = @intValue and b.ParameterID = @ParameterID1";

                        Log.Info("Query to Update the Calculated Parameter values for NOX: " + cmd.CommandText);
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(dt.Rows[0]["StationID"]));
                        cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(dt.Rows[0]["DeviceId"]));
                        cmd.Parameters.AddWithValue("@ParameterID1", Convert.ToInt32(dt.Rows[1]["ID"]));
                        cmd.Parameters.AddWithValue("@Interval", intervalValue);
                        cmd.Parameters.AddWithValue("@IntervalType", intervalCode);
                        cmd.Parameters.AddWithValue("@intValue", intraval);
                        int i = cmd.ExecuteNonQuery();
                    }

                }
                else
                {
                    if (percentage >= 75)
                        paramValue = $"AVG(sd.Parametervalue*{Convert.ToDouble(conversionFactor)})";
                    using (SqlCommand cmd = new SqlCommand(string.Empty, conObj))
                    {
                        cmd.CommandText = $"UPDATE b SET b.Parametervalue = a.Parametervalue,b.LoggerFlags = {priorityLoggerflag}" +
                                          $" FROM(SELECT StationID, DeviceID, ParameterID,dateadd({interval}, datediff({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0) Interval," +
                                          $"{paramValue} Parametervalue,{typeIdValue} TypeID,GETDATE() date FROM {readingTableName} sd JOIN {flagTableName}  f ON sd.LoggerFlags = f.ID and f.Type != 'Validation' " +
                                          $" WHERE sd.StationID = @StationID AND sd.DeviceID = @DeviceID AND sd.ParameterID = @ParameterID GROUP BY StationID, DeviceID, ParameterID, dateadd({interval}, datediff({interval}, 0, sd.CreatedTime) / @Interval * @Interval, 0)" +
                                          $" ) AS a JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID " +
                                          $" AND b.TypeID = a.TypeID WHERE b.TypeID = {typeIdValue} AND a.Interval =@intValue and b.ParameterID=@ParameterID1";

                        Log.Info("Query to Updated teh calculated parameter values for other calculated parameters : " + cmd.CommandText);
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(dt.Rows[0]["StationID"]));
                        cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(dt.Rows[0]["DeviceId"]));
                        cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(dt.Rows[0]["ID"]));

                        cmd.Parameters.AddWithValue("@ParameterID1", Convert.ToInt32(dt.Rows[1]["ID"]));

                        cmd.Parameters.AddWithValue("@Interval", intervalValue);
                        cmd.Parameters.AddWithValue("@IntervalType", intervalCode);
                        cmd.Parameters.AddWithValue("@intValue", intraval);
                        int i = cmd.ExecuteNonQuery();
                    }
                    if (parameterName == "NO" || parameterName == "NO2")
                    {
                        double noConversionFactor = Convert.ToDouble(GetFormulaForParameter(conObj, "NO"));
                        double no2ConversionFactor = Convert.ToDouble(GetFormulaForParameter(conObj, "NO2"));

                        using (SqlCommand cmd = new SqlCommand(string.Empty, conObj))
                        {
                            //To get nox_ug/m3 parameter id
                            cmd.CommandText = $"Select b.ID from {parameterTableName} b INNER JOIN {driverTableName} d ON b.DriverID = d.ID where b.StationID=@StationID and b.DeviceID=@DeviceID and b.iscalculated = 1 and d.DriverName  ='NOX'";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(dt.Rows[0]["StationID"]));
                            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(dt.Rows[0]["DeviceId"]));
                            var parameterID = cmd.ExecuteScalar();
                            //Update NOX ug/m3 if user changes no or no2
                            cmd.CommandText = $"UPDATE b SET b.Parametervalue = ({noConversionFactor} * a.no +{no2ConversionFactor} * a.no2),b.LoggerFlags = {priorityLoggerflag} From ( " +
                                              $"SELECT dateadd({interval}, datediff({interval}, 0, a.CreatedTime) / @Interval * @Interval, 0) Interval, avg(CASE WHEN d.DriverName = 'no' THEN a.ParameterValue END) AS[no], avg(CASE WHEN d.DriverName = 'no2' THEN a.ParameterValue END) AS no2," +
                                              $"MIN(a.LoggerFlags) AS loggerflags, a.StationID, a.DeviceId, {typeIdValue} TypeID FROM {readingTableName} a INNER JOIN {parameterTableName} b ON a.ParameterID = b.ID INNER JOIN {driverTableName} d ON b.DriverID = d.ID" +
                                              $" WHERE a.StationID = b.StationID AND a.DeviceID = b.DeviceID and a.ParameterID = b.ID and a.StationID = @StationID and b.DeviceID = @DeviceID and (b.iscalculated != 1 OR b.iscalculated IS NULL) and d.DriverName in ('NO', 'NO2')" +
                                              $" group by dateadd({interval}, datediff({interval}, 0, a.CreatedTime) / @Interval * @Interval, 0), a.StationID, a.DeviceID ) AS a " +
                                              $" JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID AND b.TypeID = a.TypeID WHERE b.TypeID = {typeIdValue} AND a.Interval = @intValue and b.ParameterID = @ParameterID1";

                            Log.Info("Update Query for NOX parameter : " + cmd.CommandText);
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(dt.Rows[0]["StationID"]));
                            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(dt.Rows[0]["DeviceId"]));
                            cmd.Parameters.AddWithValue("@ParameterID1", Convert.ToInt32(parameterID));  //nox_ug/m3 parameter id
                            cmd.Parameters.AddWithValue("@Interval", intervalValue);
                            cmd.Parameters.AddWithValue("@IntervalType", intervalCode);
                            cmd.Parameters.AddWithValue("@intValue", intraval);
                            int i = cmd.ExecuteNonQuery();
                            //To get NOx_ug/m3 parameter id
                            cmd.CommandText = $"Select b.ID from {parameterTableName} b INNER JOIN {driverTableName} d ON b.DriverID = d.ID where b.StationID=@StationID and b.DeviceID=@DeviceID and (b.iscalculated = 1 OR b.iscalculated IS NULL) and d.DriverName  ='NOX'";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(dt.Rows[0]["StationID"]));
                            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(dt.Rows[0]["DeviceId"]));
                            parameterID = cmd.ExecuteScalar();
                            ////Update NOX ug/m3 if user changes no or no2
                            cmd.CommandText = $"UPDATE b SET b.Parametervalue = (a.no + a.no2),b.LoggerFlags = {priorityLoggerflag} From ( " +
                                              $"SELECT dateadd({interval}, datediff({interval}, 0, a.CreatedTime) / @Interval * @Interval, 0) Interval, avg(CASE WHEN d.DriverName = 'NO' THEN a.ParameterValue END) AS[no], avg(CASE WHEN d.DriverName = 'NO2' THEN a.ParameterValue END) AS no2," +
                                              $"MIN(a.LoggerFlags) AS loggerflags, a.StationID, a.DeviceId, {typeIdValue} TypeID FROM {readingTableName} a INNER JOIN {parameterTableName} b ON a.ParameterID = b.ID INNER JOIN {driverTableName} d ON b.DriverID = d.ID" +
                                              $" WHERE a.StationID = b.StationID AND a.DeviceID = b.DeviceID and a.ParameterID = b.ID and a.StationID = @StationID and b.DeviceID = @DeviceID and (b.iscalculated != 1 OR b.iscalculated IS NULL) and d.DriverName in ('NO', 'NO2') " +
                                              $" group by dateadd({interval}, datediff({interval}, 0, a.CreatedTime) / @Interval * @Interval, 0), a.StationID, a.DeviceID ) AS a " +
                                              $" JOIN {averageTableName} b ON a.Interval = b.Interval AND a.StationID = b.StationID AND a.DeviceID = b.DeviceID AND b.TypeID = a.TypeID WHERE b.TypeID = {typeIdValue} AND a.Interval = @intValue and b.ParameterID = @ParameterID1";


                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(dt.Rows[0]["StationID"]));
                            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(dt.Rows[0]["DeviceId"]));
                            cmd.Parameters.AddWithValue("@ParameterID1", Convert.ToInt32(parameterID));  //nox_ug/m3 parameter id
                            cmd.Parameters.AddWithValue("@Interval", intervalValue);
                            cmd.Parameters.AddWithValue("@IntervalType", intervalCode);
                            cmd.Parameters.AddWithValue("@intValue", intraval);
                            i = cmd.ExecuteNonQuery();

                        }
                    }

                }
            }
        }
        public bool CalculateParameterAvgs(string sqlConnectionString)
        {
            Log.Info(sqlConnectionString);
            bool blnStatus = UpdateParameterAveragesIfAny(sqlConnectionString);
            bool blnInsertStatus = InsertParameterAvgData(sqlConnectionString, defaultInterval);
            bool blnAQIInsertStatus = InsertAQIParameterAvgData(sqlConnectionString);
            bool blnMonthAverage = InsertParameterAvgDataMonth(sqlConnectionString);
            bool blnYearAverage = InsertParameterAvgDataYear(sqlConnectionString);
            bool blstatus = blnStatus && blnInsertStatus && blnAQIInsertStatus && blnMonthAverage && blnYearAverage;
            //bool blstatus = blnAQIInsertStatus;
            if (!blstatus)
                ErrorLog.Error("There was some problem with transfer data. Please contact administrator");
            return blstatus;
        }



        public bool UpdateParameterAveragesIfAny(string sqlConnectionString)
        {
            Log logObj = new Log();
            SqlConnection conObj = new SqlConnection(sqlConnectionString);
            bool blnStatus = false;
            try
            {
                if (conObj.State != ConnectionState.Open)
                {
                    conObj.Open();
                }
                SqlCommand cmd7 = new SqlCommand(string.Empty, conObj);
                SqlCommand cmd4 = new SqlCommand(string.Empty, conObj);
                SqlCommand cmd9 = new SqlCommand(string.Empty, conObj);
                int intServerInterval = Convert.ToInt32(defaultInterval);
                DataTable dtupdatecnt = GetUpdatedRecordsFromReadingsTable(conObj);
                if (dtupdatecnt.Rows.Count > 0)
                {
                    //LogObj.writeLog("records found with update status", filePath);
                    foreach (DataRow row in dtupdatecnt.Rows)
                    {
                        var Interval = row["ServerAvgInterval"].ToString();
                        var IntervalArray = Interval.Split(',');
                        //for data avg interval
                        intServerInterval = row["DataSyncFrequency"] != DBNull.Value ? Convert.ToInt32(row["DataSyncFrequency"]) : Convert.ToInt32(defaultInterval); ;

                        for (var i = 0; i < IntervalArray.Length; i++) // loop the intervals in dmn parameters
                        {
                            var IntervalType = IntervalArray[i].Split('-');
                            string interval = IntervalType[1] == "M" ? "MINUTE" : "HOUR";
                            int PtypeID = IntervalType[1] == "M" ? int.Parse(IntervalType[0]) : int.Parse(IntervalType[0]) * 60;
                            //it is for data avilable or not
                            if (PtypeID < intServerInterval)
                            {
                                continue;
                            }
                            int PriorityLoggerflag1 = GetHighPriorityLoggerFlagForUpdateRecords(cmd7, row, IntervalType[0], interval, row["CreatedTime"]);
                            DataTable udt = GetValidRecordsCountForUpdationeachInterval(cmd4, row, IntervalType[0], interval, row["CreatedTime"]);
                            if (udt.Rows.Count > 0)
                            {
                                foreach (DataRow crow in udt.Rows)
                                {
                                    int validateRecCount = (int.Parse(crow["cnt"].ToString()) * intServerInterval);
                                    int percentage = IntervalType[1] == "M" ? (validateRecCount * 100) / int.Parse(IntervalType[0]) : ((validateRecCount * 100) / (int.Parse(IntervalType[0]) * 60));
                                    UpdateDataIntoAvgTable(cmd9, percentage, row, interval, PriorityLoggerflag1, IntervalType[1], IntervalType[0], crow["Interval"]);
                                    CheckAndUpdateCalculatedParameters(conObj, percentage, row, interval, PriorityLoggerflag1, IntervalType[1], IntervalType[0], crow["Interval"]);
                                }
                            }
                            else
                            {
                                UpdateDataIntoAvgTable(cmd9, 0, row, interval, PriorityLoggerflag1, IntervalType[1], IntervalType[0], row["CreatedTime"]);
                                CheckAndUpdateCalculatedParameters(conObj, 0, row, interval, PriorityLoggerflag1, IntervalType[1], IntervalType[0], row["CreatedTime"]);
                            }
                        }
                        UpdateReadingsTableRowStatus(new SqlCommand(string.Empty, conObj), row);
                    }
                }
                blnStatus = true;
            }
            catch (Exception ex)
            {
                //logObj.writeLog(ex.Message + "-" + ex.StackTrace + " : " + DateTime.Now.ToString(), filePath);
                ErrorLog.Error("", ex);
                if (conObj.State == ConnectionState.Open)
                {
                    WriteToLogTable(conObj, ex.Message, "Exception");

                }
            }
            finally
            {
                if (conObj != null)
                {
                    conObj.Close();
                    conObj.Dispose();
                }
            }
            return blnStatus;
        }


        public bool InsertParameterAvgData(string sqlConnectionString, string defaultInterval)
        {
            Log logObj = new Log();
            bool blnStatus = false;
            SqlConnection ConObj = new SqlConnection(sqlConnectionString);
            try
            {
                if (ConObj.State != ConnectionState.Open)
                {
                    ConObj.Open();
                }
                InsertParameterSamplingsForCalculatedParameters(ConObj);
                // SqlCommand cmd = new SqlCommand($"select  * from {parameterTableName}", ConObj);
                SqlCommand cmd = new SqlCommand($"SELECT p.*, d.DriverName AS ParameterDriverName FROM {parameterTableName} p inner join {driverTableName} d ON p.DriverID = d.ID where d.DriverName != 'AQI Index'", ConObj);

                DataTable dt = new DataTable();
                SqlDataAdapter adapter = new SqlDataAdapter(cmd);
                adapter.Fill(dt);
                SqlCommand cmd1 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd2 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd3 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd4 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd5 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd6 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd7 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd8 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd9 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd10 = new SqlCommand(string.Empty, ConObj);
                int intServerInterval = Convert.ToInt32(defaultInterval); //here 15 means in parameterreadings table 15 minutes data is there. so we are multiplying total records with 15

                foreach (DataRow row in dt.Rows)
                {
                    var Interval = row["ServerAvgInterval"].ToString();
                    var IntervalArray = Interval.Split(',');
                    //for data avg interval
                    intServerInterval = row["DataSyncFrequency"] != DBNull.Value ? Convert.ToInt32(row["DataSyncFrequency"]) : Convert.ToInt32(defaultInterval);

                    for (var i = 0; i < IntervalArray.Length; i++) // loop the intervals in dmn parameters
                    {
                        var IntervalType = IntervalArray[i].Split('-');
                        string interval = IntervalType[1] == "M" ? "MINUTE" : "HOUR";
                        int PtypeID = IntervalType[1] == "M" ? int.Parse(IntervalType[0]) : int.Parse(IntervalType[0]) * 60;
                        //it is for data avilable
                        if (PtypeID < intServerInterval)
                        {
                            continue;
                        }
                        //to get the latest interval record from avearge table to start the process from that record
                        DateTime? FormatInterval = GetLatestIntervalRecordFromAvgTable(cmd6, row, PtypeID);

                        //Get Total number of records for each interval
                        DataTable dtcnt = GetRecordCountForEachInterval(cmd5, row, FormatInterval, interval, IntervalType[0]);

                        // LogObj.writeLog("records count:" + dtcnt.Rows.Count,filePath);
                        if (dtcnt.Rows.Count > 0)
                        {
                            foreach (DataRow row2 in dtcnt.Rows)
                            {
                                string totalRecordCount = row2["TotReccnt"].ToString();
                                totalRecordCount = (int.Parse(totalRecordCount) * intServerInterval).ToString();
                                //if (totalRecordCount == PtypeID.ToString())//if totalrecords and pTypeId are equal
                                //{
                                //To check the latest record count
                                if (dtcnt.Rows.IndexOf(row2) == (dtcnt.Rows.Count - 1) && totalRecordCount != PtypeID.ToString())
                                {
                                    // Skip this iteration
                                    continue;
                                }
                                DateTime startTime = Convert.ToDateTime(row2["interval"]);
                                TimeSpan elapsedTime = DateTime.Now - startTime;
                                bool isIntervalComplete = IntervalType[1] == "M"
                                    ? elapsedTime.TotalMinutes >= int.Parse(IntervalType[0])
                                    : elapsedTime.TotalHours >= int.Parse(IntervalType[0]);

                                if (isIntervalComplete)
                                {
                                    //command to text the highest priority logger value by joining dmn parameters table and dmn flag table.
                                    int PriorityLoggerflag = GetHighPriorityLoggerFlag(cmd7, row, IntervalType[0], interval, row2["interval"]);
                                    DataTable dt1 = GetValidRecordsCountForeachInterval(cmd4, row, IntervalType[0], interval, row2["interval"]);
                                    if (dt1.Rows.Count > 0)
                                    {
                                        foreach (DataRow row1 in dt1.Rows)
                                        {
                                            int validateRecCount = (int.Parse(row1["cnt"].ToString()) * intServerInterval);
                                            int percentage = IntervalType[1] == "M" ? (validateRecCount * 100) / int.Parse(IntervalType[0]) : ((validateRecCount * 100) / (int.Parse(IntervalType[0]) * 60)); ;
                                            InsertDataIntoAvgTable(cmd1, percentage, row, interval, PriorityLoggerflag, IntervalType[1], IntervalType[0], row1["Interval"]);
                                        }
                                    }
                                    else // if there are not valid records insert nulls in that interval
                                    {
                                        InsertDataIntoAvgTable(cmd1, 0, row, interval, PriorityLoggerflag, IntervalType[1], IntervalType[0], row2["Interval"]);
                                    }
                                }
                            }
                        }
                    }
                }
                //Write to LogTable
                WriteToLogTable(ConObj, "Parmeter inserted", "InsertParameter");
                blnStatus = true;
            }
            catch (Exception ex)
            {
                //logObj.writeLog(ex.Message + "-" + ex.StackTrace + " : " + DateTime.Now.ToString(), filePath);
                ErrorLog.Error("An error occured in InsertParameterAvgData", ex);
                if (ConObj.State == ConnectionState.Open)
                {
                    WriteToLogTable(ConObj, ex.Message, "Exception");
                }
            }
            finally
            {
                if (ConObj != null)
                {
                    ConObj.Close();
                    ConObj.Dispose();
                }
            }
            return blnStatus;
        }

        public bool InsertParameterAvgDataMonth(string sqlConnectionString)
        {
            Log logObj = new Log();
            bool blnStatus = false;
            SqlConnection ConObj = new SqlConnection(sqlConnectionString);

            try
            {
                if (ConObj.State != ConnectionState.Open)
                {
                    ConObj.Open();
                }
                SqlCommand cmd = new SqlCommand($"SELECT p.*, d.DriverName AS ParameterDriverName FROM {parameterTableName} p inner join {driverTableName} d ON p.DriverID = d.ID", ConObj);
                SqlCommand cmd1 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd2 = new SqlCommand(string.Empty, ConObj);
                DataTable dt = new DataTable();
                SqlDataAdapter adapter = new SqlDataAdapter(cmd);
                adapter.Fill(dt);
                string interval = "Month";
                string IntervalType = "-MO";
                string intervalValue = "43200";
                foreach (DataRow row in dt.Rows)
                {
                    InsertDataIntoAvgTableMonth(cmd2, row, interval, 1, IntervalType, intervalValue, sqlConnectionString);
                }
                blnStatus = true;

            }
            catch (Exception ex)
            {
                //logObj.writeLog(ex.Message + "-" + ex.StackTrace + " : " + DateTime.Now.ToString(), filePath);
                ErrorLog.Error("An error occured in InsertParameterAvgData", ex);
                if (ConObj.State == ConnectionState.Open)
                {
                    WriteToLogTable(ConObj, ex.Message, "Exception Internal");
                }
            }
            finally
            {
                if (ConObj != null)
                {
                    ConObj.Close();
                    ConObj.Dispose();
                }
            }
            return blnStatus;
        }

        public bool InsertParameterAvgDataYear(string sqlConnectionString)
        {
            Log logObj = new Log();
            bool blnStatus = false;
            SqlConnection ConObj = new SqlConnection(sqlConnectionString);

            try
            {
                if (ConObj.State != ConnectionState.Open)
                {
                    ConObj.Open();
                }
                SqlCommand cmd = new SqlCommand($"SELECT p.*, d.DriverName AS ParameterDriverName FROM {parameterTableName} p inner join {driverTableName} d ON p.DriverID = d.ID", ConObj);
                SqlCommand cmd1 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd2 = new SqlCommand(string.Empty, ConObj);
                DataTable dt = new DataTable();
                SqlDataAdapter adapter = new SqlDataAdapter(cmd);
                adapter.Fill(dt);
                string interval = "Year";
                string IntervalType = "-Y";
                string intervalValue = "365";
                foreach (DataRow row in dt.Rows)
                {
                    InsertDataIntoAvgTableYear(cmd2, row, interval, 1, IntervalType, intervalValue, sqlConnectionString);
                }
                blnStatus = true;

            }
            catch (Exception ex)
            {
                //logObj.writeLog(ex.Message + "-" + ex.StackTrace + " : " + DateTime.Now.ToString(), filePath);
                ErrorLog.Error("An error occured in InsertParameterAvgData", ex);
                if (ConObj.State == ConnectionState.Open)
                {
                    WriteToLogTable(ConObj, ex.Message, "Exception Internal");
                }
            }
            finally
            {
                if (ConObj != null)
                {
                    ConObj.Close();
                    ConObj.Dispose();
                }
            }
            return blnStatus;
        }

        public bool InsertAQIParameterAvgData(string sqlConnectionString)
        {
            Log logObj = new Log();
            bool blnStatus = false;
            SqlConnection ConObj = new SqlConnection(sqlConnectionString);
            try
            {
                if (ConObj.State != ConnectionState.Open)
                {
                    ConObj.Open();
                }
                SqlCommand cmd = new SqlCommand($"SELECT p.*, d.DriverName AS ParameterDriverName FROM {parameterTableName} p inner join {driverTableName} d ON p.DriverID = d.ID where d.DriverName='AQI Index'", ConObj);
                DataTable dt = new DataTable();
                SqlDataAdapter adapter = new SqlDataAdapter(cmd);
                adapter.Fill(dt);
                SqlCommand cmd1 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd2 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd3 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd4 = new SqlCommand(string.Empty, ConObj);
                SqlCommand cmd5 = new SqlCommand(string.Empty, ConObj);
                int intServerInterval = Convert.ToInt32(defaultInterval); //here 15 means in parameterreadings table 15 minutes data is there. so we are multiplying total records with 15

                foreach (DataRow row in dt.Rows)
                {
                    var Interval = row["ServerAvgInterval"].ToString();
                    var IntervalArray = Interval.Split(',');
                    //for data avg interval
                    intServerInterval = row["DataSyncFrequency"] != DBNull.Value ? Convert.ToInt32(row["DataSyncFrequency"]) : Convert.ToInt32(defaultInterval);
                    int StationID = Convert.ToInt32(row["StationID"]);
                    for (var i = 0; i < IntervalArray.Length; i++) // loop the intervals in dmn parameters
                    {
                        var IntervalType = IntervalArray[i].Split('-');
                        string interval = IntervalType[1] == "M" ? "MINUTE" : "HOUR";
                        int PtypeID = IntervalType[1] == "M" ? int.Parse(IntervalType[0]) : int.Parse(IntervalType[0]) * 60;
                        //it is for data avilable
                        if (PtypeID < intServerInterval)
                        {
                            continue;
                        }
                        //to get the latest interval record from avearge table to start the process from that record
                        DateTime? FormatInterval = GetLatestIntervalRecordFromAvgTable(cmd2, row, PtypeID);
                        if (PtypeID == 60)
                        {
                            //Get Total number of records for each interval
                            DataTable dtcnt = GetRecordCountForEachIntervalAQI(cmd3, row, FormatInterval, interval, IntervalType[0], PtypeID);
                            if (dtcnt.Rows.Count > 0)
                            {
                                foreach (DataRow row2 in dtcnt.Rows)
                                {

                                    DateTime startTime = Convert.ToDateTime(row2["interval"]);
                                    TimeSpan elapsedTime = DateTime.Now - startTime;
                                    bool isIntervalComplete = IntervalType[1] == "M"
                                        ? elapsedTime.TotalMinutes >= int.Parse(IntervalType[0])
                                        : elapsedTime.TotalHours >= int.Parse(IntervalType[0]);

                                    if (isIntervalComplete)
                                    {
                                        double? pm10 = null;
                                        double? o3 = null;
                                        double? so2 = null;
                                        double? no2 = null;
                                        double? co = null;
                                        double? pm25 = null;
                                        double? aqipm10 = null;
                                        double? aqio3 = null;
                                        double? aqiso2 = null;
                                        double? aqino2 = null;
                                        double? aqico = null;
                                        double? aqipm25 = null;
                                        DataTable Parametervalues = GetParametervaluesForEachIntervalAQI(cmd4, row2, PtypeID);
                                        DataRow Valuesrow = Parametervalues.Rows[0];  // Assuming you want the first row

                                        // Check if each column exists before trying to parse
                                        pm10 = TryParseNullableDouble(Parametervalues, "PM10");
                                        o3 = TryParseNullableDouble(Parametervalues, "O3");
                                        so2 = TryParseNullableDouble(Parametervalues, "SO2");
                                        no2 = TryParseNullableDouble(Parametervalues, "NO2");
                                        co = TryParseNullableDouble(Parametervalues, "CO");
                                        pm25 = TryParseNullableDouble(Parametervalues, "PM2.5");
                                        double? eightHourO3AQIValue;
                                        double? oneHourO3AQIValue;

                                        if (o3 <= 200)
                                        {
                                            aqio3 = CalculateEightHoursRollingAverageAQI(StationID, startTime, "8_O3", PtypeID, ConObj);
                                        }
                                        else if (o3 > 200 && o3 <= 392)
                                        {
                                            eightHourO3AQIValue = CalculateEightHoursRollingAverageAQI(StationID, startTime, "8_O3", PtypeID, ConObj);

                                            oneHourO3AQIValue = CalculatePollutantAQIValues(o3, "1_O3");

                                            if (eightHourO3AQIValue > oneHourO3AQIValue)
                                            {
                                                aqio3 = eightHourO3AQIValue;
                                            }
                                            else
                                            {
                                                aqio3 = oneHourO3AQIValue;
                                            }
                                        }
                                        else if (o3 > 392)
                                        {
                                            aqio3 = CalculatePollutantAQIValues(o3, "1_O3");
                                        }

                                        aqico = CalculateEightHoursRollingAverageAQI(StationID, startTime, "8_CO", PtypeID, ConObj);
                                        aqino2 = CalculatePollutantAQIValues(no2, "1_NO2");
                                        aqipm10 = CalculateTwentryFourHoursRollingAverage(StationID, startTime, "24_PM10", PtypeID, ConObj);
                                        aqipm25 = CalculateTwentryFourHoursRollingAverage(StationID, startTime, "24_PM2.5", PtypeID, ConObj);

                                        if (so2 <= 797)
                                        {
                                            aqiso2 = CalculatePollutantAQIValues(so2, "1_SO2");
                                        }
                                        else
                                        {
                                            aqiso2 = CalculateTwentryFourHoursRollingAverage(StationID, startTime, "24_SO2", PtypeID, ConObj);
                                        }

                                        double? aqi = null;

                                        // Check if all values are null
                                        if (aqipm10 != null || aqio3 != null || aqiso2 != null || aqino2 != null || aqico != null || aqipm25 != null)
                                        {
                                            // Set initial value for aqi as the first non-null value
                                            aqi = aqipm10 ?? aqio3 ?? aqiso2 ?? aqino2 ?? aqico ?? aqipm25;

                                            // Compare each value and update aqi to the maximum
                                            if (aqipm10 != null && aqipm10 > aqi) aqi = aqipm10;
                                            if (aqio3 != null && aqio3 > aqi) aqi = aqio3;
                                            if (aqiso2 != null && aqiso2 > aqi) aqi = aqiso2;
                                            if (aqino2 != null && aqino2 > aqi) aqi = aqino2;
                                            if (aqico != null && aqico > aqi) aqi = aqico;
                                            if (aqipm25 != null && aqipm25 > aqi) aqi = aqipm25;
                                        }
                                        InsertAQI(ConObj, row, startTime, PtypeID, aqi, StationID);

                                    }
                                }
                            }
                        }
                        else
                        {
                            //Get Total number of records for each interval
                            DataTable dtcnt = GetRecordCountForEachIntervalAQI1(cmd3, row, FormatInterval, interval, IntervalType[0], PtypeID);
                            if (dtcnt.Rows.Count > 0)
                            {
                                foreach (DataRow row2 in dtcnt.Rows)
                                {
                                    string totalRecordCount = row2["TotReccnt"].ToString();
                                    totalRecordCount = (int.Parse(totalRecordCount) * intServerInterval).ToString();
                                    //if (totalRecordCount == PtypeID.ToString())//if totalrecords and pTypeId are equal
                                    //{
                                    //To check the latest record count
                                    if (dtcnt.Rows.IndexOf(row2) == (dtcnt.Rows.Count - 1) && totalRecordCount != PtypeID.ToString())
                                    {
                                        // Skip this iteration
                                        continue;
                                    }
                                    DateTime startTime = Convert.ToDateTime(row2["interval"]);
                                    TimeSpan elapsedTime = DateTime.Now - startTime;
                                    bool isIntervalComplete = IntervalType[1] == "M"
                                        ? elapsedTime.TotalMinutes >= int.Parse(IntervalType[0])
                                        : elapsedTime.TotalHours >= int.Parse(IntervalType[0]);

                                    if (isIntervalComplete)
                                    {
                                        double? parameterAvg;
                                        if (!Double.TryParse(row2["Parameteravg"]?.ToString(), out double tempValue))
                                        {
                                            parameterAvg = null; // Set to null if parsing fails
                                        }
                                        else
                                        {
                                            parameterAvg = tempValue; // Set to parsed value if successful
                                        }
                                        InsertAQI(ConObj, row, startTime, PtypeID, parameterAvg, StationID);
                                    }
                                }
                            }


                        }

                    }
                }
                blnStatus = true;
            }
            catch (Exception ex)
            {
                //logObj.writeLog(ex.Message + "-" + ex.StackTrace + " : " + DateTime.Now.ToString(), filePath);
                ErrorLog.Error("An error occured in InsertAQIParameterAvgData", ex);
                if (ConObj.State == ConnectionState.Open)
                {
                    WriteToLogTable(ConObj, ex.Message, "Exception");
                }
            }
            finally
            {
                if (ConObj != null)
                {
                    ConObj.Close();
                    ConObj.Dispose();
                }
            }
            return blnStatus;
        }

        private void InsertAQI(SqlConnection ConObj, DataRow row, DateTime startTime, int PtypeID, double? aqi, int StationID)
        {
            try
            {
                string query = $@"INSERT INTO {averageTableName} " +
                  "(StationID, DeviceID, ParameterIDRef, Parametervalue, Interval,CreatedTime, LoggerFlags, TypeID, ParameterID) " +
                  "VALUES (@StationID, @DeviceID, @ParameterIDRef, @Parametervalue, @Interval,@CreatedTime, @LoggerFlags, @TypeID, @ParameterID)";

                using (SqlCommand cmd5 = new SqlCommand(query, ConObj))
                {
                    // Add parameters to the command
                    cmd5.Parameters.AddWithValue("@StationID", StationID); // Replace 'startTime' with the actual variable if it's defined elsewhere
                    cmd5.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceID"]));
                    cmd5.Parameters.AddWithValue("@ParameterIDRef", Convert.ToInt32(row["ParameterID"]));
                    cmd5.Parameters.AddWithValue("@Parametervalue", aqi ?? (object)DBNull.Value);
                    cmd5.Parameters.AddWithValue("@Interval", startTime);
                    cmd5.Parameters.AddWithValue("@CreatedTime", DateTime.Now);
                    cmd5.Parameters.AddWithValue("@LoggerFlags", 1);
                    cmd5.Parameters.AddWithValue("@TypeID", PtypeID);
                    cmd5.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));

                    // Execute the command
                    cmd5.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Error("Error in InsertAQI", ex);
            }
        }
        private double? TryParseNullableDouble(DataTable dt, string columnName)
        {
            string filterExpression = $@"DriverName = '{columnName}'"; // Define the filter expression
            DataRow[] filteredRows = dt.Select(filterExpression);
            if (filteredRows.Length > 0)
            {

                if (filteredRows[0]["DriverName"].ToString() == columnName && filteredRows[0]["ConvertedParameterValue"] != DBNull.Value)
                {
                    if (double.TryParse(filteredRows[0]["ConvertedParameterValue"].ToString(), out double result))
                    {
                        return result;
                    }
                }
            }
            return null;
        }
        public double? CalculatePollutantAQIValues(double? pollutantvalue, string pollutantname)
        {
            if (pollutantvalue == null)
            {
                return pollutantvalue;
            }
            double? pollutantaqivalue = 0.0;
            if (pollutantname == "8_O3")
            {
                if (pollutantvalue >= 0 && pollutantvalue <= 100.5)
                {
                    pollutantaqivalue = (50.0 - 0) / (100.0 - 0) * (pollutantvalue - 0) + 0;
                }
                else if (pollutantvalue > 100.5 && pollutantvalue <= 120.5)
                {
                    pollutantaqivalue = (100.0 - 51.0) / (120.0 - 101.0) * (pollutantvalue - 101.0) + 51;
                }
                else if (pollutantvalue > 120.5 && pollutantvalue <= 167.5)
                {
                    pollutantaqivalue = (150.0 - 101.0) / (167.0 - 121.0) * (pollutantvalue - 121.0) + 101;
                }
                else if (pollutantvalue > 167.5 && pollutantvalue <= 206.5)
                {
                    pollutantaqivalue = (200.0 - 151.0) / (206.0 - 168.0) * (pollutantvalue - 168.0) + 151;
                }
                else if (pollutantvalue > 206.5)
                {
                    pollutantaqivalue = (300.0 - 201.0) / (392.0 - 207.0) * (pollutantvalue - 207.0) + 201;
                }
            }
            else if (pollutantname == "1_O3")
            {
                if (pollutantvalue >= 200 && pollutantvalue <= 322.5)
                {
                    pollutantaqivalue = (150.0 - 101.0) / (322.0 - 200.0) * (pollutantvalue - 200.0) + 101;
                }
                else if (pollutantvalue > 322.5 && pollutantvalue <= 400.5)
                {
                    pollutantaqivalue = (200.0 - 151.0) / (400.0 - 323.0) * (pollutantvalue - 323.0) + 151;
                }
                else if (pollutantvalue > 400.5 && pollutantvalue <= 792.5)
                {
                    pollutantaqivalue = (300.0 - 201.0) / (792.0 - 401.0) * (pollutantvalue - 401.0) + 201;
                }
                else if (pollutantvalue > 792.5)
                {
                    pollutantaqivalue = (500.0 - 301.0) / (1184.0 - 793.0) * (pollutantvalue - 793.0) + 301;
                }
            }
            else if (pollutantname == "8_CO")
            {
                if (pollutantvalue >= 0.0 && pollutantvalue <= 5.4)
                {
                    pollutantaqivalue = (50.0 - 0) / (5.4 - 0.0) * (pollutantvalue - 0.0) + 0;
                }
                else if (pollutantvalue > 5.4 && pollutantvalue <= 10.4)
                {
                    pollutantaqivalue = (100.0 - 51.0) / (10.4 - 5.5) * (pollutantvalue - 5.5) + 51;
                }
                else if (pollutantvalue > 10.4 && pollutantvalue <= 14.4)
                {
                    pollutantaqivalue = (150.0 - 101.0) / (14.4 - 10.5) * (pollutantvalue - 10.5) + 101;
                }
                else if (pollutantvalue > 14.4 && pollutantvalue <= 17.9)
                {
                    pollutantaqivalue = (200.0 - 151.0) / (17.9 - 14.5) * (pollutantvalue - 14.5) + 151;
                }
                else if (pollutantvalue > 17.9 && pollutantvalue <= 35.4)
                {
                    pollutantaqivalue = (300.0 - 201.0) / (35.4 - 18.0) * (pollutantvalue - 18.0) + 201;
                }
                else if (pollutantvalue > 35.4)
                {
                    pollutantaqivalue = (500.0 - 301.0) / (58.4 - 35.5) * (pollutantvalue - 35.5) + 301;
                }
            }
            else if (pollutantname == "1_SO2")
            {
                if (pollutantvalue >= 0 && pollutantvalue <= 92.5)
                {
                    pollutantaqivalue = (50.0 - 0.0) / (92.0 - 0.0) * (pollutantvalue - 0) + 0;
                }
                else if (pollutantvalue > 92.5 && pollutantvalue <= 350.5)
                {
                    pollutantaqivalue = (100.0 - 51.0) / (350.0 - 93.0) * (pollutantvalue - 93) + 51;
                }
                else if (pollutantvalue > 350.5 && pollutantvalue <= 485.5)
                {
                    pollutantaqivalue = (150.0 - 101.0) / (485.0 - 351.0) * (pollutantvalue - 351) + 101;
                }
                else if (pollutantvalue > 485.5)
                {
                    pollutantaqivalue = (200.0 - 151.0) / (797.0 - 486.0) * (pollutantvalue - 486) + 151;
                }
            }
            else if (pollutantname == "24_SO2")
            {
                if (pollutantvalue > 797 && pollutantvalue <= 1583.5)
                {
                    pollutantaqivalue = (300.0 - 201.0) / (1583.0 - 798.0) * (pollutantvalue - 798) + 201;
                }
                else if (pollutantvalue > 1583.5)
                {
                    pollutantaqivalue = (500.0 - 301.0) / (2631.0 - 1584.0) * (pollutantvalue - 1584) + 301;
                }

            }
            else if (pollutantname == "1_NO2")
            {
                if (pollutantvalue >= 0 && pollutantvalue <= 100.5)
                {
                    pollutantaqivalue = (50.0 - 0) / (100.0 - 0) * (pollutantvalue - 0) + 0;
                }
                else if (pollutantvalue > 100.5 && pollutantvalue <= 400.5)
                {
                    pollutantaqivalue = (100.0 - 51.0) / (400.0 - 101.0) * (pollutantvalue - 101) + 51;
                }
                else if (pollutantvalue > 400.5 && pollutantvalue <= 677.5)
                {
                    pollutantaqivalue = (150.0 - 101.0) / (677.0 - 401.0) * (pollutantvalue - 401) + 101;
                }
                else if (pollutantvalue > 677.5 && pollutantvalue <= 1221.5)
                {
                    pollutantaqivalue = (200.0 - 151.0) / (1221.0 - 678.0) * (pollutantvalue - 678) + 151;
                }
                else if (pollutantvalue > 1221.5 && pollutantvalue <= 2349.5)
                {
                    pollutantaqivalue = (300.0 - 201.0) / (2349.0 - 1222.0) * (pollutantvalue - 1222) + 201;
                }
                //else if (pollutantvalue > 2349.5 && pollutantvalue <= 3853)
                else if (pollutantvalue > 2349.5)
                {
                    pollutantaqivalue = (500.0 - 301.0) / (3853.0 - 2350.0) * (pollutantvalue - 2350) + 301;
                }
            }
            else if (pollutantname == "24_PM10")
            {
                if (pollutantvalue >= 0 && pollutantvalue <= 75.5)
                {
                    pollutantaqivalue = (50.0 - 0) / (75.0 - 0) * (pollutantvalue - 0) + 0;
                }
                else if (pollutantvalue > 75.5 && pollutantvalue <= 150.5)
                {
                    pollutantaqivalue = (100.0 - 51.0) / (150.0 - 76.0) * (pollutantvalue - 76) + 51;
                }
                else if (pollutantvalue > 150.5 && pollutantvalue <= 250.5)
                {
                    pollutantaqivalue = (150.0 - 101.0) / (250.0 - 151.0) * (pollutantvalue - 151) + 101;
                }
                else if (pollutantvalue > 250.5 && pollutantvalue <= 350.5)
                {
                    pollutantaqivalue = (200.0 - 151.0) / (350.0 - 251.0) * (pollutantvalue - 251) + 151;
                }
                else if (pollutantvalue > 350.5 && pollutantvalue <= 420.5)
                {
                    pollutantaqivalue = (300.0 - 201.0) / (420.0 - 351.0) * (pollutantvalue - 351) + 201;
                }
                //else if (pollutantvalue > 420.5 && pollutantvalue <= 600)
                else if (pollutantvalue > 420.5)
                {
                    pollutantaqivalue = (500.0 - 301.0) / (600.0 - 421.0) * (pollutantvalue - 421) + 301;
                }
            }
            else if (pollutantname == "24_PM2.5")
            {
                if (pollutantvalue >= 0.0 && pollutantvalue <= 50.4)
                {
                    pollutantaqivalue = (50.0 - 0) / (50.4 - 0.0) * (pollutantvalue - 0.0) + 0;
                }
                else if (pollutantvalue > 50.4 && pollutantvalue <= 60.4)
                {
                    pollutantaqivalue = (100.0 - 51.0) / (60.4 - 50.5) * (pollutantvalue - 50.5) + 51;
                }
                else if (pollutantvalue > 60.4 && pollutantvalue <= 75.4)
                {
                    pollutantaqivalue = (150.0 - 101.0) / (75.4 - 60.5) * (pollutantvalue - 60.5) + 101;
                }
                else if (pollutantvalue > 75.4 && pollutantvalue <= 150.4)
                {
                    pollutantaqivalue = (200.0 - 151.0) / (150.4 - 75.5) * (pollutantvalue - 75.5) + 151;
                }
                else if (pollutantvalue > 150.4 && pollutantvalue <= 250.4)
                {
                    pollutantaqivalue = (300.0 - 201.0) / (250.4 - 150.5) * (pollutantvalue - 150.5) + 201;
                }
                //else if (pollutantvalue > 250.4 && pollutantvalue <= 500.4)
                else if (pollutantvalue > 250.4)
                {
                    pollutantaqivalue = (500.0 - 301.0) / (500.4 - 250.4) * (pollutantvalue - 250.4) + 301;
                }
            }
            return pollutantaqivalue;
        }

        public double? CalculateEightHoursRollingAverageAQI(int StationID, DateTime Interval, string pollutantName, int TypeID, SqlConnection ConObj)
        {
            double? eightHourAvgValue = 0.0;
            double? AQIValue = 0.0;
            try
            {
                string paramter = pollutantName.Split('_')[1];
                SqlCommand cmd = new SqlCommand(string.Empty, ConObj);
                DataTable dtpollutant = new DataTable();
                DateTime Interval2 = Interval.AddHours(-8);
                cmd.CommandText = $@"
           SELECT d.DriverName,pa.ParameterValue * COALESCE(CASE WHEN u.UnitName <> pc.SecondaryUnit THEN TRY_CAST(pc.ConversionFactor AS FLOAT)
                ELSE 1 END, 1) AS ConvertedParameterValue FROM  
        {averageTableName} pa INNER JOIN {parameterTableName} dp ON pa.ParameterID = dp.ID INNER JOIN {driverTableName} d ON dp.DriverID = d.ID 
        INNER JOIN ReportedUnits u ON dp.UnitID = u.ID LEFT JOIN Parameter_Conversion pc ON d.DriverName = pc.Parameter 
    WHERE  pa.StationID = @StationID AND pa.Interval <= @Interval AND pa.Interval > @Interval2 AND pa.TypeID = @TypeID AND d.DriverName = @DriverName order by pa.Interval desc";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", StationID);
                cmd.Parameters.AddWithValue("@Interval", Interval);
                cmd.Parameters.AddWithValue("@Interval2", Interval2);
                cmd.Parameters.AddWithValue("@TypeID", TypeID);
                cmd.Parameters.AddWithValue("@DriverName", paramter);

                Log.Info("Query To fetch the number of records for each Interval: " + cmd.CommandText);

                using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                {
                    adapter.Fill(dtpollutant);
                }
                int dividedindex = 0;
                double? rollingPollutantAvg = null;
                if (dtpollutant.Rows.Count == 0)
                {
                    AQIValue = null;
                }
                else
                {
                    for (int j = 0; j < dtpollutant.Rows.Count; j++)
                    {
                        double indival = 0.0;
                        if (double.TryParse(dtpollutant.Rows[j]["ConvertedParameterValue"].ToString(), out indival))
                        {
                            if (indival <= 0)
                            {
                                continue;
                            }

                            dividedindex++;
                            if (rollingPollutantAvg == null)
                            {
                                rollingPollutantAvg = 0;
                            }
                            rollingPollutantAvg += indival;
                        }
                    }

                    eightHourAvgValue = rollingPollutantAvg != null ? rollingPollutantAvg / dividedindex : rollingPollutantAvg;
                    AQIValue = CalculatePollutantAQIValues(eightHourAvgValue, pollutantName);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            return AQIValue;
        }

        public double? CalculateTwentryFourHoursRollingAverage(int StationID, DateTime Interval, string pollutantName, int TypeID, SqlConnection ConObj)
        {
            double? AQIValue = 0.0;
            double? twentyfourhourAvgValue = 0.0;

            try
            {
                string paramter = pollutantName.Split('_')[1];
                SqlCommand cmd = new SqlCommand(string.Empty, ConObj);
                DataTable dtpollutant = new DataTable();
                DateTime Interval2 = Interval.AddHours(-24);
                cmd.CommandText = $@"
           SELECT d.DriverName,pa.ParameterValue * COALESCE(CASE WHEN u.UnitName <> pc.SecondaryUnit THEN TRY_CAST(pc.ConversionFactor AS FLOAT)
                ELSE 1 END, 1) AS ConvertedParameterValue FROM  
        {averageTableName} pa INNER JOIN {parameterTableName} dp ON pa.ParameterID = dp.ID INNER JOIN {driverTableName} d ON dp.DriverID = d.ID 
        INNER JOIN ReportedUnits u ON dp.UnitID = u.ID LEFT JOIN Parameter_Conversion pc ON d.DriverName = pc.Parameter 
    WHERE  pa.StationID = @StationID AND pa.Interval <= @Interval AND pa.Interval > @Interval2 AND pa.TypeID = @TypeID AND d.DriverName = @DriverName order by pa.Interval desc";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@StationID", StationID);
                cmd.Parameters.AddWithValue("@Interval", Interval);
                cmd.Parameters.AddWithValue("@Interval2", Interval2);
                cmd.Parameters.AddWithValue("@TypeID", TypeID);
                cmd.Parameters.AddWithValue("@DriverName", paramter);

                Log.Info("Query To fetch the number of records for each Interval: " + cmd.CommandText);

                using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                {
                    adapter.Fill(dtpollutant);
                }

                int dividedindex = 0;
                double? rollingPollutantAvg = null;
                if (dtpollutant.Rows.Count == 0)
                {
                    AQIValue = null;
                }
                else
                {
                    for (int j = 0; j < dtpollutant.Rows.Count; j++)
                    {
                        double indival = 0.0;
                        if (double.TryParse(dtpollutant.Rows[j]["ConvertedParameterValue"].ToString(), out indival))
                        {
                            if (indival <= 0)
                            {
                                continue;
                            }

                            dividedindex++;
                            if (rollingPollutantAvg == null)
                            {
                                rollingPollutantAvg = 0;
                            }
                            rollingPollutantAvg += indival;
                        }
                    }

                    twentyfourhourAvgValue = rollingPollutantAvg != null ? rollingPollutantAvg / dividedindex : rollingPollutantAvg;
                    AQIValue = CalculatePollutantAQIValues(twentyfourhourAvgValue, pollutantName);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            return AQIValue;
        }

        private void WriteToLogTable(SqlConnection conObj, string logDesc, string logSource)
        {
            string query1 = $"Insert into {logTableName}(LogDescription,LogSource,LogState,LogTime) VALUES(@LogDesc,@LogSource,@LogState,@LogTime)";
            SqlCommand cmdsuccess = new SqlCommand(query1, conObj);
            cmdsuccess.Parameters.Clear();
            cmdsuccess.Parameters.AddWithValue("@LogDesc", logDesc);
            cmdsuccess.Parameters.AddWithValue("@LogSource", logSource);
            cmdsuccess.Parameters.AddWithValue("@LogState", 1);
            cmdsuccess.Parameters.AddWithValue("@LogTime", DateTime.Now);
            cmdsuccess.ExecuteNonQuery();
        }
        private void UpdateReadingsTableRowStatus(SqlCommand cmd, DataRow row)
        {
            cmd.CommandText = $"Update {readingTableName} set UpdateStatus='0' where StationID= @StationID and DeviceID = @DeviceID and ParameterID = @ParameterID and UpdateStatus = 1";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@StationID", Convert.ToInt32(row["StationID"]));
            cmd.Parameters.AddWithValue("@DeviceID", Convert.ToInt32(row["DeviceId"]));
            cmd.Parameters.AddWithValue("@ParameterID", Convert.ToInt32(row["ID"]));
            cmd.ExecuteNonQuery();
        }

    }
}