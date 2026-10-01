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

##### Port of a C written Epitech project: [NWP_myftp_2019](https://github.com/valentinbreiz/NWP_myftp_2019)

## Usage

Add the package to your kernel .csproj:

```xml
<ItemGroup>
    <PackageReference Include="Cosmos.Network.Ftp" Version="2.0.0" />
</ItemGroup>
```

The kernel needs an IP configuration (DHCP or static) and a mounted filesystem. `Listen()` serves every client on the calling thread until `Close()` is called, so start it on a thread of its own to keep your shell:

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

### Data connections

Passive mode is the one to use. Each passive transfer listens on a port from `PassivePortMin` to `PassivePortMax` (50000 to 50009 by default). Active mode (`PORT`, `EPRT`) only connects back to the address the client is connected from, so it does not work through NAT.

### Reaching a kernel running in QEMU

With QEMU user-mode networking, forward the control port and every passive port to the guest. With the Cosmos CLI:

```sh
cosmos run --hostfwd tcp::2121-:21 --hostfwd tcp::50000-:50000 --hostfwd tcp::50001-:50001  # and so on, up to 50009
```

## Authors

👤 **[@valentinbreiz](https://github.com/valentinbreiz)**

## 🤝 Contributing

Contributions, issues and feature requests are welcome!

Feel free to check [issues page](https://github.com/CosmosOS/CosmosFtp/issues). 

## 📝 License

Copyright © 2022-2026 [CosmosOS](https://github.com/CosmosOS).

This project is [BSD Clause 3](https://github.com/CosmosOS/CosmosFtp/blob/main/LICENSE.txt) licensed.
