using NetVips;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Jobs;

/// <summary>Turns exceptions into short explanations a non-technical user can act on.</summary>
public static class ErrorDescriber
{
    private const int DiskFullHResult = unchecked((int)0x80070070);

    public static string Describe(Exception exception) => exception switch
    {
        ToolException tool => tool.Message,
        FileNotFoundException or DirectoryNotFoundException => Strings.Error_FileNotFound,
        UnauthorizedAccessException => Strings.Error_AccessDenied,
        IOException io when io.HResult == DiskFullHResult => Strings.Error_DiskFull,
        IOException => Strings.Error_FileInUse,
        VipsException => Strings.Error_ImageUnreadable,
        OutOfMemoryException => Strings.Error_OutOfMemory,
        _ => Strings.Error_Unknown,
    };
}
