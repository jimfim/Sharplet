using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using k8s;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Sharplet.Core.Tests;

/// <summary>
/// <c>AddVirtualKubelet</c> wires Kestrel onto the kubelet's real listeners: 10255 (read-only
/// HTTP: livez/readyz/healthz, pods, stats) and 10250 (HTTPS, what the API server dials for
/// pod logs and exec; a client certificate is required and, when a client CA is configured,
/// verified against it). These tests start the app for real and probe both listeners,
/// including the ~/.sharplet certificate fallback used for local debugging. The embedded
/// certificate is a throwaway self-signed pair (CN sharplet-test, valid to 2126) — never
/// use it outside tests.
/// </summary>
public class KubeletListenerTests
{
    private const string ServerCertPem = """
        -----BEGIN CERTIFICATE-----
        MIIDEzCCAfugAwIBAgIUHm85jNCm7A9D4T7hGEKJlNb9W1AwDQYJKoZIhvcNAQEL
        BQAwGDEWMBQGA1UEAwwNc2hhcnBsZXQtdGVzdDAgFw0yNjA5MTUxODE5MDNaGA8y
        MTI2MDgyMjE4MTkwM1owGDEWMBQGA1UEAwwNc2hhcnBsZXQtdGVzdDCCASIwDQYJ
        KoZIhvcNAQEBBQADggEPADCCAQoCggEBAL1VsNHeZY0bYtKQvWDzPtaHI9QbMzqI
        i/ooy2C7RfkJT/UBbM1QX9s0Pp4VraBskeI4koMEcCm1oe9UHKe+Q1Z3uJ2NA496
        Dvvq0BGDCgGh4KoS1b2ttB81HEtipfZvB7JWCYImFPGFjDcnSpKi2y5zfSuZYKyp
        2HPuRneVYCwKNfq9oGGjy17BSu12q3oAlUvx2ZQ8qZRMK7N/GhG47X0S3dAG1/L2
        qdv5bLnQeYYb4Zd6+4rbBUD3xMugtOPuCgjoHBErR+L2gM5x9q3gJb2U1z0cIPu5
        9Wt+6c83y0T6MW+wnRccmaE7reK7eUc82Bw6zI+U3SajttOn461aqkcCAwEAAaNT
        MFEwHQYDVR0OBBYEFPAVVncJoYEayZu6N6eHaVN8zOWsMB8GA1UdIwQYMBaAFPAV
        VncJoYEayZu6N6eHaVN8zOWsMA8GA1UdEwEB/wQFMAMBAf8wDQYJKoZIhvcNAQEL
        BQADggEBACtS87QP6F0GQ61zzHGb/uv9cHtmFNCPY+LB8F0PMK8Xh/HN1BUXfate
        E96IQjpWJLKyXUxMFVdOHGP/VE6u9d3JVfYA9dys4rb0WYGiV95lkEMFoW80E6q2
        1Yuwjt1UvKlYDKQqqh4EBGgYbWbTm4I1OEs6yJwi4QuLsNrEH75/qXp/P5BITaaU
        B5L58bXbfy9ao2oD1SwIG46fmsubuSemaGdW/M4QqpXVP1jnu6VeLiXG4hMXgJsV
        PFtDGlMYof2ELuHUDoostBS7F6x+8vfByhMJnMArPUk1LI/dg4kTIYmMxoeFpAGY
        03SpBdnmuj2H+MF1KEMsHtgaDG1TlRc=
        -----END CERTIFICATE-----
        """;

    private const string ServerKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC9VbDR3mWNG2LS
        kL1g8z7WhyPUGzM6iIv6KMtgu0X5CU/1AWzNUF/bND6eFa2gbJHiOJKDBHAptaHv
        VBynvkNWd7idjQOPeg776tARgwoBoeCqEtW9rbQfNRxLYqX2bweyVgmCJhTxhYw3
        J0qSotsuc30rmWCsqdhz7kZ3lWAsCjX6vaBho8tewUrtdqt6AJVL8dmUPKmUTCuz
        fxoRuO19Et3QBtfy9qnb+Wy50HmGG+GXevuK2wVA98TLoLTj7goI6BwRK0fi9oDO
        cfat4CW9lNc9HCD7ufVrfunPN8tE+jFvsJ0XHJmhO63iu3lHPNgcOsyPlN0mo7bT
        p+OtWqpHAgMBAAECggEACwwFZvFgISVlv4hvbEk1COUAq6mx9FUu6aKDX0+ooQOw
        lR3cqyFok7ckJ4Ah+HgX452jusjsVxqrFKDmknOmT9S+F6njI8tzirJO1T69O47Q
        4iDQNM1+SCCGrUVXuUhi0r50tpHMlgenQI6RKiYeE23snI8fQLkg5n69a3vH4o8q
        CIUVz8Zr4r7Q5NrLWMYa0l6xe0qJ+ZT9Pqys16PbaoXHeiOODNWybE4gkmDhxNMb
        L5+PujFhiy3PefXRwyu1n/WEZCZBa+5Hr1B1mFHID/auPGTUBfv3eB608V19A+J6
        4mdgV0GWhfRRI9NLmouCOsFji/FVTr3zv8Dp5Ay23QKBgQD7+DCY3m6A7uONCsLB
        LI5522PJtlm1cYi3zcCCfdISjM2K0Cz7QGbwKhB/aTNAAirky0Wy5jL98Z3YoeQJ
        iS6h/03IqP9fTia957mo3JJdEx+2vnyYFI88LBxo6UwfFT1XMuX4j5fhSP3vORyQ
        9chnIBlgDCxKHQkfmYdvDL5X7QKBgQDAXQNCtFJV7gdAbHs9sYl6G0ioT0BXFrnv
        cKJzRUHj6u9wwJrW+Dol3H82XdQUQYDZFm0j1oO83s80t5TDqE823/Zmtd3aWnmI
        +SpvWe6GJbGI3MVVL8jNqT2teEksmizOTlv4uCwmmyfhOIbVRVc+nFpPVcTQC36Y
        fxheEXTcgwKBgQDJVb0HOZ0U203qMnICR2clSb/HuzSdfjXfoMG1w3HrrqTCyatX
        rFNUjlgWZuozuEesAB0WYUjXj4wwQNPlJr+jZEw0DY3ZCqp8TkAVBQLS6mgJ7tXB
        85OsYhblYZ2YrLESDzKhVaPnuRpnX7xKvIpAlO6Rx8hQBDl5DYWhn44s1QKBgB0q
        vUDS+J0A32acTD95eN/j3StwANBzqLOuf2M9ABWf3Lha699meeKdwUgsB+keWXwR
        E3FYqFbt7bsPjuXv0jr+0GyYbNAb4cusBAwoNatvcbDP0Lfu6+KLI8f2shmqMtsB
        NJ7Mxh0Ab5aNrJwPzH+401SuK45j/8j9lGNHAFIjAoGAVlbreyAuehkBqabmVKW3
        jn+XAEU5VIQ0fZdpM8FDDa3rTr8rJw6rFenrmel/JAEzRRnSWD1js/g/Y1pcg78t
        S3xcEihW99oB+TfMtRwpgXNuBRLfWm1iz6gTp4TXksBzaVQwbMGhP4IbfIZexNSQ
        C5GS0J4mTGKLPOg88Te6G6E=
        -----END PRIVATE KEY-----
        """;

    private WebApplication _app = null!;
    private LeaderElectionService _leaderElection = null!;
    private string _tempDir = null!;
    private HttpClient? _httpClient;
    private HttpClient? _httpsClient;

    [Fact]
    public async Task Kestrel_ServesTheKubeletListenersOn10250And10255()
    {
        RequireKubeletPortsFree();
        _tempDir = NewTempDir();
        WriteServerCertificates(Path.Combine(_tempDir, "cert.pem"), Path.Combine(_tempDir, "key.pem"));

        string? previousCert = Environment.GetEnvironmentVariable("APISERVER_CERT_LOCATION");
        string? previousKey = Environment.GetEnvironmentVariable("APISERVER_KEY_LOCATION");
        Environment.SetEnvironmentVariable("APISERVER_CERT_LOCATION", Path.Combine(_tempDir, "cert.pem"));
        Environment.SetEnvironmentVariable("APISERVER_KEY_LOCATION", Path.Combine(_tempDir, "key.pem"));
        try
        {
            await StartKubeletApp();

            // 10255: plain HTTP, no client certificate required.
            _httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:10255") };
            HttpResponseMessage livez = await _httpClient.GetAsync("/livez", TestContext.Current.CancellationToken);
            Assert.Equal(200, (int)livez.StatusCode);
            Assert.Equal("\"alive\"", await livez.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            // 10250: HTTPS, client certificate required. No client CA is configured here, so
            // any certificate is accepted - but an anonymous caller is rejected at the TLS layer.
            HttpClient anonymous = CreateHttpsClient(null);
            await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.GetAsync("/livez", TestContext.Current.CancellationToken));
            anonymous.Dispose();

            _httpsClient = CreateHttpsClient(CreateSelfSignedClientCertificate());
            HttpResponseMessage httpsLivez = await _httpsClient.GetAsync("/livez", TestContext.Current.CancellationToken);
            Assert.Equal(200, (int)httpsLivez.StatusCode);
            Assert.Equal("\"alive\"", await httpsLivez.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            HttpResponseMessage readyz = await _httpsClient.GetAsync("/readyz", TestContext.Current.CancellationToken);
            Assert.Equal(200, (int)readyz.StatusCode);
            Assert.Equal("\"ready\"", await readyz.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APISERVER_CERT_LOCATION", previousCert);
            Environment.SetEnvironmentVariable("APISERVER_KEY_LOCATION", previousKey);
            await DisposeKubeletApp();
        }
    }

    [Fact]
    public async Task Kestrel_FallsBackToHomeDirCerts_WhenEnvVarsUnset()
    {
        RequireKubeletPortsFree();
        _tempDir = NewTempDir();
        string homeSharplet = Path.Combine(_tempDir, ".sharplet");
        System.IO.Directory.CreateDirectory(homeSharplet);
        WriteServerCertificates(Path.Combine(homeSharplet, "cert.pem"), Path.Combine(homeSharplet, "key.pem"));

        string? previousHome = Environment.GetEnvironmentVariable("HOME");
        string? previousCert = Environment.GetEnvironmentVariable("APISERVER_CERT_LOCATION");
        string? previousKey = Environment.GetEnvironmentVariable("APISERVER_KEY_LOCATION");
        Environment.SetEnvironmentVariable("HOME", _tempDir);
        Environment.SetEnvironmentVariable("APISERVER_CERT_LOCATION", null);
        Environment.SetEnvironmentVariable("APISERVER_KEY_LOCATION", null);
        try
        {
            // The CSR tool writes ~/.sharplet when /etc/sharplet is not writable; the
            // listener must pick those files up and still serve 10250/10255.
            await StartKubeletApp();

            _httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:10255") };
            HttpResponseMessage livez = await _httpClient.GetAsync("/livez", TestContext.Current.CancellationToken);
            Assert.Equal(200, (int)livez.StatusCode);
            Assert.Equal("\"alive\"", await livez.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            _httpsClient = CreateHttpsClient(CreateSelfSignedClientCertificate());
            HttpResponseMessage httpsLivez = await _httpsClient.GetAsync("/livez", TestContext.Current.CancellationToken);
            Assert.Equal(200, (int)httpsLivez.StatusCode);
            Assert.Equal("\"alive\"", await httpsLivez.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.SetEnvironmentVariable("APISERVER_CERT_LOCATION", previousCert);
            Environment.SetEnvironmentVariable("APISERVER_KEY_LOCATION", previousKey);
            await DisposeKubeletApp();
        }
    }

    [Fact]
    public async Task Kestrel_ValidatesClientCertificates_WhenClientCaIsConfigured()
    {
        RequireKubeletPortsFree();
        _tempDir = NewTempDir();
        WriteServerCertificates(Path.Combine(_tempDir, "cert.pem"), Path.Combine(_tempDir, "key.pem"));

        // A client CA with one certificate signed by it (must be accepted) and one self-signed
        // stranger (must be rejected at the TLS layer).
        X509Certificate2 clientCa = CreateCertificateAuthority();
        string caPath = Path.Combine(_tempDir, "ca.pem");
        System.IO.File.WriteAllText(caPath, clientCa.ExportCertificatePem());
        X509Certificate2 trustedClient = CreateClientCertificate(clientCa);
        X509Certificate2 untrustedClient = CreateSelfSignedClientCertificate();

        string? previousCert = Environment.GetEnvironmentVariable("APISERVER_CERT_LOCATION");
        string? previousKey = Environment.GetEnvironmentVariable("APISERVER_KEY_LOCATION");
        string? previousCa = Environment.GetEnvironmentVariable("SHARPLET_CLIENT_CA");
        Environment.SetEnvironmentVariable("APISERVER_CERT_LOCATION", Path.Combine(_tempDir, "cert.pem"));
        Environment.SetEnvironmentVariable("APISERVER_KEY_LOCATION", Path.Combine(_tempDir, "key.pem"));
        Environment.SetEnvironmentVariable("SHARPLET_CLIENT_CA", caPath);
        try
        {
            await StartKubeletApp();

            HttpResponseMessage trusted = await CreateHttpsClient(trustedClient).GetAsync("/livez", TestContext.Current.CancellationToken);
            Assert.Equal(200, (int)trusted.StatusCode);

            HttpClient untrusted = CreateHttpsClient(untrustedClient);
            await Assert.ThrowsAsync<HttpRequestException>(() => untrusted.GetAsync("/livez", TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APISERVER_CERT_LOCATION", previousCert);
            Environment.SetEnvironmentVariable("APISERVER_KEY_LOCATION", previousKey);
            Environment.SetEnvironmentVariable("SHARPLET_CLIENT_CA", previousCa);
            await DisposeKubeletApp();
        }
    }

    private void RequireKubeletPortsFree()
    {
        // The kubelet's listener configuration binds fixed ports (production behavior);
        // on a runner where they are already taken the tests cannot run.
        int[] ports = { 10250, 10255 };
        foreach (int port in ports)
        {
            TcpListener listener = new(IPAddress.Any, port);
            try
            {
                listener.Start();
                listener.Stop();
            }
            catch (SocketException)
            {
                Assert.Skip($"port {port} is already in use on this runner");
            }
            finally
            {
                listener.Dispose();
            }
        }
    }

    private async Task StartKubeletApp()
    {
        using TemporaryKubeConfig kubeConfig = new();
        IPodController podController = Substitute.For<IPodController>();
        INodeController nodeController = Substitute.For<INodeController>();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.AddVirtualKubelet(new SharpConfig { NodeName = "test-node" }, services =>
        {
            services.AddSingleton(podController);
            services.AddSingleton(nodeController);
        });
        // The registration builds a real client from the (fake) kubeconfig; register the
        // mocked cluster after it so the host's leader election and status loops resolve
        // the mock (last registration wins).
        MockCluster cluster = MockCluster.CreateLeader();
        builder.Services.AddSingleton<IKubernetes>(cluster.Kubernetes);

        _app = builder.Build();
        _app.MapKubeletEndpoints();
        await _app.StartAsync();
        _leaderElection = _app.Services.GetRequiredService<LeaderElectionService>();
        await TestHelper.WaitUntilAsync(() => _leaderElection.IsLeader);
    }

    private async Task DisposeKubeletApp()
    {
        if (_app is null)
        {
            return;
        }
        try
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        finally
        {
            _app = null!;
            _httpClient?.Dispose();
            _httpClient = null;
            _httpsClient?.Dispose();
            _httpsClient = null;
            if (!string.IsNullOrEmpty(_tempDir))
            {
                System.IO.Directory.Delete(_tempDir, recursive: true);
                _tempDir = null!;
            }
        }
    }

    private static string NewTempDir()
    {
        return System.IO.Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "sharplet-listener-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    private static void WriteServerCertificates(string certPath, string keyPath)
    {
        System.IO.File.WriteAllText(certPath, ServerCertPem);
        System.IO.File.WriteAllText(keyPath, ServerKeyPem);
    }

    private static HttpClient CreateHttpsClient(X509Certificate2? clientCert)
    {
        // The embedded server certificate is self-signed (CN sharplet-test); the point is that
        // the TLS listener enforces its client-certificate policy, not that its identity is
        // validated.
        HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        if (clientCert is not null)
        {
            handler.ClientCertificates.Add(clientCert);
        }
        return new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:10250") };
    }

    private static X509Certificate2 CreateCertificateAuthority()
    {
        RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=sharplet-test-client-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10));
    }

    private static X509Certificate2 CreateClientCertificate(X509Certificate2 clientCa)
    {
        // A client certificate signed by the test client CA. The key is re-associated from
        // PEM because Create(issuer) returns the certificate without the request's key, and
        // a client certificate cannot sign the TLS handshake without it. The validity stays
        // inside the CA's ten-year validity so the serial/validity invariants hold.
        RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=sharplet-test-client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        X509Certificate2 signed = request.Create(clientCa, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(9), RandomNumberGenerator.GetBytes(8));
        return X509Certificate2.CreateFromPem(signed.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private static X509Certificate2 CreateSelfSignedClientCertificate()
    {
        RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=sharplet-test-standalone-client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10));
    }
}