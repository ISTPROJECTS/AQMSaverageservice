using System;
using System.Collections.Generic;

namespace AQMSDataUpdateLibrary.Models
{
    // Core data models
    public class ParameterReading
    {
        public int StationID { get; set; }
        public int DeviceID { get; set; }
        public int ParameterID { get; set; }
        public int? ParameterIDRef { get; set; }
        public double? ParameterValue { get; set; }
        public int? LoggerFlags { get; set; }
        public DateTime CreatedTime { get; set; }
        public string ParameterDriverName { get; set; }
    }


    public class ParameterInfo
    {
        public int ID { get; set; }
        public int StationID { get; set; }
        public int DeviceID { get; set; }
        public int ParameterID { get; set; }
        public string ParameterName { get; set; }
        public string ParameterDriverName { get; set; }
        public string ServerAvgInterval { get; set; }
        public int? DataSyncFrequency { get; set; }
        public bool? IsCalculated { get; set; }
        public bool? ShouldUseForAqi { get; set; }
    }

    
}