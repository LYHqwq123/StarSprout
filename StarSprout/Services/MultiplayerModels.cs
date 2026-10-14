using System.Diagnostics;

namespace StarSprout.Services;

public sealed class MultiplayerPeer
{
    public string Hostname { get; set; } = "";
    public string VirtualIp { get; set; } = "";
    public double? LatencyMs { get; set; }
    public string ConnectionType { get; set; } = "";
}

public sealed class MultiplayerState
{
    public bool CoreInstalled { get; set; }
    public string CoreVersion { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string Role { get; set; } = "";
    public string InvitationCode { get; set; } = "";
    public string VirtualIp { get; set; } = "";
    public string JoinAddress { get; set; } = "";
    public int PeerCount { get; set; }
    public List<MultiplayerPeer> Peers { get; set; } = new();
    public bool Running { get; set; }
    public string Notice { get; set; } = "";
    public string Error { get; set; } = "";
    public List<string> Logs { get; set; } = new();
}

/// <summary>Bounded, versioned invitation for StarSprout rooms, independent of Minecraft/Terracotta.</summary>
public sealed class MultiplayerRoom
{
    public int Version { get; set; } = 1;
    public string NetworkName { get; set; } = "";
    public string Secret { get; set; } = "";
    public string HostIp { get; set; } = "10.126.0.1";
    public string Peer { get; set; } = "tcp://public.easytier.top:11010";
}

/// <summary>Injectable process boundary: production owns only the core and CLI processes that it starts.</summary>
public interface IMultiplayerRuntime : IDisposable
{
    bool IsRunning { get; }
    void Start(ProcessStartInfo startInfo);
    Task<string> QueryAsync(string cliPath, int rpcPort, string instanceName, string command, CancellationToken cancellationToken);
    void Stop();
    IReadOnlyList<string> GetRecentLogs();
}
