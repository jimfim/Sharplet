// See https://aka.ms/new-console-template for more information

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Json.Patch;
using k8s;
using k8s.Models;

string GenerateCertificate(string name)
{
    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddIpAddress(IPAddress.Loopback);
    // sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
    // sanBuilder.AddDnsName("localhost");
    // sanBuilder.AddDnsName(Environment.MachineName);

    var distinguishedName = new X500DistinguishedName($"CN=system:node:{name},O=system:nodes");

    using var rsa = RSA.Create(4096);
    var request = new CertificateRequest(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var privateKeyPem = rsa.ExportRSAPrivateKeyPem();
    var privateKeyBase64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(privateKeyPem));
    //Console.WriteLine("--- private");
    Console.WriteLine($"apiserverKey: {privateKeyBase64}");
    //File.WriteAllText("/etc/virtual-kubelet/key.pem", privateKeyPem);
    request.CertificateExtensions.Add(
        new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));
    request.CertificateExtensions.Add(
        new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false)); // server auth
    //request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false)); // client auth

    request.CertificateExtensions.Add(sanBuilder.Build());
    var csr = request.CreateSigningRequest();
    var pemKey = "-----BEGIN CERTIFICATE REQUEST-----\r\n" +
                 Convert.ToBase64String(csr) +
                 "\r\n-----END CERTIFICATE REQUEST-----";

    return pemKey;
}


var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
IKubernetes client = new Kubernetes(config);
//Console.WriteLine("Starting Request!");
var name = "demo";
var x509 = GenerateCertificate(name);
var encodedCsr = Encoding.UTF8.GetBytes(x509);
try
{
    await client.CertificatesV1.DeleteCertificateSigningRequestWithHttpMessagesAsync(name);
}
catch
{
}

var request = new V1CertificateSigningRequest
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
        //SignerName = "kubernetes.io/kube-apiserver-client-kubelet",
        Usages = new List<string> { "key encipherment", "digital signature", "server auth" },
        //Usages = new List<string> { "key encipherment", "digital signature", "client auth" },
        ExpirationSeconds = 600 // minimum should be 10 minutes
    }
};

await client.CertificatesV1.CreateCertificateSigningRequestAsync(request);

var serializeOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
var readCert = await client.CertificatesV1.ReadCertificateSigningRequestAsync(name);
var old = JsonSerializer.SerializeToDocument(readCert, serializeOptions);

var replace = new List<V1CertificateSigningRequestCondition>
{
    new("True", "Approved", DateTime.UtcNow, DateTime.UtcNow, "This certificate was approved by k8s client", "Approve")
};
readCert.Status.Conditions = replace;

var expected = JsonSerializer.SerializeToDocument(readCert, serializeOptions);

var patch = old.CreatePatch(expected);
await client.CertificatesV1.PatchCertificateSigningRequestApprovalAsync(new V1Patch(patch, V1Patch.PatchType.JsonPatch),
    name);
await Task.Delay(2000);
var latest = await client.CertificatesV1.ReadCertificateSigningRequestAsync(name);

Console.WriteLine($"apiserverCert: {Convert.ToBase64String(latest.Status.Certificate)}");