using System;
using System.Collections.Generic;

namespace presents.PluginTestKit;

internal sealed class TestResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Scope { get; set; } = "global";
    public bool Passed { get; set; }
    public bool Warning { get; set; }
    public long DurationMs { get; set; }
    public string Message { get; set; } = "";
    public List<string> Details { get; set; } = new();
}

internal sealed class TestReport
{
    public string At { get; set; } = "";
    public long DurationMs { get; set; }
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Warnings { get; set; }
    public string Summary { get; set; } = "";
    public List<TestResult> Results { get; set; } = new();
}

internal static class TestFactory
{
    public static TestResult Ok(string id, string name, string category, string scope, long ms, string message = "")
        => new() { Id = id, Name = name, Category = category, Scope = scope, Passed = true, DurationMs = ms, Message = message };

    public static TestResult Fail(string id, string name, string category, string scope, long ms, string message,
        IEnumerable<string>? details = null, bool warning = false)
        => new()
        {
            Id = id, Name = name, Category = category, Scope = scope, Passed = false, Warning = warning,
            DurationMs = ms, Message = message,
            Details = details == null ? new List<string>() : new List<string>(details)
        };
}
