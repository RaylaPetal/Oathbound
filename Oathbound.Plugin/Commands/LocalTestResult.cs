namespace Oathbound.Plugin.Commands;

public readonly record struct LocalTestResult(bool Success, string Message)
{
    public static LocalTestResult Ok(string message) => new(true, message);
    public static LocalTestResult Fail(string message) => new(false, message);
}
