using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using SubworldLibrary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using static Terraria.ModLoader.ModContent;

namespace SLUMP;

public class SLUMP : Mod
{
    #region ----- Variables -----
    #region Instance
    public static SLUMP Instance;
    #endregion

    #region Reflection
    private static Type SubserverLinkType;
    private static Type SubserverSocketType;
    private static Type SubworldSystemType;
    #endregion

    #region Communication
    public static Thread SubServerWriteLoopThread = null;
    public static List<SubServerPacket> SubServerPackets;

    public static Thread MainServerWriteLoopThread = null;
    public static List<MainServerPacket> MainServerPackets;
    #endregion

    #region Other
    public static bool IsSubserver = false;
    public static bool PacketFilterActive = false;
    public static bool WriteLoopActive = false;
    public static bool ServerCrashDetectionActive = false;
    #endregion
    #endregion

    #region ----- Load/Unload -----
    public override void Load()
    {
        Instance = this;

        if (Main.dedServ)
        {
            LoadServer();
        }
        else
        {
            LoadClient();
        }
    }
     
    private static void LoadServer()
    {
        // =================================================
        // = Initialize ====================================
        // =================================================

        IsSubserver = Program.LaunchParameters.ContainsKey("-subworld");

        SubServerPackets = [];
        MainServerPackets = [];

        // =================================================
        // = IL HOOKS ======================================
        // =================================================

        // Implement the packet filter patch:
        IL_NetMessage.CheckBytes += ModifyCheckBytes;

        // Implement the write loop patch:
        Type subworldLibrary = GetInstance<SubworldLibrary.SubworldLibrary>().GetType();
        Type[] sublibTypes = subworldLibrary?.Assembly.GetTypes();

        if (sublibTypes.Any(t => t.Name == "SubserverLink")) { SubserverLinkType = sublibTypes.First(t => t.Name == "SubserverLink"); }
        if (sublibTypes.Any(t => t.Name == "SubserverSocket")) {SubserverSocketType = sublibTypes.First(t => t.Name == "SubserverSocket"); }
        if (sublibTypes.Any(t => t.Name == "SubworldSystem")) { SubworldSystemType = sublibTypes.First(t => t.Name == "SubworldSystem"); }

        if (subworldLibrary != null && SubserverLinkType != null && SubserverSocketType != null && SubworldSystemType != null)
        {
            MethodInfo subserverLinkConnectAndSend = SubserverLinkType.GetMethod("ConnectAndSend", BindingFlags.Instance | BindingFlags.Public);
            if (subserverLinkConnectAndSend != null)
            {
                MonoModHooks.Modify(subserverLinkConnectAndSend, ModifySubserverLinkConnectAndSend);
            }

            MethodInfo subserverLinkClose = SubserverLinkType.GetMethod("Close", BindingFlags.Instance | BindingFlags.Public);
            if (subserverLinkClose != null)
            {
                MonoModHooks.Modify(subserverLinkClose, ModifySubserverLinkClose);
            }

            MethodInfo subserverSocketAsyncSend = SubserverSocketType.GetMethod("Terraria.Net.Sockets.ISocket.AsyncSend", BindingFlags.Instance | BindingFlags.NonPublic);
            if (subserverSocketAsyncSend != null)
            {
                MonoModHooks.Modify(subserverSocketAsyncSend, ModifyPipeOutWrite);
            }

            MethodInfo subworldSystemSendToMainServer = SubworldSystemType.GetMethod("SendToMainServer", BindingFlags.Static | BindingFlags.Public);
            if (subworldSystemSendToMainServer != null)
            {
                MonoModHooks.Modify(subworldSystemSendToMainServer, ModifySendToMainServer);
            }

            MethodInfo movePlayerToSubserver = SubworldSystemType.GetMethod("MovePlayerToSubserver", BindingFlags.Static | BindingFlags.NonPublic);
            if (movePlayerToSubserver != null)
            {
                MonoModHooks.Modify(movePlayerToSubserver, ModifySendWithDisconnectPacket);
            }

            MethodInfo syncDisconnect = SubworldSystemType.GetMethod("SyncDisconnect", BindingFlags.Static | BindingFlags.NonPublic);
            if (syncDisconnect != null)
            {
                MonoModHooks.Modify(syncDisconnect, ModifySendWithDisconnectPacket);
            }

            MethodInfo sendToSubserver = SubworldSystemType.GetMethod("SendToSubserver", BindingFlags.Static | BindingFlags.Public);
            if (sendToSubserver != null)
            {
                MonoModHooks.Modify(sendToSubserver, (ILContext il) => { ModifySend(il, 1); });
            }

            MethodInfo sendToAllSubservers = SubworldSystemType.GetMethod("SendToAllSubservers", BindingFlags.Static | BindingFlags.Public);
            if (sendToAllSubservers != null)
            {
                MonoModHooks.Modify(sendToAllSubservers, (ILContext il) => { ModifySend(il, 1);  });
            }

            MethodInfo sendToAllSubserversFromMod = SubworldSystemType.GetMethod("SendToAllSubserversFromMod", BindingFlags.Static | BindingFlags.Public);
            if (sendToAllSubserversFromMod != null)
            {
                MonoModHooks.Modify(sendToAllSubserversFromMod, (ILContext il) => { ModifySend(il, 1); });
            }

            MethodInfo startSubserver = SubworldSystemType.GetMethod("StartSubserver", BindingFlags.Static | BindingFlags.Public);
            if (startSubserver != null)
            { 
                MonoModHooks.Modify(startSubserver, ModifyStartSubserver);
            }

            MethodInfo stopSubserver = SubworldSystemType.GetMethod("StopSubserver", BindingFlags.Static | BindingFlags.Public);
            if (stopSubserver != null)
            {
                MonoModHooks.Modify(stopSubserver, ModifyStopSubserver);
            }

            MethodInfo sendBestiary = subworldLibrary.GetMethod("SendBestiary", BindingFlags.Static | BindingFlags.NonPublic);
            if (sendBestiary != null)
            {
                MonoModHooks.Modify(sendBestiary, (ILContext il) => { ModifySend(il, 1); });
            }

            MethodInfo sendText = subworldLibrary.GetMethod("SendText", BindingFlags.Static | BindingFlags.NonPublic);
            if (sendText != null)
            {
                MonoModHooks.Modify(sendText, (ILContext il) => { ModifySend(il, 4); });
            }

            MethodInfo denyRead = subworldLibrary.GetMethod("DenyRead", BindingFlags.Static | BindingFlags.NonPublic);
            if (denyRead != null)
            {
                MonoModHooks.Modify(denyRead, (ILContext il) => { ModifySend(il, 1); });
            }
        }
        else
        {
            Instance.Logger.Debug("FAILED TO PATCH SUBWORLD LIBRARY :( ");
            return;
        }

        // =================================================
        // = WRITELOOP =====================================
        // =================================================

        // Determine which loop to boot based on the 
        // type of server
        if (!IsSubserver)
        {
            // Launched on the main server
            SubServerWriteLoopThread = new Thread(() => SubServerWriteLoop())
            {
                Name = "SLUMP Sub Write Loop",
                IsBackground = true,
            };
            SubServerWriteLoopThread.Start();
        }
        else
        {
            // Launched on the sub server
            MainServerWriteLoopThread = new Thread(() => MainServerWriteLoop())
            {
                Name = "SLUMP Main Write Loop",
                IsBackground = true,
            };
            MainServerWriteLoopThread.Start();
        }

        // =================================================
        // = OTHER =========================================
        // =================================================

        // Re-implement the failed patch of sublib

        if (!IsSubserver
            && ModLoader.TryGetMod("SubworldLibrary", out Mod sublib)
            && sublib.Version == new Version(2, 2, 3, 1))
        {
            IL_Netplay.UpdateConnectedClients += ModifyUpdateConnectedClients;
        }
    }

    private static void LoadClient()
    { 
    
    }

    public override void Unload()
    {
        Instance = null;

        if (Main.dedServ)
        {
            UnLoaderServer();
        }
        else
        {
            UnLoadClient();
        }
    }

    private static void UnLoaderServer()
    {
        // Clear all the data
        SubServerPackets?.Clear();
        SubServerPackets = null;
        MainServerPackets?.Clear();
        MainServerPackets = null;

        SubserverLinkType = null;
        SubworldSystemType = null;
        SubserverSocketType = null;
    }

    private static void UnLoadClient()
    { 
    
    }
    #endregion

    #region ----- IL Patches -----
    #region Packet Filter Patch
    private static void ModifyCheckBytes(ILContext il)
    {
        ILCursor c = new(il);
        ILCursor d = new(il);

        int startIndex = -1;

        if (c.TryGotoNext(
            x => x.MatchLdfld<MessageBuffer>("readBuffer"),
            x => x.MatchLdloc(out startIndex),
            x => x.MatchCall(typeof(BitConverter), "ToUInt16"),
            x => x.MatchStloc(out _))
            && c.TryGotoNext(moveType: MoveType.After,
            x => x.MatchCallvirt(typeof(Stream), "get_Position"),
            x => x.MatchStloc(out _))
            && (d = c.Clone()).TryGotoNext(
            x => x.MatchLdsfld(typeof(NetMessage), "buffer"),
            x => x.MatchLdarg(0),
            x => x.MatchLdelemRef(),
            x => x.MatchLdfld(typeof(MessageBuffer), "reader")))
        {
            c.Emit(OpCodes.Ldsfld, typeof(NetMessage).GetField("buffer"));
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldelem_Ref);
            c.Emit(OpCodes.Ldloc, startIndex);
            c.Emit(OpCodes.Call, typeof(SLUMP).GetMethod("DenyPacket", BindingFlags.NonPublic | BindingFlags.Static));
            
            ILLabel label = c.DefineLabel();
            c.Emit(OpCodes.Brtrue, label);
            
            d.MarkLabel(label);
        }
        else
        {
            Instance.Logger.Debug("FAILED: 1");
        }
    }


    /// <summary>
    /// Denies reading a packet on the main thread. This bypasses the bootplayer code that would
    /// trigger when a client sends a packet the server is not allowed to receive in the
    /// current client's state.
    /// </summary>
    private static bool DenyPacket(MessageBuffer buffer, int startIndex)
    {
        // When the packet filter is disabled, do not block any packets.
        if (!PacketFilterActive)
        {
            return false;
        }

        // Block all packets that are not HELLO when the client's state is ZERO
        if (Netplay.Clients[buffer.whoAmI].State == 0 && buffer.readBuffer[startIndex + 2] != MessageID.Hello)
        {
            return true;
        }

        // When the client is in the login process (not TEN), only let certain packets through.
        if (Netplay.Clients[buffer.whoAmI].State < 10)
        {
            // Determine the Message ID
            switch (buffer.readBuffer[startIndex + 2])
            {
                // Let messages with these ID's through during login
                case <= 12 or 16 or 38 or 42 or 50 or 68 or 93 or 147 or >= 250:
                    break;

                // Block all other packets
                default:
                    return true;
            }
        }

        // If the client is connected, do not block anything.
        return false;
    }
    #endregion

    #region Write Loop Thread
    private static void ModifySubserverLinkConnectAndSend(ILContext il)
    {
        ILCursor c = new(il);

        int link = -1;

        if (c.TryGotoNext(
            x => x.MatchLdarg(out link),
            x => x.MatchLdfld("SubworldLibrary.SubserverLink", "pipeIn"),
            x => x.MatchCallvirt<NamedPipeServerStream>("WaitForConnection")))
        {
            c.Index += 3;

            ILLabel exit = il.DefineLabel();

            // Access the config option and branch if necessary
            c.Emit(OpCodes.Ldsfld, typeof(SLUMP).GetField("WriteLoopActive", BindingFlags.Static | BindingFlags.Public));
            c.Emit(OpCodes.Brfalse, exit);

            // Push the fields to the stack
            c.Emit(OpCodes.Ldarg, link);
            c.Emit(OpCodes.Ldfld, SubserverLinkType.GetField("pipeIn", BindingFlags.Instance | BindingFlags.NonPublic));
            c.Emit(OpCodes.Ldarg, link);
            c.Emit(OpCodes.Ldfld, SubserverLinkType.GetField("queue", BindingFlags.Instance | BindingFlags.NonPublic));

            // Execute the patch
            c.Emit(OpCodes.Call, typeof(SLUMP).GetMethod("AddSubServerPacket", BindingFlags.Static | BindingFlags.NonPublic));

            // RETURN here
            c.Emit(OpCodes.Ret);

            // Exit label when not used
            c.MarkLabel(exit);
        }
        else
        {
            Instance.Logger.Debug("FAILED: 2");
        }
    }

    private static void ModifySend(ILContext il, int amount)
    {
        ILCursor c = new(il);

        for (int i = 0; i < amount; i++)
        {
            int data = -1;
            ILLabel start = il.DefineLabel();
            ILLabel exit = il.DefineLabel();

            if (c.TryGotoNext(
                x => x.MatchLdloc(out data),
                x => x.MatchCall("SubworldLibrary.SubserverLink", "Send")))
            {
                c.Index += 2;
                c.MarkLabel(exit);

                if (c.TryGotoPrev(
                    x => x.MatchLdfld("SubworldLibrary.Subworld", "link"),
                    x => x.MatchDup(),
                    x => x.MatchBrtrue(out start),
                    x => x.MatchPop()))
                {
                    c.GotoLabel(start, moveType: MoveType.AfterLabel);
                    // From here there is a LINK on the stack!

                    // Create a new label
                    ILLabel branch = il.DefineLabel();

                    // Insert the config option
                    c.Emit(OpCodes.Ldsfld, typeof(SLUMP).GetField("WriteLoopActive", BindingFlags.Static | BindingFlags.Public));
                    c.Emit(OpCodes.Brfalse, branch);

                    // Get the pipeIn object
                    c.Emit(OpCodes.Ldfld, SubserverLinkType.GetField("pipeIn", BindingFlags.Instance | BindingFlags.NonPublic));

                    // Load the data
                    c.Emit(OpCodes.Ldloc, data);

                    // Call the method
                    c.Emit(OpCodes.Call, typeof(SLUMP).GetMethod("AddSubServerPacket", BindingFlags.Static | BindingFlags.NonPublic));

                    // Goto exit label
                    c.Emit(OpCodes.Br, exit);

                    // Mark branch
                    c.MarkLabel(branch);

                    // Move 2
                    c.Index += 2;
                }
                else
                {
                    Instance.Logger.Debug("FAILED: 3 " + i);
                }
            }
            else
            {
                Instance.Logger.Debug("FAILED: 4 " + i);
            }
        }
    }

    private static void ModifySendWithDisconnectPacket(ILContext il)
    {
        ILCursor c = new(il);

        ILLabel start = il.DefineLabel();
        ILLabel exit = il.DefineLabel();
        int player = -1;

        if (c.TryGotoNext(
            x => x.MatchCallvirt("System.Collections.Generic.List`1<SubworldLibrary.Subworld>", "get_Item"),
            x => x.MatchLdfld("SubworldLibrary.Subworld", "link"),
            x => x.MatchDup(),
            x => x.MatchBrtrue(out start),
            x => x.MatchPop(),
            x => x.MatchBr(out exit),
            x => x.MatchLdarg(out player)))
        {
            c.GotoLabel(start, moveType: MoveType.AfterLabel);
            // From here there is a LINK on the stack!

            // Create a new label
            ILLabel branch = il.DefineLabel();

            // Insert the config option
            c.Emit(OpCodes.Ldsfld, typeof(SLUMP).GetField("WriteLoopActive", BindingFlags.Static | BindingFlags.Public));
            c.Emit(OpCodes.Brfalse, branch);

            // Get the pipeIn object
            c.Emit(OpCodes.Ldfld, SubserverLinkType.GetField("pipeIn", BindingFlags.Instance | BindingFlags.NonPublic));

            // Get the packet
            c.Emit(OpCodes.Ldarg, player);
            c.EmitDelegate(() => { return GetInstance<SubworldLibrary.SubworldLibrary>().NetID; });
            c.Emit(OpCodes.Call, SubworldSystemType.GetMethod("GetDisconnectPacket", BindingFlags.Static | BindingFlags.NonPublic));

            // Call the method
            c.Emit(OpCodes.Call, typeof(SLUMP).GetMethod("AddSubServerPacket", BindingFlags.Static | BindingFlags.NonPublic));

            // Move to the exit
            c.Emit(OpCodes.Br, exit);

            // Mark branch
            c.MarkLabel(branch);
        }
        else
        {
            Instance.Logger.Debug("FAILED: 5");
        }
    }

    private static void ModifyPipeOutWrite(ILContext il)
    {
        ILCursor c = new(il);

        int data = -1;

        if (c.TryGotoNext(
            x => x.MatchNewarr(out _),
            x => x.MatchStloc(out data))
            && c.TryGotoNext(
            x => x.MatchLdsfld("SubworldLibrary.SubworldSystem", "pipeOut")))
        {
            ILLabel exit = il.DefineLabel();

            // Access the config option and branch if necessary
            c.Emit(OpCodes.Ldsfld, typeof(SLUMP).GetField("WriteLoopActive", BindingFlags.Static | BindingFlags.Public));
            c.Emit(OpCodes.Brfalse, exit);

            // Push the fields to the stack
            c.Emit(OpCodes.Ldsfld, typeof(SubworldSystem).GetField("pipeOut", BindingFlags.Static | BindingFlags.NonPublic));
            c.Emit(OpCodes.Ldloc, data);

            // Execute the patch
            c.Emit(OpCodes.Call, typeof(SLUMP).GetMethod("AddMainServerPacket", BindingFlags.Static | BindingFlags.NonPublic));

            // RETURN here
            c.Emit(OpCodes.Ret);

            // Exit label when not used
            c.MarkLabel(exit);
        }
        else
        {
            Instance.Logger.Debug("FAILED: 6");
        }
    }

    private static void ModifySendToMainServer(ILContext il)
    {
        ILCursor c = new(il);

        int data = -1;

        if (c.TryGotoNext(
            x => x.MatchLdsfld("SubworldLibrary.SubworldSystem", "pipeOut"),
            x => x.MatchLdloc(out data)))
        {
            ILLabel exit = il.DefineLabel();

            // Access the config option and branch if necessary
            c.Emit(OpCodes.Ldsfld, typeof(SLUMP).GetField("WriteLoopActive", BindingFlags.Static | BindingFlags.Public));
            c.Emit(OpCodes.Brfalse, exit);

            // Push the fields to the stack
            c.Emit(OpCodes.Ldsfld, typeof(SubworldSystem).GetField("pipeOut", BindingFlags.Static | BindingFlags.NonPublic));
            c.Emit(OpCodes.Ldloc, data);

            // Execute the patch
            c.Emit(OpCodes.Call, typeof(SLUMP).GetMethod("AddMainServerPacket", BindingFlags.Static | BindingFlags.NonPublic));

            // RETURN here
            c.Emit(OpCodes.Ret);

            // Exit label when not used
            c.MarkLabel(exit);
        }
        else
        {
            Instance.Logger.Debug("FAILED: 7");
        }
    }

    private static void AddSubServerPacket(NamedPipeServerStream pipe, byte[] data)
    {
        lock (SubServerPackets)
        {
            SubServerPackets.Add(new SubServerPacket(pipe, data));
        }
    }

    private static void AddMainServerPacket(NamedPipeClientStream pipe, byte[] data)
    {
        lock (MainServerPackets)
        {
            MainServerPackets.Add(new MainServerPacket(pipe, data));
        }
    }

    public class SubServerPacket(NamedPipeServerStream pipe, byte[] data)
    {
        private readonly NamedPipeServerStream _pipe = pipe;
        private readonly byte[] _data = data;

        public void Send()
        {
            try
            {
                if (_pipe.IsConnected)
                {
                    _pipe.Write(_data);
                }
            }
            catch (Exception ex) { Instance.Logger.Error(ex); }
        }
    }

    public class MainServerPacket(NamedPipeClientStream pipe, byte[] data)
    {
        private readonly NamedPipeClientStream _pipe = pipe;
        private readonly byte[] _data = data;

        public void Send()
        {
            try
            {
                if (_pipe.IsConnected)
                {
                    _pipe.Write(_data);
                }
            }
            catch (Exception ex) { Instance.Logger.Error(ex); }
        }
    }

    public static void SubServerWriteLoop()
    {
        // Create a dummy packet object to hold the packet
        SubServerPacket packet;

        // Execute the WriteLoop
        while (!Netplay.Disconnect)
        {
            // Verify the contents of the list
            if (SubServerPackets == null || SubServerPackets.Count <= 0)
            {
                // No packets to send
                continue;
            }

            // Lock the packet list temporarily to move the packet on index 0
            lock (SubServerPackets)
            {
                packet = SubServerPackets[0];
                SubServerPackets.RemoveAt(0);
            }

            // Try to send the packet
            try
            {
                packet.Send();
            } catch (Exception ex) { Instance.Logger.Error(ex); }

            // Cleanup
            packet = null;
        }
    }

    public static void MainServerWriteLoop()
    {
        // Create a dummy packet object to hold the packet
        MainServerPacket packet;

        // Execute the WriteLoop
        while (!Netplay.Disconnect)
        {
            // Verify the contents of the list
            if (MainServerPackets == null || MainServerPackets.Count <= 0)
            {
                // No packets to send
                continue;
            }

            // Lock the packet list temporarily to move the packet on index 0
            lock (MainServerPackets)
            {
                packet = MainServerPackets[0];
                MainServerPackets.RemoveAt(0);
            }

            // Try to send the packet
            try
            {
                packet.Send();
            } catch (Exception ex) { Instance.Logger.Error(ex); }

            // Cleanup
            packet = null;
        }
    }
    #endregion

    #region Server Crash Detection
    private static void ModifyStartSubserver(ILContext il)
    {
        ILCursor c = new(il);

        if (!c.TryGotoNext(
            x => x.MatchDup(),
            x => x.MatchCallvirt<Process>("get_StartInfo")))
        {
            Instance.Logger.Debug("FAILED: 8");
            return;
        }
        
        c.Emit(OpCodes.Dup);
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate((Process p, int id) => 
        { 
            p.EnableRaisingEvents = true;
            p.Exited += (sender, e) =>
            {
                if (ServerCrashDetectionActive)
                {
                    SubworldSystem.StopSubserver(id);
                }
            };
        });
    }

    private static void ModifyStopSubserver(ILContext il)
    {
        ILCursor c = new(il);

        ILLabel bypass = il.DefineLabel();
        int subworld = -1;

        if (!c.TryGotoNext(
            x => x.MatchLdloc(out subworld),
            x => x.MatchLdfld<Subworld>("link"),
            x => x.MatchBrtrue(out _)))
        {
            Instance.Logger.Debug("FAILED: 9");
            return;
        }

        int startIndex = c.Index;

        c.Index += 3;

        if (!c.TryGotoNext(
            x => x.MatchLdloc(out _),
            x => x.MatchLdfld<Subworld>("link"),
            x => x.MatchCallvirt("SubworldLibrary.SubserverLink", "Close")))
        {
            Instance.Logger.Debug("FAILED: 10");
            return;
        }

        c.Index += 3;

        c.MarkLabel(bypass);

        c.Index = startIndex;
        c.Emit(OpCodes.Br, bypass);
        c.Index--;

        // ----------------------------

        ILLabel exit = il.DefineLabel();

        c.Emit(OpCodes.Ldloc, subworld);
        c.Emit(OpCodes.Ldfld, typeof(Subworld).GetField("link", BindingFlags.Instance | BindingFlags.NonPublic));
        c.Emit(OpCodes.Brfalse, exit);

        c.Emit(OpCodes.Ldloc, subworld);
        c.Emit(OpCodes.Ldfld, typeof(Subworld).GetField("link", BindingFlags.Instance | BindingFlags.NonPublic));
        c.Emit(OpCodes.Ldfld, SubserverLinkType.GetField("_connected", BindingFlags.Instance | BindingFlags.NonPublic));
        c.EmitDelegate((bool connected) =>
        {
            if (!connected)
            {
                Instance.Logger.Info("Server crash detected!");
                ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral("Server crash detected! Please check logs!"), Color.Red);
            }
        });

        c.Emit(OpCodes.Ldloc, subworld);
        c.Emit(OpCodes.Ldfld, typeof(Subworld).GetField("link", BindingFlags.Instance | BindingFlags.NonPublic));
        c.Emit(OpCodes.Callvirt, SubserverLinkType.GetMethod("Close", BindingFlags.Instance | BindingFlags.Public));

        c.MarkLabel(exit);
    }

    private static void ModifySubserverLinkClose(ILContext il)
    {
        ILCursor c = new(il);

        c.Emit(OpCodes.Ldarg_0);
        c.Emit(OpCodes.Ldfld, SubserverLinkType.GetField("_connected", BindingFlags.Instance | BindingFlags.NonPublic));
        c.EmitDelegate((bool connected) =>
        {
            if (!connected)
            {
                Instance.Logger.Info("Server crash detected!");
                ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral("Server crash detected! Please check logs!"), Color.Red);  
            }
        });

        Instance.Logger.Debug(il.ToString());
    }
    #endregion

    #region Other
    private static void ModifyUpdateConnectedClients(ILContext il)
    {
        ILCursor c = new(il);

        int index = -1;

        if (c.TryGotoNext(
            x => x.MatchLdloc(out index),
            x => x.MatchLdelemRef(),
            x => x.MatchCallvirt<RemoteClient>("Reset")))
        {
            c.Index += 3;

            c.Emit(OpCodes.Ldloc, index);
            c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("SyncDisconnect", BindingFlags.NonPublic | BindingFlags.Static));
        }
        else
        {
            Instance.Logger.Error("FAILED: 11");
        }
    }
    #endregion
    #endregion
}
