// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;

namespace Cosmos.Network.Ftp.Tests;

/// <summary>
/// Drives a server listening on loopback, serving a fresh temporary
/// directory, through <see cref="FtpTestClient"/>.
/// </summary>
public class FtpServerTests
{
    private const string Password = "s3cret";

    private string _root = string.Empty;
    private FtpServer? _server;
    private Thread? _thread;

    private FtpServer Server => _server ?? throw new InvalidOperationException("No server was started.");

    [SetUp]
    public void StartServer()
    {
        _root = Directory.CreateTempSubdirectory("cosmosftp-").FullName;
        int passivePort = FreePort();
        _server = new FtpServer(_root, FreePort())
        {
            PassivePortMin = passivePort,
            PassivePortMax = passivePort,
            Authenticate = static (user, password) => user != "locked" || password == Password,
        };

        _thread = Run(_server);
    }

    [TearDown]
    public void StopServer()
    {
        _server?.Close();
        _thread?.Join(5000);
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Starts <paramref name="server"/> on a thread of its own, and returns once it listens.</summary>
    private static Thread Run(FtpServer server)
    {
        Thread thread = new(server.Listen) { IsBackground = true };
        thread.Start();
        Stopwatch waited = Stopwatch.StartNew();
        while (!server.IsListening)
        {
            Assert.That(waited.ElapsedMilliseconds, Is.LessThan(5000), "the server did not start listening");
            Thread.Sleep(10);
        }

        return thread;
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public class Constructor : FtpServerTests
    {
        [Test]
        public void WhenRootDirectoryIsMissing_Throws()
        {
            Assert.Throws<DirectoryNotFoundException>(() => _ = new FtpServer(Path.Combine(_root, "missing")));
        }
    }

    public class Listen : FtpServerTests
    {
        [Test]
        public void WhenConnected_AndNotLoggedIn_CommandsAreRefused()
        {
            using FtpTestClient client = new(Server.Port);

            Assert.That(client.Greeting, Does.StartWith("220"));
            Assert.That(client.Send("PWD"), Does.StartWith("530"));
            Assert.That(client.Send("SYST"), Is.EqualTo("215 UNIX Type: L8"));
            Assert.That(client.Send("FEAT"), Does.StartWith("211-").And.Contain(" EPSV").And.EndWith("211 End"));
        }

        [Test]
        public void WhenPasswordIsWrong_AndThenRight_OnlyTheRightOneLogsIn()
        {
            using FtpTestClient client = new(Server.Port);

            Assert.That(client.Send("USER locked"), Does.StartWith("331"));
            Assert.That(client.Send("PASS wrong"), Does.StartWith("530"));
            Assert.That(client.Send("PWD"), Does.StartWith("530"));
            client.Login("locked", Password);
            Assert.That(client.Send("PWD"), Is.EqualTo("257 \"/\" is the current directory."));
        }

        [Test]
        public void WhenStoredThenRetrieved_ContentRoundTrips()
        {
            byte[] content = new byte[300_000];
            new Random(42).NextBytes(content);
            using FtpTestClient client = new(Server.Port);
            client.Login();

            client.Store("blob.bin", content);

            Assert.That(File.ReadAllBytes(Path.Combine(_root, "blob.bin")), Is.EqualTo(content));
            Assert.That(client.Send("SIZE blob.bin"), Is.EqualTo("213 300000"));
            Assert.That(client.Retrieve("RETR blob.bin"), Is.EqualTo(content));
            Assert.That(client.Retrieve("RETR blob.bin", extended: false), Is.EqualTo(content));
        }

        [Test]
        public void WhenAppended_FileGrows()
        {
            File.WriteAllText(Path.Combine(_root, "log.txt"), "one\n");
            using FtpTestClient client = new(Server.Port);
            client.Login();

            client.Store("log.txt", "two\n"u8.ToArray(), verb: "APPE");

            Assert.That(File.ReadAllText(Path.Combine(_root, "log.txt")), Is.EqualTo("one\ntwo\n"));
        }

        [Test]
        public void WhenListed_EntriesAppearInLongAndNameForms()
        {
            Directory.CreateDirectory(Path.Combine(_root, "music"));
            File.WriteAllBytes(Path.Combine(_root, "a.txt"), new byte[1234]);
            using FtpTestClient client = new(Server.Port);
            client.Login();

            string listing = client.RetrieveText("LIST -la");
            string names = client.RetrieveText("NLST");

            Assert.That(listing, Does.Match(@"(?m)^drwxr-xr-x 1 owner group +0 \w{3} [ \d]\d [ \d]\d:?\d\d music\r$"));
            Assert.That(listing, Does.Match(@"(?m)^-rw-r--r-- 1 owner group +1234 .* a\.txt\r$"));
            Assert.That(names.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), Is.EquivalentTo(new[] { "music", "a.txt" }));
        }

        [Test]
        public void WhenPathsClimbAboveRoot_TheyStopAtRoot()
        {
            using FtpTestClient client = new(Server.Port);
            client.Login();

            Assert.That(client.Send("CWD ../../.."), Does.StartWith("250"));
            Assert.That(client.Send("PWD"), Is.EqualTo("257 \"/\" is the current directory."));
            Assert.That(client.Send("SIZE ../../../../etc/passwd"), Does.StartWith("550"));
            Assert.That(client.Send("MKD ../../escape"), Is.EqualTo("257 \"/escape\" created."));
            Assert.That(Directory.Exists(Path.Combine(_root, "escape")), Is.True);
            Assert.That(client.Send("RMD /"), Does.StartWith("550"));
        }

        [Test]
        public void WhenDirectoriesAndFilesAreManaged_TheFileSystemFollows()
        {
            using FtpTestClient client = new(Server.Port);
            client.Login();

            Assert.That(client.Send("MKD dir one"), Is.EqualTo("257 \"/dir one\" created."));
            Assert.That(client.Send("CWD dir one"), Does.StartWith("250"));
            Assert.That(client.Send("PWD"), Is.EqualTo("257 \"/dir one\" is the current directory."));
            client.Store("a.txt", "a"u8.ToArray());
            Assert.That(client.Send("RNFR a.txt"), Does.StartWith("350"));
            Assert.That(client.Send("RNTO b.txt"), Does.StartWith("250"));
            Assert.That(File.Exists(Path.Combine(_root, "dir one", "b.txt")), Is.True);
            Assert.That(client.Send("RNTO c.txt"), Does.StartWith("503"), "RNTO needs an RNFR right before it");
            Assert.That(client.Send("CDUP"), Does.StartWith("250"));
            Assert.That(client.Send("RMD dir one"), Does.StartWith("550"), "a directory with files is not removed");
            Assert.That(client.Send("DELE dir one/b.txt"), Does.StartWith("250"));
            Assert.That(client.Send("RMD dir one"), Does.StartWith("250"));
            Assert.That(Directory.Exists(Path.Combine(_root, "dir one")), Is.False);
        }

        [Test]
        public void WhenPortNamesAnotherHost_ItIsRefused()
        {
            using FtpTestClient client = new(Server.Port);
            client.Login();

            Assert.That(client.Send("PORT 10,9,8,7,200,1"), Is.EqualTo("500 Illegal PORT command."));
            Assert.That(client.Send("EPRT |1|10.9.8.7|51201|"), Is.EqualTo("500 Illegal EPRT command."));
            Assert.That(client.Send("EPRT |2|::1|51201|"), Does.StartWith("522"));
        }

        [Test]
        public void WhenActiveMode_ServerConnectsBackToClient()
        {
            using TcpListener clientListener = new(IPAddress.Loopback, 0);
            clientListener.Start();
            int port = ((IPEndPoint)clientListener.LocalEndpoint).Port;
            File.WriteAllText(Path.Combine(_root, "a.txt"), "active");
            using FtpTestClient client = new(Server.Port);
            client.Login();

            Assert.That(client.Send($"EPRT |1|127.0.0.1|{port}|"), Does.StartWith("200"));
            Assert.That(client.Send("RETR a.txt"), Does.StartWith("150"));
            using TcpClient data = clientListener.AcceptTcpClient();
            using StreamReader reader = new(data.GetStream());

            Assert.That(reader.ReadToEnd(), Is.EqualTo("active"));
            Assert.That(client.ReadReply(), Does.StartWith("226"));
        }

        [Test]
        public void WhenNoDataConnectionWasSetUp_TransferIsRefused()
        {
            File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
            using FtpTestClient client = new(Server.Port);
            client.Login();

            Assert.That(client.Send("RETR a.txt"), Is.EqualTo("425 Use PORT or PASV first."));
            Assert.That(client.Send("STOR a.txt"), Is.EqualTo("425 Use PORT or PASV first."));
            Assert.That(File.ReadAllText(Path.Combine(_root, "a.txt")), Is.EqualTo("a"), "a refused STOR leaves the file as it was");
            using TcpClient data = client.OpenExtendedPassive();
            Assert.That(client.Send("RETR missing.txt"), Does.StartWith("550"));
        }

        [Test]
        public void WhenTwoClientsConnect_BothAreServed()
        {
            using FtpTestClient first = new(Server.Port);
            first.Login();
            using FtpTestClient second = new(Server.Port);
            second.Login();

            Assert.That(second.Send("MKD shared"), Does.StartWith("257"));
            Assert.That(first.Send("CWD shared"), Does.StartWith("250"));
            Assert.That(first.Send("QUIT"), Does.StartWith("221"));
            Assert.That(second.Send("NOOP"), Does.StartWith("200"));
        }
    }

    public class PassiveAddress : FtpServerTests
    {
        [Test]
        public void WhenSet_PasvNamesItAndEpsvNamesNoAddress()
        {
            int passivePort = FreePort();
            FtpServer server = new(_root, FreePort())
            {
                PassivePortMin = passivePort,
                PassivePortMax = passivePort,
                PassiveAddress = IPAddress.Parse("192.0.2.7"),
            };
            Thread thread = Run(server);
            try
            {
                using FtpTestClient client = new(server.Port);
                client.Login();

                Assert.That(client.Send("PASV"), Is.EqualTo($"227 Entering Passive Mode (192,0,2,7,{passivePort >> 8},{passivePort & 0xFF})."));
                Assert.That(client.Send("EPSV"), Is.EqualTo($"229 Entering Extended Passive Mode (|||{passivePort}|)"));
            }
            finally
            {
                server.Close();
                thread.Join(5000);
            }
        }

        [Test]
        public void WhenNotIPv4_ListenThrows()
        {
            FtpServer server = new(_root, FreePort()) { PassiveAddress = IPAddress.IPv6Loopback };

            Assert.Throws<InvalidOperationException>(server.Listen);
        }
    }

    public class Close : FtpServerTests
    {
        [Test]
        public void WhenClosed_ListenReturns()
        {
            Server.Close();

            Assert.That(_thread!.Join(5000), Is.True);
            Assert.That(Server.IsListening, Is.False);
            Assert.Throws<ObjectDisposedException>(Server.Listen);
        }
    }
}
