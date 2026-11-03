using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace MNVTEngine.Core.Logging;

/// <summary>Where a log message came from: namespace, class and method, plus file and line when the caller supplied them.</summary>
public sealed record LogCallSite(string Namespace, string Class, string Method, string? File = null, int Line = 0)
{
    /// <summary>Set by <see cref="GameLog"/> for the duration of one log call: an exact site, so no stack walk is needed.</summary>
    [ThreadStatic]
    internal static LogCallSite? Pending;

    public string FullClass => Namespace.Length == 0 ? Class : $"{Namespace}.{Class}";

    public override string ToString() => $"{FullClass}.{Method}";

    /// <summary>The site of a type and method (compiler-generated names are mapped back to the source method).</summary>
    public static LogCallSite For(Type type, string method, string? file = null, int line = 0)
    {
        ArgumentNullException.ThrowIfNull(type);
        var (ns, name, enclosingMethod) = Describe(type);
        return new LogCallSite(ns, name, SourceName(method) ?? enclosingMethod ?? method, file, line);
    }

    /// <summary>
    /// Walks the stack to the first frame outside the logging plumbing (Microsoft.Extensions.Logging, this namespace and
    /// [LoggerMessage] methods). Methods the JIT inlined have no frame, so the site may name their caller instead.
    /// </summary>
    public static LogCallSite? Capture()
    {
        var frames = new StackTrace(1, fNeedFileInfo: false).GetFrames();
        foreach (var frame in frames)
        {
            if (frame.GetMethod() is not { DeclaringType: { } type } method || IsPlumbing(method, type))
            {
                continue;
            }

            return For(type, method.Name);
        }

        return null;
    }

    private static bool IsPlumbing(MethodBase method, Type type)
    {
        var ns = type.Namespace ?? "";
        return ns.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal)
            || ns == typeof(LogCallSite).Namespace
            || type == typeof(GameLog)
            || method.IsDefined(typeof(LoggerMessageAttribute), inherit: false);
    }

    /// <summary>Namespace, readable class name (Outer.Inner, no generic arity) and, for lambdas/iterators, the source method.</summary>
    private static (string Namespace, string Class, string? Method) Describe(Type type)
    {
        string? method = null;
        var names = new List<string>();
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current.Name.StartsWith('<'))
            {
                method ??= SourceName(current.Name); // state machine "<Reload>d__12" or closure "<>c__DisplayClass3_0"
                continue;
            }

            var tick = current.Name.IndexOf('`', StringComparison.Ordinal);
            names.Insert(0, tick < 0 ? current.Name : current.Name[..tick]);
            type = current;
        }

        return (type.Namespace ?? "", names.Count == 0 ? type.Name : string.Join('.', names), method);
    }

    /// <summary>"&lt;Start&gt;b__0" → "Start"; null for plain names and for "&lt;&gt;c"-style names without a method.</summary>
    private static string? SourceName(string name)
    {
        if (!name.StartsWith('<'))
        {
            return null;
        }

        var end = name.IndexOf('>', StringComparison.Ordinal);
        return end > 1 ? name[1..end] : null;
    }
}
