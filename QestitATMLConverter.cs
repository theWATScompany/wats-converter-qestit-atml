using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;
using Virinco.WATS.Interface;

namespace Virinco.WATS.Converter.Qestit
{
    public class QestitATMLConverter : IReportConverter_v2
    {
        private Dictionary<string, string> arguments = new Dictionary<string, string>
        {
            { "dateTimeFormat", "yyyy-MM-ddTHH:mm:ss.FFFFZ" },
            { "cultureInfo", "da-DK" },
            { "operationTypeCode", "50" },
            { "replaceApostropheWithDotInRevision", "true" },
        };

        public Dictionary<string, string> ConverterParameters => arguments;

        public void CleanUp()
        {
            // Cleanup if needed
        }

        public QestitATMLConverter() { }

        public QestitATMLConverter(Dictionary<string, string> args)
        {
            arguments = args;
        }

        public static XElement FindFirstElementByLocalName(XElement xml, string elementName)
        {
            return xml.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);
        }

        public static IEnumerable<XElement> FindAllElementsByLocalName(XElement xml, string elementName)
        {
            return xml.Elements().Where(e => e.Name.LocalName == elementName);
        }

        // The namespaces are placed here as global
        XNamespace ns = "http://www.ieee.org/ATML/2007/TestResults";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        XNamespace qrm = "http://www.addq.se/QRM";
        XNamespace c = "http://www.ieee.org/ATML/2006/Common";

        public Report ImportReport(TDM api, Stream file)
        {
            api.TestMode = TestModeType.Active;
            api.ValidationMode = ValidationModeType.AutoTruncate;

            XDocument doc = XDocument.Load(file);
            XElement testResults = doc.Root;

            // I use namespace to split these into separate XElements
            XElement testProgramEl = testResults.Element(ns + "TestProgram");
            XElement personnelEl = testResults.Element(ns + "Personnel");
            XElement testDescriptionEl = testResults.Element(ns + "TestDescription"); // This one is not used yet
            XElement testStationEl = testResults.Element(ns + "TestStation");
            XElement uutEl = testResults.Element(ns + "UUT");
            XElement resultSetEl = testResults.Element(ns + "ResultSet");

            // Now i start using local name because I think its easier
            XElement systemOperatorEl = FindFirstElementByLocalName(personnelEl, "SystemOperator");
            string systemOperator = systemOperatorEl.Attribute("name").Value;

            // I don't want to allow null everywhere with '?' because it won't indicate if my assumption about the fields is incorrect during testing.
            string partNumber = FindFirstElementByLocalName(uutEl, "PartNumber").Value;
            string partRevision = ReplaceApostropheWithDotInRevision(FindFirstElementByLocalName(uutEl, "Version")?.Value, ConverterParameters["replaceApostropheWithDotInRevision"]);
            string serialNumber = FindFirstElementByLocalName(uutEl, "SerialNumber").Value;
            string sequenceName = FindFirstElementByLocalName(testProgramEl, "ModelName").Value;
            string sequenceVersion = FindFirstElementByLocalName(testProgramEl, "Version").Value;
            string stationName = FindFirstElementByLocalName(testStationEl, "ModelName").Value;

            string startDateTimeString = resultSetEl.Attribute("startDateTime").Value;
            string endDateTimeString = resultSetEl.Attribute("endDateTime").Value;

            // Create UUTReport
            UUTReport uut = api.CreateUUTReport(
                systemOperator,
                partNumber,
                partRevision,
                serialNumber,
                ConverterParameters["operationTypeCode"],
                sequenceName,
                sequenceVersion
            );

            // StartTime and ExecutionTime
            DateTime startDateTime = ParseDateTime(startDateTimeString);
            DateTime endDateTime = ParseDateTime(endDateTimeString);
            uut.StartDateTime = startDateTime;
            uut.ExecutionTime = (endDateTime - startDateTime).TotalSeconds;
            
            // StationInfo
            uut.StationName = stationName;

            // Everything that i dont know what is I add to MiscInfo
            string tmpSerialNumber = FindFirstElementByLocalName(uutEl, "TmpSerialNumber").Value;
            string manufactureWeek = FindFirstElementByLocalName(uutEl, "ManufactureWeek").Value;
            string variant = FindFirstElementByLocalName(uutEl, "Variant").Value;
            string productFamily = FindFirstElementByLocalName(uutEl, "ProductFamily").Value;

            AddToMiscInfo(uut, "TmpSerialNumber", tmpSerialNumber);
            AddToMiscInfo(uut, "ManufactureWeek", manufactureWeek);
            AddToMiscInfo(uut, "Variant", variant);
            AddToMiscInfo(uut, "ProductFamily", productFamily);

            XElement uutSettings = FindFirstElementByLocalName(uutEl, "UUTSettings");
            if (uutSettings != null)
            {
                var settingsEls = FindAllElementsByLocalName(uutSettings, "Setting").ToList();
                foreach (XElement settingEl in settingsEls)
                {
                    string key = settingEl.Attribute("name").Value;
                    string value = FindFirstElementByLocalName(settingEl, "ValueString").Value;
                    AddToMiscInfo(uut, key, value);
                }
            }

            // Adding assets that have a serialnumber
            AddAssetsWithUsageCount(testStationEl, uut);

            SequenceCall rootSequence = uut.GetRootSequenceCall();

            ProcessResultSet(rootSequence, resultSetEl);


            // Setting report status last to make sure it overrides if active mode changes the report status
            XElement outcomeEl = FindFirstElementByLocalName(resultSetEl, "Outcome");
            uut.Status = GetUUTStatusType(outcomeEl);

            api.Submit(uut);
            return null;
        }

        private void ProcessResultSet(SequenceCall rootSequence, XElement resultSetEl)
        {
            var testElements = FindAllElementsByLocalName(resultSetEl, "Test");
            foreach (XElement testEl in testElements)
            {
                string sequenceName = testEl.Attribute("name").Value;
                string sequenceDescription = FindFirstElementByLocalName(testEl, "Description")?.Value;

                string endDateTimeString = testEl.Attribute("endDateTime").Value;
                string startDateTimeString = testEl.Attribute("startDateTime").Value;

                string sequenceFullName = sequenceName;
                if (!string.IsNullOrEmpty(sequenceDescription))
                {
                    sequenceFullName += " - " + sequenceDescription;
                }

                SequenceCall currentSequence = rootSequence.AddSequenceCall(sequenceFullName);

                DateTime startDateTime = ParseDateTime(startDateTimeString);
                DateTime endDateTime = ParseDateTime(endDateTimeString);
                currentSequence.StepTime = (endDateTime - startDateTime).TotalSeconds;

                var testResultsElements = FindAllElementsByLocalName(testEl, "TestResult").ToList();
               
                for (int i = 0; i < testResultsElements.Count; i++)
                {
                    DateTime testResultStartDateTime = DateTime.Now;
                    DateTime testResultEndDateTime = DateTime.Now;
                    double stepTime = 0;

                    XElement thisTestResultEl = testResultsElements[i];
                    XElement thisTestDataEl = FindFirstElementByLocalName(thisTestResultEl, "TestData");
                    string thisStartDateTime = thisTestDataEl.Attribute("acquisitionTimeStamp").Value;
                    testResultStartDateTime = ParseDateTime(thisStartDateTime);

                    if (i < testResultsElements.Count - 1)
                    {
                        XElement nextTestResultEl = testResultsElements[i + 1];
                        XElement nextTestDataEl = FindFirstElementByLocalName(nextTestResultEl, "TestData");
                        string nextStartDateTime = nextTestDataEl.Attribute("acquisitionTimeStamp").Value;
                        testResultEndDateTime = ParseDateTime(nextStartDateTime);
                    } else
                    {
                        testResultEndDateTime = endDateTime;
                    }

                    stepTime = (testResultEndDateTime - testResultStartDateTime).TotalSeconds;

                    ProcessTestResult(currentSequence, thisTestResultEl, stepTime);
                }
            }
        }

        private void ProcessTestResult(SequenceCall currentSequence, XElement testResultEl, double stepTime)
        {

            XElement testDataEl = FindFirstElementByLocalName(testResultEl, "TestData");
            XElement testLimitsEl = FindFirstElementByLocalName(testResultEl, "TestLimits");
            XElement extension = FindFirstElementByLocalName(testResultEl, "Extension");
          

            XElement testDataDatumEl = FindFirstElementByLocalName(testDataEl, "Datum");

            string testDataValue = testDataDatumEl.Attribute("value")?.Value ?? FindFirstElementByLocalName(testDataDatumEl,"Value")?.Value;
            string testDataType = testDataDatumEl.Attribute(xsi + "type").Value;
            string testDataUnit = testDataDatumEl.Attribute("nonStandardUnit")?.Value; // This one may or may not exist
            string comment = FindFirstElementByLocalName(testResultEl, "Comment")?.Value;

            if (testDataUnit == null)
            {
                testDataUnit = "";
            }


            if (comment == null)
            {
                comment = "";
            }



            var (compOperatorType, lowerLimit, upperLimit) = GetCompOperatorType(testLimitsEl);

            string stepName = testResultEl.Attribute("name").Value;

            XElement testOutcome = FindFirstElementByLocalName(testResultEl, "Outcome");
            StepStatusType stepStatus = GetStepStatusType(testOutcome);


            switch (testDataType)
            {
                case "c:boolean":
                    ProcessBooleanTest(currentSequence, stepName, stepTime, testDataValue, stepStatus, comment);
                    break;

                case "c:double":
                case "c:integer":
                case "c:unsignedInteger":
                    ProcessNumericTest(currentSequence, stepName, stepTime, testDataValue, testDataUnit, stepStatus, compOperatorType, lowerLimit, upperLimit, comment);
                    break;

                case "c:string":
                    ProcessStringTest(currentSequence, stepName, stepTime, testDataValue, stepStatus, compOperatorType, lowerLimit, upperLimit, comment);
                    break;

                default:
                    throw new FormatException($"Unsupported testDataType: '{testDataType}'");
            }



        }

        // Helper methods for specific data types
        private void ProcessBooleanTest(SequenceCall currentSequence, string stepName, double stepTime, string testDataValue, StepStatusType stepStatus, string comment)
        {
            if (TryConvertToBool(testDataValue, out bool boolValue))
            {
                var passFailStep = currentSequence.AddPassFailStep(stepName);
                passFailStep.StepTime = stepTime;
                passFailStep.AddTest(boolValue, stepStatus);
                passFailStep.ReportText = comment;
            }
            else
            {
                throw new FormatException($"Failed to convert '{testDataValue}' to boolean.");
            }
        }

        private void ProcessNumericTest(SequenceCall currentSequence, string stepName, double stepTime, string testDataValue, string testDataUnit, StepStatusType stepStatus, CompOperatorType compOperatorType, object lowerLimit, object upperLimit, string comment)
        {
            if (double.TryParse(testDataValue, NumberStyles.Any, CultureInfo.GetCultureInfo(ConverterParameters["cultureInfo"]), out double doubleValue))
            {
                var numericLimitStep = currentSequence.AddNumericLimitStep(stepName);
                numericLimitStep.StepTime = stepTime;
                numericLimitStep.ReportText = comment;

                if (lowerLimit == null && upperLimit == null)
                {
                    numericLimitStep.AddTest(doubleValue, testDataUnit, stepStatus);
                }
                else if (lowerLimit.Equals(upperLimit))
                {
                    numericLimitStep.AddTest(doubleValue, compOperatorType, (double)lowerLimit, testDataUnit, stepStatus);
                }
                else
                {
                    numericLimitStep.AddTest(doubleValue, compOperatorType, (double)lowerLimit, (double)upperLimit, testDataUnit, stepStatus);
                }
            }
            else
            {
                throw new FormatException($"Failed to convert '{testDataValue}' to a valid number.");
            }
        }

        private void ProcessStringTest(SequenceCall currentSequence, string stepName, double stepTime, string testDataValue, StepStatusType stepStatus, CompOperatorType compOperatorType, object lowerLimit, object upperLimit , string comment)
        {
            var stringValueStep = currentSequence.AddStringValueStep(stepName);
            stringValueStep.StepTime = stepTime;
            stringValueStep.ReportText = comment;

            if (lowerLimit == null && upperLimit == null)
            {
                stringValueStep.AddTest(testDataValue, stepStatus);
            }
            else
            {
                stringValueStep.AddTest(compOperatorType, testDataValue, (string)lowerLimit, stepStatus);
            }
        }

        void AddAssetsWithUsageCount(XElement testStationEl, UUTReport uut)
        {
            // Find the "Equipments" element
            XElement equipmentsEl = FindFirstElementByLocalName(testStationEl, "Equipments");
            if (equipmentsEl == null) return;

            // Get all "Equipment" elements
            var equipmentEls = FindAllElementsByLocalName(equipmentsEl, "Equipment");

            // Dictionary to count occurrences of each serial number
            Dictionary<string, int> serialNumberCounts = new Dictionary<string, int>();

            // Count occurrences of each serial number
            foreach (XElement equipmentEl in equipmentEls)
            {
                string equipmentSerialNumber = FindFirstElementByLocalName(equipmentEl, "SerialNumber")?.Value;
                if (!string.IsNullOrEmpty(equipmentSerialNumber))
                {
                    if (serialNumberCounts.ContainsKey(equipmentSerialNumber))
                    {
                        serialNumberCounts[equipmentSerialNumber]++;
                    }
                    else
                    {
                        serialNumberCounts[equipmentSerialNumber] = 1;
                    }
                }
            }

            // Add assets with the correct usage count
            foreach (var kvp in serialNumberCounts)
            {
                uut.AddAsset(kvp.Key, kvp.Value);
            }
        }

        public string ReplaceApostropheWithDotInRevision(string inputString, string replaceApostropheWithDotInRevision)
        {
            bool replaceApostropheWithDot = false;

            if (!string.IsNullOrEmpty(inputString))
            { 

                replaceApostropheWithDotInRevision = replaceApostropheWithDotInRevision.ToUpper();

                if (replaceApostropheWithDotInRevision == "TRUE")
                {
                    replaceApostropheWithDot = true;
                }
                else if (replaceApostropheWithDotInRevision == "FALSE")
                {
                    replaceApostropheWithDot = false;
                }
                else
                {
                    throw new FormatException($"Invalid value for 'replaceApostropheWithDotInRevision': {replaceApostropheWithDotInRevision}. Expected 'true' or 'false'");
                }

                if (replaceApostropheWithDot)
                {
                    return inputString.Replace("'", ".");
                }

            }

            return inputString;
        }


        private (CompOperatorType, object, object) GetCompOperatorType(XElement testLimitsEl)
        {
            // Using object to handle both double and string values, but only for single limit cases
            object lowerLimit = null;
            object upperLimit = null;

            if (testLimitsEl == null)
            {
                return (CompOperatorType.LOG, null, null);
            }

            // Attempt to find <c:SingleLimit> or <c:Expected> (single-limit scenario)
            XElement singleLimit = testLimitsEl.Descendants().FirstOrDefault(el => el.Name.LocalName == "SingleLimit" || el.Name.LocalName == "Expected");

            if (singleLimit != null)
            {
                string comparator = singleLimit.Attribute("comparator").Value;
                XElement datumEl = FindFirstElementByLocalName(singleLimit, "Datum");

                if (datumEl != null)
                {
                    // Check if it's a string type datum
                    if (datumEl.Attribute(xsi + "type").Value == "c:string")
                    {
                        // Handle string value
                        XElement valueEl = FindFirstElementByLocalName(datumEl, "Value");
                        if (valueEl != null)
                        {
                            string stringValue = valueEl.Value;
                            lowerLimit = stringValue;
                            upperLimit = stringValue; // Same value for both in single limit case
                        }
                    }
                    else
                    {
                        // Handle numeric value (existing logic)
                        if (double.TryParse(datumEl.Attribute("value")?.Value, out double value))
                        {
                            lowerLimit = value;
                            upperLimit = value; // Single limit usually applies to one value
                        }
                    }
                }

                return (MapSingleComparator(comparator), lowerLimit, upperLimit);
            }

            // Otherwise, check if we have a <c:LimitPair> (paired-limits scenario)
            // Note: For LimitPair, we only handle double values (not strings)
            XElement limitPair = testLimitsEl.Descendants().FirstOrDefault(el => el.Name.LocalName == "LimitPair");

            if (limitPair != null)
            {
                string pairOperator = limitPair.Attribute("operator")?.Value;
                var limits = FindAllElementsByLocalName(limitPair, "Limit").ToList();

                if (limits.Count == 2)
                {
                    string comparator1 = limits[0].Attribute("comparator")?.Value;
                    string comparator2 = limits[1].Attribute("comparator")?.Value;

                    XElement datumEl1 = FindFirstElementByLocalName(limits[0], "Datum");
                    XElement datumEl2 = FindFirstElementByLocalName(limits[1], "Datum");

                    double? value1 = null;
                    double? value2 = null;

                    // For LimitPair, only handle numeric values
                    if (datumEl1 != null && double.TryParse(datumEl1.Attribute("value")?.Value, out double parsedValue1))
                    {
                        value1 = parsedValue1;
                    }

                    if (datumEl2 != null && double.TryParse(datumEl2.Attribute("value")?.Value, out double parsedValue2))
                    {
                        value2 = parsedValue2;
                    }

                    // Handle numeric limits
                    if (value1.HasValue && value2.HasValue)
                    {
                        lowerLimit = Math.Min(value1.Value, value2.Value);
                        upperLimit = Math.Max(value1.Value, value2.Value);
                    }

                    return (MapPairComparator(pairOperator, comparator1, comparator2), lowerLimit, upperLimit);
                }
            }

            return (CompOperatorType.LOG, null, null);
        }


        private CompOperatorType MapSingleComparator(string comparator)
        {
            // You can adapt or extend this mapping as needed
            switch (comparator?.ToUpperInvariant())
            {
                case "EQ": return CompOperatorType.EQ;
                case "NE": return CompOperatorType.NE;
                case "GT": return CompOperatorType.GT;
                case "LT": return CompOperatorType.LT;
                case "GE": return CompOperatorType.GE;
                case "LE": return CompOperatorType.LE;
                default: return CompOperatorType.LOG;
            }
        }


        private CompOperatorType MapPairComparator(string pairOperator, string comp1, string comp2)
        {
            // We only support AND for pairs; anything else is unexpected
            if (!"AND".Equals(pairOperator, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Unknown operatorpair");
                return CompOperatorType.LOG;
            }

            // Because XML may not guarantee comparator order, handle them as a set
            var pairSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                comp1, comp2
            };

            // AND combos. Make sure we handle both orders (e.g. (GT,LT) or (LT,GT)).
            if (pairSet.SetEquals(new[] { "GT", "LT" }))
            {
                return CompOperatorType.GTLT;
            }
            else if (pairSet.SetEquals(new[] { "GE", "LE" }))
            {
                return CompOperatorType.GELE;
            }
            else if (pairSet.SetEquals(new[] { "GE", "LT" }))
            {
                return CompOperatorType.GELT;
            }
            else if (pairSet.SetEquals(new[] { "GT", "LE" })) 
            { 
                return CompOperatorType.GTLE; 
            }
            else
            {
                Console.WriteLine("Not known combo");
            }

            // If pair comparators don't match any known combination, log or fallback
            return CompOperatorType.LOG;
        }

        bool TryConvertToBool(string value, out bool result)
        {
            if (bool.TryParse(value, out result))
            {
                return true;
            }

            if (value == "1")
            {
                result = true;
                return true;
            }
            else if (value == "0")
            {
                result = false;
                return true;
            }

            result = false;
            return false;
        }

        private void AddToMiscInfo(UUTReport uut, string key, string value)
        {
            if (value != null)
            {
                uut.AddMiscUUTInfo(key, value);
            }
        }

        private DateTime ParseDateTime(string dateTimeString)
        {
            if (DateTime.TryParseExact(dateTimeString, ConverterParameters["dateTimeFormat"], CultureInfo.GetCultureInfo(ConverterParameters["cultureInfo"]), DateTimeStyles.None, out DateTime parsedDateTime))
            {
                return parsedDateTime;
            }
            else
            {
                throw new FormatException($"Could not parse date time string '{dateTimeString}' using format '{ConverterParameters["dateTimeFormat"]}'");
            }
        }

        private UUTStatusType GetUUTStatusType(XElement outcomeEl)
        {
            string outcomeString = outcomeEl.Attribute("value").Value;
            
            switch (outcomeString)
            {
                case "Passed":
                {
                    return UUTStatusType.Passed;
                }
                case "Failed":
                {
                    return UUTStatusType.Failed;
                }
                case "Aborted":
                {
                    return UUTStatusType.Terminated;
                }
                default:
                {
                    throw new FormatException($"Unexpected outcome: {outcomeString}");
                }

            }
        }

        private StepStatusType GetStepStatusType(XElement outcomeEl)
        {
            string outcomeString = outcomeEl.Attribute("value").Value;
            
            switch (outcomeString)
            {
                case "Passed":
                    {
                        return StepStatusType.Passed;
                    }
                case "Failed":
                    {
                        return StepStatusType.Failed;
                    }
                default:
                    {
                        throw new FormatException($"Unexpected outcome: {outcomeString}");
                    }

            }
        }
    }
}
