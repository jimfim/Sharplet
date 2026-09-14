using System.Text;
using Sharplet.CSR;
using k8s;
using k8s.Models;

// The CSR tool's orchestration: config -> key + CSR generation -> delete stale CSR ->
// create -> approve -> write the certificate out. The steps themselves live on Csr.
KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
Kubernetes client = new(config);
string name = Environment.GetEnvironmentVariable("SHARPLET_NODE_NAME") ?? "sharplet";
string certFile = Environment.GetEnvironmentVariable("APISERVER_CERT_LOCATION") ?? Path.Combine(Csr.ResolveCertificateDirectory(), "cert.pem");
string keyFile = Environment.GetEnvironmentVariable("APISERVER_KEY_LOCATION") ?? Path.Combine(Csr.ResolveCertificateDirectory(), "key.pem");

string x509 = Csr.GenerateCertificate(name, keyFile);
byte[] encodedCsr = Encoding.UTF8.GetBytes(x509);
try
{
    await client.CertificatesV1.DeleteCertificateSigningRequestWithHttpMessagesAsync(name);
}
catch
{
    // Expected on first run: there is no CSR to delete. Any other failure (e.g. the API
    // being unreachable) will surface when the create below is attempted.
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

byte[]? certificate = await Csr.ApproveCertificateAsync(client, name);
if (certificate is null)
{
    await Console.Error.WriteLineAsync($"CSR '{name}' was not signed by the cluster; nothing to write.");
    return 1;
}

await Csr.WriteCertificateAsync(certificate, certFile);

await Console.Out.WriteLineAsync($"certificate written to {certFile}");
await Console.Out.WriteLineAsync($"private key written to {keyFile}");
return 0;