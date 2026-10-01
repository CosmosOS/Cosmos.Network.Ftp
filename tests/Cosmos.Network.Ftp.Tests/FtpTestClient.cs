// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;

namespace Cosmos.Network.Ftp.Tests;

/// <summary>
/// A bare FTP client over a socket, so the tests see the server's replies
/// as a client parses them, with no client library in between.
/// </summary>
internal sealed class FtpTestClient : IDisposable
{
    private const int TimeoutMs = 5000;

    private readonly TcpClient _control = new() { ReceiveTimeout = TimeoutMs, SendTimeout = TimeoutMs };
    private readonly StreamReader _reader;
    private readonly NetworkStream _stream;

    public string Greeting { get; }

    public FtpTestClient(int port)
    {
        _control.Connect(IPAddress.Loopback, port);
        _stream = _control.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
        Greeting = ReadReply();
    }

    /// <summary>Sends a command and returns its reply, the lines of a multi-line one joined by '\n'.</summary>
    public string Send(string command)
    {
        byte[] line = Encoding.UTF8.GetBytes($"{command}\r\n");
        _stream.Write(line, 0, line.Length);
        return ReadReply();
    }

    public string ReadReply()
    {
        string line = _reader.ReadLine() ?? throw new IOException("The server closed the control connection.");
        if (line.Length < 4 || line[3] != '-')
        {
            return line;
        }

        StringBuilder reply = new(line);
        string end = $"{line.Substring(0, 3)} ";
        string next;
        do
        {
            next = _reader.ReadLine() ?? throw new IOException("The server closed the control connection.");
            reply.Append('\n').Append(next);
        }
        while (!next.StartsWith(end, StringComparison.Ordinal));

        return reply.ToString();
    }

    public void Login(string user = "anonymous", string password = "guest@example.com")
    {
        Assert.That(Send($"USER {user}"), Does.StartWith("331"));
        Assert.That(Send($"PASS {password}"), Does.StartWith("230"));
    }

    /// <summary>Opens the data connection of an EPSV.</summary>
    public TcpClient OpenExtendedPassive()
    {
        string reply = Send("EPSV");
        Assert.That(reply, Does.StartWith("229"));
        int start = reply.IndexOf("(|||", StringComparison.Ordinal) + 4;
        int port = int.Parse(reply.Substring(start, reply.IndexOf('|', start) - start));
        return Connect(port);
    }

    /// <summary>Opens the data connection of a PASV, at the address its reply names.</summary>
    public TcpClient OpenPassive()
    {
        string reply = Send("PASV");
        Assert.That(reply, Does.StartWith("227"));
        int start = reply.IndexOf('(') + 1;
        string[] fields = reply.Substring(start, reply.IndexOf(')') - start).Split(',');
        Assert.That(fields[0..4], Is.EqualTo(new[] { "127", "0", "0", "1" }), "PASV names the address the client reached");
        return Connect((int.Parse(fields[4]) << 8) | int.Parse(fields[5]));
    }

    public void Store(string path, byte[] content, string verb = "STOR")
    {
        using TcpClient data = OpenExtendedPassive();
        Assert.That(Send($"{verb} {path}"), Does.StartWith("150"));
        data.GetStream().Write(content, 0, content.Length);
        data.Close();
        Assert.That(ReadReply(), Does.StartWith("226"));
    }

    public byte[] Retrieve(string command, bool extended = true)
    {
        using TcpClient data = extended ? OpenExtendedPassive() : OpenPassive();
        Assert.That(Send(command), Does.StartWith("150"));
        using MemoryStream received = new();
        data.GetStream().CopyTo(received);
        Assert.That(ReadReply(), Does.StartWith("226"));
        return received.ToArray();
    }

    public string RetrieveText(string command) => Encoding.UTF8.GetString(Retrieve(command));

    public void Dispose() => _control.Dispose();

    private static TcpClient Connect(int port)
    {
        TcpClient data = new() { ReceiveTimeout = TimeoutMs, SendTimeout = TimeoutMs };
        data.Connect(IPAddress.Loopback, port);
        return data;
    }
}
