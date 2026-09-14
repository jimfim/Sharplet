using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Json.Patch;
using k8s;
using k8s.Models;

string GenerateCertificate(string name, string keyFile)
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
            Console.Error.WriteLine($"{podIp} (VKUBELET_POD_IP/POD_IP) is not a valid IP address; it will not be added to the certificate SAN");
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
    File.WriteAllText(keyFile, privateKeyPem);

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

string? resolvedCertDir = null;
string ResolveCertDirectory()
{
    if (resolvedCertDir is not null)
    {
        return resolvedCertDir;
    }
    string etcDir = "/etc/sharplet";
    try
    {
        Directory.CreateDirectory(etcDir);
        resolvedCertDir = etcDir;
        return etcDir;
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        string homeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharplet");
        Directory.CreateDirectory(homeDir);
        resolvedCertDir = homeDir;
        Console.Error.WriteLine($"{etcDir} is not writable; wrote to {homeDir} instead.");
        Console.Error.WriteLine($"point the app there: APISERVER_CERT_LOCATION={Path.Combine(homeDir, "cert.pem")} APISERVER_KEY_LOCATION={Path.Combine(homeDir, "key.pem")}");
        return homeDir;
    }
}

// Approve the CSR (DEV shortcut: the tool patches status.conditions itself, which only works
// with cluster-admin; in production a human or an approval policy approves the
// 'kubernetes.io/kubelet-serving' CSR instead - keep this path for throwaway clusters like
// minikube only) and wait for the cluster to sign it. Returns the certificate PEM, or null
// when the cluster has not signed the request.
async Task<byte[]?> ApproveCertificateAsync(IKubernetes client, string name)
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
async Task WriteCertificateAsync(byte[] certificatePem, string certFile)
{
    X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(certificatePem);
    string? certDir = Path.GetDirectoryName(certFile);
    if (!string.IsNullOrEmpty(certDir))
    {
        Directory.CreateDirectory(certDir);
    }
    await File.WriteAllTextAsync(certFile, certificate.ExportCertificatePem());
}

string certFile = Environment.GetEnvironmentVariable("APISERVER_CERT_LOCATION") ?? Path.Combine(ResolveCertDirectory(), "cert.pem");
string keyFile = Environment.GetEnvironmentVariable("APISERVER_KEY_LOCATION") ?? Path.Combine(ResolveCertDirectory(), "key.pem");

KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
IKubernetes client = new Kubernetes(config);
string name = Environment.GetEnvironmentVariable("SHARPLET_NODE_NAME") ?? "sharplet";
string x509 = GenerateCertificate(name, keyFile);
byte[] encodedCsr = Encoding.UTF8.GetBytes(x509);
try
{
    await client.CertificatesV1.DeleteCertificateSigningRequestWithHttpMessagesAsync(name);
}
catch
{
}

V1CertificateSigningRequest request = new()
{
    ApiVersion = "certificates.k8s.io/v1",
    Kind = "CertificateSigningRequest",
    Metadata = new V1ObjectMeta
    {
        Name = name
    },
    Spec = new V1CertificateSigningRequestSpec
    {
        Request = encodedCsr,
        SignerName = "kubernetes.io/kubelet-serving",
        Usages = new List<string> { "key encipherment", "digital signature", "server auth" },
        ExpirationSeconds = 62208000 // 720 days: max the kubelet-serving signer allows, what real kubelets request
    }
};

await client.CertificatesV1.CreateCertificateSigningRequestAsync(request);

byte[]? certificate = await ApproveCertificateAsync(client, name);

if (certificate is null)
{
    await Console.Error.WriteLineAsync($"CSR '{name}' was not signed by the cluster; nothing to write.");
    return 1;
}

await WriteCertificateAsync(certificate, certFile);

Console.WriteLine($"certificate written to {certFile}");
Console.WriteLine($"private key written to {keyFile}");
return 0;