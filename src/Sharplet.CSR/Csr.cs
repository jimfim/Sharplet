using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Json.Patch;
using k8s;
using k8s.Models;

namespace Sharplet.CSR;

// The steps of the CSR flow. They are methods on a named type rather than local
// functions in Program.cs because, for a top-level program, local functions count
// toward the top-level file's own cognitive complexity: S3776 kept firing no matter
// how the top-level statements were split.
internal static class Csr
{
    private static string? s_resolvedCertDir;

    public static async Task<string> GenerateCertificateAsync(string name, string keyFile)
    {
        SubjectAlternativeNameBuilder sanBuilder = new();
        sanBuilder.AddIpAddress(IPAddress.Loopback);

        // the API server dials the kubelet by pod IP (the node's InternalIP); it may also resolve the node name,
        // so both must be in the SAN or TLS validation on the server side fails
        string? podIp = Environment.GetEnvironmentVariable("VKUBELET_POD_IP") ?? Environment.GetEnvironmentVariable("POD_IP");
        if (podIp is not null)
        {
            if (IPAddress.TryParse(podIp, out IPAddress? podIpAddress))
            {
                sanBuilder.AddIpAddress(podIpAddress);
            }
            else
            {
                await Console.Error.WriteLineAsync($"{podIp} (VKUBELET_POD_IP/POD_IP) is not a valid IP address; it will not be added to the certificate SAN");
            }
        }
        sanBuilder.AddDnsName(name);
        if (IPAddress.TryParse(name, out IPAddress? nameIpAddress))
        {
            sanBuilder.AddIpAddress(nameIpAddress);
        }

        X500DistinguishedName distinguishedName = new($"CN=system:node:{name},O=system:nodes");

        using RSA rsa = RSA.Create(4096);
        CertificateRequest request = new(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        string privateKeyPem = rsa.ExportRSAPrivateKeyPem();
        string? keyDir = Path.GetDirectoryName(keyFile);
        if (!string.IsNullOrEmpty(keyDir))
        {
            Directory.CreateDirectory(keyDir);
        }
        await File.WriteAllTextAsync(keyFile, privateKeyPem);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false)); // server auth

        request.CertificateExtensions.Add(sanBuilder.Build());
        byte[] csr = request.CreateSigningRequest();
        string pemKey = "-----BEGIN CERTIFICATE REQUEST-----\r\n" +
                        Convert.ToBase64String(csr) +
                        "\r\n-----END CERTIFICATE REQUEST-----";

        return pemKey;
    }

    public static async Task<string> ResolveCertificateDirectoryAsync()
    {
        if (s_resolvedCertDir is not null)
        {
            return s_resolvedCertDir;
        }
        string etcDir = "/etc/sharplet";
        try
        {
            Directory.CreateDirectory(etcDir);
            s_resolvedCertDir = etcDir;
            return etcDir;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string homeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharplet");
            Directory.CreateDirectory(homeDir);
            s_resolvedCertDir = homeDir;
            await Console.Error.WriteLineAsync($"{etcDir} is not writable; wrote to {homeDir} instead.");
            await Console.Error.WriteLineAsync($"point the app there: APISERVER_CERT_LOCATION={Path.Combine(homeDir, "cert.pem")} APISERVER_KEY_LOCATION={Path.Combine(homeDir, "key.pem")}");
            return homeDir;
        }
    }

    // Approve the CSR (DEV shortcut: the tool patches status.conditions itself, which only works
    // with cluster-admin; in production a human or an approval policy approves the
    // 'kubernetes.io/kubelet-serving' CSR instead - keep this path for throwaway clusters like
    // minikube only) and wait for the cluster to sign it. Returns the certificate PEM, or null
    // when the cluster has not signed the request.
    public static async Task<byte[]?> ApproveCertificateAsync(IKubernetes client, string name)
    {
        JsonSerializerOptions serializeOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        V1CertificateSigningRequest readCert = await client.CertificatesV1.ReadCertificateSigningRequestAsync(name);
        JsonDocument old = JsonSerializer.SerializeToDocument(readCert, serializeOptions);

        List<V1CertificateSigningRequestCondition> replace = new()
        {
            new V1CertificateSigningRequestCondition
            {
                Status = "True",
                Type = "Approved",
                LastTransitionTime = DateTime.UtcNow,
                LastUpdateTime = DateTime.UtcNow,
                Message = "This certificate was approved by k8s client",
                Reason = "Approve"
            }
        };
        readCert.Status.Conditions = replace;

        JsonDocument expected = JsonSerializer.SerializeToDocument(readCert, serializeOptions);

        JsonPatch patch = old.CreatePatch(expected);
        await client.CertificatesV1.PatchCertificateSigningRequestApprovalAsync(new V1Patch(patch, V1Patch.PatchType.JsonPatch),
            name);
        await Task.Delay(2000);
        V1CertificateSigningRequest latest = await client.CertificatesV1.ReadCertificateSigningRequestAsync(name);
        return latest.Status.Certificate;
    }

    // Load the signed certificate PEM and write it to disk, creating the target directory if needed.
    public static async Task WriteCertificateAsync(byte[] certificatePem, string certFile)
    {
        X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(certificatePem);
        string? certDir = Path.GetDirectoryName(certFile);
        if (!string.IsNullOrEmpty(certDir))
        {
            Directory.CreateDirectory(certDir);
        }
        await File.WriteAllTextAsync(certFile, certificate.ExportCertificatePem());
    }
}