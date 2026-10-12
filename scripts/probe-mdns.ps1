# PC-side evidence only. Run with Wireless debugging enabled on the phone.
# Keep the pairing dialog open during C if pairing-service evidence is wanted.
# No elevation, adb process, server startup/restart, firewall or adapter changes.
[CmdletBinding()]
param(
    [ValidateRange(1, 60)][int]$Seconds = 15,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
if (-not ('AdbViewMdnsProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

public static class AdbViewMdnsProbe {
    public static readonly string[] Domains = {
        "_adb-tls-connect._tcp.local", "_adb-tls-pairing._tcp.local"
    };
    public sealed class Reply {
        public string Mode, LocalInterface, Sender, Instance, Port;
    }
    public sealed class Measurement {
        public readonly List<Reply> Replies = new List<Reply>();
        public readonly List<string> Errors = new List<string>();
        public int Sent, Datagrams, RelevantDatagrams;
        public string Bind = "unavailable";
    }
    public static byte[] Query(string domain) {
        using (var stream = new MemoryStream()) {
            byte[] header = new byte[12];
            header[0] = 0x41; header[1] = 0x44; header[5] = 1;
            stream.Write(header, 0, header.Length);
            foreach (string label in domain.Split('.')) {
                byte[] bytes = Encoding.UTF8.GetBytes(label);
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
            stream.WriteByte(0);
            stream.Write(new byte[] { 0, 12, 0, 1 }, 0, 4); // PTR, IN; no QU
            return stream.ToArray();
        }
    }
    private static int U16(byte[] data, int offset) {
        if (offset < 0 || offset + 2 > data.Length) throw new FormatException("Truncated DNS integer");
        return data[offset] * 256 + data[offset + 1];
    }
    private static string Name(byte[] data, ref int offset) {
        int cursor = offset, end = -1;
        var labels = new List<string>();
        for (int hops = 0; hops < 128; hops++) {
            if (cursor >= data.Length) throw new FormatException("Truncated DNS name");
            int length = data[cursor++];
            if (length == 0) { offset = end < 0 ? cursor : end; return string.Join(".", labels); }
            if ((length & 0xc0) == 0xc0) {
                if (cursor >= data.Length) throw new FormatException("Truncated DNS pointer");
                if (end < 0) end = cursor + 1;
                cursor = ((length & 0x3f) << 8) | data[cursor];
            } else {
                if (length > 63 || cursor + length > data.Length) throw new FormatException("Invalid DNS label");
                labels.Add(Encoding.UTF8.GetString(data, cursor, length)); cursor += length;
            }
        }
        throw new FormatException("DNS compression loop");
    }
    private static bool Relevant(string name) {
        foreach (string domain in Domains)
            if (name.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    public static Dictionary<string, string> Parse(byte[] packet) {
        var instances = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (packet.Length < 12 || (packet[2] & 0x80) == 0) return instances;
        int offset = 12;
        for (int i = 0; i < U16(packet, 4); i++) { Name(packet, ref offset); offset += 4; }
        int count = U16(packet, 6) + U16(packet, 8) + U16(packet, 10);
        for (int i = 0; i < count; i++) {
            string name = Name(packet, ref offset);
            int type = U16(packet, offset), length = U16(packet, offset + 8);
            offset += 10;
            int end = offset + length;
            if (end > packet.Length) throw new FormatException("Truncated DNS record");
            if (type == 12) {
                int cursor = offset;
                string target = Name(packet, ref cursor);
                if (cursor > end) throw new FormatException("PTR exceeds record");
                if (Relevant(target) && !instances.ContainsKey(target)) instances[target] = "unknown";
            } else if (type == 33 && Relevant(name)) {
                if (length < 7) throw new FormatException("Truncated SRV");
                instances[name] = U16(packet, offset + 4).ToString();
            }
            offset = end;
        }
        return instances;
    }
    private static Socket SocketFor(int port, bool shared) {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            if (shared) {
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        } catch { socket.Dispose(); throw; }
    }
    private static string InterfaceName(IPPacketInformation info) {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
            var v4 = nic.GetIPProperties().GetIPv4Properties();
            if (v4 != null && v4.Index == info.Interface)
                return nic.Name + " (#" + info.Interface + ", destination " + info.Address + ")";
        }
        return "#" + info.Interface + " (destination " + info.Address + ")";
    }
    public static Measurement Run(string mode, IPAddress[] addresses, int seconds) {
        var result = new Measurement();
        var endpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        try {
            using (var socket = SocketFor(mode == "C" ? 5353 : 0, mode == "C")) {
                result.Bind = socket.LocalEndPoint.ToString();
                Console.WriteLine("Mode " + mode + ": " + result.Bind + ", " + seconds + " s");
                if (mode == "C") {
                    foreach (IPAddress address in addresses) {
                        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(endpoint.Address, address)); }
                        catch (SocketException ex) { result.Errors.Add("Join " + address + ": " + ex.SocketErrorCode); }
                    }
                    if (addresses.Length == 0) result.Errors.Add("No eligible interface to join");
                } else if (mode == "B" && addresses.Length == 0) {
                    result.Errors.Add("No eligible interface for B; no default-route substitution");
                }
                var watch = Stopwatch.StartNew();
                int rounds = 0;
                var buffer = new byte[65535];
                while (watch.Elapsed.TotalSeconds < seconds) {
                    if (mode != "C" && rounds < 2 && watch.Elapsed.TotalSeconds >= rounds) {
                        rounds++;
                        IPAddress[] sends = mode == "A" ? new[] { IPAddress.Any } : addresses;
                        foreach (IPAddress address in sends) {
                            try {
                                if (mode == "B") socket.SetSocketOption(SocketOptionLevel.IP,
                                    SocketOptionName.MulticastInterface, address.GetAddressBytes());
                                foreach (string domain in Domains) {
                                    byte[] query = Query(domain);
                                    socket.SendTo(query, endpoint); result.Sent++;
                                }
                            } catch (SocketException ex) {
                                result.Errors.Add("Send " + address + ": " + ex.SocketErrorCode);
                            }
                        }
                    }
                    if (!socket.Poll(100000, SelectMode.SelectRead)) continue;
                    EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    SocketFlags flags = SocketFlags.None;
                    IPPacketInformation info;
                    int size;
                    try { size = socket.ReceiveMessageFrom(buffer, 0, buffer.Length, ref flags, ref sender, out info); }
                    catch (SocketException ex) { result.Errors.Add("Receive: " + ex.SocketErrorCode); continue; }
                    result.Datagrams++;
                    byte[] packet = new byte[size]; Array.Copy(buffer, packet, size);
                    Dictionary<string, string> parsed;
                    try { parsed = Parse(packet); }
                    catch (FormatException ex) { result.Errors.Add("DNS decode: " + ex.Message); continue; }
                    if (parsed.Count > 0) result.RelevantDatagrams++;
                    foreach (var row in parsed) {
                        string local = InterfaceName(info);
                        var reply = new Reply { Mode = mode, LocalInterface = local,
                            Sender = ((IPEndPoint)sender).Address.ToString(), Instance = row.Key, Port = row.Value };
                        result.Replies.Add(reply);
                        Console.WriteLine(mode + " | " + local + " | " + reply.Sender + " | " + row.Key + " | " + row.Value);
                    }
                }
            }
        } catch (SocketException ex) { result.Errors.Add("Mode unavailable: " + ex.SocketErrorCode); }
        return result;
    }
    // Exact read-only service used by `adb mdns services`, without CLI auto-start.
    public static string AdbServices() {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
            socket.SendTimeout = 3000; socket.ReceiveTimeout = 3000;
            var connect = socket.ConnectAsync(IPAddress.Loopback, 5037);
            if (!connect.Wait(3000)) throw new TimeoutException("Existing ADB server connection timed out");
            byte[] request = Encoding.ASCII.GetBytes("0012host:mdns:services");
            // The service is 18 ASCII bytes; only an existing server is queried.
            int sent = 0;
            while (sent < request.Length) sent += socket.Send(request, sent, request.Length - sent, SocketFlags.None);
            string status = Encoding.ASCII.GetString(Read(socket, 4));
            int length = Convert.ToInt32(Encoding.ASCII.GetString(Read(socket, 4)), 16);
            string body = Encoding.UTF8.GetString(Read(socket, length));
            if (status != "OKAY") throw new IOException("ADB query returned " + status + ": " + body);
            return body;
        }
    }
    private static byte[] Read(Socket socket, int length) {
        var bytes = new byte[length]; int offset = 0;
        while (offset < length) {
            int count = socket.Receive(bytes, offset, length - offset, SocketFlags.None);
            if (count == 0) throw new IOException("Existing ADB server closed the connection");
            offset += count;
        }
        return bytes;
    }
    public static void SelfTest() {
        byte[] query = Query(Domains[0]);
        if (query[5] != 1 || query[query.Length - 1] != 1) throw new Exception("Query shape failed");
        var bytes = new List<byte>(query); bytes[2] = 0x80; bytes[7] = 1;
        bytes.AddRange(new byte[] { 5, 112, 104, 111, 110, 101, 0xc0, 12,
            0, 33, 0, 1, 0, 0, 0, 120, 0, 8, 0, 0, 0, 0, 0x11, 0x5c, 0xc0, 12 });
        byte[] response = bytes.ToArray();
        var parsed = Parse(response);
        if (parsed["phone." + Domains[0]] != "4444") throw new Exception("Compressed SRV failed");
        bool rejected = false;
        try { Parse(new byte[] { 0, 0, 0x80, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xc0, 12 }); }
        catch (FormatException) { rejected = true; }
        if (!rejected) throw new Exception("Compression loop not rejected");
        using (var receiver = SocketFor(0, false))
        using (var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
            sender.SendTo(response, new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)receiver.LocalEndPoint).Port));
            if (!receiver.Poll(1000000, SelectMode.SelectRead)) throw new Exception("Loopback receive timed out");
            var buffer = new byte[1500]; EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            SocketFlags flags = SocketFlags.None; IPPacketInformation info;
            int size = receiver.ReceiveMessageFrom(buffer, 0, buffer.Length, ref flags, ref remote, out info);
            if (size != response.Length || info.Interface == 0 || !IPAddress.IsLoopback(info.Address))
                throw new Exception("Loopback packet information failed");
        }
    }
}
'@
}

if ($SelfTest) {
    [AdbViewMdnsProbe]::SelfTest()
    Write-Host 'probe-mdns: offline query, compressed DNS, malformed packet and loopback metadata tests passed.'
    return
}

$report = [Collections.Generic.List[string]]::new()
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$report.Add('# Windows multicast discovery probe')
$report.Add('')
$report.Add("Time: $(Get-Date -Format o); Windows: $([Environment]::OSVersion.Version); PowerShell: $($PSVersionTable.PSVersion)")
$report.Add("A = default route / ephemeral; B = per-interface / ephemeral; C = passive shared 5353. Each mode runs for $Seconds s, sequentially; C sends no queries. Enable Wireless debugging throughout. Open the pairing dialog during C for pairing advertisements.")
$report.Add('No elevation or ADB/environment/server/firewall/adapter mutations. Missing collectors/modes are recorded as errors, not zero-reply proof.')

function Add-ProbeSection([string]$Title, [scriptblock]$Collect) {
    $report.Add(''); $report.Add("## $Title")
    try { $content = (& $Collect | Out-String).TrimEnd() }
    catch { $content = "UNAVAILABLE: $($_.Exception.Message)" }
    Write-Host "${Title}:`n$content"
    $report.Add('```text'); $report.Add($content); $report.Add('```')
}
Add-ProbeSection 'adb mdns services (existing server, read-only host:mdns:services)' {
    $services = [AdbViewMdnsProbe]::AdbServices()
    if ([string]::IsNullOrWhiteSpace($services)) { '(empty)' } else { $services }
}
Add-ProbeSection 'UDP 5353 holders' {
    Get-NetUDPEndpoint -LocalPort 5353 -ErrorAction Stop | ForEach-Object {
        $name = try { (Get-Process -Id $_.OwningProcess -ErrorAction Stop).ProcessName } catch { "unavailable: $($_.Exception.Message)" }
        [pscustomobject]@{ LocalAddress = $_.LocalAddress; Port = $_.LocalPort; PID = $_.OwningProcess; Process = $name }
    } | Format-Table -AutoSize
}
Add-ProbeSection 'Get-NetRoute 224.0.0.0/4 and interface metrics' {
    Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '224.0.0.0/4' -ErrorAction Stop |
        Select-Object InterfaceIndex, InterfaceAlias, DestinationPrefix, NextHop, RouteMetric, InterfaceMetric |
        Format-Table -AutoSize
    Get-NetIPInterface -AddressFamily IPv4 -ErrorAction Stop |
        Select-Object InterfaceIndex, InterfaceAlias, ConnectionState, InterfaceMetric | Format-Table -AutoSize
}
Add-ProbeSection 'Up adapters' {
    Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object Status -eq 'Up' |
        Select-Object Name, InterfaceDescription, ifIndex, Status, LinkSpeed | Format-Table -AutoSize
}
$addresses = [Collections.Generic.List[Net.IPAddress]]::new()
Add-ProbeSection 'Eligible multicast IPv4 addresses' {
    foreach ($nic in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -ne 'Up' -or -not $nic.SupportsMulticast -or $nic.NetworkInterfaceType -eq 'Loopback') { continue }
        foreach ($unicast in $nic.GetIPProperties().UnicastAddresses) {
            if ($unicast.Address.AddressFamily -eq 'InterNetwork' -and -not [Net.IPAddress]::IsLoopback($unicast.Address)) {
                $addresses.Add($unicast.Address)
                [pscustomobject]@{ Name = $nic.Name; Address = $unicast.Address; Index = $nic.GetIPProperties().GetIPv4Properties().Index }
            }
        }
    }
}
$report.Add(''); $report.Add('## Replies (each relevant reply; unknown port means no SRV in that datagram)')
$report.Add('| Mode | Receiving interface / destination | Sender IP | Instance | Port |')
$report.Add('|---|---|---|---|---|')
function ConvertTo-ProbeCell([string]$Value) { return $Value.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') }
$summaries = [Collections.Generic.List[string]]::new()
foreach ($mode in @('A', 'B', 'C')) {
    $measurement = [AdbViewMdnsProbe]::Run($mode, $addresses.ToArray(), $Seconds)
    foreach ($reply in $measurement.Replies) {
        $cells = @($reply.Mode, $reply.LocalInterface, $reply.Sender, $reply.Instance, $reply.Port) | ForEach-Object { ConvertTo-ProbeCell $_ }
        $report.Add('| ' + ($cells -join ' | ') + ' |')
    }
    $errors = if ($measurement.Errors.Count) { $measurement.Errors -join '; ' } else { 'none' }
    $summaries.Add("| $mode | $($measurement.Bind) | $($measurement.Sent) | $($measurement.Datagrams) | $($measurement.RelevantDatagrams) | $(ConvertTo-ProbeCell $errors) |")
    Write-Host "Mode ${mode}: sent=$($measurement.Sent), datagrams=$($measurement.Datagrams), relevant=$($measurement.RelevantDatagrams), errors=$errors"
}
$report.Add(''); $report.Add('## Mode summary')
$report.Add('| Mode | Bind | Queries sent | Datagrams | Relevant datagrams | Errors |')
$report.Add('|---|---|---|---|---|---|')
$report.AddRange($summaries)
$report.Add(''); $report.Add('Interpret only with phone state held constant: A/B empty and C relevant is evidence for considering Task 4. No replies in any mode does not prove a product bug. A failed mode/collector is inconclusive.')
$outputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$outputPath = Join-Path $outputDirectory "probe-mdns-$timestamp.md"
[IO.File]::WriteAllLines($outputPath, $report, [Text.UTF8Encoding]::new($false))
Write-Host "Report: $outputPath"
