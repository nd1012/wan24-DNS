# wan24-DNS

Domain Name Services (DNS) are required to resolve a domain name to an IP 
address, so you don't have to write an IP address into a web browsers address 
bar, but you can use a talking domain name. For resolving the IP address, 
your computer uses a DNS resolver, which usually caches resolved IP addresses 
('cause the mapping may not change often). This resolver requests a DNS 
server for the domain name resolution, which is usually the one your ISP 
offers. This can be seen a a bigger, shared cache, 'cause this DNS server has 
to request the DNS server which is responsible for the domain name and manages 
the IP address mapping (and other things). For knowing which DNS server is 
responsible, there is a top level DNS server infrastructure, which gives 
details about specific top level domains and their registered responsible DNS 
servers.

Since the internet is free, there's no restriction about clients which want to 
use the DNS system to resolve an IP address from a domain name. But somewhere 
groups try to add restrictions and manipulate the DNS system to block people 
from accessing internet hosted resources, or for spying internet activity. 
Also an ISP DNS server can log DNS requests and create a surfing profile from 
individuals. And finally the DNS communication isn't encrypted, and since 
DNSSEC (a signature scheme for DNS responses) isn't widely available, DNS 
responses can be manipulated easy.

There are tries to make the whole thing more secure and stop groups from 
disturbing the DNS system, but there's no standard, which is forced and 
accepted worldwide - so there are problems, which require a solution:

You don't want to use your ISP's DNS server, cause it may impact your privacy, 
and your ISP may block resources. DNS responses may also be manipulated on the 
way back to your computer, so that you think you're talking to `abc.tld`, but 
in real you're talking to `xzy.tld`, and you have no chance to see it. This 
makes you become an easy victim for black hats.

`wan24-DNS` doesn't solve the problems of this world, but it promises more 
privacy and security for you:

- The DNS resolver is an anonymous host, which uses TLS secured WebSockets for 
communication with a client
- DNS requests from your computer will be routed 1:1 from your computer to the 
resolver
- The resolver will do the DNS lookup and route the DNS responses 1:1 back to 
your computer

So this fixes some issues:

- DNS lookup queries from your computer will be processed anonymous
- DNS requests are transfered encrypted (as possible)

And this is the changed new process of a DNS request from your computer:

1. Any application (your browser maybe) requires to resolve a domain name to 
an IP address and does ask the local DNS resolver
2. The local DNS resolver (usually provided by your OS) forwards the request 
to the `wan24-DNS` client, listening on `127.0.0.1:53`
3. The `wan24-DNS` client forwards the request to the `wan24-DNS` server using 
a TLS secured WebSocket connection
4. The `wan24-DNS` server forwards the request to the configured DNS server
5. The `wan24-DNS` server forwards the DNS servers response to the `wan24-DNS` 
client
6. The `wan24-DNS` client forwards the response to the local DNS resolver
7. The local DNS resolver forwards the response to the waiting application

## Usage

### Client

The `wan24-DNS` client is a simple CLI tool, which will run as a service as 
soon as it was started. All configuration can be done in the 
`appsettings.json` file.

There's a `-test` flag, which will test the connection to the `wan24-DNS` 
server and quit.

On a Linux system you can use `dnshttpproxyclient.service` to enable running 
the daemon using systemd.

Per default the `wan24-DNS` client listens on `127.0.0.1:53`, so you can 
configure `127.0.0.1` as your DNS server in your systems network settings. The 
`Resolver` in the `appsettings.json` configuration file needs to be set to the 
`wan24-DNS` server URI, which should use `wss://...` for TLS encrypted 
communication. The `ResolverAuthToken` value must be set to one of the 
configured tokens in the `wan24-DNS` server `appsettings.json` configuration 
file.

Things that can be configured in the `appsettings.json` file:

- `EndPoints`: A list of UDP IP endpoints the client will listen at for 
incoming DNS requests
- `Resolver`: WebSocket URI to the `wan24-DNS` server
- `ResolverAuthToken`: Pre-shared authentication token
- `LogFile`: Path to the logfile (may be `null`)
- `LogLevel`: Log message filter level

### Server

The `wan24-DNS` server is a ASP.NET application, which should run behind a 
http proxy webserver (like Apache). It listens to WebSocket connections only.

To establish a WebSocket connection, a connecting peer needs to authenticate 
using a pre-defined token (see `appsettings.json`). The server restricts one 
token to be used from one cient at a time only. If a second client uses the 
same token, the existing client using the same token will be disconnected.

On a Linux host you can use `dnshttpprotyserver.service` to enable running the 
daemon using systemd.

Per default the `wan24-DNS` server listens on `http://127.0.0.1:8090`, so you 
need to configure your http proxy server so forward requests to that local 
URI accordingly.

**NOTE**: Per default `8.8.8.8:53` (Googles public DNS server) will be used 
as resolver, but you may specify any other resolver in the `appsettings.json` 
configuration file.

Things that can be configured in the `appsettings.json` file:

- `Urls`: The URI(s) to listen at
- `Resolver`: Final DNS resolver UDP IP endpoint
- `AuthToken`: A list of pre-shared client authentication token
- `LogFile`: Path to the logfile (may be `null`)
- `LogLevel`: Log message filter level

## About DoH (DoT)

DNS over HTTP (DoH) is defined as RFC 8484 standard. But since only some 
applications are able to use DoH, and not every ISP supports DoH, the old and 
ugly insecure DNS protocol is still alive.

`wan24-DNS` server doesn't implement DoH, the final resolver needs to provide 
the old DNS protocol standards. But it's possible to setup a local DNS server 
on the host which runs the `wan24-DNS` server, and configure this server to 
use DoH, if possible. Then `wan24-DNS` server would use the local DNS server, 
which uses DoH for resolving requests. bind9 is a widely used DNS server which 
supports DoH, for example.

**NOTE**: Even `wan24-DNS` uses http (respetively WS), it's not DoH! 
`wan24-DNS` was designed to establish a long living TCP channel between client 
and server for fast DNS query resolving without having to establish a 
communication channel often. Anyway, the `wan24-DNS` client/server 
communication may be called "DoH", but it has nothing to do with the RFC 
standard.

However, DoH is only a solution for avoiding DNS request/response 
manipulation, but it won't protect your privacy (as you'd expect), and it also 
won't avoid DNS firewalls, which are used to block internet resources. The 
only way to "surf safe" is to use a private hosted solution like `wan24-DNS` 
and share a server with friends (maybe from oversea).

## State of this project

This is an early release of `wan24-DNS`, I'm sure many things can be improved. 
And I will improve many things for sure. Some of the features that I'm 
thinking about:

- [ ] Multiple resolvers for a client, which will be chosen randomly
- [ ] Multiple resolvers for a server, which will be chosen randomly
- [ ] Support for `wan24-DNS` resolver target for a server
- [ ] Direct DoH resolver support for `wan24-DNS` server
- [ ] REST API for managing authentication token
- [ ] Make more details configurable
- [ ] Windows service for the `wan24-DNS` client
- [ ] `wan24-DNS` client GUI with an installer
- [ ] TCP DNS support (currently only UDP messages will be processed)
- [ ] Limit parallel processing requests per client on the server
- [ ] Detached DNS cache
