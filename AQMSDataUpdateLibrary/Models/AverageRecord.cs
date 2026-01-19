using System;
using System.Collections.Generic;

namespace AQMSDataUpdateLibrary.Models
{
    
    public class AverageRecord
    {
        public int StationID { get; set; }
        public int DeviceID { get; set; }
        public int ParameterID { get; set; }
        public int? ParameterIDRef { get; set; }
        public double? ParameterValue { get; set; }
        public double? SubIndex { get; set; }
        public string Type { get; set; }
        public DateTime Interval { get; set; }
        public int? LoggerFlags { get; set; }
        public int TypeID { get; set; }
        public DateTime CreatedTime { get; set; }
    }

    

    public class IntervalData
    {
        public DateTime Interval { get; set; }
        public int TotalRecordCount { get; set; }
        public int ValidRecordCount { get; set; }
        public double? ParameterAvg { get; set; }
    }

    
}