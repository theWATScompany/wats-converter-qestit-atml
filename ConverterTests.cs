using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Virinco.WATS.Interface;
using Xunit;
using Xunit.Abstractions;
using WATS.Testing;
using Virinco.WATS.Converter.Qestit;

namespace Virinco.WATS.Converter.Qestit.Tests
{
    [SupportedOSPlatform("windows7.0")]
    public class ConverterTests : TextConverterTestBase
    {
        public ConverterTests(ITestOutputHelper output) : base(output) { }
        protected override IReportConverter_v2 CreateConverter() => new QestitATMLConverter();

        [Fact, Trait("TestMode", "ConvertOnly")]
        public void ConvertOnly_AllFiles() => RunAllFiles(TestMode.ConvertOnly);

        [Fact, Trait("TestMode", "ConvertAndValidate")]
        public void ConvertAndValidate_AllFiles() => RunAllFiles(TestMode.ConvertAndValidate);

        [Fact, Trait("TestMode", "ConvertAndSimulate")]
        public void ConvertAndSimulate_AllFiles() => RunAllFiles(TestMode.ConvertAndSimulate);
    }

    public class CompatibilityTests
    {
        private static readonly XNamespace Common = "http://www.ieee.org/ATML/2006/Common";
        private static readonly XNamespace Qrm = "http://www.addq.se/QRM";
        private static readonly XNamespace Results = "http://www.ieee.org/ATML/2007/TestResults";
        private static readonly XNamespace SchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";
        private readonly ITestOutputHelper output;

        public CompatibilityTests(ITestOutputHelper output) => this.output = output;

        [Theory]
        [InlineData("3.25E0", "da-DK", 3.25)]
        [InlineData("3,25E0", "da-DK", 3.25)]
        [InlineData("1.234,5", "da-DK", 1234.5)]
        [InlineData("1,234.5", "en-US", 1234.5)]
        public void LegacyCultureAndScientificNotationRemainSupported(string source, string culture, double expected)
        {
            var document = LoadSample();
            SetNumericData(document, source, source, source);
            var report = Convert(document, "sv-SE", culture);
            var test = Assert.Single(Assert.Single(report.AllSteps.OfType<NumericLimitStep>()).Tests);
            Assert.Equal(expected, test.NumericValue);
            Assert.Equal(expected, test.LowLimit);
            Assert.Equal(expected, test.HighLimit);
        }

        [Theory]
        [InlineData("EQ", CompOperatorType.EQ)]
        [InlineData("NE", CompOperatorType.NE)]
        [InlineData("GT", CompOperatorType.GT)]
        [InlineData("GE", CompOperatorType.GE)]
        [InlineData("LT", CompOperatorType.LT)]
        [InlineData("LE", CompOperatorType.LE)]
        public void SingleLimitsPreserveComparatorAndValue(string comparator, CompOperatorType expected)
        {
            var document = LoadSample();
            document.Descendants(Common + "LimitPair").Single().ReplaceWith(
                new XElement(Common + "SingleLimit", new XAttribute("comparator", comparator),
                    new XElement(Common + "Datum", new XAttribute("value", "3,5"),
                        new XAttribute(SchemaInstance + "type", "c:double"))));
            var report = Convert(document, "sv-SE");
            var test = Assert.Single(Assert.Single(report.AllSteps.OfType<NumericLimitStep>()).Tests);
            Assert.Equal(expected, test.CompOperator);
            Assert.True(test.LowLimit == 3.5 || test.HighLimit == 3.5);
        }

        [Theory]
        [InlineData("GT", "LT", CompOperatorType.GTLT)]
        [InlineData("GE", "LE", CompOperatorType.GELE)]
        [InlineData("GE", "LT", CompOperatorType.GELT)]
        [InlineData("GT", "LE", CompOperatorType.GTLE)]
        public void ReversedPairOrderPreservesBounds(string lowerComparator, string upperComparator, CompOperatorType expected)
        {
            var document = LoadSample();
            var pair = document.Descendants(Common + "LimitPair").Single();
            var limits = pair.Elements().ToArray();
            limits[0].SetAttributeValue("comparator", lowerComparator);
            limits[1].SetAttributeValue("comparator", upperComparator);
            pair.ReplaceNodes(Enumerable.Reverse(limits));
            var test = Assert.Single(Assert.Single(Convert(document, "sv-SE").AllSteps.OfType<NumericLimitStep>()).Tests);
            Assert.Equal(expected, test.CompOperator);
            Assert.Equal(3.1, test.LowLimit);
            Assert.Equal(3.5, test.HighLimit);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("1", true)]
        [InlineData("0", false)]
        public void BooleanValuesAndExpectedLimitsRemainSupported(string value, bool expected)
        {
            var document = LoadSample();
            var datum = document.Descendants(Results + "TestData").Single().Element(Common + "Datum");
            datum.SetAttributeValue("value", value);
            datum.SetAttributeValue(SchemaInstance + "type", "c:boolean");
            document.Descendants(Common + "LimitPair").Single().ReplaceWith(
                new XElement(Common + "Expected", new XAttribute("comparator", "EQ"), new XElement(datum)));
            var step = Assert.Single(Convert(document).AllSteps.OfType<PassFailStep>());
            Assert.Equal(expected, Assert.Single(step.Tests).Passed);
        }

        [Fact]
        public void StringExpectedLimitRemainsSupported()
        {
            var document = LoadSample();
            var datum = document.Descendants(Results + "TestData").Single().Element(Common + "Datum");
            datum.Attribute("value").Remove();
            datum.SetAttributeValue(SchemaInstance + "type", "c:string");
            datum.Add(new XElement(Common + "Value", "Example text"));
            document.Descendants(Common + "LimitPair").Single().ReplaceWith(
                new XElement(Common + "Expected", new XAttribute("comparator", "EQ"), new XElement(datum)));
            var test = Assert.Single(Assert.Single(Convert(document).AllSteps.OfType<StringValueStep>()).Tests);
            Assert.Equal("Example text", test.StringValue);
            Assert.Equal("Example text", test.StringLimit);
            Assert.Equal(CompOperatorType.EQ, test.CompOperator);
        }

        [Theory]
        [InlineData("Passed", UUTStatusType.Passed)]
        [InlineData("Failed", UUTStatusType.Failed)]
        [InlineData("Aborted", UUTStatusType.Terminated)]
        public void LegacyHeadersAndOutcomesRemainUnchanged(string outcome, UUTStatusType expected)
        {
            var document = LoadSample();
            document.Root.Element(Results + "ResultSet").Element(Results + "Outcome").SetAttributeValue("value", outcome);
            document.Descendants(Results + "Extension").Remove();
            var report = Convert(document);
            Assert.Equal(expected, report.Status);
            Assert.Equal("1.0", report.PartRevisionNumber);
            Assert.Equal("Example program", report.SequenceName);
            Assert.Equal("1.0", report.SequenceVersion);
            Assert.Equal(-1, report.TestSocketIndex);
            Assert.Equal(4, report.MiscInfo.Length);
        }

        [Fact]
        public void MissingLimitsRemainLogOnly()
        {
            var document = LoadSample();
            document.Descendants(Results + "TestLimits").Remove();
            var test = Assert.Single(Assert.Single(Convert(document).AllSteps.OfType<NumericLimitStep>()).Tests);
            Assert.Equal(3.25, test.NumericValue);
            Assert.Equal(CompOperatorType.LOG, test.CompOperator);
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("sv-SE")]
        [InlineData("da-DK")]
        public void SourceReportsMatchConvertedData(string hostCulture)
        {
            VerifySourceReport(LoadSample(), hostCulture);
            string archivePath = Environment.GetEnvironmentVariable("QESTIT_SAMPLE_ARCHIVE");
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                output.WriteLine("Synthetic fixture verified. No optional external archive configured.");
                return;
            }

            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.NotEmpty(entries);
            int readings = 0;
            foreach (var entry in entries)
            {
                using var stream = entry.Open();
                readings += VerifySourceReport(XDocument.Load(stream), hostCulture);
            }
            output.WriteLine($"Verified {entries.Length} external reports and {readings} readings with host culture {hostCulture}.");
        }

        private static int VerifySourceReport(XDocument document, string hostCulture)
        {
            var report = Convert(document, hostCulture);
            var root = document.Root;
            var sourceUut = root.Element(Results + "UUT");
            var resultSet = root.Element(Results + "ResultSet");
            Assert.Equal(sourceUut.Descendants().First(element => element.Name.LocalName == "SerialNumber").Value, report.SerialNumber);
            Assert.Equal(sourceUut.Descendants().First(element => element.Name.LocalName == "PartNumber").Value, report.PartNumber);
            Assert.Equal(sourceUut.Descendants().First(element => element.Name.LocalName == "Version").Value.Replace("'", "."), report.PartRevisionNumber);
            Assert.Equal(root.Element(Results + "Personnel").Descendants(Results + "SystemOperator").Single().Attribute("name").Value, report.Operator);
            Assert.Equal(root.Element(Results + "TestProgram").Descendants(Common + "ModelName").Single().Value, report.SequenceName);
            Assert.Equal(root.Element(Results + "TestProgram").Descendants(Qrm + "Version").Single().Value, report.SequenceVersion);
            Assert.Equal(root.Element(Results + "TestStation").Descendants(Common + "ModelName").Single().Value, report.StationName);
            var sourceStart = DateTimeOffset.Parse(resultSet.Attribute("startDateTime").Value, CultureInfo.InvariantCulture);
            var sourceEnd = DateTimeOffset.Parse(resultSet.Attribute("endDateTime").Value, CultureInfo.InvariantCulture);
            Assert.Equal(sourceStart.UtcDateTime, report.StartDateTimeOffset.UtcDateTime);
            Assert.Equal((sourceEnd - sourceStart).TotalSeconds, report.ExecutionTime, 6);
            string outcome = resultSet.Element(Results + "Outcome").Attribute("value").Value;
            Assert.Equal(outcome == "Aborted" ? "Terminated" : outcome, report.Status.ToString());
            var socket = resultSet.Descendants(Qrm + "ResultSetExtension").SingleOrDefault()?.Attribute("testSocketIndex");
            Assert.Equal(socket == null ? -1 : int.Parse(socket.Value, CultureInfo.InvariantCulture), report.TestSocketIndex);

            var sources = resultSet.Elements(Results + "Test").Elements(Results + "TestResult").ToArray();
            var steps = report.AllSteps.Where(step => !(step is SequenceCall)).ToArray();
            Assert.Equal(sources.Length, steps.Length);
            for (int index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                var step = steps[index];
                Assert.Equal(source.Attribute("name").Value.Trim(), step.Name);
                Assert.Equal(source.Element(Results + "Outcome").Attribute("value").Value, step.Status.ToString());
                var datum = source.Element(Results + "TestData").Element(Common + "Datum");
                if (step is NumericLimitStep numeric)
                {
                    var test = Assert.Single(numeric.Tests);
                    Assert.Equal(double.Parse(datum.Attribute("value").Value, CultureInfo.InvariantCulture), test.NumericValue);
                    Assert.Equal(datum.Attribute("nonStandardUnit")?.Value ?? "", test.Units);
                    var pair = source.Descendants(Common + "LimitPair").SingleOrDefault();
                    if (pair != null)
                    {
                        var bounds = pair.Descendants(Common + "Datum")
                            .Select(limit => double.Parse(limit.Attribute("value").Value, CultureInfo.InvariantCulture)).OrderBy(value => value).ToArray();
                        Assert.Equal(bounds[0], test.LowLimit);
                        Assert.Equal(bounds[1], test.HighLimit);
                        Assert.NotEqual(CompOperatorType.LOG, test.CompOperator);
                    }
                    else
                    {
                        var limit = source.Descendants(Common + "SingleLimit").Concat(source.Descendants(Common + "Expected"))
                            .Descendants(Common + "Datum").SingleOrDefault();
                        if (limit != null)
                        {
                            double expected = double.Parse(limit.Attribute("value").Value, CultureInfo.InvariantCulture);
                            Assert.True(test.LowLimit == expected || test.HighLimit == expected);
                            Assert.NotEqual(CompOperatorType.LOG, test.CompOperator);
                        }
                    }
                }
                else if (step is StringValueStep text)
                {
                    Assert.Equal(datum.Element(Common + "Value")?.Value ?? datum.Attribute("value")?.Value, Assert.Single(text.Tests).StringValue);
                }
                else if (step is PassFailStep boolean)
                {
                    string value = datum.Attribute("value").Value;
                    Assert.Equal(value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase), Assert.Single(boolean.Tests).Passed);
                }
                else
                {
                    Assert.Fail($"Unexpected step type: {step.GetType().Name}");
                }
            }
            return sources.Length;
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("sv-SE")]
        public void EqualPairedLimitsKeepBothBounds(string hostCulture)
        {
            var document = LoadSample();
            SetNumericData(document, "2", "2", "2");
            var report = Convert(document, hostCulture);
            var test = Assert.Single(Assert.Single(report.AllSteps.OfType<NumericLimitStep>()).Tests);
            Assert.Equal(2, test.LowLimit);
            Assert.Equal(2, test.HighLimit);
            Assert.Equal(CompOperatorType.GELE, test.CompOperator);
        }

        [Theory]
        [InlineData(null, -1)]
        [InlineData("0", 0)]
        [InlineData("4", 4)]
        public void SocketIndexIsOptional(string sourceIndex, short expected)
        {
            var document = LoadSample();
            document.Descendants(Qrm + "ResultSetExtension").Single()
                .SetAttributeValue("testSocketIndex", sourceIndex);
            Assert.Equal(expected, Convert(document).TestSocketIndex);
        }

        [Theory]
        [InlineData("not-a-number")]
        [InlineData("32768")]
        [InlineData("-2")]
        public void InvalidSocketIndexIsRejected(string sourceIndex)
        {
            var document = LoadSample();
            document.Descendants(Qrm + "ResultSetExtension").Single()
                .SetAttributeValue("testSocketIndex", sourceIndex);
            Assert.Contains("testSocketIndex", Assert.Throws<FormatException>(() => Convert(document)).Message);
        }

        [Theory]
        [InlineData("invalid", "3.1", "3.5")]
        [InlineData("3.25", "invalid", "3.5")]
        [InlineData("3.25", "3.1", "invalid")]
        public void MalformedNumbersAreRejected(string value, string low, string high)
        {
            var document = LoadSample();
            SetNumericData(document, value, low, high);
            Assert.Contains("Invalid numeric", Assert.Throws<FormatException>(() => Convert(document)).Message);
        }

        [Theory]
        [InlineData("en-US", "3.25", "3.1", "3.5")]
        [InlineData("sv-SE", "3.25", "3.1", "3.5")]
        [InlineData("da-DK", "3.25", "3.1", "3.5")]
        [InlineData("en-US", "3,25", "3,1", "3,5")]
        [InlineData("sv-SE", "3,25", "3,1", "3,5")]
        public void NumericFormatsPreserveValuesAndLimits(string hostCulture, string value, string low, string high)
        {
            var document = LoadSample();
            SetNumericData(document, value, low, high);
            var report = Convert(document, hostCulture);
            var test = Assert.Single(Assert.Single(report.AllSteps.OfType<NumericLimitStep>()).Tests);
            Assert.Equal(3.25, test.NumericValue);
            Assert.Equal(3.1, test.LowLimit);
            Assert.Equal(3.5, test.HighLimit);
            Assert.Equal(CompOperatorType.GELE, test.CompOperator);
        }

        private static XDocument LoadSample() => XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Data", "synthetic-qrm.xml"));

        private static void SetNumericData(XDocument document, string value, string low, string high)
        {
            document.Descendants().Single(element => element.Name.LocalName == "TestData")
                .Element(Common + "Datum").SetAttributeValue("value", value);
            var limits = document.Descendants(Common + "Limit").ToArray();
            limits[0].Element(Common + "Datum").SetAttributeValue("value", low);
            limits[1].Element(Common + "Datum").SetAttributeValue("value", high);
        }

        private static UUTReport Convert(XDocument document, string hostCulture = "en-US", string numberCulture = "da-DK")
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(hostCulture);
                var api = new OfflineCapture();
                var converter = new QestitATMLConverter();
                converter.ConverterParameters["cultureInfo"] = numberCulture;
                using var stream = new MemoryStream();
                document.Save(stream);
                stream.Position = 0;
                Assert.Null(converter.ImportReport(api, stream));
                var report = Assert.Single(api.Reports);
                report.ValidateForSubmit();
                return report;
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        private sealed class OfflineCapture : TDM
        {
            public List<UUTReport> Reports { get; } = new List<UUTReport>();

            public OfflineCapture() => InitializeAPI(false);

            public override bool Submit(SubmitMethod method, Report report)
            {
                Reports.Add(Assert.IsType<UUTReport>(report));
                return true;
            }
        }
    }
}
