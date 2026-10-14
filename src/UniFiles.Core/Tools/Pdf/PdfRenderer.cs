using System.Runtime.InteropServices;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Pdf;

/// <summary>
/// Draws PDF pages with PDFium (Chrome's PDF engine), for the page organizer's thumbnails.
/// PDFium isn't thread-safe: every call goes through one lock.
/// </summary>
public sealed partial class PdfRenderer : IDisposable
{
    private const string Library = "pdfium";
    private const int RenderAnnotations = 0x01;
    private const uint White = 0xFFFFFFFF;

    private static readonly object Sync = new();
    private static bool _initialized;

    private readonly IntPtr _document;
    // PDFium reads the file from this memory until the document is closed.
    private readonly IntPtr _data;

    private PdfRenderer(IntPtr document, IntPtr data, int pageCount)
    {
        _document = document;
        _data = data;
        PageCount = pageCount;
    }

    public int PageCount { get; }

    /// <summary>Opens a PDF (read from memory, so any file name works on every system).</summary>
    public static unsafe PdfRenderer Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        lock (Sync)
        {
            if (!_initialized)
            {
                InitLibrary();
                _initialized = true;
            }
            var data = (IntPtr)NativeMemory.Alloc((nuint)bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);
            var document = LoadMemDocument64(data, (nuint)bytes.Length, null);
            if (document == IntPtr.Zero)
            {
                NativeMemory.Free((void*)data);
                // 4: password required.
                throw new ToolException(GetLastError() == 4 ? Strings.Error_PdfProtected : Strings.Error_PdfUnreadable);
            }
            return new PdfRenderer(document, data, GetPageCount(document));
        }
    }

    /// <summary>
    /// Draws a page so that its longer side measures <paramref name="maxSide"/> pixels, on white.
    /// Returns BGRA pixels, rows of <c>width * 4</c> bytes.
    /// </summary>
    public unsafe byte[] Render(int pageIndex, int maxSide, out int width, out int height)
    {
        lock (Sync)
        {
            var page = LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero)
                throw new ToolException(Strings.Error_PdfUnreadable);
            try
            {
                var pageWidth = GetPageWidthF(page);
                var pageHeight = GetPageHeightF(page);
                var scale = maxSide / Math.Max(pageWidth, pageHeight);
                width = Math.Max(1, (int)Math.Round(pageWidth * scale));
                height = Math.Max(1, (int)Math.Round(pageHeight * scale));

                var bitmap = BitmapCreate(width, height, 0);
                try
                {
                    BitmapFillRect(bitmap, 0, 0, width, height, White);
                    RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, RenderAnnotations);
                    var stride = BitmapGetStride(bitmap);
                    var buffer = (byte*)BitmapGetBuffer(bitmap);
                    var pixels = new byte[width * height * 4];
                    for (var y = 0; y < height; y++)
                        Marshal.Copy((IntPtr)(buffer + (long)y * stride), pixels, y * width * 4, width * 4);
                    // The "no alpha" bitmap leaves the 4th byte undefined: make it opaque.
                    for (var i = 3; i < pixels.Length; i += 4)
                        pixels[i] = 255;
                    return pixels;
                }
                finally
                {
                    BitmapDestroy(bitmap);
                }
            }
            finally
            {
                ClosePage(page);
            }
        }
    }

    public unsafe void Dispose()
    {
        lock (Sync)
        {
            CloseDocument(_document);
            NativeMemory.Free((void*)_data);
        }
    }

    [LibraryImport(Library, EntryPoint = "FPDF_InitLibrary")]
    private static partial void InitLibrary();

    [LibraryImport(Library, EntryPoint = "FPDF_LoadMemDocument64", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr LoadMemDocument64(IntPtr data, nuint size, string? password);

    [LibraryImport(Library, EntryPoint = "FPDF_GetLastError")]
    private static partial uint GetLastError();

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageCount")]
    private static partial int GetPageCount(IntPtr document);

    [LibraryImport(Library, EntryPoint = "FPDF_LoadPage")]
    private static partial IntPtr LoadPage(IntPtr document, int index);

    [LibraryImport(Library, EntryPoint = "FPDF_ClosePage")]
    private static partial void ClosePage(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDF_CloseDocument")]
    private static partial void CloseDocument(IntPtr document);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageWidthF")]
    private static partial float GetPageWidthF(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageHeightF")]
    private static partial float GetPageHeightF(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_Create")]
    private static partial IntPtr BitmapCreate(int width, int height, int alpha);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_FillRect")]
    private static partial int BitmapFillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library, EntryPoint = "FPDF_RenderPageBitmap")]
    private static partial void RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_GetBuffer")]
    private static partial IntPtr BitmapGetBuffer(IntPtr bitmap);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_GetStride")]
    private static partial int BitmapGetStride(IntPtr bitmap);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_Destroy")]
    private static partial void BitmapDestroy(IntPtr bitmap);
}
