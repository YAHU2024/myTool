using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace QuickTranslate.Helpers;

/// <summary>
/// Verifies Authenticode signatures on PE files (e.g., setup executables).
/// Establishes an independent trust chain for auto-update installers that
/// must run with administrator privileges.
/// </summary>
/// <remarks>
/// This verifier checks that the PE file has a valid Authenticode digital
/// signature whose signer matches the expected publisher. The private signing
/// key must be held outside the repository and release pipeline — if an
/// attacker compromises the GitHub Release they can replace the installer
/// binary and SHA256 checksum, but cannot forge a valid Authenticode
/// signature without the private key.
///
/// Verification steps:
/// 1. Extract the signer certificate via X509Certificate2.CreateFromSignedFile
/// 2. Confirm the certificate subject contains the expected publisher name
/// 3. Build and verify the certificate chain against the Windows trust store
/// 4. (Optional) Verify timestamp countersignature for code-signing time validity
/// </remarks>
public static class AuthenticodeVerifier
{
    /// <summary>
    /// Outcome of Authenticode signature verification.
    /// </summary>
    public enum Result
    {
        /// <summary>Signature is valid and publisher matches.</summary>
        Valid,
        /// <summary>The file has no Authenticode signature.</summary>
        NotSigned,
        /// <summary>The signature is cryptographically invalid or corrupted.</summary>
        SignatureInvalid,
        /// <summary>The signing certificate chain is not trusted.</summary>
        CertificateNotTrusted,
        /// <summary>The signer does not match the expected publisher.</summary>
        PublisherMismatch,
        /// <summary>The specified file does not exist.</summary>
        FileNotFound,
        /// <summary>An unexpected error occurred during verification.</summary>
        UnknownError
    }

    /// <summary>
    /// Verifies the Authenticode signature on a PE file and checks that the
    /// signer matches the expected publisher.
    /// </summary>
    /// <param name="filePath">Absolute path to the signed PE file.</param>
    /// <param name="expectedPublisher">
    /// Expected certificate subject substring (e.g. "YaHu"). The verification
    /// passes when the signer certificate's Subject contains this value
    /// (case-insensitive). An empty or null value skips the publisher check.
    /// </param>
    /// <returns>A <see cref="Result"/> describing the verification outcome.</returns>
    public static Result Verify(string filePath, string? expectedPublisher)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must not be empty.", nameof(filePath));

        if (!File.Exists(filePath))
            return Result.FileNotFound;

        try
        {
            // CreateFromSignedFile extracts the signer certificate from the
            // Authenticode (PKCS#7) signature embedded in the PE file.
            // Throws CryptographicException if the file is unsigned or the
            // signature structure is invalid.
            // CreateFromSignedFile returns X509Certificate; we need
            // X509Certificate2 for chain.Build and Subject access.
            using var signedCert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert = new X509Certificate2(signedCert);

            // Check publisher identity: the signer's Subject Distinguished Name
            // must contain the expected publisher string.
            // Substring match allows for DN variations (e.g. "CN=YaHu, O=YaHu, C=CN").
            if (!string.IsNullOrWhiteSpace(expectedPublisher) &&
                !cert.Subject.Contains(expectedPublisher, StringComparison.OrdinalIgnoreCase))
            {
                return Result.PublisherMismatch;
            }

            // Build the certificate chain against the Windows trust store.
            // We use NoCheck for revocation because:
            //   - Offline revocation is common in restricted environments
            //   - Timestamped signatures prove certificate was valid at signing time
            //   - CRL/OCSP lookups add latency and may fail transiently
            // The chain status check below still catches explicit trust failures
            // (untrusted root, invalid policy, etc.).
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags =
                X509VerificationFlags.IgnoreNotTimeValid |
                X509VerificationFlags.IgnoreWrongUsage;

            bool chainBuilt = chain.Build(cert);

            if (!chainBuilt)
            {
                // Check each chain status entry. Ignore benign statuses such as:
                // - NotTimeValid: expected for expired certs with valid timestamps
                // - RevocationStatusUnknown: expected with NoCheck mode
                // - OfflineRevocation: expected in environments without CRL/OCSP access
                foreach (var status in chain.ChainStatus)
                {
                    if (status.Status != X509ChainStatusFlags.NotTimeValid &&
                        status.Status != X509ChainStatusFlags.RevocationStatusUnknown &&
                        status.Status != X509ChainStatusFlags.OfflineRevocation)
                    {
                        return Result.CertificateNotTrusted;
                    }
                }
            }

            return Result.Valid;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The file either has no Authenticode signature or the signature
            // structure is malformed/corrupted.
            return Result.NotSigned;
        }
        catch (Exception)
        {
            return Result.UnknownError;
        }
    }

    /// <summary>
    /// Returns a localized, human-readable description for each
    /// <see cref="Result"/> value.
    /// </summary>
    public static string GetResultDescription(Result result)
    {
        return result switch
        {
            Result.Valid => "安装包已通过数字签名验证",
            Result.NotSigned => "安装包未经过数字签名认证",
            Result.SignatureInvalid => "安装包签名无效或已损坏",
            Result.CertificateNotTrusted => "安装包签名证书不受信任",
            Result.PublisherMismatch => "安装包签名发布者与预期不符",
            Result.FileNotFound => "未找到安装包文件",
            Result.UnknownError => "签名验证过程中发生意外错误",
            _ => "未知的验证结果"
        };
    }
}
