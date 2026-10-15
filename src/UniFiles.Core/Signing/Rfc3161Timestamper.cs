using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Signing;

/// <summary>
/// Asks a time-stamping authority (RFC 3161) to vouch for the time of a signature. Only the 32-byte
/// digest of the signature value is sent, never the document. The answer is checked (digest, nonce,
/// authority's signature) before it is used.
/// </summary>
public sealed class Rfc3161Timestamper(Uri authority, HttpMessageHandler? handler = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public Uri Authority { get; } = authority;

    public TimestampProvider Provider => RequestAsync;

    public async Task<byte[]> RequestAsync(byte[] sha256Digest, CancellationToken cancellationToken)
    {
        var request = Rfc3161TimestampRequest.CreateFromHash(sha256Digest, HashAlgorithmName.SHA256,
            nonce: RandomNumberGenerator.GetBytes(8), requestSignerCertificates: true);

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout;
        using var content = new ByteArrayContent(request.Encode());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");

        byte[] answer;
        try
        {
            using var response = await client.PostAsync(Authority, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ToolException(string.Format(Strings.Sign_TimestampFailed, Authority.Host));
            answer = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ToolException(string.Format(Strings.Sign_TimestampFailed, Authority.Host), ex);
        }

        try
        {
            var token = request.ProcessResponse(answer, out _);
            return token.AsSignedCms().Encode();
        }
        catch (CryptographicException ex)
        {
            throw new ToolException(string.Format(Strings.Sign_TimestampFailed, Authority.Host), ex);
        }
    }
}
