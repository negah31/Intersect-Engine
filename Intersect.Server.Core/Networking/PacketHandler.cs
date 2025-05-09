using Intersect.Enums;
using Intersect.GameObjects;
using Intersect.Network;
using Intersect.Network.Packets;
using Intersect.Network.Packets.Client;
using Intersect.Server.Database;
using Intersect.Server.Database.Logging.Entities;
using Intersect.Server.Database.PlayerData;
using Intersect.Server.Database.PlayerData.Players;
using Intersect.Server.Database.PlayerData.Security;
using Intersect.Server.Entities;
using Intersect.Server.General;
using Intersect.Server.Localization;
using Intersect.Server.Maps;
using Intersect.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Intersect.Core;
using Intersect.Framework.Core;
using Intersect.Framework.Core.GameObjects.Crafting;
using Intersect.Framework.Core.GameObjects.Events;
using Intersect.Framework.Core.GameObjects.Items;
using Intersect.Framework.Core.GameObjects.Maps;
using Intersect.Framework.Core.GameObjects.PlayerClass;
using Intersect.Framework.Core.Security;
using Intersect.Network.Packets.Server;
using Intersect.Server.Core;
using Microsoft.Extensions.Logging;
using ChatMsgPacket = Intersect.Network.Packets.Client.ChatMsgPacket;
using LoginPacket = Intersect.Network.Packets.Client.LoginPacket;
using PartyInvitePacket = Intersect.Network.Packets.Client.PartyInvitePacket;
using PingPacket = Intersect.Network.Packets.Client.PingPacket;
using TradeRequestPacket = Intersect.Network.Packets.Client.TradeRequestPacket;

namespace Intersect.Server.Networking;

internal sealed partial class PacketHandler
{
    public IServerContext Context { get; }

    public ILogger Logger => Context.Logger;

    public PacketHandlerRegistry Registry { get; }

    public static PacketHandler Instance { get; private set; }

    public static long ReceivedBytes => AcceptedBytes + DroppedBytes;

    public static long ReceivedPackets => AcceptedPackets + DroppedPackets;

    public static ConcurrentDictionary<string, long> AcceptedPacketTypes = new ConcurrentDictionary<string, long>();

    public static long AcceptedBytes { get; set; }

    public static long DroppedBytes { get; set; }

    public static long AcceptedPackets { get; set; }

    public static long DroppedPackets { get; set; }

    public static void ResetMetrics()
    {
        AcceptedBytes = 0;
        AcceptedPackets = 0;
        DroppedBytes = 0;
        DroppedPackets = 0;
    }

    public PacketHandler(IServerContext context, PacketHandlerRegistry packetHandlerRegistry)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Registry = packetHandlerRegistry ?? throw new ArgumentNullException(nameof(packetHandlerRegistry));

        if (!Registry.TryRegisterAvailableMethodHandlers(GetType(), this, false) || Registry.IsEmpty)
        {
            throw new InvalidOperationException("Failed to register method handlers, see logs for more details.");
        }

        if (!Registry.TryRegisterAvailableTypeHandlers(GetType().Assembly))
        {
            throw new InvalidOperationException("Failed to register type handlers, see logs for more details.");
        }

        Instance = this;
    }

    public bool PreProcessPacket(IConnection connection, long pSize)
    {
        if (ShouldAcceptPacket(connection, pSize))
        {
            AcceptedPackets++;
            AcceptedBytes += pSize;
            return true;
        }
        DroppedPackets++;
        DroppedBytes += pSize;
        return false;
    }

    public bool ShouldAcceptPacket(IConnection connection, long pSize)
    {
        var client = Client.FindBeta4Client(connection);
        if (client == null)
        {
            return false;
        }

        if (client.Banned || client.FloodKicked)
        {
            return false;
        }

        var packetOptions = Options.Instance.Security?.Packets;
        var thresholds = client.PacketFloodingThresholds;


        if (pSize > thresholds.MaxPacketSize)
        {
            ApplicationContext.Context.Value?.Logger.LogError(
                Strings.Errors.ErrorFloodSize.ToString(
                    pSize, client?.User?.Name ?? "", client?.Entity?.Name ?? "", client.Ip
                )
            );

            client.FloodKicked = true;
            client.Disconnect("Flooding detected.");

            return false;
        }

        if (client.PacketTimer > Timing.Global.Milliseconds)
        {
            client.PacketCount++;
            if (client.PacketCount > thresholds.MaxPacketPerSec)
            {
                ApplicationContext.Context.Value?.Logger.LogError(
                    Strings.Errors.ErrorFloodBurst.ToString(
                        client.PacketCount, client?.User?.Name ?? "", client?.Entity?.Name ?? "", client.Ip
                    )
                );

                client.FloodKicked = true;
                client.Disconnect("Flooding detected.");

                return false;
            }
            else if (client.PacketCount > thresholds.KickAvgPacketPerSec && !client.PacketFloodDetect)
            {
                client.FloodDetects++;
                client.TotalFloodDetects++;
                client.PacketFloodDetect = true;

                if (client.FloodDetects > 3)
                {
                    //ApplicationContext.Context.Value?.Logger.LogError(
                    //    Strings.Errors.floodaverage.ToString(
                    //        client.TotalFloodDetects, client?.User?.Name ?? "", client?.Entity?.Name ?? "",
                    //        client.GetIp()
                    //    )
                    //);

                    client.FloodKicked = true;
                    client.Disconnect("Flooding detected.");

                    return false;
                }

                //TODO: Make this check a rolling average somehow to prevent constant flooding right below the threshholds.
                if (client.TotalFloodDetects > 10)
                {
                    //ApplicationContext.Context.Value?.Logger.LogError(string.Format("[Flood]: Total Detections: {00} [User: {01} | Player: {02} | IP {03}]", client.TotalFloodDetects, client?.User?.Name ?? "", client?.Entity?.Name ?? "", client.GetIp()));
                    //client.Disconnect("Flooding detected.");
                    //return false;
                }
            }
            else if (client.PacketCount < thresholds.KickAvgPacketPerSec / 2)
            {
                if (client.FloodDetects > 1)
                {
                    client.FloodDetects--;
                }
            }
        }
        else
        {
            if (client.PacketFloodDetect)
            {
                ApplicationContext.Context.Value?.Logger.LogTrace(string.Format("Possible Flood Detected: Packets in last second {00} [User: {01} | Player: {02} | IP {03}]", client.PacketCount, client?.User?.Name ?? "", client?.Entity?.Name ?? "", client.Ip));
            }

            client.PacketCount = 0;
            client.PacketTimer = Timing.Global.Milliseconds + 1000;
            client.PacketFloodDetect = false;
        }

        return true;
    }

    public bool HandlePacket(IConnection connection, IPacket packet)
    {
        packet.ReceiveTime = Timing.Global.Milliseconds;

        var client = Client.FindBeta4Client(connection);
        if (client == null)
        {
            ApplicationContext.Context.Value?.Logger.LogError("Client was null when packet was being handled.");
            return false;
        }

        while (client.RecentPackets.Count > 75)
        {
            client.RecentPackets.TryDequeue(out IPacket pkt);
        }

        client.RecentPackets.Enqueue(packet);

        if (client.Banned)
        {
            return false;
        }

        switch (packet)
        {
            case Network.Packets.EditorPacket _ when !client.IsEditor:
                return false;

            case null:
                ApplicationContext.Context.Value?.Logger.LogError($@"Received null packet from {client.Id} ({client.Name}).");
                client.Disconnect("Error processing packet.");

                return true;
        }

        if (!packet.IsValid)
        {
            return false;
        }

        try
        {
            var sanitizedFields = packet.Sanitize();
            if (sanitizedFields is { Count: > 0 })
            {
                var sanitizationBuilder = new StringBuilder(256, 8192);
                sanitizationBuilder.Append("Received out-of-bounds values in '");
                sanitizationBuilder.Append(packet.GetType().Name);
                sanitizationBuilder.Append("' packet from '");
                sanitizationBuilder.Append(client.Ip);
                sanitizationBuilder.Append("', '");
                sanitizationBuilder.Append(client.Name);
                sanitizationBuilder.AppendLine("': ");

                foreach (var field in sanitizedFields)
                {
                    sanitizationBuilder.Append(field.Key);
                    sanitizationBuilder.Append(" = ");
                    sanitizationBuilder.Append(field.Value.Before);
                    sanitizationBuilder.Append(" => ");
                    sanitizationBuilder.Append(field.Value.After);
                    sanitizationBuilder.AppendLine();
                }

                ApplicationContext.Context.Value?.Logger.LogWarning(sanitizationBuilder.ToString());
            }
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(
                $"Client Packet Error! [Packet: {packet.GetType().Name} | User: {client.Name ?? ""} | Player: {client.Entity?.Name ?? ""} | IP {client.Ip}]"
            );

            ApplicationContext.Context.Value?.Logger.LogError(exception, "Client packet error");
            client.Disconnect("Error processing packet.");

            return false;
        }

        if (packet is AbstractTimedPacket timedPacket)
        {
            var ping = connection.Statistics.Ping;

            var localAdjusted = Timing.Global.Ticks;
            var localAdjustedMs = localAdjusted / TimeSpan.TicksPerMillisecond;
            var localOffsetMs = Timing.Global.MillisecondsOffset;
            var localUtcMs = localOffsetMs + localAdjustedMs;

            var remoteAdjusted = timedPacket.Adjusted;
            var remoteAdjustedMs = remoteAdjusted / TimeSpan.TicksPerMillisecond;
            var remoteUtcMs = timedPacket.UTC / TimeSpan.TicksPerMillisecond;
            var remoteOffsetMs = timedPacket.Offset / TimeSpan.TicksPerMillisecond;

            var deltaAdjusted = localAdjustedMs - remoteAdjustedMs;
            var deltaWithPing = deltaAdjusted - ping;

            var configurableMininumPing = Options.Instance.Security.Packets.MinimumPing;
            var configurableErrorMarginFactor = Options.Instance.Security.Packets.ErrorMarginFactor;
            var configurableNaturalLowerMargin = Options.Instance.Security.Packets.NaturalLowerMargin;
            var configurableNaturalUpperMargin = Options.Instance.Security.Packets.NaturalUpperMargin;
            var configurableAllowedSpikePackets = Options.Instance.Security.Packets.AllowedSpikePackets;
            var configurableBaseDesyncForgiveness = Options.Instance.Security.Packets.BaseDesyncForegiveness;
            var configurablePingDesyncForgivenessFactor =
                Options.Instance.Security.Packets.DesyncForgivenessFactor;

            var configurablePacketDesyncForgivenessInternal =
                Options.Instance.Security.Packets.DesyncForgivenessInterval;

            var errorMargin = Math.Max(ping, configurableMininumPing) * configurableErrorMarginFactor;
            var errorRangeMinimum = ping - errorMargin;
            var errorRangeMaximum = ping + errorMargin;

            var deltaWithErrorMinimum = deltaAdjusted - errorRangeMinimum;
            var deltaWithErrorMaximum = deltaAdjusted - errorRangeMaximum;

            var natural = configurableNaturalLowerMargin < deltaAdjusted &&
                          deltaAdjusted < configurableNaturalUpperMargin;

            var naturalWithErrorMinimum = configurableNaturalLowerMargin < deltaWithErrorMinimum &&
                                          deltaWithErrorMinimum < configurableNaturalUpperMargin;

            var naturalWithErrorMaximum = configurableNaturalLowerMargin < deltaWithErrorMaximum &&
                                          deltaWithErrorMaximum < configurableNaturalUpperMargin;

            var naturalWithPing = configurableNaturalLowerMargin < deltaWithPing &&
                                  deltaWithPing < configurableNaturalUpperMargin;

            var adjustedDesync = Math.Abs(deltaAdjusted);
            var timeDesync = adjustedDesync >
                             configurableBaseDesyncForgiveness +
                             errorRangeMaximum * configurablePingDesyncForgivenessFactor;

            if (timeDesync && Timing.Global.MillisecondsUtc > client.LastPacketDesyncForgiven)
            {
                client.LastPacketDesyncForgiven =
                    Timing.Global.MillisecondsUtc + configurablePacketDesyncForgivenessInternal;

                PacketSender.SendPing(client, false);
                timeDesync = false;
            }

            var logDiagnostics = Debugger.IsAttached;
#if !DIAGNOSTIC
            if (packet is PingPacket)
            {
                logDiagnostics = false;
            }
#endif

            if (logDiagnostics)
            {
                ApplicationContext.Context.Value?.Logger.LogDebug(
                    "\n\t" +
                    $"Ping[Connection={ping}, Error={Math.Abs(ping)}]\n\t" +
                    $"Error[G={Math.Abs(localAdjustedMs - remoteAdjustedMs)}, R={Math.Abs(localUtcMs - remoteUtcMs)}, O={Math.Abs(localOffsetMs - remoteOffsetMs)}]\n\t" +
                    $"Delta[Adjusted={deltaAdjusted}, AWP={deltaWithPing}, AWEN={deltaWithErrorMinimum}, AWEX={deltaWithErrorMaximum}]\n\t" +
                    $"Natural[A={natural} WP={naturalWithPing}, WEN={naturalWithErrorMinimum}, WEX={naturalWithErrorMaximum}]\n\t" +
                    $"Time Desync[{timeDesync}]\n\t" +
                    $"Packet[{packet.ToString()}]"
                );
            }

            var naturalWithError = naturalWithErrorMinimum || naturalWithErrorMaximum;

            if (!(natural || naturalWithError || naturalWithPing) || timeDesync)
            {
                //No matter what, let's send the ping to resync time.
                PacketSender.SendPing(client, false);

                if (client.TimedBufferPacketsRemaining-- < 1 || timeDesync)
                {
                    //if (!(packet is PingPacket))
                    //{
                    //    ApplicationContext.Context.Value?.Logger.LogWarning(
                    //        "Dropping Packet. Time desync? Debug Info:\n\t" +
                    //        $"Ping[Connection={ping}, NetConnection={ncPing}, Error={Math.Abs(ncPing - ping)}]\n\t" +
                    //        $"Server Time[Ticks={Timing.Global.Ticks}, AdjustedMs={localAdjustedMs}, TicksUTC={Timing.Global.TicksUTC}, Offset={Timing.Global.TicksOffset}]\n\t" +
                    //        $"Client Time[Ticks={timedPacket.Adjusted}, AdjustedMs={remoteAdjustedMs}, TicksUTC={timedPacket.UTC}, Offset={timedPacket.Offset}]\n\t" +
                    //        $"Error[G={Math.Abs(localAdjustedMs - remoteAdjustedMs)}, R={Math.Abs(localUtcMs - remoteUtcMs)}, O={Math.Abs(localOffsetMs - remoteOffsetMs)}]\n\t" +
                    //        $"Delta[Adjusted={deltaAdjusted}, AWP={deltaWithPing}, AWEN={deltaWithErrorMinimum}, AWEX={deltaWithErrorMaximum}]\n\t" +
                    //        $"Natural[A={natural} WP={naturalWithPing}, WEN={naturalWithErrorMinimum}, WEX={naturalWithErrorMaximum}]\n\t" +
                    //        $"Time Desync[{timeDesync}]\n\t" +
                    //        $"Packet[{packet}]"
                    //    );
                    //}

                    try
                    {
                        HandleDroppedPacket(client, packet);
                    }
                    catch (Exception exception)
                    {
                        ApplicationContext.Context.Value?.Logger.LogDebug(
                            exception,
                            $"Exception thrown dropping packet ({packet.GetType().Name}/{client.Ip}/{client.Name ?? ""}/{client.Entity?.Name ?? ""})"
                        );
                    }

                    return false;
                }
            }
            else if (natural && naturalWithPing && naturalWithError)
            {
                client.TimedBufferPacketsRemaining = configurableAllowedSpikePackets;
            }
            else if (natural && naturalWithPing ||
                     naturalWithPing && naturalWithError ||
                     naturalWithError && natural)
            {
                client.TimedBufferPacketsRemaining += (int)Math.Ceiling(
                    (configurableAllowedSpikePackets - client.TimedBufferPacketsRemaining) / 2.0
                );
            }
            else
            {
                client.TimedBufferPacketsRemaining = Math.Min(configurableAllowedSpikePackets, client.TimedBufferPacketsRemaining++);
            }
        }

        if (!AcceptedPacketTypes.ContainsKey(packet.GetType().Name))
        {
            AcceptedPacketTypes.TryAdd(packet.GetType().Name, 0);
        }
        AcceptedPacketTypes[packet.GetType().Name]++;

        client.HandlePacketQueue.Enqueue(packet);
        lock (client.HandlePacketQueue)
        {
            if (!client.PacketHandlingQueued)
            {
                client.PacketHandlingQueued = true;
                Client.EnqueueNetworkTask(client.HandlePackets);
            }
        }

        return true;
    }

    public bool ProcessPacket(IPacket packet, Client client)
    {
        if (!Registry.TryGetHandler(packet, out HandlePacketGeneric handler))
        {
            Logger.LogError($"No registered handler for {packet.GetType().FullName}!");

            return false;
        }

        if (Registry.TryGetPreprocessors(packet, out var preprocessors))
        {
            if (!preprocessors.All(preprocessor => preprocessor.Handle(client, packet)))
            {
                // Preprocessors are intended to be silent filter functions
                return false;
            }
        }

        if (Registry.TryGetPreHooks(packet, out var preHooks))
        {
            if (!preHooks.All(hook => hook.Handle(client, packet)))
            {
                // Hooks should not fail, if they do that's an error
                Logger.LogError($"PreHook handler failed for {packet.GetType().FullName}.");
                return false;
            }
        }

        if (!handler(client, packet))
        {
            return false;
        }

        if (Registry.TryGetPostHooks(packet, out var postHooks))
        {
            if (!postHooks.All(hook => hook.Handle(client, packet)))
            {
                // Hooks should not fail, if they do that's an error
                Logger.LogError($"PostHook handler failed for {packet.GetType().FullName}.");
                return false;
            }
        }

        return true;
    }

    #region "Client Packets"

    public void HandleDroppedPacket(Client client, IPacket packet)
    {
        switch (packet)
        {
            case MovePacket _:
                PacketSender.SendEntityPositionTo(client, client.Entity);

                break;
        }
    }

    //PingPacket
    public void HandlePacket(Client client, PingPacket packet)
    {
        client.Pinged();
        if (!packet.Responding)
        {
            PacketSender.SendPing(client, false);
        }
    }

    //LoginPacket
    public void HandlePacket(Client client, LoginPacket packet)
    {
        if (client.AccountAttempts > 3 && client.TimeoutMs > Timing.Global.Milliseconds)
        {
            PacketSender.SendError(client, Strings.Errors.ErrorTimeout, Strings.General.NoticeError);
            client.ResetTimeout();

            return;
        }


        client.ResetTimeout();

        // Are we at capacity yet, or can this user still log in?
        if (Player.OnlinePlayers.Count >= Options.Instance.MaximumLoggedInUsers)
        {
            PacketSender.SendError(client, Strings.Networking.ServerFull, Strings.General.NoticeError);

            return;
        }

        var username = packet.Username;
        if (!User.TryLogin(username, packet.Password, out var user, out var failureReason))
        {
            UserActivityHistory.LogActivity(
                Guid.Empty,
                Guid.Empty,
                client?.Ip,
                UserActivityHistory.PeerType.Client,
                UserActivityHistory.UserAction.FailedLogin,
                $"{username},{failureReason.Type}"
            );

            if (failureReason.Type == LoginFailureType.InvalidCredentials)
            {
                client.FailedAttempt();
                PacketSender.SendError(client, Strings.Account.BadLogin, Strings.General.NoticeError);
            }
            else
            {
                PacketSender.SendError(client, Strings.Account.UnknownServerErrorRetryLogin, Strings.General.NoticeError);
            }

            return;
        }

        List<TaskCompletionSource> logoutCompletionSources = [];

        var disconnectedClients = false;
        lock (Client.GlobalLock)
        {
            foreach (var otherClient in Client.Instances.ToArray())
            {
                if (otherClient == null)
                {
                    continue;
                }

                if (otherClient == client)
                {
                    continue;
                }

                if (otherClient.IsEditor)
                {
                    continue;
                }

                if (!string.Equals(otherClient.Name, username, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                TaskCompletionSource logoutCompletionSource = new();
                otherClient.Disconnect(logoutCompletionSource: logoutCompletionSource);
                logoutCompletionSources.Add(logoutCompletionSource);

                disconnectedClients = true;
            }
        }

        if (disconnectedClients)
        {
            var disconnectionCount = logoutCompletionSources.Count;
            ApplicationContext.Context.Value?.Logger.LogInformation($"Login of {username} waiting on {disconnectionCount} clients before continuing...");

            Task.WaitAll(logoutCompletionSources.Select(source => source.Task).ToArray());

            ApplicationContext.Context.Value?.Logger.LogInformation($"Continuing login of {username}...");

            if (!User.TryLogin(
                    username,
                    packet.Password,
                    out user,
                    out failureReason
                ))
            {
                UserActivityHistory.LogActivity(
                    Guid.Empty,
                    Guid.Empty,
                    client?.Ip,
                    UserActivityHistory.PeerType.Client,
                    UserActivityHistory.UserAction.FailedLogin,
                    $"{username},{failureReason.Type}"
                );

                if (failureReason.Type == LoginFailureType.InvalidCredentials)
                {
                    client.FailedAttempt();
                    PacketSender.SendError(client, Strings.Account.BadLogin, Strings.General.NoticeError);
                }
                else
                {
                    PacketSender.SendError(client, Strings.Account.UnknownServerErrorRetryLogin, Strings.General.NoticeError);
                }

                return;
            }
        }

        client.SetUser(user);

        if (client.User != null)
        {
            //Logged In
            client.PacketFloodingThresholds = Options.Instance.Security.Packets.PlayerThresholds;

            if (client.User.Power.IsAdmin || client.User.Power.IsModerator)
            {
                client.PacketFloodingThresholds = Options.Instance.Security.Packets.ModAdminThresholds;
            }
        }

        //Check for ban
        var isBanned = Ban.CheckBan(client.User, client.Ip);
        if (isBanned != null)
        {
            client.SetUser(null);
            client.Banned = true;
            PacketSender.SendError(client, isBanned, Strings.General.NoticeError);

            return;
        }

        //Check that server is in admin only mode
        if (Options.Instance.AdminOnly)
        {
            if (client.Power == UserRights.None)
            {
                PacketSender.SendError(client, Strings.Account.AdminOnly, Strings.General.NoticeError);

                return;
            }
        }

        //Check Mute Status and Load into user property
        Mute.FindMuteReason(client.User, client.Ip);

        UserActivityHistory.LogActivity(user?.Id ?? Guid.Empty, Guid.Empty, client?.Ip, UserActivityHistory.PeerType.Client, UserActivityHistory.UserAction.Login, null);

        // PacketSender.SendServerConfig(client); // TODO: We already send this when the client is initialized, why do we send it again here?

        //Check if we already have a player online/stuck in combat.. if so we will login straight to him
        foreach (var chr in client.Characters)
        {
            if (Player.FindOnline(chr.Id) != null)
            {
                client.LoadCharacter(chr);
                client.Entity.SetOnline();

                PacketSender.SendJoinGame(client);
                return;
            }
        }

        // Send newly accounts with 0 characters thru the character creation menu.
        if (client.Characters == default || client.Characters.Count < 1)
        {
            PacketSender.SendGameObjects(client, GameObjectType.Class);
            PacketSender.SendCreateCharacter(client, force: true);
            return;
        }

        // Show character select menu or login right away by following configuration preferences.
        if (Options.Instance.Player.MaxCharacters > 1 || !Options.Instance.Player.SkipCharacterSelect)
        {
            PacketSender.SendPlayerCharacters(client);
        }
        else
        {
            var character = DbInterface.GetUserCharacter(
                client.User,
                client.Characters.First().Id,
                explicitLoad: true
            );
            client.LoadCharacter(character);
            client.Entity.SetOnline();
            PacketSender.SendJoinGame(client);
        }
    }

    //LogoutPacket
    public void HandlePacket(Client client, LogoutPacket packet)
    {
        if (client == null)
        {
            return;
        }

        UserActivityHistory.LogActivity(client.User?.Id ?? Guid.Empty, Guid.Empty, client.Ip,
            UserActivityHistory.PeerType.Client,
            packet.ReturningToCharSelect
                ? UserActivityHistory.UserAction.SwitchPlayer
                : UserActivityHistory.UserAction.DisconnectLogout, $"{client.Name},{client.Entity?.Name}");

        if (packet.ReturningToCharSelect &&
            (Options.Instance.Player.MaxCharacters > 1 || !Options.Instance.Player.SkipCharacterSelect))
        {
            ApplicationContext.Context.Value?.Logger.LogDebug($"[{nameof(LogoutPacket)}] Returning to character select from player {client.Entity?.Id} ({client.User?.Id})");
            client.Entity?.TryLogout(false, true);
            client.Entity = default;
            PacketSender.SendPlayerCharacters(client, skipLoadingRelationships: true);
        }
        else
        {
            client.Logout();
        }
    }

    //NeedMapPacket
    public void HandlePacket(Client client, GetObjectData<MapDescriptor> packet)
    {
        var player = client?.Entity;

        if (player == default)
        {
            return;
        }

        if (!MapController.TryGet(player.MapId, out var playerMapController))
        {
            return;
        }

        var playerMapGrid = DbInterface.GetGrid(playerMapController.MapGrid);
        var validKeys = packet.CacheKeys.Where(
                key => playerMapGrid.MapIds.Contains(key.Id.Guid) && MapController.Lookup.Keys.Contains(key.Id.Guid)
            )
            .ToArray();

        if (validKeys.Any(key => key.Id.Guid == player.MapId))
        {
            PacketSender.SendMapGrid(client, playerMapGrid);
        }

        foreach (var cacheKey in validKeys)
        {
            if (!MapController.TryGet(cacheKey.Id.Guid, out var descriptor))
            {
                continue;
            }

            var version = MapPacket.ComputeCacheVersion(
                descriptor.Id,
                descriptor.Revision,
                descriptor.MapGridX,
                descriptor.MapGridY,
                descriptor.GetCameraHolds()
            );

            var checksumToCompare = string.Equals(cacheKey.Version, version, StringComparison.Ordinal)
                ? cacheKey.Checksum
                : default;

            PacketSender.SendMap(client, cacheKey.Id.Guid, checksum: checksumToCompare);
        }
    }

    //MovePacket
    public void HandlePacket(Client client, MovePacket packet)
    {
        try
        {
            var player = client?.Entity;
            if (player == null)
            {
                System.Diagnostics.Debug.WriteLine("Mouvement rejeté: Player null");
                return;
            }

            foreach (var status in player.CachedStatuses)
            {
                if (status.Type == SpellEffect.Stun ||
                    status.Type == SpellEffect.Snare ||
                    status.Type == SpellEffect.Sleep)
                {
                    System.Diagnostics.Debug.WriteLine($"Mouvement rejeté: Statut bloquant, Type={status.Type}");
                    return;
                }
            }

            if (!TileHelper.IsTileValid(packet.MapId, packet.X, packet.Y))
            {
                PacketSender.SendEntityPositionTo(client, client.Entity);
                System.Diagnostics.Debug.WriteLine($"Mouvement rejeté: Tile invalide, MapId={packet.MapId}, X={packet.X}, Y={packet.Y}");
                return;
            }

            var clientTime = packet.Adjusted / TimeSpan.TicksPerMillisecond;
            var timeTolerance = 50; // Tolérance de 50 ms
            if (player.ClientMoveTimer <= clientTime + timeTolerance &&
                (Options.Instance.Player.AllowCombatMovement || player.ClientAttackTimer <= clientTime))
            {
                if (
                    (player.CanMoveInDirection(packet.Dir, out var blockerType, out _) || blockerType == MovementBlockerType.Slide) &&
                    client.Entity.MoveRoute == default
                )
                {
                    player.Sprinting = packet.Sprinting;
                    player.Move(packet.Dir, player, false);
                    var utcDeltaMs = (Timing.Global.TicksUtc - packet.UTC) / TimeSpan.TicksPerMillisecond;
                    var latencyAdjustmentMs = -(client.Ping + Math.Max(0, utcDeltaMs));
                    var currentMs = Timing.Global.Ticks; // Remplacement temporaire de packet.ReceiveTime
                    if (player.MoveTimer > currentMs)
                    {
                        player.MoveTimer = currentMs + latencyAdjustmentMs + (long)(player.GetMovementTime() * 0.75f);
                        player.ClientMoveTimer = clientTime + (long)player.GetMovementTime();
                        System.Diagnostics.Debug.WriteLine($"Mouvement accepté: ClientTime={clientTime}, ClientMoveTimer={player.ClientMoveTimer}, MovementTime={player.GetMovementTime()}, Sprinting={player.Sprinting}");
                    }
                }
                else
                {
                    PacketSender.SendEntityPositionTo(client, client.Entity);
                    System.Diagnostics.Debug.WriteLine($"Mouvement rejeté: Direction bloquée, Dir={packet.Dir}, BlockerType={blockerType}");
                    return;
                }
            }
            else
            {
                PacketSender.SendEntityPositionTo(client, client.Entity);
                System.Diagnostics.Debug.WriteLine($"Mouvement rejeté: Temps invalide, ClientMoveTimer={player.ClientMoveTimer}, ClientTime={clientTime}, AllowCombatMovement={Options.Instance.Player.AllowCombatMovement}, ClientAttackTimer={player.ClientAttackTimer}");
                return;
            }

            if (packet.MapId != client.Entity.MapId || packet.X != client.Entity.X || packet.Y != client.Entity.Y)
            {
                PacketSender.SendEntityPositionTo(client, client.Entity);
                System.Diagnostics.Debug.WriteLine($"Mouvement rejeté: Position incorrecte, PacketMapId={packet.MapId}, EntityMapId={client.Entity.MapId}, PacketX={packet.X}, EntityX={client.Entity.X}, PacketY={packet.Y}, EntityY={client.Entity.Y}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erreur dans HandlePacket: {ex.Message}, StackTrace: {ex.StackTrace}");
        }
    }
    //ChatMsgPacket
    public void HandlePacket(Client client, ChatMsgPacket packet)
    {
        var player = client?.Entity;

        if (player == null)
        {
            return;
        }

        var msg = packet.Message;
        var channel = packet.Channel;

        if (string.IsNullOrWhiteSpace(msg))
        {
            return;
        }

        if (client?.User.IsMuted ?? false) //Don't let the tongueless toxic kids speak.
        {
            PacketSender.SendChatMsg(player, client?.User?.Mute?.Reason, ChatMessageType.Notice);

            return;
        }

        if (player.LastChatTime > Timing.Global.MillisecondsUtc)
        {
            PacketSender.SendChatMsg(player, Strings.Chat.TooFast, ChatMessageType.Notice);
            player.LastChatTime = Timing.Global.MillisecondsUtc + Options.Instance.Chat.MinIntervalBetweenChats;

            return;
        }

        if (packet.Message.Length > Options.Instance.Chat.MaxChatLength)
        {
            return;
        }

        //If no /command, then use the designated channel.
        var cmd = string.Empty;
        if (!msg.StartsWith("/"))
        {
            switch (channel)
            {
                case 0: //local
                    cmd = Strings.Chat.LocalCommand;

                    break;

                case 1: //global
                    cmd = Strings.Chat.AllCommand;

                    break;

                case 2: //party
                    cmd = Strings.Chat.PartyCommand;

                    break;

                case 3:
                    cmd = Strings.Guilds.GuildCommand;
                    break;

                case 4: //admin
                    cmd = Strings.Chat.AdminCommand;

                    break;

                case 5: //private
                    PacketSender.SendChatMsg(player, msg, ChatMessageType.Local);

                    return;
            }
        }
        else
        {
            cmd = msg.Split()[0].ToLower();
            msg = msg.Remove(0, cmd.Length);
        }

        var msgSplit = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        if (cmd == Strings.Chat.LocalCommand)
        {
            if (msg.Trim().Length == 0)
            {
                return;
            }

            var chatColor = CustomColors.Chat.LocalChat;

            if (client?.Power.IsAdmin ?? false)
            {
                chatColor = CustomColors.Chat.AdminLocalChat;
            }
            else if (client?.Power.IsModerator ?? false)
            {
                chatColor = CustomColors.Chat.ModLocalChat;
            }

            PacketSender.SendProximityMsgToLayer(
                Strings.Chat.Local.ToString(player.Name, msg), ChatMessageType.Local, player.MapId, player.MapInstanceId, chatColor,
                player.Name
            );
            PacketSender.SendChatBubble(player.Id, player.MapInstanceId, (int)EntityType.GlobalEntity, msg, player.MapId);
            ChatHistory.LogMessage(player, msg.Trim(), ChatMessageType.Local, Guid.Empty);
        }
        else if (cmd == Strings.Chat.AllCommand || cmd == Strings.Chat.GlobalCommand)
        {
            if (msg.Trim().Length == 0)
            {
                return;
            }

            var chatColor = CustomColors.Chat.GlobalChat;
            if (client?.Power.IsAdmin ?? false)
            {
                chatColor = CustomColors.Chat.AdminGlobalChat;
            }
            else if (client?.Power.IsModerator ?? false)
            {
                chatColor = CustomColors.Chat.ModGlobalChat;
            }

            PacketSender.SendGlobalMsg(Strings.Chat.Global.ToString(player.Name, msg), chatColor, player.Name);
            ChatHistory.LogMessage(player, msg.Trim(), ChatMessageType.Global, Guid.Empty);
        }
        else if (cmd == Strings.Chat.PartyCommand)
        {
            if (msg.Trim().Length == 0)
            {
                return;
            }

            if (player.InParty(player))
            {
                PacketSender.SendPartyMsg(
                    player, Strings.Chat.Party.ToString(player.Name, msg), CustomColors.Chat.PartyChat, player.Name
                );
                ChatHistory.LogMessage(player, msg.Trim(), ChatMessageType.Party, Guid.Empty);
            }
            else
            {
                PacketSender.SendChatMsg(player, Strings.Parties.NotInParty, ChatMessageType.Party, CustomColors.Alerts.Error);
            }
        }
        else if (cmd == Strings.Chat.AdminCommand)
        {
            if (msg.Trim().Length == 0)
            {
                return;
            }

            if (client?.Power.IsModerator ?? false)
            {
                PacketSender.SendAdminMsg(
                    Strings.Chat.Admin.ToString(player.Name, msg), CustomColors.Chat.AdminChat, player.Name
                );
                ChatHistory.LogMessage(player, msg.Trim(), ChatMessageType.Admin, Guid.Empty);
            }
        }
        else if (cmd == Strings.Guilds.GuildCommand)
        {
            if (player.Guild == null)
            {
                PacketSender.SendChatMsg(player, Strings.Guilds.NotInGuild, ChatMessageType.Guild, CustomColors.Alerts.Error);
                return;
            }

            if (msg.Trim().Length == 0)
            {
                return;
            }

            //Normalize Rank
            var rank = Options.Instance.Guild.Ranks[Math.Max(0, Math.Min(player.GuildRank, Options.Instance.Guild.Ranks.Length - 1))].Title;
            PacketSender.SendGuildMsg(player, Strings.Guilds.GuildChat.ToString(rank, player.Name, msg), CustomColors.Chat.GuildChat);
            ChatHistory.LogMessage(player, msg.Trim(), ChatMessageType.Guild, player.Guild.Id);

        }
        else if (cmd == Strings.Chat.AnnouncementCommand)
        {
            if (msg.Trim().Length == 0)
            {
                return;
            }

            if (client?.Power.IsModerator ?? false)
            {
                PacketSender.SendGlobalMsg(
                    Strings.Chat.Announcement.ToString(player.Name, msg), CustomColors.Chat.AnnouncementChat,
                    player.Name
                );

                // Show an announcement banner if configured to do so as well!
                if (Options.Instance.Chat.ShowAnnouncementBanners)
                {
                    // TODO: Make the duration configurable through chat?
                    PacketSender.SendGameAnnouncement(msg, Options.Instance.Chat.AnnouncementDisplayDuration);
                }

                ChatHistory.LogMessage(player, msg.Trim(), ChatMessageType.Notice, Guid.Empty);
            }
        }
        else if (cmd == Strings.Chat.PrivateMessageCommand || cmd == Strings.Chat.MessageCommand)
        {
            if (msgSplit.Length < 2)
            {
                return;
            }

            msg = msg.Remove(0, msgSplit[0].Length + 1); //Chop off the player name parameter

            if (msg.Trim().Length == 0)
            {
                return;
            }

            var target = Player.FindOnline(msgSplit[0].ToLower());

            if (target == player)
            {
                return;
            }

            if (target != null)
            {
                PacketSender.SendChatMsg(
                    player, Strings.Chat.PrivateTo.ToString(target.Name, msg), ChatMessageType.PM, CustomColors.Chat.PrivateChatTo,
                    player.Name
                );

                PacketSender.SendChatMsg(
                    target, Strings.Chat.PrivateFrom.ToString(player.Name, msg), ChatMessageType.PM,
                    CustomColors.Chat.PrivateChatFrom, player.Name
                );

                target.ChatTarget = player;
                player.ChatTarget = target;
                ChatHistory.LogMessage(player, msg.Trim(), ChatMessageType.PM, target?.Id ?? Guid.Empty);
            }
            else
            {
                PacketSender.SendChatMsg(player, Strings.Player.Offline, ChatMessageType.PM, CustomColors.Alerts.Error);
            }
        }
        else if (cmd == Strings.Chat.ReplyCommand || cmd == Strings.Chat.ReplyShortcutCommand)
        {
            if (msg.Trim().Length == 0)
            {
                return;
            }

            if (player.ChatTarget != null && Player.FindOnline(player.ChatTarget.Id) != null)
            {
                PacketSender.SendChatMsg(
                    player, Strings.Chat.PrivateTo.ToString(player.ChatTarget.Name, msg), ChatMessageType.PM, CustomColors.Chat.PrivateChatTo,
                    player.Name
                );

                PacketSender.SendChatMsg(
                    player.ChatTarget, Strings.Chat.PrivateFrom.ToString(player.Name, msg), ChatMessageType.PM,
                    CustomColors.Chat.PrivateChatFrom, player.Name
                );

                player.ChatTarget.ChatTarget = player;
                ChatHistory.LogMessage(player, msg.Trim(), ChatMessageType.PM, player.ChatTarget?.Id ?? Guid.Empty);
            }
            else
            {
                PacketSender.SendChatMsg(player, Strings.Player.Offline, ChatMessageType.PM, CustomColors.Alerts.Error);
            }
        }
        else
        {
            //Search for command activated events and run them
            foreach (var evt in EventDescriptor.Lookup)
            {
                var eventDescriptor = evt.Value as EventDescriptor;
                if (eventDescriptor == default)
                {
                    continue;
                }

                if (client.Entity.UnsafeStartCommonEvent(eventDescriptor, CommonEventTrigger.SlashCommand, cmd.TrimStart('/'), msg))
                {
                    return; //Found our /command, exit now :)
                }
            }

            //No common event /command, invalid command.
            PacketSender.SendChatMsg(player, Strings.Commands.invalid, ChatMessageType.Error, CustomColors.Alerts.Error);
        }
    }

    //BlockPacket
    public void HandlePacket(Client client, BlockPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        //check if player is stunned or sleeping
        var statuses = client.Entity.Statuses.Values.ToArray();
        foreach (var status in statuses)
        {
            if (status.Type == SpellEffect.Stun)
            {
                PacketSender.SendChatMsg(player, Strings.Combat.StunBlocking, ChatMessageType.Combat);

                return;
            }

            if (status.Type == SpellEffect.Sleep)
            {
                PacketSender.SendChatMsg(player, Strings.Combat.SleepBlocking, ChatMessageType.Combat);

                return;
            }
        }

        client.Entity.TryBlock(packet.Blocking);
    }

    //BumpPacket
    public void HandlePacket(Client client, BumpPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.TryBumpEvent(packet.MapId, packet.EventId);
    }

    //AttackPacket
    public void HandlePacket(Client client, AttackPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        var unequippedAttack = false;
        var target = packet.Target;

        var clientTime = packet.Adjusted / TimeSpan.TicksPerMillisecond;
        if (player.ClientAttackTimer > clientTime ||
            (!Options.Instance.Player.AllowCombatMovement && player.ClientMoveTimer > clientTime))
        {
            return;
        }

        if (player.IsAttacking)
        {
            return;
        }

        if (player.IsCasting)
        {
            if (Options.Instance.Combat.EnableCombatChatMessages)
            {
                PacketSender.SendChatMsg(player, Strings.Combat.ChannelingNoAttack, ChatMessageType.Combat);
            }

            return;
        }

        var utcDeltaMs = (Timing.Global.TicksUtc - packet.UTC) / TimeSpan.TicksPerMillisecond;
        var latencyAdjustmentMs = -(client.Ping + Math.Max(0, utcDeltaMs));

        //check if player is blinded or stunned or in stealth mode
        foreach (var status in player.CachedStatuses)
        {
            if (status.Type == SpellEffect.Stun)
            {
                if (Options.Instance.Combat.EnableCombatChatMessages)
                {
                    PacketSender.SendChatMsg(player, Strings.Combat.StunAttacking, ChatMessageType.Combat);
                }

                return;
            }

            if (status.Type == SpellEffect.Sleep)
            {
                if (Options.Instance.Combat.EnableCombatChatMessages)
                {
                    PacketSender.SendChatMsg(player, Strings.Combat.SleepAttacking, ChatMessageType.Combat);
                }

                return;
            }

            if (status.Type == SpellEffect.Blind)
            {
                PacketSender.SendActionMsg(player, Strings.Combat.Miss, CustomColors.Combat.Missed);

                return;
            }

            //Remove stealth status.
            if (status.Type == SpellEffect.Stealth)
            {
                status.RemoveStatus();
            }
        }

        var attackingTile = new TileHelper(player.MapId, player.X, player.Y);
        switch (player.Dir)
        {
            case Direction.Up:
                attackingTile.Translate(0, -1);
                break;

            case Direction.Down:
                attackingTile.Translate(0, 1);
                break;

            case Direction.Left:
                attackingTile.Translate(-1, 0);
                break;

            case Direction.Right:
                attackingTile.Translate(1, 0);
                break;

            case Direction.UpLeft:
                attackingTile.Translate(-1, -1);
                break;

            case Direction.UpRight:
                attackingTile.Translate(1, -1);
                break;

            case Direction.DownLeft:
                attackingTile.Translate(-1, 1);
                break;

            case Direction.DownRight:
                attackingTile.Translate(1, 1);
                break;
        }

        PacketSender.SendEntityAttack(player, player.CalculateAttackTime());

        player.ClientAttackTimer = clientTime + player.CalculateAttackTime();

        //Fire projectile instead if weapon has it

        if (player.TryGetEquippedItem(Options.Instance.Equipment.WeaponSlot, out var equippedWeapon))
        {
            var weaponItem = equippedWeapon.Descriptor;

            //Check for animation
            var attackAnim = weaponItem.AttackAnimation;

            if (attackAnim != null && attackingTile.TryFix())
            {
                PacketSender.SendAnimationToProximity(
                    attackAnim.Id, -1, player.Id, attackingTile.GetMapId(), attackingTile.GetX(),
                    attackingTile.GetY(), player.Dir, player.MapInstanceId
                );
            }

            var projectileBase = ProjectileDescriptor.Get(weaponItem?.ProjectileId ?? Guid.Empty);

            if (projectileBase != null)
            {
                if (projectileBase.AmmoItemId != Guid.Empty)
                {
                    var itemSlot = player.FindInventoryItemSlot(
                        projectileBase.AmmoItemId, projectileBase.AmmoRequired
                    );

                    if (itemSlot == null)
                    {
                        PacketSender.SendChatMsg(
                            player,
                            Strings.Items.NotEnough.ToString(ItemDescriptor.GetName(projectileBase.AmmoItemId)),
                            ChatMessageType.Inventory,
                            CustomColors.Combat.NoAmmo
                        );

                        return;
                    }
#if INTERSECT_DIAGNOSTIC
                            PacketSender.SendPlayerMsg(client,
                                Strings.Get("items", "notenough", $"REGISTERED_AMMO ({projectileBase.Ammo}:'{ItemBase.GetName(projectileBase.Ammo)}':{projectileBase.AmmoRequired})"),
                                ChatMessageType.Inventory, CustomColors.NoAmmo);
#endif
                    if (!player.TryTakeItem(projectileBase.AmmoItemId, projectileBase.AmmoRequired))
                    {
#if INTERSECT_DIAGNOSTIC
                                PacketSender.SendPlayerMsg(client,
                                    Strings.Get("items", "notenough", "FAILED_TO_DEDUCT_AMMO"),
                                    CustomColors.NoAmmo);
                                PacketSender.SendPlayerMsg(client,
                                    Strings.Get("items", "notenough", $"FAILED_TO_DEDUCT_AMMO {client.Entity.CountItems(projectileBase.Ammo)}"),
                                    ChatMessageType.Inventory, CustomColors.NoAmmo);
#endif
                    }
                }
#if INTERSECT_DIAGNOSTIC
                        else
                        {
                            PacketSender.SendPlayerMsg(client,
                                Strings.Get("items", "notenough", "NO_REGISTERED_AMMO"),
                                ChatMessageType.Inventory, CustomColors.NoAmmo);
                        }
#endif
                if (MapController.TryGetInstanceFromMap(player.MapId, player.MapInstanceId, out var mapInstance))
                {
                    mapInstance
                        .SpawnMapProjectile(
                            player, projectileBase, null, weaponItem, player.MapId,
                            (byte)player.X, (byte)player.Y, (byte)player.Z, player.Dir, null);

                    player.AttackTimer = Timing.Global.Milliseconds +
                                         latencyAdjustmentMs +
                                         player.CalculateAttackTime();
                }

                return;
            }
#if INTERSECT_DIAGNOSTIC
                    else
                    {
                        PacketSender.SendPlayerMsg(client,
                            Strings.Get("items", "notenough", "NONPROJECTILE"),
                            ChatMessageType.Inventory, CustomColors.NoAmmo);
                        return;
                    }
#endif
        }
        else
        {
            unequippedAttack = true;
        }

        if (unequippedAttack)
        {
            var classBase = ClassDescriptor.Get(player.ClassId);
            if (classBase != null)
            {
                //Check for animation
                if (classBase.AttackAnimation != null)
                {
                    PacketSender.SendAnimationToProximity(
                        classBase.AttackAnimationId, -1, player.Id, attackingTile.GetMapId(), attackingTile.GetX(),
                        attackingTile.GetY(), player.Dir, player.MapInstanceId
                    );
                }
            }
        }

        foreach (var mapInstance in MapController.GetSurroundingMapInstances(player.Map.Id, player.MapInstanceId, true))
        {
            foreach (var entity in mapInstance.GetEntities())
            {
                if (entity.Id == target)
                {
                    player.TryAttack(entity);

                    break;
                }
            }
        }

        if (player.IsAttacking)
        {
            player.AttackTimer = Timing.Global.Milliseconds + latencyAdjustmentMs + player.CalculateAttackTime();
        }
    }

    //DirectionPacket
    public void HandlePacket(Client client, DirectionPacket packet)
    {
        var player = client?.Entity;

        player?.ChangeDir(packet.Direction);
    }

    //EnterGamePacket
    public void HandlePacket(Client client, EnterGamePacket packet)
    {
    }

    //ActivateEventPacket
    public void HandlePacket(Client client, ActivateEventPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.TryActivateEvent(packet.EventId);
    }

    //EventResponsePacket
    public void HandlePacket(Client client, EventResponsePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.RespondToEvent(packet.EventId, packet.Response);
    }

    //EventInputVariablePacket
    public void HandlePacket(Client client, EventInputVariablePacket packet)
    {
        client.Entity.RespondToEventInput(
            packet.EventId,
            packet.BooleanValue,
            packet.Value,
            packet.StringValue,
            packet.Canceled
        );
    }

    //CreateAccountPacket
    public void HandlePacket(Client client, UserRegistrationRequestPacket packet)
    {
        if (client.TimeoutMs > Timing.Global.Milliseconds)
        {
            PacketSender.SendError(client, Strings.Errors.ErrorTimeout, Strings.General.NoticeError);
            client.ResetTimeout();

            return;
        }

        client.ResetTimeout();

        if (Options.Instance.BlockClientRegistrations)
        {
            PacketSender.SendError(client, Strings.Account.RegistrationsBlocked, Strings.General.NoticeError);

            return;
        }

        if (!FieldChecking.IsValidUsername(packet.Username, Strings.Regex.Username))
        {
            PacketSender.SendError(client, Strings.Account.InvalidName, Strings.General.NoticeError);

            return;
        }

        if (!FieldChecking.IsWellformedEmailAddress(packet.Email, Strings.Regex.Email))
        {
            PacketSender.SendError(client, Strings.Account.InvalidEmail, Strings.General.NoticeError);

            return;
        }

        //Check for ban
        var isBanned = Ban.CheckBan(client.Ip);
        if (isBanned != null)
        {
            PacketSender.SendError(client, isBanned, Strings.General.NoticeError);

            return;
        }

        if (User.UserExists(packet.Username))
        {
            PacketSender.SendError(client, Strings.Account.AccountAlreadyExists, Strings.General.NoticeError);
        }
        else
        {
            if (User.UserExists(packet.Email))
            {
                PacketSender.SendError(client, Strings.Account.EmailExists, Strings.General.NoticeError);
            }
            else
            {

                UserActivityHistory.LogActivity(client.User?.Id ?? Guid.Empty, Guid.Empty, client?.Ip, UserActivityHistory.PeerType.Client, UserActivityHistory.UserAction.Create, client?.Name);

                DbInterface.CreateAccount(client, packet.Username, packet.Password, packet.Email);

                if (client.User != null)
                {
                    //Logged In
                    client.PacketFloodingThresholds = Options.Instance.Security.Packets.PlayerThresholds;

                    if (client.User.Power.IsAdmin || client.User.Power.IsModerator)
                    {
                        client.PacketFloodingThresholds = Options.Instance.Security.Packets.ModAdminThresholds;
                    }
                }

                // PacketSender.SendServerConfig(client); // TODO: We already send this when the client is initialized, why do we send it again here?

                //Check that server is in admin only mode
                if (Options.Instance.AdminOnly)
                {
                    if (client.Power == UserRights.None)
                    {
                        PacketSender.SendError(client, Strings.Account.AdminOnly, Strings.General.NoticeError);

                        return;
                    }
                }

                //Start the character creation process for the newly created account.
                PacketSender.SendGameObjects(client, GameObjectType.Class);
                PacketSender.SendCreateCharacter(client, force: true);
            }
        }
    }

    //CreateCharacterPacket
    public void HandlePacket(Client client, CreateCharacterPacket packet)
    {
        if (client.User == null)
        {
            return;
        }

        if (!FieldChecking.IsValidUsername(packet.Name, Strings.Regex.Username))
        {
            PacketSender.SendError(client, Strings.Account.InvalidName, Strings.General.NoticeError);

            return;
        }

        var index = client.Id;
        var classBase = ClassDescriptor.Get(packet.ClassId);
        if (classBase == null || classBase.Locked)
        {
            PacketSender.SendError(client, Strings.Account.InvalidClass, Strings.General.NoticeError);

            return;
        }

        if (Player.PlayerExists(packet.Name))
        {
            PacketSender.SendError(client, Strings.Account.CharacterExists, Strings.General.NoticeError);
            return;
        }

        Player newChar = new()
        {
            Id = Guid.NewGuid(),
        };

        newChar.ValidateLists();
        for (var i = 0; i < Options.Instance.Equipment.Slots.Count; i++)
        {
            newChar.Equipment[i] = -1;
        }

        newChar.Name = packet.Name;
        newChar.ClassId = packet.ClassId;
        newChar.Level = 1;

        if (classBase.Sprites.Count > 0)
        {
            var spriteIndex = Math.Max(0, Math.Min(classBase.Sprites.Count, packet.Sprite));
            newChar.Sprite = classBase.Sprites[spriteIndex].Sprite;
            newChar.Face = classBase.Sprites[spriteIndex].Face;
            newChar.Gender = classBase.Sprites[spriteIndex].Gender;
        }

        client.LoadCharacter(newChar);

        newChar.SetVital(Vital.Health, classBase.BaseVital[(int)Vital.Health]);
        newChar.SetVital(Vital.Mana, classBase.BaseVital[(int)Vital.Mana]);

        for (var i = 0; i < Enum.GetValues<Stat>().Length; i++)
        {
            newChar.Stat[i].BaseStat = 0;
        }

        newChar.StatPoints = classBase.BasePoints;

        for (var i = 0; i < classBase.Spells.Count; i++)
        {
            if (classBase.Spells[i].Level <= 1)
            {
                var tempSpell = new Spell(classBase.Spells[i].Id);
                newChar.TryTeachSpell(tempSpell, false);
            }
        }

        foreach (var item in classBase.Items)
        {
            if (ItemDescriptor.Get(item.Id) != null)
            {
                var tempItem = new Item(item.Id, item.Quantity);
                newChar.TryGiveItem(tempItem, ItemHandling.Normal, false, -1, false);
            }
        }

        UserActivityHistory.LogActivity(client?.User?.Id ?? Guid.Empty, client?.Entity?.Id ?? Guid.Empty, client?.Ip, UserActivityHistory.PeerType.Client, UserActivityHistory.UserAction.CreatePlayer, $"{client?.Name},{client?.Entity?.Name}");

        if (!client.User.TryAddCharacter(newChar))
        {
            client.LogAndDisconnect(newChar.Id);
            return;
        }

        newChar.SetOnline();

        PacketSender.SendJoinGame(client);
    }

    //PickupItemPacket
    public void HandlePacket(Client client, PickupItemPacket packet)
    {
        var player = client.Entity;
        if (player == null || packet.TileIndex < 0 || packet.TileIndex >= Options.Instance.Map.MapWidth * Options.Instance.Map.MapHeight)
        {
            return;
        }

        if (!MapController.TryGetInstanceFromMap(packet.MapId, player.MapInstanceId, out var playerMapInstance))
        {
            return;
        }

        var playerMapController = playerMapInstance.GetController();

        var lootDistance = Math.Min(
            Math.Min(Options.Instance.Map.MapWidth, Options.Instance.Map.MapHeight),
            Options.Instance.Loot.MaximumLootWindowDistance
        );

        // Is our user within range of the item they are trying to pick up?
        if (player.GetDistanceTo(playerMapController, packet.TileIndex % Options.Instance.Map.MapWidth, (int)Math.Floor(packet.TileIndex / (float)Options.Instance.Map.MapWidth)) > lootDistance)
        {
            return;
        }

        var giveItems = new Dictionary<MapController, List<MapItem>>();
        // Are we trying to pick up everything on this location or one specific item?
        if (packet.UniqueId == Guid.Empty)
        {
            // GET IT ALL! BE GREEDY!
            foreach (var (mapWithItems, value) in playerMapController.FindSurroundingTiles(new Point(player.X, player.Y), lootDistance))
            {
                if (!mapWithItems.TryGetInstance(player.MapInstanceId, out var mapInstanceWithItems))
                {
                    continue;
                }

                if (!giveItems.TryGetValue(mapWithItems, out var itemsFromMap))
                {
                    itemsFromMap = new List<MapItem>();
                    giveItems[mapWithItems] = itemsFromMap;
                }

                itemsFromMap.AddRange(value.SelectMany(itemLoc => mapInstanceWithItems.FindItemsAt(itemLoc)));
            }
        }
        else
        {
            // One specific item.
            giveItems.Add(playerMapController, new List<MapItem> { playerMapInstance.FindItem(packet.UniqueId) });
        }

        // Go through each item we're trying to give our player and see if we can do so.
        foreach (var (mapWithItems, value) in giveItems)
        {
            if (!mapWithItems.TryGetInstance(player.MapInstanceId, out var mapInstanceWithItems))
            {
                continue;
            }

            // Remove null or missing map items from the list
            var validMapItems = value.Where(
                mapItem => mapItem != default && mapInstanceWithItems.FindItem(mapItem.UniqueId) != default
            );
            foreach (var mapItem in validMapItems)
            {
                if (mapItem.Owner != default &&
                    mapItem.Owner != player.Id &&
                    Timing.Global.Milliseconds < mapItem.OwnershipTime)
                {
                    continue;
                }

                // Remove the item from the map now, because otherwise the overflow would just add to the existing quantity
                mapInstanceWithItems.RemoveItem(mapItem);

                // Try to give the item to our player.
                if (!player.TryGiveItem(mapItem, ItemHandling.Overflow, false, -1, true, mapItem.X, mapItem.Y))
                {
                    // We couldn't give the player their item, notify them.
                    PacketSender.SendChatMsg(
                        player,
                        Strings.Items.NoSpaceForItem,
                        ChatMessageType.Inventory,
                        CustomColors.Alerts.Error
                    );
                    continue;
                }

                if (ItemDescriptor.TryGet(mapItem.ItemId, out var item))
                {
                    PacketSender.SendActionMsg(player, item.Name, CustomColors.Items.Rarities[item.Rarity]);
                }
            }
        }
    }

    //SwapInvItemsPacket
    public void HandlePacket(Client client, SwapInvItemsPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.SwapItems(packet.Slot1, packet.Slot2);
    }

    //DropItemPacket
    public void HandlePacket(Client client, DropItemPacket packet)
    {
        var player = client?.Entity;
        if (packet == null)
        {
            return;
        }

        player?.DropItemFrom(packet.Slot, packet.Quantity);
    }

    //UseItemPacket
    public void HandlePacket(Client client, UseItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        Entity target = null;
        if (packet.TargetId != Guid.Empty)
        {
            foreach (var mapInstance in MapController.GetSurroundingMapInstances(player.Map.Id, player.MapInstanceId, true))
            {
                foreach (var en in mapInstance.GetEntities())
                {
                    if (en.Id == packet.TargetId)
                    {
                        target = en;

                        break;
                    }
                }
            }
        }

        player.UseItem(packet.Slot, target);
    }

    //SwapSpellsPacket
    public void HandlePacket(Client client, SwapSpellsPacket packet)
    {
        var player = client?.Entity;
        if (player == null || player.IsCasting)
        {
            return;
        }

        player.SwapSpells(packet.Slot1, packet.Slot2);
    }

    //ForgetSpellPacket
    public void HandlePacket(Client client, ForgetSpellPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.ForgetSpell(packet.Slot);
    }

    //UseSpellPacket
    public void HandlePacket(Client client, UseSpellPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        var casted = false;

        if (packet.TargetId != Guid.Empty)
        {
            foreach (var mapInstance in MapController.GetSurroundingMapInstances(player.Map.Id, player.MapInstanceId, true))
            {
                foreach (var en in mapInstance.GetEntities())
                {
                    if (en.Id == packet.TargetId)
                    {
                        player.UseSpell(packet.Slot, en, packet.SoftRetargetOnSelfCast);
                        casted = true;

                        break;
                    }
                }
            }
        }

        if (!casted)
        {
            player.UseSpell(packet.Slot, null, packet.SoftRetargetOnSelfCast);
        }
    }

    //UnequipItemPacket
    public void HandlePacket(Client client, UnequipItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.UnequipItem(packet.Slot);
    }

    //UpgradeStatPacket
    public void HandlePacket(Client client, UpgradeStatPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.UpgradeStat(packet.Stat);
    }

    //HotbarUpdatePacket
    public void HandlePacket(Client client, HotbarUpdatePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.HotbarChange(packet.HotbarSlot, packet.Type, packet.Index);
    }

    //HotbarSwapPacket
    public void HandlePacket(Client client, HotbarSwapPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.HotbarSwap(packet.Slot1, packet.Slot2);
    }

    //BuyItemPacket
    public void HandlePacket(Client client, BuyItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.BuyItem(packet.Slot, packet.Quantity);
    }

    //SellItemPacket
    public void HandlePacket(Client client, SellItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.SellItem(packet.Slot, packet.Quantity);
    }

    //CloseShopPacket
    public void HandlePacket(Client client, CloseShopPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.CloseShop();
    }

    //CloseCraftingPacket
    public void HandlePacket(Client client, CloseCraftingPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.CloseCraftingTable();
    }

    //CraftItemPacket
    public void HandlePacket(Client client, CraftItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        if (player.CraftJournalMode)
        {
            PacketSender.SendChatMsg(player, Strings.Crafting.InJournalMode, ChatMessageType.Notice);
            return;
        }

        lock (player.EntityLock)
        {
            //if player hit stop button in crafting window
            if (packet.CraftId == default)
            {
                player.CraftingState = default;
                return;
            }

            if (!CraftingRecipeDescriptor.TryGet(packet.CraftId, out var craftDescriptor))
            {
                ApplicationContext.Context.Value?.Logger.LogWarning($"Player {player.Id} tried to craft {packet.CraftId} which does not exist.");
                return;
            }

            if (player.OpenCraftingTableId == default)
            {
                ApplicationContext.Context.Value?.Logger.LogWarning($"Player {player.Id} tried to craft {packet.CraftId} without having opened a table yet.");
                return;
            }

            if (player.CraftingState != default)
            {
                PacketSender.SendChatMsg(player, Strings.Crafting.AlreadyCrafting, ChatMessageType.Crafting, CustomColors.Alerts.Error);
                return;
            }

            player.CraftingState = new CraftingState
            {
                Id = packet.CraftId,
                CraftCount = packet.Count,
                RemainingCount = packet.Count,
                DurationPerCraft = craftDescriptor.Time,
                NextCraftCompletionTime = Timing.Global.Milliseconds + craftDescriptor.Time
            };
        }
    }

    //CloseBankPacket
    public void HandlePacket(Client client, CloseBankPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.CloseBank();
    }

    //DepositItemPacket
    public void HandlePacket(Client client, DepositItemPacket packet)
    {
        client?.Entity?.BankInterface?.TryDepositItem(default, packet.Slot, packet.Quantity, packet.BankSlot);
    }

    //WithdrawItemPacket
    public void HandlePacket(Client client, WithdrawItemPacket packet)
    {
        client?.Entity?.BankInterface?.TryWithdrawItem(default, packet.Slot, packet.Quantity, packet.InvSlot);
    }

    //MoveBankItemPacket
    public void HandlePacket(Client client, SwapBankItemsPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player?.BankInterface?.SwapBankItems(packet.Slot1, packet.Slot2);
    }

    //PartyInvitePacket
    public void HandlePacket(Client client, PartyInvitePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        var target = packet.TargetId != Guid.Empty ?
            Player.FindOnline(packet.TargetId) :
            Player.FindOnline(packet.Target.Trim());

        if (target != null && target.Id != player.Id)
        {
            target.InviteToParty(player);

            return;
        }

        PacketSender.SendChatMsg(player, Strings.Parties.OutOfRange, ChatMessageType.Combat, CustomColors.Combat.NoTarget);
    }

    //PartyInviteResponsePacket
    public void HandlePacket(Client client, PartyInviteResponsePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        var leader = packet.PartyId;
        if (player.PartyRequester != null && player.PartyRequester.Id == leader)
        {
            if (packet.AcceptingInvite)
            {
                if (player.PartyRequester.IsValidPlayer)
                {
                    _ = player.PartyRequester.TryAddParty(player);
                }
            }
            else
            {
                PacketSender.SendChatMsg(
                    player.PartyRequester, Strings.Parties.Declined.ToString(client.Entity.Name),
                    ChatMessageType.Party,
                    CustomColors.Alerts.Declined
                );

                if (player.PartyRequests.ContainsKey(player.PartyRequester))
                {
                    player.PartyRequests[player.PartyRequester] =
                        Timing.Global.Milliseconds + Options.Instance.Player.RequestTimeout;
                }
                else
                {
                    player.PartyRequests.Add(
                        player.PartyRequester, Timing.Global.Milliseconds + Options.Instance.Player.RequestTimeout
                    );
                }
            }

            player.PartyRequester = null;
        }
    }

    //PartyKickPacket
    public void HandlePacket(Client client, PartyKickPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.KickParty(packet.TargetId);
    }

    //PartyLeavePacket
    public void HandlePacket(Client client, PartyLeavePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.LeaveParty();
    }

    //QuestResponsePacket
    public void HandlePacket(Client client, QuestResponsePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        if (packet.AcceptingQuest)
        {
            player.AcceptQuest(packet.QuestId);
        }
        else
        {
            player.DeclineQuest(packet.QuestId);
        }
    }

    //AbandonQuestPacket
    public void HandlePacket(Client client, AbandonQuestPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player.CancelQuest(packet.QuestId);
    }

    //TradeRequestPacket
    public void HandlePacket(Client client, TradeRequestPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        var target = Player.FindOnline(packet.TargetId);

        if (target != null && target.Id != player.Id && player.InRangeOf(target, Options.Instance.Player.TradeRange))
        {
            if (player.InRangeOf(target, Options.Instance.Player.TradeRange))
            {
                target.InviteToTrade(player);

                return;
            }
        }

        //Player Out of Range Or Offline
        PacketSender.SendChatMsg(player, Strings.Trading.OutOfRange.ToString(), ChatMessageType.Trading, CustomColors.Combat.NoTarget);
    }

    //TradeRequestResponsePacket
    public void HandlePacket(Client client, TradeRequestResponsePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        var target = packet.TradeId;
        if (player.Trading.Requester != null && player.Trading.Requester.Id == target)
        {
            if (player.Trading.Requester.IsValidPlayer)
            {
                if (packet.AcceptingInvite)
                {
                    if (player.Trading.Requester.Trading.Counterparty == null
                    ) //They could have accepted another trade since.
                    {
                        if (player.InRangeOf(player.Trading.Requester, Options.Instance.Player.TradeRange))
                        {
                            //Check if still in range lolz
                            player.Trading.Requester.StartTrade(player);
                        }
                        else
                        {
                            PacketSender.SendChatMsg(
                                player, Strings.Trading.OutOfRange.ToString(), ChatMessageType.Trading, CustomColors.Combat.NoTarget
                            );
                        }
                    }
                    else
                    {
                        PacketSender.SendChatMsg(
                            player, Strings.Trading.Busy.ToString(player.Trading.Requester.Name), ChatMessageType.Trading, Color.Red
                        );
                    }
                }
                else
                {
                    PacketSender.SendChatMsg(
                        player.Trading.Requester, Strings.Trading.Declined.ToString(player.Name),
                        ChatMessageType.Trading,
                        CustomColors.Alerts.Declined
                    );

                    if (player.Trading.Requests.ContainsKey(player.Trading.Requester))
                    {
                        player.Trading.Requests[player.Trading.Requester] =
                            Timing.Global.Milliseconds + Options.Instance.Player.RequestTimeout;
                    }
                    else
                    {
                        player.Trading.Requests.Add(
                            player.Trading.Requester, Timing.Global.Milliseconds + Options.Instance.Player.RequestTimeout
                        );
                    }
                }
            }
        }

        player.Trading.Requester = null;
    }

    //OfferTradeItemPacket
    public void HandlePacket(Client client, OfferTradeItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null || player.Trading.Counterparty == null)
        {
            return;
        }

        player?.OfferItem(packet.Slot, packet.Quantity);
    }

    //RevokeTradeItemPacket
    public void HandlePacket(Client client, RevokeTradeItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null || player.Trading.Counterparty == null)
        {
            return;
        }

        if (player.Trading.Counterparty.Trading.Accepted)
        {
            PacketSender.SendChatMsg(
                player, Strings.Trading.RevokeNotAllowed.ToString(player.Trading.Counterparty.Name), ChatMessageType.Trading,
                CustomColors.Alerts.Declined
            );
        }
        else
        {
            player?.RevokeItem(packet.Slot, packet.Quantity);
        }
    }

    //AcceptTradePacket
    public void HandlePacket(Client client, AcceptTradePacket packet)
    {
        var player = client?.Entity;
        if (player == null || player.Trading.Counterparty == null)
        {
            return;
        }

        player.Trading.Accepted = true;
        if (player.Trading.Counterparty.Trading.Accepted)
        {
            if (Options.Instance.Logging.Trade)
            {
                //Duplicate the items we are trading because they are messed with in the ReturnTradeItems() function below
                var tradeId = Guid.NewGuid();
                var ourItems = player.Trading.Offer.Where(i => i != null && i.ItemId != Guid.Empty).Select(i => i.Clone()).ToArray();
                var theirItems = player.Trading.Counterparty.Trading.Offer.Where(i => i != null && i.ItemId != Guid.Empty).Select(i => i.Clone()).ToArray();
                TradeHistory.LogTrade(tradeId, player, player.Trading.Counterparty, ourItems, theirItems);
                TradeHistory.LogTrade(tradeId, player.Trading.Counterparty, player, theirItems, ourItems);
            }
            //Swap the trade boxes over, then return the trade boxes to their new owners!
            var t = player.Trading.Offer;
            player.Trading.Offer = player.Trading.Counterparty.Trading.Offer;
            player.Trading.Counterparty.Trading.Offer = t;
            player.Trading.Counterparty.ReturnTradeItems();
            player.ReturnTradeItems();

            PacketSender.SendChatMsg(player, Strings.Trading.Accepted, ChatMessageType.Trading, CustomColors.Alerts.Accepted);
            PacketSender.SendChatMsg(
                player.Trading.Counterparty, Strings.Trading.Accepted, ChatMessageType.Trading, CustomColors.Alerts.Accepted
            );

            PacketSender.SendTradeClose(player.Trading.Counterparty);
            PacketSender.SendTradeClose(player);
            player.Trading.Counterparty.Trading.Counterparty = null;
            player.Trading.Counterparty = null;
        }
        else
        {
            PacketSender.SendChatMsg(
                player.Trading.Counterparty, Strings.Trading.OfferAccepted.ToString(player.Name), ChatMessageType.Trading, CustomColors.Alerts.Accepted
            );
        }
    }

    //DeclineTradePacket
    public void HandlePacket(Client client, DeclineTradePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player?.CancelTrade();
    }

    //CloseBagPacket
    public void HandlePacket(Client client, CloseBagPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player?.CloseBag();
    }

    //StoreBagItemPacket
    public void HandlePacket(Client client, StoreBagItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player?.StoreBagItem(packet.Slot, packet.Quantity, packet.BagSlot);
    }

    //RetrieveBagItemPacket
    public void HandlePacket(Client client, RetrieveBagItemPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player?.RetrieveBagItem(packet.Slot, packet.Quantity, packet.InventorySlot);
    }

    //SwapBagItemPacket
    public void HandlePacket(Client client, SwapBagItemsPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        player?.SwapBagItems(packet.Slot1, packet.Slot2);
    }

    //RequestFriendsPacket
    public void HandlePacket(Client client, RequestFriendsPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        PacketSender.SendFriends(player);
    }

    //UpdateFriendsPacket
    public void HandlePacket(Client client, UpdateFriendsPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        if (packet.Adding)
        {
            //Don't add yourself!
            if (packet.Name.ToLower() == client.Entity.Name.ToLower())
            {
                return;
            }

            if (client.Entity.GetFriendId(packet.Name) == Guid.Empty)
            {
                var target = Player.FindOnline(packet.Name);
                if (target != null)
                {
                    if (target.CombatTimer < Timing.Global.Milliseconds)
                    {
                        target.FriendRequest(client.Entity);
                    }
                    else
                    {
                        PacketSender.SendChatMsg(player, Strings.Friends.Busy.ToString(target.Name), ChatMessageType.Friend);
                    }
                }
                else
                {
                    PacketSender.SendChatMsg(player, Strings.Player.Offline, ChatMessageType.Friend, CustomColors.Alerts.Error);
                }
            }
            else
            {
                PacketSender.SendChatMsg(
                    player, Strings.Friends.AlreadyFriends.ToString(packet.Name), ChatMessageType.Friend, CustomColors.Alerts.Info
                );
            }
        }
        else
        {
            //Check if we have this friend
            var friendId = player.GetFriendId(packet.Name);
            if (friendId != Guid.Empty)
            {
                var otherPlayer = Player.FindOnline(friendId);
                player.CachedFriends.Remove(friendId);
                PacketSender.SendFriends(player);
                PacketSender.SendChatMsg(player, Strings.Friends.FriendRemoved, ChatMessageType.Friend, CustomColors.Alerts.Declined);

                if (otherPlayer?.CachedFriends.ContainsKey(player.Id) ?? false)
                {
                    otherPlayer.CachedFriends.Remove(player.Id);
                    PacketSender.SendFriends(otherPlayer);
                }

                if (!Player.TryRemoveFriendship(player.Id, friendId))
                {
                    client.LogAndDisconnect(player.Id, nameof(Player.TryRemoveFriendship));
                    return;
                }
            }
        }
    }

    //FriendRequestResponsePacket
    public void HandlePacket(Client client, FriendRequestResponsePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        var target = Player.FindOnline(packet.FriendId);

        if (target == null || target.Id == player.Id)
        {
            return;
        }

        if (packet.AcceptingRequest)
        {
            if (!player.CachedFriends.ContainsKey(target.Id)) // Incase one user deleted friend then re-requested
            {
                if (!player.TryAddFriend(target))
                {
                    return;
                }

                PacketSender.SendChatMsg(
                    player, Strings.Friends.FriendNotification.ToString(target.Name), ChatMessageType.Friend, CustomColors.Alerts.Accepted
                );

                PacketSender.SendFriends(player);
            }

            if (!target.CachedFriends.ContainsKey(player.Id)) // Incase one user deleted friend then re-requested
            {
                if (!target.TryAddFriend(player))
                {
                    return;
                }

                PacketSender.SendChatMsg(
                    target, Strings.Friends.Accept.ToString(player.Name), ChatMessageType.Friend, CustomColors.Alerts.Accepted
                );

                PacketSender.SendFriends(target);
            }
        }
        else
        {
            if (player.FriendRequester == target)
            {
                if (player.FriendRequester.IsValidPlayer)
                {
                    if (player.FriendRequests.ContainsKey(player.FriendRequester))
                    {
                        player.FriendRequests[player.FriendRequester] =
                            Timing.Global.Milliseconds + Options.Instance.Player.RequestTimeout;
                    }
                    else
                    {
                        player.FriendRequests.Add(
                            client.Entity.FriendRequester, Timing.Global.Milliseconds + Options.Instance.Player.RequestTimeout
                        );
                    }
                }
            }
        }

        player.FriendRequester = null;
    }

    //SelectCharacterPacket
    public void HandlePacket(Client client, SelectCharacterPacket packet)
    {
        if (client.User == null)
        {
            return;
        }

        var character = DbInterface.GetUserCharacter(client.User, packet.CharacterId, true);
        if (character == null)
        {
            return;
        }

        if (character.IsSaving)
        {
            PacketSender.SendError(
                client,
                Strings.Account.PlayerSavingTryAgainLater.ToString(character.Name),
                Strings.General.NoticeError
            );
            return;
        }

        ObjectDisposedException.ThrowIf(character.IsDisposed, character);

        client.LoadCharacter(character);

        UserActivityHistory.LogActivity(
            client.User?.Id ?? Guid.Empty,
            client?.Entity?.Id ?? Guid.Empty,
            client?.Ip,
            UserActivityHistory.PeerType.Client,
            UserActivityHistory.UserAction.SelectPlayer,
            $"{client?.Name},{client?.Entity?.Name}"
        );

        try
        {
            client.Entity?.SetOnline();

            PacketSender.SendJoinGame(client);
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogWarning(
                exception,
                "Failed to set player as online or send JoinGame packet"
            );
            PacketSender.SendError(client, Strings.Account.LoadFail, Strings.General.NoticeError);
            client.Logout();
        }
    }

    //DeleteCharacterPacket
    public void HandlePacket(Client client, DeleteCharacterPacket packet)
    {
        if (client.User == null)
        {
            return;
        }

        if (Player.FindOnline(packet.CharacterId) != null)
        {
            PacketSender.SendError(client, Strings.Account.DeleteCharacterError, Strings.General.NoticeError);
            PacketSender.SendPlayerCharacters(client);
            return;
        }

        var character = DbInterface.GetUserCharacter(client.User, packet.CharacterId);
        if (character != null)
        {
            character.LoadGuild();
            if (character.Guild != null && character.GuildRank == 0)
            {
                PacketSender.SendError(client, Strings.Guilds.DeleteGuildLeader, Strings.General.NoticeError);
                return;
            }

            foreach (var chr in client.Characters.ToArray())
            {
                if (chr.Id == packet.CharacterId)
                {
                    UserActivityHistory.LogActivity(client?.User?.Id ?? Guid.Empty, client?.Entity?.Id ?? Guid.Empty, client?.Ip, UserActivityHistory.PeerType.Client, UserActivityHistory.UserAction.DeletePlayer, $"{client?.Name},{client?.Entity?.Name}");

                    if (!client.User.TryDeleteCharacter(chr))
                    {
                        client.LogAndDisconnect(chr.Id, nameof(User.TryDeleteCharacter));
                    }
                }
            }
        }

        PacketSender.SendError(client, Strings.Account.CharacterDeleted, Strings.General.Notice);
        PacketSender.SendPlayerCharacters(client);
    }

    //NewCharacterPacket
    public void HandlePacket(Client client, NewCharacterPacket packet)
    {
        if (client?.Characters?.Count < Options.Instance.Player.MaxCharacters)
        {
            PacketSender.SendGameObjects(client, GameObjectType.Class);
            PacketSender.SendCreateCharacter(client);
        }
        else
        {
            PacketSender.SendError(client, Strings.Account.MaxCharacters, Strings.General.NoticeError);
        }
    }

    //ResetPasswordPacket
    public void HandlePacket(Client client, PasswordChangeRequestPacket passwordChangeRequestPacket)
    {
        //Find account with that name or email

        if (client.TimeoutMs > Timing.Global.Milliseconds)
        {
            PacketSender.SendError(client, Strings.Errors.ErrorTimeout, Strings.General.NoticeError);
            client.ResetTimeout();

            return;
        }

        var identifier = passwordChangeRequestPacket.Identifier?.Trim();
        if (string.IsNullOrWhiteSpace(identifier))
        {
            Logger.LogWarning(
                "Received {PasswordChangePacket} with empty identifier from {ClientId}",
                nameof(PasswordChangeRequestPacket),
                client.Id
            );
            PacketSender.SendPasswordResetResult(client, PasswordResetResultType.InvalidRequest);
            return;
        }

        var token = passwordChangeRequestPacket.Token?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Logger.LogWarning(
                "Received {PasswordChangePacket} with empty token from {ClientId}",
                nameof(PasswordChangeRequestPacket),
                client.Id
            );
            PacketSender.SendPasswordResetResult(client, PasswordResetResultType.InvalidRequest);
            return;
        }

        var password = passwordChangeRequestPacket.Password?.Trim();
        if (string.IsNullOrWhiteSpace(password))
        {
            Logger.LogWarning(
                "Received {PasswordChangePacket} with empty password from {ClientId}",
                nameof(PasswordChangeRequestPacket),
                client.Id
            );
            PacketSender.SendPasswordResetResult(client, PasswordResetResultType.InvalidRequest);
            return;
        }

        var user = User.FindFromNameOrEmail(identifier);
        if (user == null)
        {
            Logger.LogWarning(
                "Received {PasswordChangePacket} from {ClientId} for a user '{MissingIdentifier}' that cannot be found",
                nameof(PasswordChangeRequestPacket),
                client.Id,
                identifier
            );
            PacketSender.SendPasswordResetResult(client, PasswordResetResultType.NoUserFound);
            return;
        }

        if (string.Equals(user.PasswordResetCode, token, StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.UtcNow < user.PasswordResetTime)
            {
                user.PasswordResetCode = string.Empty;
                user.PasswordResetTime = DateTime.MinValue;
                DbInterface.UpdatePassword(user, passwordChangeRequestPacket.Password);
                ApplicationContext.CurrentContext.Logger.LogInformation("Password changed via reset token for {UserId}", user.Id);
                PacketSender.SendPasswordResetResult(client, PasswordResetResultType.Success);
                return;
            }

            Logger.LogWarning(
                "Received {PasswordChangePacket} from {ClientId} for user {UserId} with an expired password reset token",
                nameof(PasswordChangeRequestPacket),
                client.Id,
                user.Id
            );
            PacketSender.SendPasswordResetResult(client, PasswordResetResultType.InvalidToken);
            return;
        }

        if (user.IsPasswordValid(token))
        {
            user.PasswordResetCode = string.Empty;
            user.PasswordResetTime = DateTime.MinValue;
            DbInterface.UpdatePassword(user, passwordChangeRequestPacket.Password);
            ApplicationContext.CurrentContext.Logger.LogInformation("Password changed via existing password for {UserId}", user.Id);
            PacketSender.SendPasswordResetResult(client, PasswordResetResultType.Success);
            return;
        }

        Logger.LogWarning(
            "Received {PasswordChangePacket} from {ClientId} for user {UserId} with an invalid token",
            nameof(PasswordChangeRequestPacket),
            client.Id,
            user.Id
        );
        PacketSender.SendPasswordResetResult(client, PasswordResetResultType.InvalidToken);
    }

    //RequestGuildPacket
    public void HandlePacket(Client client, RequestGuildPacket packet)
    {
        var player = client.Entity;
        if (player == null)
        {
            return;
        }
        PacketSender.SendGuild(player);
    }


    //UpdateGuildMemberPacket
    public void HandlePacket(Client client, UpdateGuildMemberPacket packet)
    {
        var player = client.Entity;
        if (player == null)
        {
            return;
        }

        var guild = player.Guild;

        // Are we in a guild?
        if (guild == null)
        {
            PacketSender.SendChatMsg(player, Strings.Guilds.NotInGuild, ChatMessageType.Guild, CustomColors.Alerts.Error);
            return;
        }

        var isOwner = player.GuildRank == 0;
        var rank = Options.Instance.Guild.Ranks[Math.Max(0, Math.Min(player.GuildRank, Options.Instance.Guild.Ranks.Length - 1))];
        Intersect.Network.Packets.Server.GuildMember member = null;

        // Handle our desired action, assuming we're allowed to of course.
        switch (packet.Action)
        {
            case GuildMemberUpdateAction.Invite:
                // Are we allowed to invite players?
                var inviteRankIndex = Options.Instance.Guild.Ranks.Length - 1;
                var inviteRank = Options.Instance.Guild.Ranks[inviteRankIndex];
                if (!rank.Permissions.Invite)
                {
                    PacketSender.SendChatMsg(player, Strings.Guilds.NotAllowed, ChatMessageType.Guild, CustomColors.Alerts.Error);
                    return;
                }

                if (inviteRank.Limit > -1 && guild.Members.Where(m => m.Value.Rank == inviteRankIndex).Count() >= inviteRank.Limit)
                {
                    PacketSender.SendChatMsg(player, Strings.Guilds.RankLimitResponse.ToString(inviteRank.Title, player.Name), ChatMessageType.Guild, CustomColors.Alerts.Error);
                    return;
                }

                var target = Player.Find(packet.Name);
                if (target != null)
                {
                    // Are we already in a guild? or have a pending invite?
                    if (target.Guild == null && target.PendingGuildInvite == default)
                    {
                        // Thank god, we can FINALLY get started!
                        // Set our invite and send our players the relevant messages.
                        target.PendingGuildInvite = new GuildInvite
                        {
                            From = player,
                            FromId = player.Id,
                            To = guild,
                            ToId = guild.Id,
                        };

                        PacketSender.SendChatMsg(
                            player,
                            Strings.Guilds.InviteSent.ToString(target.Name, guild.Name),
                            ChatMessageType.Guild,
                            CustomColors.Alerts.Info
                        );

                        if (target.IsOnline)
                        {
                            PacketSender.SendGuildInvite(target, player);
                        }
                        else
                        {
                            ApplicationContext.Context.Value?.Logger.LogInformation(
                                $"[Guild] Player {player.Id} sent an offline guild invite to guild {guild.Id} to player {target.Id}"
                            );
                            target.Save();
                        }
                    }
                    else
                    {
                        PacketSender.SendChatMsg(player, Strings.Guilds.InviteAlreadyInGuild, ChatMessageType.Guild, CustomColors.Alerts.Error);
                    }
                }
                else
                {
                    PacketSender.SendChatMsg(player, Strings.Guilds.InviteNotOnline, ChatMessageType.Guild, CustomColors.Alerts.Error);
                }
                break;
            case GuildMemberUpdateAction.Remove:
                if (guild.Members.TryGetValue(packet.Id, out member))
                {
                    if ((!rank.Permissions.Kick && !isOwner) || member.Rank <= player.GuildRank)
                    {
                        PacketSender.SendChatMsg(player, Strings.Guilds.NotAllowed, ChatMessageType.Guild, CustomColors.Alerts.Error);
                        return;
                    }

                    // Start common events for all online guild members that this one left
                    foreach (var mem in guild.FindOnlineMembers())
                    {
                        mem.StartCommonEventsWithTrigger(CommonEventTrigger.GuildMemberKicked, guild.Name, member.Name);
                    }

                    guild.TryRemoveMember(packet.Id, default, player, GuildHistory.GuildActivityType.Kicked);
                }
                else
                {
                    PacketSender.SendChatMsg(player, Strings.Guilds.NoSuchPlayer, ChatMessageType.Guild, CustomColors.Alerts.Error);
                }
                break;
            case GuildMemberUpdateAction.Promote:
                if (guild.Members.TryGetValue(packet.Id, out member))
                {
                    var promotionRankIndex = Math.Max(0, Math.Min(packet.Rank, Options.Instance.Guild.Ranks.Length - 1));
                    var promotionRank = Options.Instance.Guild.Ranks[promotionRankIndex];
                    if ((!rank.Permissions.Promote && !isOwner) || member.Rank <= player.GuildRank || packet.Rank <= player.GuildRank || packet.Rank > member.Rank)
                    {
                        PacketSender.SendChatMsg(player, Strings.Guilds.NotAllowed, ChatMessageType.Guild, CustomColors.Alerts.Error);
                        return;
                    }

                    if (promotionRank.Limit > -1 && guild.Members.Where(m => m.Value.Rank == promotionRankIndex).Count() >= promotionRank.Limit)
                    {
                        PacketSender.SendChatMsg(player, Strings.Guilds.RankLimitResponse.ToString(promotionRank.Title, player.Name), ChatMessageType.Guild, CustomColors.Alerts.Error);
                        return;
                    }

                    guild.SetPlayerRank(packet.Id, packet.Rank, player);

                    PacketSender.SendGuildMsg(player, Strings.Guilds.Promoted.ToString(member.Name, promotionRank.Title), CustomColors.Alerts.Success);
                }
                else
                {
                    PacketSender.SendChatMsg(player, Strings.Guilds.NoSuchPlayer, ChatMessageType.Guild, CustomColors.Alerts.Error);
                }
                break;
            case GuildMemberUpdateAction.Demote:
                if (guild.Members.TryGetValue(packet.Id, out member))
                {
                    var demotionRankIndex = Math.Max(0, Math.Min(packet.Rank, Options.Instance.Guild.Ranks.Length - 1));
                    var demotionRank = Options.Instance.Guild.Ranks[demotionRankIndex];
                    if ((!rank.Permissions.Demote && !isOwner) || member.Rank <= player.GuildRank || packet.Rank <= player.GuildRank || packet.Rank < member.Rank)
                    {
                        PacketSender.SendChatMsg(player, Strings.Guilds.NotAllowed, ChatMessageType.Guild, CustomColors.Alerts.Error);
                        return;
                    }

                    if (demotionRank.Limit > -1 && guild.Members.Where(m => m.Value.Rank == demotionRankIndex).Count() >= demotionRank.Limit)
                    {
                        PacketSender.SendChatMsg(player, Strings.Guilds.RankLimitResponse.ToString(demotionRank.Title, player.Name), ChatMessageType.Guild, CustomColors.Alerts.Error);
                        return;
                    }

                    guild.SetPlayerRank(packet.Id, packet.Rank, player);

                    PacketSender.SendGuildMsg(player, Strings.Guilds.Demoted.ToString(member.Name, demotionRank.Title), CustomColors.Alerts.Error);
                }
                else
                {
                    PacketSender.SendChatMsg(player, Strings.Guilds.NoSuchPlayer, ChatMessageType.Guild, CustomColors.Alerts.Error);
                }
                break;
            case GuildMemberUpdateAction.Transfer:
                if (guild.Members.TryGetValue(packet.Id, out member))
                {
                    if (!isOwner)
                    {
                        PacketSender.SendChatMsg(player, Strings.Guilds.NotAllowed, ChatMessageType.Guild, CustomColors.Alerts.Error);
                        return;
                    }

                    guild.TransferOwnership(Player.Find(packet.Id));

                    PacketSender.SendGuildMsg(player, Strings.Guilds.Transferred.ToString(guild.Name, player.Name, member.Name), CustomColors.Alerts.Success);
                }
                else
                {
                    PacketSender.SendChatMsg(player, Strings.Guilds.NoSuchPlayer, ChatMessageType.Guild, CustomColors.Alerts.Error);
                }
                break;
            default:
                /// ???
                break;
        }

        player.UnequipInvalidItems();
    }

    //GuildInviteAcceptPacket
    public void HandlePacket(Client client, GuildInviteAcceptPacket packet)
    {
        var player = client.Entity;
        if (player == null)
        {
            return;
        }

        // Have we received an invite at all?
        if (player.PendingGuildInvite == default)
        {
            PacketSender.SendChatMsg(
                player,
                Strings.Guilds.NotReceivedInvite,
                ChatMessageType.Guild,
                CustomColors.Alerts.Error
            );
            return;
        }

        if (Options.Instance.Guild is not { } guildOptions)
        {
            PacketSender.SendChatMsg(
                player,
                Strings.Guilds.ErrorWhileAcceptingInvite,
                ChatMessageType.Guild,
                CustomColors.Alerts.Error
            );
            PacketSender.SendGuildInvite(player, player.PendingGuildInviteFrom);
            return;
        }

        var inviter = player.PendingGuildInviteFrom ?? Player.FindOnline(player.PendingGuildInviteFromId ?? default);
        var guild = player.PendingGuildInviteTo ?? Guild.LoadGuild(player.PendingGuildInviteToId ?? default);

        if (guildOptions.Ranks[^1].Limit > -1)
        {
            if (guildOptions.Ranks[^1].Limit <= guild.Members.Count(m => m.Value.Rank == guildOptions.Ranks.Length - 1))
            {
                // Inform the inviter that the guild is full
                if (player.PendingGuildInvite.FromId is var inviterId)
                {
                    var onlinePlayer = Player.FindOnline(inviterId);
                    if (onlinePlayer != null)
                    {
                        PacketSender.SendChatMsg(
                            onlinePlayer,
                            Strings.Guilds.RankLimitResponse.ToString(guildOptions.Ranks[^1].Title, player.Name),
                            ChatMessageType.Guild,
                            CustomColors.Alerts.Error
                        );
                    }
                }

                //Inform the acceptor that they are actually not in the guild
                PacketSender.SendChatMsg(player, Strings.Guilds.RankLimit.ToString(guild.Name), ChatMessageType.Guild, CustomColors.Alerts.Error);

                player.PendingGuildInvite = default;
                player.Save();

                return;
            }
        }

        // Accept our invite!
        if (!guild.TryAddMember(player, guildOptions.Ranks.Length - 1, inviter))
        {
            PacketSender.SendChatMsg(
                player,
                Strings.Guilds.ErrorWhileAcceptingInvite,
                ChatMessageType.Guild,
                CustomColors.Alerts.Error
            );
            PacketSender.SendGuildInvite(player, inviter);
            return;
        }

        player.PendingGuildInvite = default;
        player.Save();

        // Start common events for all online guild members that this one left
        foreach (var member in guild.FindOnlineMembers())
        {
            member.StartCommonEventsWithTrigger(CommonEventTrigger.GuildMemberJoined, guild.Name, player.Name);
        }

        // Send the updated data around.
        PacketSender.SendEntityDataToProximity(player);
    }

    //GuildInviteDeclinePacket
    public void HandlePacket(Client client, GuildInviteDeclinePacket packet)
    {
        var player = client.Entity;
        if (player == null)
        {
            return;
        }

        // Have we received an invite at all?
        if (player.PendingGuildInvite == default)
        {
            PacketSender.SendChatMsg(
                player,
                Strings.Guilds.NotReceivedInvite,
                ChatMessageType.Guild,
                CustomColors.Alerts.Error
            );
            return;
        }

        var inviter = player.PendingGuildInviteFrom ?? Player.FindOnline(player.PendingGuildInviteFromId ?? default);
        var guild = player.PendingGuildInviteTo ?? Guild.LoadGuild(player.PendingGuildInviteToId ?? default);

        player.PendingGuildInvite = default;

        var messageToPlayer = guild == null
            ? Strings.Guilds.InviteDeclinedMissingGuild.ToString()
            : Strings.Guilds.InviteDeclined.ToString(guild.Name);

        PacketSender.SendChatMsg(
            player,
            messageToPlayer,
            ChatMessageType.Guild,
            CustomColors.Alerts.Info
        );

        // Politely decline our invite if the inviter is still online
        // ReSharper disable once InvertIf
        if (inviter != null)
        {
            var messageToInviter = guild == null
                ? Strings.Guilds.InviteDeclinedResponseMissingGuild.ToString(player.Name)
                : Strings.Guilds.InviteDeclinedResponse.ToString(player.Name, guild.Name);
            PacketSender.SendChatMsg(inviter, messageToInviter, ChatMessageType.Guild, CustomColors.Alerts.Info);
        }
    }

    //GuildLeavePacket
    public void HandlePacket(Client client, GuildLeavePacket packet)
    {
        var player = client.Entity;
        if (player == null)
        {
            return;
        }

        var guild = player.Guild;

        // Are we in a guild at all?
        if (guild == null)
        {
            return;
        }

        // Are we the guild master? If so, they're not allowed to leave.
        if (player.GuildRank == 0)
        {
            PacketSender.SendChatMsg(player, Strings.Guilds.GuildLeaderLeave, ChatMessageType.Guild, CustomColors.Alerts.Error);
            return;
        }

        // Start common events for all online guild members that this one left
        foreach (var member in guild.FindOnlineMembers())
        {
            member.StartCommonEventsWithTrigger(CommonEventTrigger.GuildMemberLeft, guild.Name, player.Name);
        }

        guild.TryRemoveMember(player.Id, player, null, GuildHistory.GuildActivityType.Left);

        // Send the newly updated player information to their surroundings.
        PacketSender.SendEntityDataToProximity(player);
        player.UnequipInvalidItems();

    }


    //PictureClosedPacket
    public void HandlePacket(Client client, PictureClosedPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }
        player.PictureClosed(packet.EventId);
    }

    public void HandlePacket(Client client, FadeCompletePacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }
        player.IsFading = false;
    }

    public void HandlePacket(Client client, TargetPacket packet)
    {
        var player = client?.Entity;
        if (player == null)
        {
            return;
        }

        if (packet.TargetId == Guid.Empty)
        {
            player.Target = default;
            return;
        }

        if (player.Map.TryGetInstance(player.MapInstanceId, out var instance))
        {
            var entity = instance.GetEntities(true).Find(e => e.Id == packet.TargetId);
            if (entity != null)
            {
                player.Target = entity;
            }
        }
    }

    #endregion
}
