using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Chickensoft.GoDotTest;
using Chickensoft.Log;

namespace Train.Tests.Engine.Bootstrap;

internal sealed class ReportAdapter(string path) : TestAdapter
{
    public override ITestReporter CreateReporter(ILog log) => new JUnitReporter(new TestReporter(log), path);
}

internal sealed class JUnitReporter(ITestReporter inner, string path) : ITestReporter
{
    private readonly Dictionary<(ITestSuite, ITestMethod), long> _starts = [];
    private readonly List<XElement> _cases = [];
    private int _executed;
    public bool HadError => inner.HadError || _executed == 0;

    public void MethodUpdate(ITestSuite suite, ITestMethod method, TestMethodEvent update)
    {
        inner.MethodUpdate(suite, method, update);
        var key = (suite, method);
        if (update is TestMethodStartedEvent)
        {
            _starts[key] = Stopwatch.GetTimestamp();
            return;
        }
        if (method.Type != TestMethodType.Test && update is not TestMethodFailedEvent)
            return;
        if (method.Type == TestMethodType.Test && update is TestMethodPassedEvent or TestMethodFailedEvent)
            _executed++;
        var seconds = _starts.TryGetValue(key, out var started) ? Stopwatch.GetElapsedTime(started).TotalSeconds : 0;
        var result = new XElement("testcase", new XAttribute("classname", suite.TestClassType.FullName!),
            new XAttribute("name", method.Type == TestMethodType.Test ? method.Name : method.Type + ":" + method.Name),
            new XAttribute("time", seconds.ToString("0.000000", CultureInfo.InvariantCulture)));
        if (update is TestMethodFailedEvent failed)
            result.Add(new XElement("failure", new XAttribute("type", failed.FailureException.GetType().FullName!),
                new XAttribute("message", failed.FailureException.Message), failed.FailureException.ToString()));
        if (update is TestMethodSkippedEvent)
            result.Add(new XElement("skipped"));
        _cases.Add(result);
    }

    public void SuiteUpdate(ITestSuite suite, TestSuiteEvent update) => inner.SuiteUpdate(suite, update);
    public void Update(TestEvent update) => inner.Update(update);
    public void OutputFinalReport()
    {
        inner.OutputFinalReport();
        var report = new XElement("testsuite", new XAttribute("name", "engine"),
            new XAttribute("tests", _cases.Count), new XAttribute("failures", _cases.Count(item => item.Element("failure") is not null)),
            new XAttribute("skipped", _cases.Count(item => item.Element("skipped") is not null)), _cases);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new XDocument(new XElement("testsuites", report)).Save(path);
    }
}
