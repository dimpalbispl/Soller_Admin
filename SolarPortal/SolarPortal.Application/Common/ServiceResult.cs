namespace SolarPortal.Application.DTOs;

public class ServiceResult<T>
{
    public bool IsSuccess { get; set; }
    public T? Data { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = new();

    public static ServiceResult<T> Success(T data, string? message = null) =>
        new() { IsSuccess = true, Data = data, Message = message };

    public static ServiceResult<T> Failure(string error) =>
        new() { IsSuccess = false, Errors = new List<string> { error } };

    public static ServiceResult<T> Failure(List<string> errors) =>
        new() { IsSuccess = false, Errors = errors };
}