using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Pdf;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Tests;

/// <summary>Time stamps from a local stand-in authority: real RFC 3161 tokens, no network.</summary>
public class TimestampTests
{
    private static readonly Uri Service = new("https://tsa.test/");

    /// <summary>A time-stamping authority answering in memory. <c>Tamper</c> changes the stamped digest.</summary>
    private sealed class FakeAuthority : HttpMessageHandler
    {
        private readonly X509Certificate2 _certificate = Certificate();

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public bool Tamper { get; init; }

        public DateTimeOffset Time { get; } = DateTimeOffset.UtcNow.AddSeconds(-1);

        public int Requests { get; private set; }

        public long LastRequestLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var query = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            LastRequestLength = query.Length;
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status);
            Assert.Equal("application/timestamp-query", request.Content.Headers.ContentType!.MediaType);
            Assert.True(Rfc3161TimestampRequest.TryDecode(query, out var parsed, out _));
            var digest = parsed!.GetMessageHash().ToArray();
            if (Tamper)
                digest[0] ^= 0xFF;

            var info = new Rfc3161TimestampTokenInfo(new Oid("1.3.6.1.4.1.99999.1"), parsed.HashAlgorithmId, digest,
                RandomNumberGenerator.GetBytes(8), Time, nonce: parsed.GetNonce());
            var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
            var signer = new CmsSigner(_certificate) { DigestAlgorithm = new Oid(CadesSigner.Sha256), IncludeOption = X509IncludeOption.EndCertOnly };
            signer.SignedAttributes.Add(new AsnEncodedData(CadesSigner.SigningCertificateV2, CadesSigner.EncodeSigningCertificateV2(_certificate)));
            cms.ComputeSignature(signer);

            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                    writer.WriteInteger(0); // granted
                writer.WriteEncodedValue(cms.Encode());
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(writer.Encode()) };
        }

        private static X509Certificate2 Certificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Horodatage de test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
            using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
        }
    }

    private static byte[] BlankPdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static Task<byte[]> SignWith(FakeAuthority authority) =>
        PdfSigner.SignAsync(BlankPdf(), new SignatureRequest
        {
            Certificate = SigningTestKit.Certificate(),
            Timestamp = new Rfc3161Timestamper(Service, authority).Provider,
        }, CancellationToken.None);

    [Fact]
    public async Task A_time_stamped_signature_is_pades_b_t_with_a_proven_time()
    {
        var authority = new FakeAuthority();
        var signed = await SignWith(authority);

        Assert.Equal(1, authority.Requests);
        Assert.True(authority.LastRequestLength < 120, "Only a digest is sent, never the document.");
        var report = Assert.Single(PdfSignatureVerifier.Verify(signed));
        Assert.True(report.Intact);
        Assert.Equal("PAdES B-T", report.Format);
        Assert.NotNull(report.TimestampTime);
        Assert.InRange((report.TimestampTime!.Value - authority.Time).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_failing_service_stops_the_signature_with_a_clear_message()
    {
        var error = await Assert.ThrowsAsync<ToolException>(() => SignWith(new FakeAuthority { Status = HttpStatusCode.InternalServerError }));
        Assert.Contains("tsa.test", error.Message);
    }

    [Fact]
    public async Task A_token_for_another_digest_is_refused()
    {
        await Assert.ThrowsAsync<ToolException>(() => SignWith(new FakeAuthority { Tamper = true }));
    }
}
