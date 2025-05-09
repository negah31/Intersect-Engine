using Intersect.Client.Entities.Events;
using Intersect.Client.Framework.Gwen.Control;
using Intersect.Client.Framework.Gwen.Control.EventArguments;
using Intersect.Client.Framework.Gwen.Control.EventArguments.InputSubmissionEvent;
using Intersect.Client.General;
using Intersect.Client.Interface.Shared;
using Intersect.Client.Maps;
using Intersect.Enums;
using Intersect.Framework;
using Intersect.Framework.Core.GameObjects.Maps;
using Intersect.Models;
using Intersect.Network.Packets.Client;
using AdminAction = Intersect.Admin.Actions.AdminAction;

namespace Intersect.Client.Networking;


public static partial class PacketSender
{

    public static void SendPing()
    {
        Network.SendPacket(new PingPacket { Responding = true });
    }

    public static void SendLogin(string username, string password)
    {
        Network.SendPacket(new LoginPacket(username, password));
    }

    public static void SendLogout(bool characterSelect = false)
    {
        Network.SendPacket(new LogoutPacket(characterSelect));
    }

    public static void SendNeedMap(params ObjectCacheKey<MapDescriptor>[] cacheKeys)
    {
        var validMapCacheKeys = cacheKeys
            .Where(cacheKey => cacheKey != default && MapInstance.MapNotRequested(cacheKey.Id.Guid))
            .Where(
                cacheKey => string.IsNullOrWhiteSpace(cacheKey.Checksum) ||
                            string.IsNullOrWhiteSpace(cacheKey.Version) ||
                            !MapInstance.TryGet(cacheKey.Id.Guid, out _)
            )
            .ToArray();
        if (validMapCacheKeys.Length < 1)
        {
            return;
        }

        Network.SendPacket(new GetObjectData<MapDescriptor>(validMapCacheKeys));
        MapInstance.UpdateMapRequestTime(validMapCacheKeys.Select(cacheKey => cacheKey.Id.Guid).ToArray());
    }

    public static void SendNeedMap(params Guid[] mapIds)
    {
        var validMapIds = mapIds.Where(
                mapId => mapId != default && !MapInstance.TryGet(mapId, out _) && MapInstance.MapNotRequested(mapId)
            )
            .ToArray();
        if (validMapIds.Length < 1)
        {
            return;
        }

        Network.SendPacket(
            new GetObjectData<MapDescriptor>(
                validMapIds.Select(id => new ObjectCacheKey<MapDescriptor>(new Id<MapDescriptor>(id))).ToArray()
            )
        );
        MapInstance.UpdateMapRequestTime(validMapIds);
    }

    public static void SendNeedMapForGrid(MapInstance? mapInstance = default)
    {
        if (mapInstance == default && !MapInstance.TryGet(Globals.Me.MapId, out mapInstance))
        {
            return;
        }

        var gridX = mapInstance.GridX;
        var gridY = mapInstance.GridY;
        var minX = Math.Max(0, gridX - 1);
        var minY = Math.Max(0, gridY - 1);
        var maxX = Math.Min(Globals.MapGridWidth, gridX + 2);
        var maxY = Math.Min(Globals.MapGridHeight, gridY + 2);

        List<Guid> idsToFetch = new(9);

        for (int x = minX; x < maxX; x++)
        {
            for (int y = minY; y < maxY; y++)
            {
                var mapId = Globals.MapGrid[x, y];
                if (mapId == Guid.Empty)
                {
                    continue;
                }

                idsToFetch.Add(mapId);
            }
        }

        SendNeedMap(idsToFetch.ToArray());
    }

    public static void SendMove(bool sprinting = false)
    {
        Network.SendPacket(new MovePacket(Globals.Me.MapId, (byte)Globals.Me.X, (byte)Globals.Me.Y, Globals.Me.DirectionFacing, sprinting));
    }

    public static void SendChatMsg(string msg, byte channel)
    {
        Network.SendPacket(new ChatMsgPacket(msg, channel));
    }

    public static void SendAttack(Guid targetId)
    {
        Network.SendPacket(new AttackPacket(targetId));
    }

    public static void SendBlock(bool blocking)
    {
        Network.SendPacket(new BlockPacket(blocking));
    }

    public static void SendDirection(Direction dir)
    {
        Network.SendPacket(new DirectionPacket(dir));
    }

    public static void SendEnterGame()
    {
        Network.SendPacket(new EnterGamePacket());
    }

    public static void SendActivateEvent(Guid eventId)
    {
        Network.SendPacket(new ActivateEventPacket(eventId));
    }

    public static void SendEventResponse(byte response, Dialog ed)
    {
        Globals.EventDialogs.Remove(ed);
        Network.SendPacket(new EventResponsePacket(ed.EventId, response));
    }

    public static void SendEventInputVariable(Base sender, InputSubmissionEventArgs args)
    {
        if (sender is not InputBox { UserData: Guid eventId })
        {
            return;
        }

        var booleanValue = args.Value is BooleanSubmissionValue booleanSubmissionValue
            ? booleanSubmissionValue.Value
            : default;

        var numericalValue = args.Value is NumericalSubmissionValue numericalSubmissionValue
            ? (int)numericalSubmissionValue.Value
            : default;

        var stringValue = args.Value is StringSubmissionValue stringSubmissionValue
            ? stringSubmissionValue.Value
            : default;

        Network.SendPacket(new EventInputVariablePacket(eventId, booleanValue, numericalValue, stringValue));
    }

    public static void SendEventInputVariableCancel(object? sender, EventArgs e)
    {
        if (sender is not InputBox { UserData: Guid eventId } inputBox)
        {
            return;
        }

        Network.SendPacket(new EventInputVariablePacket(eventId, default, default, default, true));
    }

    public static void SendUserRegistration(string username, string password, string email)
    {
        Network.SendPacket(new UserRegistrationRequestPacket(username, password, email));
    }

    public static void SendCreateCharacter(string name, Guid classId, int sprite)
    {
        Network.SendPacket(new CreateCharacterPacket(name, classId, sprite));
    }

    public static void SendPickupItem(Guid mapId, int tileIndex, Guid uniqueId)
    {
        Network.SendPacket(new PickupItemPacket(mapId, tileIndex, uniqueId));
    }

    public static void SendSwapInvItems(int item1, int item2)
    {
        Network.SendPacket(new SwapInvItemsPacket(item1, item2));
    }

    public static void SendDropItem(int slot, int amount)
    {
        Network.SendPacket(new DropItemPacket(slot, amount));
    }

    public static void SendUseItem(int slot, Guid targetId)
    {
        Network.SendPacket(new UseItemPacket(slot, targetId));
    }

    public static void SendSwapSpells(int spell1, int spell2)
    {
        Network.SendPacket(new SwapSpellsPacket(spell1, spell2));
    }

    public static void SendForgetSpell(int slot)
    {
        Network.SendPacket(new ForgetSpellPacket(slot));
    }

    public static void SendUseSpell(int slot, Guid targetId)
    {
        Network.SendPacket(new UseSpellPacket(slot, targetId, Globals.ShouldSoftRetargetOnSelfCast));
    }

    public static void SendUnequipItem(int slot)
    {
        Network.SendPacket(new UnequipItemPacket(slot));
    }

    public static void SendUpgradeStat(byte stat)
    {
        Network.SendPacket(new UpgradeStatPacket(stat));
    }

    public static void SendHotbarUpdate(int hotbarSlot, sbyte type, int itemIndex)
    {
        Network.SendPacket(new HotbarUpdatePacket(hotbarSlot, type, itemIndex));
    }

    public static void SendHotbarSwap(int index, int swapIndex)
    {
        Network.SendPacket(new HotbarSwapPacket(index, swapIndex));
    }

    public static void SendOpenAdminWindow()
    {
        Network.SendPacket(new OpenAdminWindowPacket());
    }

    //Admin Action Packet Should be Here

    public static void SendSellItem(int slot, int amount)
    {
        Network.SendPacket(new SellItemPacket(slot, amount));
    }

    public static void SendBuyItem(int slot, int amount)
    {
        Network.SendPacket(new BuyItemPacket(slot, amount));
    }

    public static void SendCloseShop()
    {
        Network.SendPacket(new CloseShopPacket());
    }

    public static void SendDepositItem(int slot, int amount, int bankSlot = -1)
    {
        Network.SendPacket(new DepositItemPacket(slot, amount, bankSlot));
    }

    public static void SendWithdrawItem(int slot, int amount, int invSlot = -1)
    {
        Network.SendPacket(new WithdrawItemPacket(slot, amount, invSlot));
    }

    public static void SendCloseBank()
    {
        Network.SendPacket(new CloseBankPacket());
    }

    public static void SendCloseCrafting()
    {
        Network.SendPacket(new CloseCraftingPacket());
    }

    public static void SendMoveBankItems(int slot1, int slot2)
    {
        Network.SendPacket(new SwapBankItemsPacket(slot1, slot2));
    }

    public static void SendCraftItem(Guid id, int count)
    {
        Network.SendPacket(new CraftItemPacket(id, count));
    }

    public static void SendPartyInvite(Guid targetId)
    {
        Network.SendPacket(new PartyInvitePacket(targetId));
    }

    public static void SendPartyInvite(string target)
    {
        Network.SendPacket(new PartyInvitePacket(target));
    }

    public static void SendPartyKick(Guid targetId)
    {
        Network.SendPacket(new PartyKickPacket(targetId));
    }

    public static void SendPartyLeave()
    {
        Network.SendPacket(new PartyLeavePacket());
    }

    public static void SendPartyAccept(object? sender, EventArgs e)
    {
        if (sender is InputBox inputBox && inputBox.UserData is Guid partyId)
        {
            Network.SendPacket(new PartyInviteResponsePacket(partyId, true));
        }
    }

    public static void SendPartyDecline(object? sender, EventArgs e)
    {
        if (sender is InputBox inputBox && inputBox.UserData is Guid partyId)
        {
            Network.SendPacket(new PartyInviteResponsePacket(partyId, false));
        }
    }

    public static void SendAcceptQuest(Guid questId)
    {
        Network.SendPacket(new QuestResponsePacket(questId, true));
    }

    public static void SendDeclineQuest(Guid questId)
    {
        Network.SendPacket(new QuestResponsePacket(questId, false));
    }

    public static void SendAbandonQuest(Guid questId)
    {
        Network.SendPacket(new AbandonQuestPacket(questId));
    }

    public static void SendTradeRequest(Guid targetId)
    {
        Network.SendPacket(new TradeRequestPacket(targetId));
    }

    public static void SendOfferTradeItem(int slot, int amount)
    {
        Network.SendPacket(new OfferTradeItemPacket(slot, amount));
    }

    public static void SendRevokeTradeItem(int slot, int amount)
    {
        Network.SendPacket(new RevokeTradeItemPacket(slot, amount));
    }

    public static void SendAcceptTrade()
    {
        Network.SendPacket(new AcceptTradePacket());
    }

    public static void SendDeclineTrade()
    {
        Network.SendPacket(new DeclineTradePacket());
    }

    public static void SendTradeRequestAccept(object? sender, EventArgs e)
    {
        if (sender is InputBox inputBox && inputBox.UserData is Guid tradeId)
        {
            Network.SendPacket(new TradeRequestResponsePacket(tradeId, true));
        }
    }

    public static void SendTradeRequestDecline(object? sender, EventArgs e)
    {
        if (sender is InputBox inputBox && inputBox.UserData is Guid tradeId)
        {
            Network.SendPacket(new TradeRequestResponsePacket(tradeId, false));
        }
    }

    public static void SendStoreBagItem(int invSlot, int amount, int bagSlot)
    {
        Network.SendPacket(new StoreBagItemPacket(invSlot, amount, bagSlot));
    }

    public static void SendRetrieveBagItem(int bagSlot, int amount, int invSlot)
    {
        Network.SendPacket(new RetrieveBagItemPacket(bagSlot, amount, invSlot));
    }

    public static void SendCloseBag()
    {
        Network.SendPacket(new CloseBagPacket());
    }

    public static void SendMoveBagItems(int slot1, int slot2)
    {
        Network.SendPacket(new SwapBagItemsPacket(slot1, slot2));
    }

    public static void SendRequestFriends()
    {
        Network.SendPacket(new RequestFriendsPacket());
    }

    public static void SendAddFriend(string name)
    {
        Network.SendPacket(new UpdateFriendsPacket(name, true));
    }

    public static void SendRemoveFriend(string name)
    {
        Network.SendPacket(new UpdateFriendsPacket(name, false));
    }

    public static void SendFriendRequestAccept(Object? sender, EventArgs e)
    {
        if (sender is InputBox inputBox && inputBox.UserData is Guid requestId)
        {
            Network.SendPacket(new FriendRequestResponsePacket(requestId, true));
        }
    }

    public static void SendFriendRequestDecline(Object? sender, EventArgs e)
    {
        if (sender is InputBox inputBox && inputBox.UserData is Guid requestId)
        {
            Network.SendPacket(new FriendRequestResponsePacket(requestId, false));
        }
    }

    public static void SendSelectCharacter(Guid charId)
    {
        Network.SendPacket(new SelectCharacterPacket(charId));
    }

    public static void SendDeleteCharacter(Guid charId)
    {
        Network.SendPacket(new DeleteCharacterPacket(charId));
    }

    public static void SendNewCharacter()
    {
        Network.SendPacket(new NewCharacterPacket());
    }

    public static void SendRequestPasswordReset(string nameEmail)
    {
        Network.SendPacket(new RequestPasswordResetPacket(nameEmail));
    }

    public static void SendPasswordChangeRequest(string identifier, string token, string passwordHash)
    {
        Network.SendPacket(new PasswordChangeRequestPacket(identifier, token, passwordHash));
    }

    public static void SendAdminAction(AdminAction action)
    {
        Network.SendPacket(new AdminActionPacket(action));
    }

    public static void SendBumpEvent(Guid mapId, Guid eventId)
    {
        Network.SendPacket(new BumpPacket(mapId, eventId));
    }

    public static void SendRequestGuild()
    {
        Network.SendPacket(new RequestGuildPacket());
    }

    public static void SendGuildInviteAccept(Object? sender, EventArgs e)
    {
        Network.SendPacket(new GuildInviteAcceptPacket());
    }

    public static void SendGuildInviteDecline(Object? sender, EventArgs e)
    {
        Network.SendPacket(new GuildInviteDeclinePacket());
    }

    public static void SendInviteGuild(string name)
    {
        Network.SendPacket(new UpdateGuildMemberPacket(Guid.Empty, name, Enums.GuildMemberUpdateAction.Invite));
    }

    public static void SendLeaveGuild()
    {
        Network.SendPacket(new GuildLeavePacket());
    }

    public static void SendKickGuildMember(Guid id)
    {
        Network.SendPacket(new UpdateGuildMemberPacket(id, null, Enums.GuildMemberUpdateAction.Remove));
    }
    public static void SendPromoteGuildMember(Guid id, int rank)
    {
        Network.SendPacket(new UpdateGuildMemberPacket(id, null, Enums.GuildMemberUpdateAction.Promote, rank));
    }

    public static void SendDemoteGuildMember(Guid id, int rank)
    {
        Network.SendPacket(new UpdateGuildMemberPacket(id, null, Enums.GuildMemberUpdateAction.Demote, rank));
    }

    public static void SendTransferGuild(Guid id)
    {
        Network.SendPacket(new UpdateGuildMemberPacket(id, null, Enums.GuildMemberUpdateAction.Transfer));
    }

    public static void SendClosePicture(Guid eventId)
    {
        if (eventId != Guid.Empty)
        {
            Network.SendPacket(new PictureClosedPacket(eventId));
        }
    }

    public static void SendFadeCompletePacket()
    {
        Network.SendPacket(new FadeCompletePacket());
    }

    public static void SendTarget(Guid targetId)
    {
        Network.SendPacket(new TargetPacket(targetId));
    }

}
