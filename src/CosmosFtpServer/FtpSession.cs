// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CosmosFtpServer;

/// <summary>
/// One client: its control connection, its login, its working directory and
/// the data connection its next transfer uses. The server polls it; a
/// transfer runs to its end inside the command that asked for it.
/// </summary>
internal sealed class FtpSession : IDisposable
{
    /// <summary>The longest command line taken, in bytes; a longer one is refused whole.</summary>
    private const int MaxLineLength = 1024;

    /// <summary>How much of a file goes through the data connection at a time, in bytes.</summary>
    private const int TransferChunkSize = 16 * 1024;

    /// <summary>How long a client has to open the data connection a transfer waits for, in milliseconds.</summary>
    private const int DataConnectionTimeoutMs = 10_000;

    /// <summary>How long an upload may go without a byte before it is abandoned, in milliseconds.</summary>
    private const int UploadIdleTimeoutMs = 30_000;

    private static readonly string[] s_features = ["EPSV", "PASV", "SIZE", "UTF8"];

    private readonly FtpServer _server;
    private readonly TcpClient _control;
    private readonly Socket _socket;
    private readonly IPAddress _peerAddress;
    private readonly IPEndPoint _localEndPoint;
    private readonly byte[] _receiveBuffer = new byte[512];
    private readonly byte[] _line = new byte[MaxLineLength];

    private int _lineLength;
    private bool _lineOverflowed;
    private string? _userName;
    private bool _loggedIn;
    private string _workingDirectory = FtpPath.Root;
    private string? _renameFrom;
    private TcpListener? _passiveListener;
    private IPEndPoint? _activeEndPoint;

    /// <summary>The client's address and port, for the log.</summary>
    public string Name { get; }

    /// <summary>The port the session's passive data listener holds, or 0.</summary>
    public int PassivePort { get; private set; }

    /// <summary>Whether the client quit or went away; the server drops the session.</summary>
    public bool IsClosed { get; private set; }

    public FtpSession(FtpServer server, TcpClient control)
    {
        _server = server;
        _control = control;
        _socket = control.Client;

        IPEndPoint remote = (IPEndPoint)(control.Client.RemoteEndPoint ?? throw new InvalidOperationException("An accepted connection has no remote end point."));
        _peerAddress = remote.Address;
        _localEndPoint = (IPEndPoint)(control.Client.LocalEndPoint ?? throw new InvalidOperationException("An accepted connection has no local end point."));
        // ToString() spelled out: interpolation formats an IPAddress through
        // ISpanFormattable, which the Cosmos socket plugs do not cover.
        Name = $"{remote.Address.ToString()}:{remote.Port}";
    }

    /// <summary>Reads what the client sent, and runs every command it completed.</summary>
    /// <returns>Whether anything arrived, or the connection ended.</returns>
    public bool Poll()
    {
        if (IsClosed)
        {
            return false;
        }

        int available = _socket.Available;
        if (available == 0)
        {
            // Readable with nothing to read: the client closed its end.
            if (_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0)
            {
                IsClosed = true;
                return true;
            }

            return false;
        }

        int read = _socket.Receive(_receiveBuffer, 0, Math.Min(available, _receiveBuffer.Length), SocketFlags.None);
        for (int i = 0; i < read && !IsClosed; i++)
        {
            byte value = _receiveBuffer[i];
            if (value == (byte)'\n')
            {
                EndLine();
            }
            else if (_lineLength < MaxLineLength)
            {
                _line[_lineLength++] = value;
            }
            else
            {
                _lineOverflowed = true;
            }
        }

        return true;
    }

    /// <summary>Sends a single-line reply.</summary>
    public void Reply(int code, string text)
    {
        byte[] reply = Encoding.UTF8.GetBytes($"{code} {text}\r\n");
        _socket.Send(reply, 0, reply.Length, SocketFlags.None);
    }

    /// <summary>Marks the session closed; the server drops it.</summary>
    public void Close() => IsClosed = true;

    public void Dispose()
    {
        IsClosed = true;
        ClosePassiveListener();
        _control.Close();
    }

    private void EndLine()
    {
        int length = _lineLength;
        bool overflowed = _lineOverflowed;
        _lineLength = 0;
        _lineOverflowed = false;

        if (overflowed)
        {
            Reply(500, "Command line too long.");
            return;
        }

        if (length > 0 && _line[length - 1] == (byte)'\r')
        {
            length--;
        }

        Execute(Encoding.UTF8.GetString(_line, 0, length));
    }

    private void Execute(string line)
    {
        int space = line.IndexOf(' ');
        string verb = (space < 0 ? line : line.Substring(0, space)).ToUpperInvariant();
        string argument = space < 0 ? string.Empty : line.Substring(space + 1);
        _server.WriteLog($"{Name} > {(verb == "PASS" ? "PASS ****" : line)}");

        // RNTO must come straight after the RNFR it completes.
        string? renameFrom = _renameFrom;
        _renameFrom = null;

        try
        {
            Dispatch(verb, argument, renameFrom);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _server.WriteLog($"{Name} {verb} failed: {exception.Message}");
            Reply(550, "Requested action not taken.");
        }
    }

    private void Dispatch(string verb, string argument, string? renameFrom)
    {
        switch (verb)
        {
            case "USER":
                User(argument);
                return;
            case "PASS":
                Pass(argument);
                return;
            case "QUIT":
                Reply(221, "Goodbye.");
                IsClosed = true;
                return;
            case "NOOP":
                Reply(200, "NOOP ok.");
                return;
            case "SYST":
                Reply(215, "UNIX Type: L8");
                return;
            case "FEAT":
                Features();
                return;
            case "OPTS":
                Options(argument);
                return;
            case "HELP":
                Reply(214, "Commands: USER PASS QUIT NOOP SYST FEAT OPTS HELP PWD CWD CDUP TYPE MODE STRU ALLO PASV EPSV PORT EPRT LIST NLST RETR STOR APPE SIZE DELE MKD RMD RNFR RNTO ABOR");
                return;
        }

        if (!_loggedIn)
        {
            Reply(530, "Please login with USER and PASS.");
            return;
        }

        switch (verb)
        {
            case "PWD":
            case "XPWD":
                Reply(257, $"{Quote(_workingDirectory)} is the current directory.");
                break;
            case "CWD":
            case "XCWD":
                ChangeDirectory(argument);
                break;
            case "CDUP":
            case "XCUP":
                ChangeDirectory("..");
                break;
            case "TYPE":
                SetType(argument);
                break;
            case "MODE":
                ReplyIfSupported(argument, "S", "Mode set to S.");
                break;
            case "STRU":
                ReplyIfSupported(argument, "F", "Structure set to F.");
                break;
            case "ALLO":
                Reply(202, "ALLO command ignored.");
                break;
            case "ABOR":
                Reply(225, "No transfer to abort.");
                break;
            case "PASV":
                EnterPassiveMode(extended: false);
                break;
            case "EPSV":
                ExtendedPassive(argument);
                break;
            case "PORT":
                Port(argument);
                break;
            case "EPRT":
                ExtendedPort(argument);
                break;
            case "LIST":
                List(argument, names: false);
                break;
            case "NLST":
                List(argument, names: true);
                break;
            case "RETR":
                Retrieve(argument);
                break;
            case "STOR":
                Store(argument, FileMode.Create);
                break;
            case "APPE":
                Store(argument, FileMode.Append);
                break;
            case "SIZE":
                Size(argument);
                break;
            case "DELE":
                Delete(argument);
                break;
            case "MKD":
            case "XMKD":
                MakeDirectory(argument);
                break;
            case "RMD":
            case "XRMD":
                RemoveDirectory(argument);
                break;
            case "RNFR":
                RenameFrom(argument);
                break;
            case "RNTO":
                RenameTo(argument, renameFrom);
                break;
            default:
                Reply(502, "Command not implemented.");
                break;
        }
    }

    private void User(string argument)
    {
        if (argument.Length == 0)
        {
            Reply(501, "Syntax error in parameters or arguments.");
            return;
        }

        _userName = argument;
        _loggedIn = false;
        Reply(331, "Please specify the password.");
    }

    private void Pass(string argument)
    {
        if (_userName is null)
        {
            Reply(503, "Login with USER first.");
            return;
        }

        if (_server.Authenticate is { } authenticate && !authenticate(_userName, argument))
        {
            _server.WriteLog($"{Name} failed to log in as {_userName}");
            _userName = null;
            Reply(530, "Login incorrect.");
            return;
        }

        _loggedIn = true;
        _server.WriteLog($"{Name} logged in as {_userName}");
        Reply(230, "Login successful.");
    }

    private void Features()
    {
        StringBuilder builder = new("211-Features:\r\n");
        foreach (string feature in s_features)
        {
            builder.Append(' ').Append(feature).Append("\r\n");
        }

        builder.Append("211 End\r\n");
        byte[] reply = Encoding.UTF8.GetBytes(builder.ToString());
        _socket.Send(reply, 0, reply.Length, SocketFlags.None);
    }

    private void Options(string argument)
    {
        // Paths are UTF-8 whether the client asks or not.
        if (argument.Equals("UTF8 ON", StringComparison.OrdinalIgnoreCase))
        {
            Reply(200, "Always in UTF8 mode.");
            return;
        }

        Reply(501, "Option not understood.");
    }

    private void ChangeDirectory(string argument)
    {
        string path = FtpPath.Resolve(_workingDirectory, argument);
        if (!Directory.Exists(ToPhysical(path)))
        {
            Reply(550, "Failed to change directory.");
            return;
        }

        _workingDirectory = path;
        Reply(250, "Directory successfully changed.");
    }

    private void SetType(string argument)
    {
        // Files go through byte for byte in either type, as most servers do.
        switch (argument.ToUpperInvariant())
        {
            case "I":
            case "L 8":
                Reply(200, "Switching to Binary mode.");
                break;
            case "A":
            case "A N":
                Reply(200, "Switching to ASCII mode.");
                break;
            default:
                Reply(504, "Command not implemented for that parameter.");
                break;
        }
    }

    private void ReplyIfSupported(string argument, string supported, string text)
    {
        if (argument.Equals(supported, StringComparison.OrdinalIgnoreCase))
        {
            Reply(200, text);
        }
        else
        {
            Reply(504, "Command not implemented for that parameter.");
        }
    }

    private void ExtendedPassive(string argument)
    {
        // "EPSV ALL" promises the client sends no PORT from now on; the
        // server needs nothing from it.
        if (argument.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            Reply(200, "EPSV ALL ok.");
            return;
        }

        if (argument.Length > 0 && argument != "1")
        {
            Reply(522, "Network protocol not supported, use (1)");
            return;
        }

        EnterPassiveMode(extended: true);
    }

    private void EnterPassiveMode(bool extended)
    {
        ClosePassiveListener();
        _activeEndPoint = null;

        byte[] address = _localEndPoint.Address.MapToIPv4().GetAddressBytes();
        if (_server.OpenPassiveListener() is not { } passive)
        {
            Reply(425, "No passive port is free, try again later.");
            return;
        }

        _passiveListener = passive.Listener;
        PassivePort = passive.Port;

        if (extended)
        {
            Reply(229, $"Entering Extended Passive Mode (|||{passive.Port}|)");
        }
        else
        {
            Reply(227, $"Entering Passive Mode ({address[0]},{address[1]},{address[2]},{address[3]},{passive.Port >> 8},{passive.Port & 0xFF}).");
        }
    }

    private void Port(string argument)
    {
        string[] fields = argument.Split(',');
        Span<byte> parts = stackalloc byte[6];
        bool valid = fields.Length == parts.Length;
        for (int i = 0; valid && i < parts.Length; i++)
        {
            valid = byte.TryParse(fields[i].Trim(), out parts[i]);
        }

        if (!valid)
        {
            Reply(501, "Syntax error in parameters or arguments.");
            return;
        }

        SetActiveEndPoint(new IPAddress(parts.Slice(0, 4)), (parts[4] << 8) | parts[5], "PORT");
    }

    private void ExtendedPort(string argument)
    {
        // |1|address|port| with any delimiter, the first character naming it.
        string[] fields = argument.Length > 0 ? argument.Split(argument[0]) : [];
        if (fields.Length != 5 || fields[0].Length != 0 || fields[4].Length != 0)
        {
            Reply(501, "Syntax error in parameters or arguments.");
            return;
        }

        if (fields[1] != "1")
        {
            Reply(522, "Network protocol not supported, use (1)");
            return;
        }

        if (!IPAddress.TryParse(fields[2], out IPAddress? address) || !int.TryParse(fields[3], out int port))
        {
            Reply(501, "Syntax error in parameters or arguments.");
            return;
        }

        SetActiveEndPoint(address, port, "EPRT");
    }

    private void SetActiveEndPoint(IPAddress address, int port, string verb)
    {
        // A data connection only goes back to the client itself: a PORT
        // naming another host would let the client aim the server at it.
        if (!IsPeer(address) || port < 1 || port > IPEndPoint.MaxPort)
        {
            Reply(500, $"Illegal {verb} command.");
            return;
        }

        ClosePassiveListener();
        _activeEndPoint = new IPEndPoint(address, port);
        Reply(200, $"{verb} command successful. Consider using PASV.");
    }

    private void List(string argument, bool names)
    {
        if (!RequireDataConnection())
        {
            return;
        }

        // Clients pass ls options ("LIST -la"); the listing is always the long one.
        string target = argument;
        while (target.StartsWith('-'))
        {
            int space = target.IndexOf(' ');
            target = space < 0 ? string.Empty : target.Substring(space + 1).TrimStart();
        }

        string path = FtpPath.Resolve(_workingDirectory, target);
        string physical = ToPhysical(path);

        FileSystemInfo[] entries;
        if (Directory.Exists(physical))
        {
            entries = new DirectoryInfo(physical).GetFileSystemInfos();
        }
        else if (File.Exists(physical))
        {
            entries = [new FileInfo(physical)];
        }
        else
        {
            Reply(550, "No such file or directory.");
            return;
        }

        StringBuilder builder = new();
        DateTime now = DateTime.UtcNow;
        foreach (FileSystemInfo entry in entries)
        {
            if (names)
            {
                FtpListing.AppendName(builder, entry);
            }
            else
            {
                FtpListing.AppendLong(builder, entry, now);
            }
        }

        byte[] listing = Encoding.UTF8.GetBytes(builder.ToString());
        Reply(150, "Here comes the directory listing.");
        using MemoryStream source = new(listing);
        SendData(source);
    }

    private void Retrieve(string argument)
    {
        if (!RequireDataConnection())
        {
            return;
        }

        string path = FtpPath.Resolve(_workingDirectory, argument);
        string physical = ToPhysical(path);
        if (!File.Exists(physical))
        {
            Reply(550, "Failed to open file.");
            return;
        }

        using FileStream file = new(physical, FileMode.Open, FileAccess.Read, FileShare.Read, TransferChunkSize);
        Reply(150, $"Opening BINARY mode data connection for {FtpPath.GetName(path)} ({file.Length} bytes).");
        SendData(file);
    }

    private void Store(string argument, FileMode mode)
    {
        if (argument.Length == 0)
        {
            Reply(501, "Syntax error in parameters or arguments.");
            return;
        }

        // Before the file is opened, which would truncate it.
        if (!RequireDataConnection())
        {
            return;
        }

        string path = FtpPath.Resolve(_workingDirectory, argument);
        string physical = ToPhysical(path);
        if (path == FtpPath.Root || Directory.Exists(physical))
        {
            Reply(553, "Could not create file.");
            return;
        }

        using FileStream file = new(physical, mode, FileAccess.Write, FileShare.None, TransferChunkSize);
        Reply(150, "Ok to send data.");
        ReceiveData(file);
    }

    private void Size(string argument)
    {
        string physical = ToPhysical(FtpPath.Resolve(_workingDirectory, argument));
        if (!File.Exists(physical))
        {
            Reply(550, "Could not get file size.");
            return;
        }

        Reply(213, new FileInfo(physical).Length.ToString());
    }

    private void Delete(string argument)
    {
        string physical = ToPhysical(FtpPath.Resolve(_workingDirectory, argument));
        if (!File.Exists(physical))
        {
            Reply(550, "Delete operation failed.");
            return;
        }

        File.Delete(physical);
        Reply(250, "Delete operation successful.");
    }

    private void MakeDirectory(string argument)
    {
        string path = FtpPath.Resolve(_workingDirectory, argument);
        string physical = ToPhysical(path);
        if (argument.Length == 0 || Directory.Exists(physical) || File.Exists(physical))
        {
            Reply(550, "Create directory operation failed.");
            return;
        }

        Directory.CreateDirectory(physical);
        Reply(257, $"{Quote(path)} created.");
    }

    private void RemoveDirectory(string argument)
    {
        string path = FtpPath.Resolve(_workingDirectory, argument);
        string physical = ToPhysical(path);
        if (argument.Length == 0 || path == FtpPath.Root || !Directory.Exists(physical))
        {
            Reply(550, "Remove directory operation failed.");
            return;
        }

        // Only an empty directory goes, as RFC 959 has it: a client that
        // removes a tree empties it first.
        Directory.Delete(physical, recursive: false);
        Reply(250, "Remove directory operation successful.");
    }

    private void RenameFrom(string argument)
    {
        string path = FtpPath.Resolve(_workingDirectory, argument);
        string physical = ToPhysical(path);
        if (argument.Length == 0 || path == FtpPath.Root || !(File.Exists(physical) || Directory.Exists(physical)))
        {
            Reply(550, "RNFR command failed.");
            return;
        }

        _renameFrom = path;
        Reply(350, "Ready for RNTO.");
    }

    private void RenameTo(string argument, string? renameFrom)
    {
        if (renameFrom is null)
        {
            Reply(503, "RNFR required first.");
            return;
        }

        string path = FtpPath.Resolve(_workingDirectory, argument);
        if (argument.Length == 0 || path == FtpPath.Root)
        {
            Reply(553, "Rename failed.");
            return;
        }

        string source = ToPhysical(renameFrom);
        string destination = ToPhysical(path);
        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else
        {
            Directory.Move(source, destination);
        }

        Reply(250, "Rename successful.");
    }

    /// <summary>Sends <paramref name="source"/> over the data connection, then the 226 or the failure.</summary>
    private void SendData(Stream source)
    {
        TcpClient? data = OpenDataConnection();
        if (data is null)
        {
            Reply(425, "Failed to establish connection.");
            return;
        }

        try
        {
            Socket socket = data.Client;
            byte[] buffer = new byte[TransferChunkSize];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                socket.Send(buffer, 0, read, SocketFlags.None);
            }
        }
        catch (Exception exception)
        {
            // Not narrowed to IOException and SocketException: the Cosmos
            // socket plugs report a connection the client dropped as a bare
            // Exception, and a dropped transfer must not end the session.
            _server.WriteLog($"{Name} transfer failed: {exception.Message}");
            data.Close();
            Reply(426, "Connection closed; transfer aborted.");
            return;
        }

        // Closed before the 226, so the client has seen the end of the data
        // by the time it reads that the transfer is complete.
        data.Close();
        Reply(226, "Transfer complete.");
    }

    /// <summary>Writes what arrives on the data connection to <paramref name="destination"/> until the client closes it, then the 226 or the failure.</summary>
    private void ReceiveData(Stream destination)
    {
        TcpClient? data = OpenDataConnection();
        if (data is null)
        {
            Reply(425, "Failed to establish connection.");
            return;
        }

        try
        {
            Socket socket = data.Client;
            byte[] buffer = new byte[TransferChunkSize];
            long lastReceived = Stopwatch.GetTimestamp();
            while (true)
            {
                int available = socket.Available;
                if (available > 0)
                {
                    int read = socket.Receive(buffer, 0, Math.Min(available, buffer.Length), SocketFlags.None);
                    destination.Write(buffer, 0, read);
                    lastReceived = Stopwatch.GetTimestamp();
                    continue;
                }

                // Readable with nothing to read: the client sent it all and closed.
                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                {
                    break;
                }

                if (Stopwatch.GetElapsedTime(lastReceived).TotalMilliseconds > UploadIdleTimeoutMs)
                {
                    throw new IOException("The client sent nothing for too long.");
                }

                Thread.Sleep(1);
            }
        }
        catch (Exception exception)
        {
            // Not narrowed, for the reason SendData gives.
            _server.WriteLog($"{Name} transfer failed: {exception.Message}");
            data.Close();
            Reply(426, "Connection closed; transfer aborted.");
            return;
        }

        data.Close();
        Reply(226, "Transfer complete.");
    }

    /// <summary>Refuses a transfer no PASV, EPSV, PORT or EPRT set up a data connection for.</summary>
    /// <returns>Whether one was set up.</returns>
    private bool RequireDataConnection()
    {
        if (_passiveListener is not null || _activeEndPoint is not null)
        {
            return true;
        }

        Reply(425, "Use PORT or PASV first.");
        return false;
    }

    /// <summary>
    /// Opens the data connection the last PASV, EPSV, PORT or EPRT set up.
    /// Each serves one transfer.
    /// </summary>
    /// <returns>The connection, or null when none was set up or it could not be opened.</returns>
    private TcpClient? OpenDataConnection()
    {
        if (_passiveListener is { } listener)
        {
            try
            {
                long start = Stopwatch.GetTimestamp();
                while (!listener.Pending())
                {
                    if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > DataConnectionTimeoutMs)
                    {
                        _server.WriteLog($"{Name} did not open its passive data connection");
                        return null;
                    }

                    Thread.Sleep(1);
                }

                TcpClient data = listener.AcceptTcpClient();

                // The data connection must come from the client the control
                // connection did, or another host could take the transfer.
                if (data.Client.RemoteEndPoint is not IPEndPoint remote || !IsPeer(remote.Address))
                {
                    _server.WriteLog($"{Name} refused a data connection from {data.Client.RemoteEndPoint}");
                    data.Close();
                    return null;
                }

                return data;
            }
            finally
            {
                ClosePassiveListener();
            }
        }

        if (_activeEndPoint is { } endPoint)
        {
            _activeEndPoint = null;
            TcpClient data = new();
            try
            {
                data.Connect(endPoint);
                return data;
            }
            catch (Exception exception)
            {
                // The kernel's socket layer reports a refused connection as a bare Exception.
                _server.WriteLog($"{Name} active data connection to {endPoint} failed: {exception.Message}");
                data.Close();
                return null;
            }
        }

        return null;
    }

    private void ClosePassiveListener()
    {
        _passiveListener?.Stop();
        _passiveListener = null;
        PassivePort = 0;
    }

    private string ToPhysical(string path) => FtpPath.ToPhysical(_server.RootDirectory, path);

    /// <summary>
    /// Whether <paramref name="address"/> is the client's. Compared by bytes:
    /// the Cosmos socket plugs keep an address beside the object, where
    /// <see cref="IPAddress.Equals(object?)"/> does not look.
    /// </summary>
    private bool IsPeer(IPAddress address) => address.GetAddressBytes().AsSpan().SequenceEqual(_peerAddress.GetAddressBytes());

    /// <summary>Quotes a path for a 257 reply, doubling the quotes inside it (RFC 959, appendix II).</summary>
    private static string Quote(string path) => $"\"{path.Replace("\"", "\"\"")}\"";
}
