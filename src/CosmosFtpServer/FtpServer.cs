// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CosmosFtpServer;

/// <summary>
/// An FTP server (RFC 959) that serves a directory tree. <see cref="Listen"/>
/// runs it on the calling thread, which serves every client in turn, so a
/// kernel that wants its shell back starts it on a thread of its own.
/// </summary>
/// <remarks>
/// Clients see <see cref="RootDirectory"/> as <c>/</c> and cannot leave it.
/// Data connections are passive (<c>PASV</c>, <c>EPSV</c>) on a port from
/// <see cref="PassivePortMin"/> to <see cref="PassivePortMax"/>, or active
/// (<c>PORT</c>, <c>EPRT</c>) to the client's own address. FTP sends
/// everything in the clear, password included: serve it on a network you
/// trust.
/// </remarks>
public sealed class FtpServer : IDisposable
{
    /// <summary>The port FTP is served on by default.</summary>
    public const int DefaultPort = 21;

    /// <summary>The first port of the default passive range.</summary>
    public const int DefaultPassivePortMin = 50000;

    /// <summary>The last port of the default passive range.</summary>
    public const int DefaultPassivePortMax = 50009;

    /// <summary>How many clients are served at once; the next one is turned away with a 421.</summary>
    private const int MaxSessions = 8;

    /// <summary>How long the server sleeps when no client had anything for it, in milliseconds.</summary>
    private const int IdleSleepMs = 10;

    private readonly List<FtpSession> _sessions = [];

    private volatile bool _closeRequested;
    private volatile bool _listening;
    private int _nextPassivePort;

    /// <summary>The directory served as <c>/</c>.</summary>
    public string RootDirectory { get; }

    /// <summary>The TCP port the server listens on.</summary>
    public int Port { get; }

    /// <summary>The first port a passive data connection may listen on.</summary>
    public int PassivePortMin { get; init; } = DefaultPassivePortMin;

    /// <summary>The last port a passive data connection may listen on.</summary>
    public int PassivePortMax { get; init; } = DefaultPassivePortMax;

    /// <summary>
    /// The IPv4 address a <c>PASV</c> reply tells the client to connect to.
    /// Left null, it is the address the client reached the server at, as
    /// the server sees it. A server behind NAT names the address clients
    /// reach it at instead: 127.0.0.1 for a client on the host of a QEMU
    /// user-mode guest. <c>EPSV</c> names no address and is not affected.
    /// </summary>
    public IPAddress? PassiveAddress { get; init; }

    /// <summary>
    /// Decides whether a user name and password log in. Left null, any
    /// user name logs in with any password.
    /// </summary>
    public Func<string, string, bool>? Authenticate { get; init; }

    /// <summary>Receives a line for every connection, command and failure, when set.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Whether <see cref="Listen"/> is running.</summary>
    public bool IsListening => _listening;

    /// <summary>Creates a server; <see cref="Listen"/> runs it.</summary>
    /// <param name="rootDirectory">The directory served as <c>/</c>.</param>
    /// <param name="port">The TCP port to listen on.</param>
    /// <exception cref="DirectoryNotFoundException"><paramref name="rootDirectory"/> does not exist.</exception>
    public FtpServer(string rootDirectory, int port = DefaultPort)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);

        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"The FTP root directory '{rootDirectory}' does not exist.");
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        Port = port;
    }

    /// <summary>
    /// Serves clients on the calling thread until <see cref="Close"/> is
    /// called. The clients still connected then are disconnected.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server already listens, the passive range is not a valid range of ports, or <see cref="PassiveAddress"/> is not an IPv4 address.</exception>
    /// <exception cref="ObjectDisposedException">The server was closed.</exception>
    /// <exception cref="SocketException">The port could not be listened on.</exception>
    public void Listen()
    {
        ObjectDisposedException.ThrowIf(_closeRequested, this);

        if (PassivePortMin < 1 || PassivePortMax > IPEndPoint.MaxPort || PassivePortMin > PassivePortMax)
        {
            throw new InvalidOperationException($"The passive port range {PassivePortMin}-{PassivePortMax} is not a range of TCP ports.");
        }

        if (PassiveAddress is not null && PassiveAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException($"The passive address {PassiveAddress} is not an IPv4 address, which is all a PASV reply can name.");
        }

        if (_listening)
        {
            throw new InvalidOperationException($"The FTP server already listens on port {Port}.");
        }

        TcpListener listener = new(IPAddress.Any, Port);
        listener.Start();
        _listening = true;
        _nextPassivePort = PassivePortMin;
        WriteLog($"Listening on port {Port}, serving {RootDirectory}");

        try
        {
            while (!_closeRequested)
            {
                bool busy = false;
                if (listener.Pending())
                {
                    Accept(listener.AcceptTcpClient());
                    busy = true;
                }

                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    busy |= Serve(_sessions[i]);
                }

                if (!busy)
                {
                    Thread.Sleep(IdleSleepMs);
                }
            }
        }
        finally
        {
            foreach (FtpSession session in _sessions)
            {
                session.Dispose();
            }

            _sessions.Clear();
            listener.Stop();
            _listening = false;
            WriteLog($"Stopped listening on port {Port}");
        }
    }

    /// <summary>
    /// Stops the server: <see cref="Listen"/> disconnects its clients and
    /// returns. A closed server does not listen again.
    /// </summary>
    public void Close() => _closeRequested = true;

    /// <inheritdoc cref="Close"/>
    public void Dispose() => Close();

    /// <summary>
    /// Opens a passive data listener on the next port of the range that no
    /// session holds, the range taken in turn so a port just released is
    /// not handed out again at once.
    /// </summary>
    /// <returns>The started listener and its port, or null when every port of the range is taken.</returns>
    internal (TcpListener Listener, int Port)? OpenPassiveListener()
    {
        int count = PassivePortMax - PassivePortMin + 1;
        for (int i = 0; i < count; i++)
        {
            int port = _nextPassivePort;
            _nextPassivePort = port == PassivePortMax ? PassivePortMin : port + 1;
            if (IsPassivePortHeld(port))
            {
                continue;
            }

            TcpListener listener = new(IPAddress.Any, port);
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (SocketException exception)
            {
                WriteLog($"Passive port {port} is not available: {exception.Message}");
            }
        }

        return null;
    }

    internal void WriteLog(string message) => Log?.Invoke($"[FTP] {message}");

    private bool IsPassivePortHeld(int port)
    {
        foreach (FtpSession session in _sessions)
        {
            if (session.PassivePort == port)
            {
                return true;
            }
        }

        return false;
    }

    private void Accept(TcpClient client)
    {
        FtpSession? session = null;
        try
        {
            session = new FtpSession(this, client);
            WriteLog($"{session.Name} connected");

            if (_sessions.Count >= MaxSessions)
            {
                session.Reply(421, $"All {MaxSessions} connections are in use, try again later.");
                session.Dispose();
                return;
            }

            session.Reply(220, "Cosmos FTP server ready.");
            _sessions.Add(session);
        }
        catch (Exception exception)
        {
            // A client gone before its greeting costs its own connection,
            // never the server.
            WriteLog($"Connection dropped while accepted: {exception.Message}");
            if (session is null)
            {
                client.Close();
            }
            else
            {
                session.Dispose();
            }
        }
    }

    /// <summary>Runs what a session received, and drops it once it is closed.</summary>
    /// <returns>Whether the session had anything to do.</returns>
    private bool Serve(FtpSession session)
    {
        bool busy;
        try
        {
            busy = session.Poll();
        }
        catch (Exception exception)
        {
            // A failure the commands do not answer themselves ends this
            // client only, never the server.
            WriteLog($"{session.Name} dropped by {exception.GetType().Name}: {exception.Message}");
            session.Close();
            busy = true;
        }

        if (session.IsClosed)
        {
            _sessions.Remove(session);
            session.Dispose();
            WriteLog($"{session.Name} disconnected");
        }

        return busy;
    }
}
