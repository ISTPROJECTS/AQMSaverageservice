using System;
using System.Collections.Generic;

namespace AQMSDataUpdateLibrary.Models
{

    

    public class AQICalculationResult
    {
        public double? AQI { get; set; }
        public double? PM25SubIndex { get; set; }
        public double? PM10SubIndex { get; set; }
        public double? O3SubIndex { get; set; }
        public double? SO2SubIndex { get; set; }
        public double? NO2SubIndex { get; set; }
        public double? COSubIndex { get; set; }
        public Dictionary<string, int?> ParameterIDs { get; set; }
    }
}