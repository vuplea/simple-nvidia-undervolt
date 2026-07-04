namespace SimpleNvidiaUndervolt;

/// <summary>Result of a single NVAPI read: either a value or the error that prevented it.</summary>
internal readonly struct Reading<T>
{
    public bool Ok { get; private init; }
    public T? Value { get; private init; }
    public string? Error { get; private init; }

    public static Reading<T> Success(T value) => new() { Ok = true, Value = value };
    public static Reading<T> Failure(string error) => new() { Ok = false, Error = error };
}

internal static class Reading
{
    public static Reading<T> Try<T>(Func<T> read)
    {
        try
        {
            return Reading<T>.Success(read());
        }
        catch (Exception ex)
        {
            return Reading<T>.Failure($"unavailable ({ex.Message})");
        }
    }
}
