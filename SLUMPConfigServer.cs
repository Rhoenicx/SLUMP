using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace SLUMP;

public class SLUMPConfigServer : ModConfig
{
    /// <summary>
    /// Settings for the server
    /// </summary>
    public override ConfigScope Mode => ConfigScope.ServerSide;

    /// <summary>
    /// Determines if the Asynchronous Send patch has been enabled.
    /// This patch is meant to fix the deadlock problem on 
    /// main server and sub servers.
    /// </summary>
    [DefaultValue(true)]
    public bool EnableAsyncWritePatch = true;

    /// <summary>
    /// Determines if the packet filter is enabled, when this is enabled
    /// the server will intentionally drop some client packets during
    /// the login process. This fixes a potential client kicking problem.
    /// </summary>
    [DefaultValue(true)]
    public bool EnablePacketFilterPatch = true;

    /// <summary>
    /// Determines if the Server Crash detection is enabled. When enabled
    /// SLUMP will check if a server stopped or crashed unexpectedly and
    /// puts all players back to main world.
    /// </summary>
    [DefaultValue(true)]
    public bool EnableServerCrashDetection = true;

    // Update the values when the config has been changed
    public override void OnChanged()
    {
        SLUMP.PacketFilterActive = EnablePacketFilterPatch;
        SLUMP.WriteLoopActive = EnableAsyncWritePatch;
        SLUMP.ServerCrashDetectionActive = EnableServerCrashDetection;
    }
}