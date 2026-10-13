using System.Security.Cryptography;

namespace UniFiles.Core.Tests;

/// <summary>A throwaway folder per test, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unifiles-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Sub(string name)
    {
        var path = File(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
    }
}
