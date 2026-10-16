using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Pdf;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Tests;

/// <summary>Identities in a memory store: the tests never touch the real keychain or certificate store.</summary>
public class IdentityTests
{
    private static byte[] BlankPdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static Task<byte[]> SignWith(IdentityStore identities, SigningIdentity identity) =>
        PdfSigner.SignAsync(BlankPdf(), new SignatureRequest { Certificate = identity.Certificate, Chain = identities.ChainOf(identity) }, CancellationToken.None);

    [Fact]
    public async Task A_personal_certificate_is_made_for_signing_and_listed()
    {
        using var temp = new TempFolder();
        var identities = new IdentityStore(new MemoryCertificateStore(), temp.Sub("signature"));

        var identity = identities.CreatePersonal("Élodie Martin", "elodie@exemple.fr");

        Assert.True(identity.CreatedByUniFiles);
        Assert.True(identity.IsSelfSigned);
        Assert.Equal("Élodie Martin", identity.Name);
        Assert.Equal("elodie@exemple.fr", identity.Email);
        using var key = identity.Certificate.GetRSAPublicKey()!;
        Assert.Equal(3072, key.KeySize);
        Assert.InRange((identity.NotAfter - DateTime.Now).TotalDays, 3 * 365 - 2, 3 * 366 + 1);
        var usage = identity.Certificate.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.Equal(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, usage.KeyUsages);
        Assert.Same(identity.Id, identity.Id);
        Assert.Equal(identity.Id, Assert.Single(identities.Identities()).Id);

        var report = Assert.Single(PdfSignatureVerifier.Verify(await SignWith(identities, identity), identities.TrustedAuthorities()));
        Assert.Equal(SignatureVerdict.ValidUnverifiedIdentity, report.Verdict);
    }

    [Fact]
    public async Task An_organization_issues_certificates_its_members_import_and_its_signatures_are_trusted()
    {
        using var temp = new TempFolder();
        var office = new IdentityStore(new MemoryCertificateStore(), temp.Sub("office"));
        var authority = office.CreateAuthority("Association des Lilas");
        var p12 = office.Issue(authority, "Zoé Bernard", "zoe@lilas.fr", "s3cret-lilas");

        Assert.Empty(office.Identities()); // an authority can't sign documents
        Assert.Equal("Zoé Bernard", Assert.Single(office.Authorities()).Issued.Single().Name);

        // Zoé's computer: imports her file, the wrong password first.
        var member = new IdentityStore(new MemoryCertificateStore(), temp.Sub("member"));
        Assert.Throws<ToolException>(() => member.Import(p12, "wrong"));
        var zoe = member.Import(p12, "s3cret-lilas");
        Assert.Equal("Association des Lilas", zoe.Issuer);
        // Imported in memory only: never in the computer's keychain or certificate store.
        Assert.DoesNotContain(new SystemCertificateStore().Certificates(), c => c.Subject.Contains("Zoé Bernard") || c.Subject.Contains("Lilas"));
        Assert.False(zoe.IsSelfSigned);
        var signed = await SignWith(member, zoe);

        // Someone outside: identity unknown. The office (and anyone who accepted its .cer): verified.
        Assert.Equal(SignatureVerdict.ValidUnverifiedIdentity, Assert.Single(PdfSignatureVerifier.Verify(signed)).Verdict);
        Assert.Equal(SignatureVerdict.Valid, Assert.Single(PdfSignatureVerifier.Verify(signed, office.TrustedAuthorities())).Verdict);

        var partner = new IdentityStore(new MemoryCertificateStore(), temp.Sub("partner"));
        partner.Trust(IdentityStore.ExportPublic(authority.Certificate));
        Assert.Equal(SignatureVerdict.Valid, Assert.Single(PdfSignatureVerifier.Verify(signed, partner.TrustedAuthorities())).Verdict);
        partner.Untrust(authority.Certificate);
        Assert.Empty(partner.TrustedAuthorities());
    }

    [Fact]
    public void Only_authorities_can_be_trusted_and_only_signing_certificates_are_listed()
    {
        using var temp = new TempFolder();
        var store = new MemoryCertificateStore();
        var identities = new IdentityStore(store, temp.Sub("signature"));

        var personal = CertificateFactory.CreatePersonal("Léo Dupont", null);
        Assert.Throws<ToolException>(() => identities.Trust(personal.Export(X509ContentType.Cert)));
        Assert.Throws<ToolException>(() => identities.Trust([1, 2, 3]));

        store.Add(X509CertificateLoader.LoadCertificate(personal.RawData));        // no private key
        store.Add(CertificateFactory.CreateAuthority("Une Autorité"));             // an authority
        using var rsa = RSA.Create(2048);
        var encryptionOnly = new CertificateRequest("CN=Chiffrement", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        encryptionOnly.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
        store.Add(encryptionOnly.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1)));
        using var developerKey = RSA.Create(2048);
        var codeSigning = new CertificateRequest("CN=Apple Development", developerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        codeSigning.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        codeSigning.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], true));
        store.Add(codeSigning.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1)));
        Assert.Empty(identities.Identities());

        store.Add(personal);                                                         // with its key, from the keychain
        var listed = Assert.Single(identities.Identities());
        Assert.False(listed.CreatedByUniFiles);
        Assert.Throws<InvalidOperationException>(() => identities.Remove(listed));
    }

    /// <summary>
    /// Windows only (it runs on the CI): a signature made by UniFiles goes into the real certificate
    /// store, is read back from it as on the next launch, and signs. Its private key must have been
    /// persisted, or Windows refuses it. Everything made here is removed at the end.
    /// </summary>
    [Fact]
    public async Task On_windows_a_created_signature_still_signs_when_read_back_from_the_store()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var temp = new TempFolder();
        var identities = new IdentityStore(new SystemCertificateStore(), temp.Sub("signature"));
        var created = identities.CreatePersonal($"UniFiles Test {Guid.NewGuid():N}", null);
        try
        {
            // Read back from the store, like the app does later: a fresh object, not the one that made the key.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var reloaded = new IdentityStore(new SystemCertificateStore(), temp.Sub("signature")).Find(created.Id);
            Assert.NotNull(reloaded);
            Assert.False(reloaded.KeyMissing);
            var signed = await SignWith(identities, reloaded);
            Assert.True(Assert.Single(PdfSignatureVerifier.Verify(signed)).Intact);
        }
        finally
        {
            identities.Remove(identities.Find(created.Id) ?? created);
        }
        Assert.Null(identities.Find(created.Id));
    }

    [Fact]
    public void Removing_a_certificate_made_by_unifiles_takes_it_out_of_the_store()
    {
        using var temp = new TempFolder();
        var store = new MemoryCertificateStore();
        var identities = new IdentityStore(store, temp.Sub("signature"));
        var identity = identities.CreatePersonal("À Supprimer", null);

        identities.Remove(identity);

        Assert.Empty(identities.Identities());
        Assert.Empty(store.Certificates());
    }

    [Fact]
    public void Expired_certificates_are_listed_last_and_cannot_be_used_now()
    {
        using var temp = new TempFolder();
        var store = new MemoryCertificateStore();
        var identities = new IdentityStore(store, temp.Sub("signature"));
        store.Add(SigningTestKit.CertificateValid(DateTimeOffset.Now.AddYears(-3), DateTimeOffset.Now.AddYears(-1)));
        identities.CreatePersonal("Zélie Valide", null);

        var list = identities.Identities();
        Assert.Equal(["Zélie Valide", "Ancien Certificat"], list.Select(i => i.Name));
        Assert.True(list[0].IsValidNow);
        Assert.False(list[1].IsValidNow);
    }
}
