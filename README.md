<h1 align="center">CosmosFTP Server 🚀</h1>
<p>
  <a href="https://www.nuget.org/packages/Cosmos.Network.Ftp/" target="_blank">
    <img alt="Version" src="https://img.shields.io/nuget/v/Cosmos.Network.Ftp.svg" />
  </a>
  <a href="https://github.com/CosmosOS/CosmosFtp/blob/main/LICENSE.txt" target="_blank">
    <img alt="License: BSD Clause 3 License" src="https://img.shields.io/badge/license-BSD License-yellow.svg" />
  </a>
</p>

> CosmosFTP is an FTP server made in C# for the Cosmos operating system construction kit.

Version 2 is for **Cosmos Gen3** kernels (NativeAOT). It is a plain .NET library over `System.Net.Sockets` and `System.IO`, which a Gen3 kernel runs on its own network stack and VFS. For Cosmos Gen2 (`Cosmos.System2`), use Cosmos.Network.Ftp 1.x.

## Usage

Add the package to your kernel .csproj:

```xml
<ItemGroup>
    <PackageReference Include="Cosmos.Network.Ftp" Version="2.0.0" />
</ItemGroup>
```

The kernel needs networking and storage (`CosmosEnableNetwork`, `CosmosEnableStorage`, both on by default), an IP configuration (DHCP or static) and a mounted filesystem. `Listen()` serves every client on the calling thread until `Close()` is called, so start it on a thread of its own to keep your shell:

```csharp
using System.Threading;
using Cosmos.Network.Ftp;

FtpServer server = new("/mnt")
{
    // Optional: left out, any user name logs in with any password.
    Authenticate = (user, password) => user == "cosmos" && password == "cosmos",
    // Optional: one line per connection, command and failure.
    Log = message => Cosmos.Kernel.System.Diagnostics.Log.WriteString(message + "\n"),
};

new Thread(server.Listen).Start();

// Later, to stop serving:
server.Close();
```

Clients see the served directory as `/` and cannot leave it. The server speaks RFC 959 with the commands common clients use: `USER`, `PASS`, `PWD`, `CWD`, `CDUP`, `LIST`, `NLST`, `RETR`, `STOR`, `APPE`, `SIZE`, `DELE`, `MKD`, `RMD`, `RNFR`/`RNTO`, `PASV`, `EPSV`, `PORT`, `EPRT`, `FEAT`, `SYST`, `TYPE`, `NOOP` and `QUIT`.

### Data connections

Passive mode is the one to use. Each passive transfer listens on a port from `PassivePortMin` to `PassivePortMax` (50000 to 50009 by default). Active mode (`PORT`, `EPRT`) only connects back to the address the client is connected from, so it does not work through NAT.

### Reaching a kernel running in QEMU

With QEMU user-mode networking, forward the control port and every passive port to the guest. With the Cosmos CLI:

```sh
cosmos run --hostfwd tcp::2121-:21 --hostfwd tcp::50000-:50000 --hostfwd tcp::50001-:50001  # and so on, up to 50009
```

A smaller range means fewer rules: `PassivePortMin = 50000, PassivePortMax = 50000` needs a single one. Then connect to `localhost:2121` in passive mode, for example `curl ftp://localhost:2121/`. A `PASV` reply names the guest's own address, 10.0.2.15, which the host cannot reach. curl and Python's `ftplib` ignore it and connect to the address they reached the server at, and `EPSV` names no address at all, but FileZilla connects where the reply says. For it, set `PassiveAddress = IPAddress.Loopback`, so that `PASV` names 127.0.0.1.

### Security

FTP sends everything in the clear, password included. Serve it on a network you trust.

## Building and testing

```sh
dotnet test CosmosFtp.slnx
```

The tests run the server on the host, over loopback, against a raw FTP client.

##### Port of a C written Epitech project: [NWP_myftp_2019](https://github.com/valentinbreiz/NWP_myftp_2019)

## Authors

👤 **[@valentinbreiz](https://github.com/valentinbreiz)**

## 🤝 Contributing

Contributions, issues and feature requests are welcome!

Feel free to check [issues page](https://github.com/CosmosOS/CosmosFtp/issues). 

## Show your support

Give a ⭐️ if this project helped you!

## 📝 License

Copyright © 2022-2026 [CosmosOS](https://github.com/CosmosOS).

This project is [BSD Clause 3](https://github.com/CosmosOS/CosmosFtp/blob/main/LICENSE.txt) licensed.
