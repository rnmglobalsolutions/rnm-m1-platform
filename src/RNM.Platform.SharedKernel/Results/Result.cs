namespace RNM.Platform.SharedKernel.Results;

public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);
}

public sealed class Result<T> : Result
{
    private Result(T? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        Value = value;
    }

    public T? Value { get; }

    public static Result<T> Success(T value) => new(value, true, Error.None);

    public static new Result<T> Failure(Error error) => new(default, false, error);
}
