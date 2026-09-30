using System.Diagnostics;
using LanHub.Core.Models;

namespace LanHub.Core;

public static class GameLauncher
{
    public static Process StartRegisteredGame(
        GameEntry game,
        string playerId,
        string launchToken,
        int sdkPort)
    {
        if (!File.Exists(game.ExePath))
            throw new FileNotFoundException("游戏可执行文件不存在", game.ExePath);

        var psi = new ProcessStartInfo
        {
            FileName = game.ExePath,
            Arguments = game.Arguments ?? "",
            WorkingDirectory = Path.GetDirectoryName(game.ExePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        psi.Environment["LANHUB_TOKEN"] = launchToken;
        psi.Environment["LANHUB_GAME_ID"] = game.GameId;
        psi.Environment["LANHUB_PLAYER_ID"] = playerId;
        psi.Environment["LANHUB_SDK_PORT"] = sdkPort.ToString();

        return Process.Start(psi) ?? throw new InvalidOperationException("无法启动进程");
    }
}
