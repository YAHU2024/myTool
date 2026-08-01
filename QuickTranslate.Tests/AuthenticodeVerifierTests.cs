using System;
using System.IO;
using QuickTranslate.Helpers;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class AuthenticodeVerifierTests
{
    [Fact]
    public void Verify_ReturnsFileNotFound_ForMissingFile()
    {
        var missingPath = Path.Combine(Path.GetTempPath(),
            $"auth-verifier-test-{Guid.NewGuid():N}.exe");

        var result = AuthenticodeVerifier.Verify(missingPath, "TestPublisher");

        Assert.Equal(AuthenticodeVerifier.Result.FileNotFound, result);
    }

    [Fact]
    public void Verify_ReturnsNotSigned_ForUnsignedFile()
    {
        // Create a dummy file that is NOT a PE, so it has no Authenticode sig
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"auth-verifier-test-{Guid.NewGuid():N}.bin");
        File.WriteAllText(tempFile, "this is not a signed PE file");

        try
        {
            var result = AuthenticodeVerifier.Verify(tempFile, "TestPublisher");

            // A plain text file has no Authenticode signature
            Assert.Equal(AuthenticodeVerifier.Result.NotSigned, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Verify_ReturnsNotSigned_ForEmptyFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"auth-verifier-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tempFile, Array.Empty<byte>());

        try
        {
            var result = AuthenticodeVerifier.Verify(tempFile, "TestPublisher");
            Assert.Equal(AuthenticodeVerifier.Result.NotSigned, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Verify_ThrowsArgumentException_ForNullOrWhitespacePath()
    {
        Assert.Throws<ArgumentException>(() =>
            AuthenticodeVerifier.Verify("", "TestPublisher"));

        Assert.Throws<ArgumentException>(() =>
            AuthenticodeVerifier.Verify("   ", "TestPublisher"));

        Assert.Throws<ArgumentException>(() =>
            AuthenticodeVerifier.Verify(null!, "TestPublisher"));
    }

    [Fact]
    public void Verify_NullPublisher_StillChecksSignatureExistence()
    {
        // When expectedPublisher is null, publisher matching is skipped
        // but signature existence is still checked.
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"auth-verifier-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tempFile, new byte[] { 0x00, 0x01, 0x02 });

        try
        {
            var result = AuthenticodeVerifier.Verify(tempFile, null);
            Assert.Equal(AuthenticodeVerifier.Result.NotSigned, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetResultDescription_ReturnsNonEmpty_ForAllEnumValues()
    {
        foreach (AuthenticodeVerifier.Result result in
                 Enum.GetValues<AuthenticodeVerifier.Result>())
        {
            var desc = AuthenticodeVerifier.GetResultDescription(result);
            Assert.False(string.IsNullOrWhiteSpace(desc),
                $"Description for {result} should not be empty");
        }
    }

    [Fact]
    public void GetResultDescription_Valid_ReturnsSuccessMessage()
    {
        var desc = AuthenticodeVerifier.GetResultDescription(
            AuthenticodeVerifier.Result.Valid);
        Assert.Contains("通过", desc);
    }

    [Fact]
    public void GetResultDescription_PublisherMismatch_MentionsPublisher()
    {
        var desc = AuthenticodeVerifier.GetResultDescription(
            AuthenticodeVerifier.Result.PublisherMismatch);
        Assert.Contains("发布者", desc);
    }

    [Fact]
    public void GetResultDescription_CertificateNotTrusted_MentionsTrust()
    {
        var desc = AuthenticodeVerifier.GetResultDescription(
            AuthenticodeVerifier.Result.CertificateNotTrusted);
        Assert.Contains("不受信任", desc);
    }

    [Fact]
    public void Verify_UnsignedExe_ReturnsNotSigned()
    {
        // Create a minimal unsigned EXE stub via a byte array that
        // starts with the MZ header but has no Authenticode section.
        // X509Certificate2.CreateFromSignedFile throws CryptographicException
        // for unsigned PE files, which we catch as NotSigned.
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"auth-verifier-test-{Guid.NewGuid():N}.exe");

        // Minimal DOS header (MZ) — enough to look like a PE but unsigned
        byte[] minimalDosStub = {
            0x4D, 0x5A, // "MZ" magic
            0x90, 0x00, 0x03, 0x00, 0x00, 0x00, // bytes in last page, etc.
            0x04, 0x00, 0x00, 0x00, // header paragraphs
            0xFF, 0xFF, // max paragraphs
            0x00, 0x00, // initial SS
            0xB8, 0x00, 0x00, 0x00, // initial SP
            0x00, 0x00, 0x00, 0x00, // checksum
            0x00, 0x00, 0x00, 0x00, // initial IP
            0x00, 0x00, 0x00, 0x00, // initial CS
            0x40, 0x00, 0x00, 0x00, // reloc table offset
            0x00, 0x00, 0x00, 0x00, // overlay
        };

        File.WriteAllBytes(tempFile, minimalDosStub);

        try
        {
            var result = AuthenticodeVerifier.Verify(tempFile, "TestPublisher");
            // Windows may still attempt to parse the signature section;
            // the file either has no signature (NotSigned) or is
            // cryptographically invalid (NotSigned).
            Assert.Equal(AuthenticodeVerifier.Result.NotSigned, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Manual verification test that validates AuthenticodeVerifier against
    /// a real signed PE file. Driven by environment variables:
    ///   AUTHENTICODE_TEST_FILE   — absolute path to the signed .exe
    ///   AUTHENTICODE_EXPECTED_PUB  — expected publisher substring (default "YaHu")
    ///
    /// Usage (after setting up a self-signed cert via scripts/test-authenticode.ps1):
    ///   $env:AUTHENTICODE_TEST_FILE = "C:\temp\signed-test.exe"
    ///   $env:AUTHENTICODE_EXPECTED_PUB = "YaHu"
    ///   dotnet test QuickTranslate.Tests --filter "ManualVerify_SignedFile"
    /// </summary>
    [SkippableFact]
    public void ManualVerify_SignedFile()
    {
        var testFile = Environment.GetEnvironmentVariable("AUTHENTICODE_TEST_FILE");
        Skip.If(string.IsNullOrEmpty(testFile),
            "Set AUTHENTICODE_TEST_FILE env var to a signed PE file path to run this test.");

        var expectedPublisher = Environment.GetEnvironmentVariable("AUTHENTICODE_EXPECTED_PUB") ?? "YaHu";

        Assert.True(File.Exists(testFile),
            $"Test file not found: {testFile}");

        var result = AuthenticodeVerifier.Verify(testFile, expectedPublisher);

        Assert.Equal(AuthenticodeVerifier.Result.Valid, result);
    }

    /// <summary>
    /// Negative counterpart: verifies that an unsigned file fails with
    /// the expected result. Driven by:
    ///   AUTHENTICODE_BAD_FILE — path to unsigned/tampered file
    ///   AUTHENTICODE_BAD_EXPECTED — expected Result enum (e.g., "NotSigned")
    /// </summary>
    [SkippableFact]
    public void ManualVerify_BadFile()
    {
        var testFile = Environment.GetEnvironmentVariable("AUTHENTICODE_BAD_FILE");
        Skip.If(string.IsNullOrEmpty(testFile),
            "Set AUTHENTICODE_BAD_FILE env var to an unsigned/tampered file path.");

        var expectedResultStr = Environment.GetEnvironmentVariable("AUTHENTICODE_BAD_EXPECTED") ?? "NotSigned";

        Assert.True(File.Exists(testFile),
            $"Test file not found: {testFile}");

        var result = AuthenticodeVerifier.Verify(testFile, "YaHu");

        var expectedResult = Enum.Parse<AuthenticodeVerifier.Result>(expectedResultStr);
        Assert.Equal(expectedResult, result);
    }
}
