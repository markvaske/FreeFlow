namespace FreeFlow.Core.Services;

public static class FtpPath
{
    public static string NormalizeDirectory(string? remotePath)
    {
        var path = (remotePath ?? string.Empty).Trim().Replace('\\', '/');

        if (string.IsNullOrEmpty(path))
            return "/";

        if (!path.StartsWith('/'))
            path = "/" + path;

        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    public static string Combine(string? remoteDirectory, string fileName)
    {
        var directory = NormalizeDirectory(remoteDirectory);
        return directory == "/" ? $"/{fileName}" : $"{directory}/{fileName}";
    }
}
