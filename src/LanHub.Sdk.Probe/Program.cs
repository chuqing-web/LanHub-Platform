using System.Text;
using LanHub.Sdk;

Console.WriteLine("LanHub.Sdk.Probe — 专业 SDK 自测（非游戏）");
Console.WriteLine("命令: /players  /host  /u <text>  /q");
Console.WriteLine();

LanHubClient client;
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANHUB_TOKEN")))
{
    client = await LanHubClient.ConnectFromEnvironmentAsync();
}
else
{
    Console.Write("SDK Port [37812]: ");
    var portText = Console.ReadLine();
    var port = int.TryParse(portText, out var p) ? p : 37812;
    Console.Write("Token: ");
    var token = Console.ReadLine()?.Trim() ?? "";
    Console.Write("GameId [probe]: ");
    var gameId = Console.ReadLine()?.Trim() ?? "";
    if (string.IsNullOrEmpty(gameId)) gameId = "probe";
    Console.Write("PlayerId: ");
    var playerId = Console.ReadLine()?.Trim() ?? "";
    if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(playerId))
    {
        Console.WriteLine("Token 与 PlayerId 必填。");
        return;
    }

    client = new LanHubClient();
    await client.ConnectAsync(port, token, gameId, playerId);
}

await using (client)
{
    Bind(client);
    PrintSession(client);
    Console.WriteLine("输入消息回车发送（可靠信道）；空行或 /q 退出。");

    while (true)
    {
        var line = Console.ReadLine();
        if (string.IsNullOrEmpty(line) || line == "/q") break;
        if (line == "/players")
        {
            foreach (var p in client.Players)
                Console.WriteLine($"  - {p.DisplayName} ({p.PlayerId}) ready={p.IsReady} host={p.IsHost}");
            continue;
        }
        if (line == "/host")
        {
            Console.WriteLine($"IsHost={client.IsHost} HostPlayerId={client.Session.HostPlayerId}");
            continue;
        }
        if (line.StartsWith("/u ", StringComparison.Ordinal))
        {
            await client.SendUnreliableToAllAsync(Encoding.UTF8.GetBytes(line[3..]));
            Console.WriteLine($">> [unreliable] {line[3..]}");
            continue;
        }

        await client.SendTextAsync(line);
        Console.WriteLine($">> [reliable] {line}");
    }
}

static void Bind(LanHubClient client)
{
    client.StateChanged += s => Console.WriteLine($"[state] {s}");
    client.RosterUpdated += list => Console.WriteLine($"[roster] count={list.Count}");
    client.PlayerJoined += p => Console.WriteLine($"[join] {p.DisplayName}");
    client.PlayerLeft += p => Console.WriteLine($"[leave] {p.DisplayName}");
    client.SessionUpdated += s => Console.WriteLine(
        $"[session] id={s.SessionId} host={s.HostPlayerId} transport={s.Transport} peers={s.Peers.Count}");
    client.Error += (code, msg) => Console.WriteLine($"[error] {code}: {msg}");
    client.Disconnected += (code, msg) => Console.WriteLine($"[disconnect] {code}: {msg}");
    client.MessageReceived += m =>
        Console.WriteLine($"<< [{m.Transport}/{m.Channel}/seq={m.Sequence}] {m.FromPlayerId}: {m.AsUtf8Text()}");
}

static void PrintSession(LanHubClient client)
{
    Console.WriteLine($"已连接。PlayerId={client.PlayerId}");
    Console.WriteLine($"SessionId={client.Session.SessionId} IsHost={client.IsHost} Transport={client.Session.Transport}");
    Console.WriteLine($"MaxPayload={client.Session.MaxPayloadBytes} GameUdp={client.Session.GameUdpPort}");
    Console.WriteLine($"房间人数={client.Players.Count} peers={client.Session.Peers.Count}");
    foreach (var p in client.Session.Peers)
        Console.WriteLine($"  peer {p.PlayerId} @ {p.IpAddress}:{p.UdpPort}");
}
