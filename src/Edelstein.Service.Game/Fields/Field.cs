using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Edelstein.Core.Gameplay.Social.Guild;
using Edelstein.Core.Gameplay.Social.Party;
using Edelstein.Core.Templates.Fields;
using Edelstein.Core.Templates.Fields.Life;
using Edelstein.Core.Templates.Mob;
using Edelstein.Core.Templates.NPC;
using Edelstein.Core.Templates.Reactor;
using Edelstein.Core.Utils.Packets;
using Edelstein.Network.Packets;
using Edelstein.Provider;
using Edelstein.Service.Game.Fields.Generators;
using Edelstein.Service.Game.Fields.Objects;
using Edelstein.Service.Game.Fields.Objects.Mob;
using Edelstein.Service.Game.Fields.Objects.NPC;
using Edelstein.Service.Game.Fields.Objects.User;
using MoreLinq;

namespace Edelstein.Service.Game.Fields
{
    public class Field : IField
    {
        private const int ScreenWidth = 1024;
        private const int ScreenHeight = 768;
        private const int ScreenWidthOffset = ScreenWidth * 75 / 100;
        private const int ScreenHeightOffset = ScreenHeight * 75 / 100;

        private readonly IDictionary<FieldObjType, IFieldPool> _pools;
        private readonly IDictionary<string, IFieldPortal> _portals;
        private readonly IFieldSplit[,] _splits;
        private readonly ICollection<IFieldGenerator> _generators;

        private DateTime LastGenObjTime { get; set; }

        public FieldTemplate Template { get; }

        public Field(FieldTemplate template, IDataTemplateManager manager)
        {
            _pools = new Dictionary<FieldObjType, IFieldPool>();
            _portals = template.Portals
                .Where(kv => kv.Value.Name != "sp" && kv.Value.Name != "tp")
                .ToDictionary(
                    kv => kv.Value.Name,
                    kv => (IFieldPortal) new FieldPortal(this, kv.Value)
                );

            var splitColCount = (template.Bounds.Width + (ScreenWidthOffset - 1)) / ScreenWidthOffset;
            var splitRowCount = (template.Bounds.Height + (ScreenHeightOffset - 1)) / ScreenHeightOffset;

            _splits = new IFieldSplit[splitColCount, splitRowCount];

            for (var col = 0; col < splitColCount; col++)
            for (var row = 0; row < splitRowCount; row++)
                _splits[col, row] = new FieldSplit(col, row);

            _generators = new List<IFieldGenerator>();

            Template = template;

            template.Life.ForEach(l =>
            {
                switch (l.Type)
                {
                    case FieldLifeType.NPC:
                        Enter(new FieldNPC(manager.Get<NPCTemplate>(l.TemplateID), l.Left)
                        {
                            Position = l.Position,
                            Foothold = (short) l.FH,
                            RX0 = l.RX0,
                            RX1 = l.RX1
                        });
                        break;
                    case FieldLifeType.Monster:
                        _generators.Add(
                            new FieldGeneratorMob(l, manager.Get<MobTemplate>(l.TemplateID))
                        );
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            });
            template.Reactors.ForEach(l =>
                _generators.Add(new FieldGeneratorReactor(l, manager.Get<ReactorTemplate>(l.TemplateID)))
            );
        }

        public IFieldObj GetObject(int id)
            => GetObjects().FirstOrDefault(o => o.ID == id);

        public T GetObject<T>(int id) where T : IFieldObj
            => GetObjects().OfType<T>().FirstOrDefault(o => o.ID == id);

        public IEnumerable<IFieldObj> GetObjects()
            => _pools.Values.SelectMany(p => p.GetObjects()).ToImmutableList();

        public IEnumerable<T> GetObjects<T>() where T : IFieldObj
            => _pools.Values.SelectMany(p => p.GetObjects<T>()).ToImmutableList();

        public IFieldObj GetControlledObject(IFieldUser controller, int id)
            => GetControlledObjects(controller).FirstOrDefault(o => o.ID == id);

        public T GetControlledObject<T>(IFieldUser controller, int id) where T : IFieldControlled
            => GetControlledObjects<T>(controller).FirstOrDefault(o => o.ID == id);

        public IEnumerable<IFieldObj> GetControlledObjects(IFieldUser controller)
            => GetControlledObjects<IFieldControlled>(controller);

        public IEnumerable<T> GetControlledObjects<T>(IFieldUser controller) where T : IFieldControlled
            => GetObjects<T>().Where(o => o.Controller == controller).ToImmutableList();

        public IFieldSplit GetSplit(Point position)
        {
            var col = (position.X - Template.Bounds.Left) / ScreenWidthOffset;
            var row = (position.Y - Template.Bounds.Top) / ScreenHeightOffset;
            return GetSplit(col, row);
        }

        private IFieldSplit GetSplit(int col, int row)
        {
            if (
                col < 0 || col >= _splits.GetLength(0) ||
                row < 0 || row >= _splits.GetLength(1)
            ) return null;
            return _splits[col, row];
        }

        public IFieldSplit[] GetEnclosingSplits(Point position)
            => GetEnclosingSplits(GetSplit(position));

        public IFieldSplit[] GetEnclosingSplits(IFieldSplit split)
        {
            var splits = new IFieldSplit[9];

            if (split == null) return splits;

            var col = split.Col;
            var row = split.Row;

            splits[0] = GetSplit(col - 1, row - 1);
            splits[1] = GetSplit(col, row - 1);
            splits[2] = GetSplit(col + 1, row - 1);

            splits[3] = GetSplit(col - 1, row);
            splits[4] = split;
            splits[5] = GetSplit(col + 1, row);

            splits[6] = GetSplit(col - 1, row + 1);
            splits[7] = GetSplit(col, row + 1);
            splits[8] = GetSplit(col + 1, row + 1);
            return splits;
        }

        public IFieldPool GetPool(FieldObjType type)
        {
            lock (this)
            {
                _pools.TryGetValue(type, out var pool);

                if (pool == null)
                {
                    pool = new FieldPool();
                    _pools[type] = pool;
                }

                return pool;
            }
        }

        public IFieldPortal GetPortal(byte portal)
            => _portals[Template.Portals[portal].Name];

        public IFieldPortal GetPortal(string portal)
            => _portals[portal];

        public Task Enter(IFieldObj obj)
            => Enter(obj, null);

        public Task Leave(IFieldObj obj)
            => Leave(obj, null);

        public Task Enter(IFieldUser user, byte portal, Func<IPacket> getEnterPacket = null)
        {
            user.Character.FieldPortal = portal;
            return Enter(user, getEnterPacket);
        }

        public Task Enter(IFieldUser user, string portal, Func<IPacket> getEnterPacket = null)
        {
            user.Character.FieldPortal = (byte) Template.Portals
                .FirstOrDefault(kv => kv.Value.Name.Equals(portal))
                .Key;
            return Enter(user, getEnterPacket);
        }

        public async Task Enter(IFieldObj obj, Func<IPacket> getEnterPacket)
        {
            var pool = GetPool(obj.Type);

            obj.Field?.Leave(obj);
            obj.Field = this;

            if (obj is IFieldUser user)
            {
                var portal = Template.Portals.ContainsKey(user.Character.FieldPortal)
                    ? Template.Portals[user.Character.FieldPortal]
                    : Template.Portals.Values
                        .First(p => p.Type == FieldPortalType.StartPoint);

                user.ID = user.Character.ID;
                user.Character.FieldID = Template.ID;
                user.Position = portal.Position;
                user.Foothold = (short) (portal.Type != FieldPortalType.StartPoint
                    ? Template.Footholds
                        .Where(kv => kv.Value.X1 <= portal.Position.X && kv.Value.X2 >= portal.Position.X)
                        .Where(kv => kv.Value.Y1 <= portal.Position.Y && kv.Value.Y2 <= portal.Position.Y)
                        .OrderBy(kv => portal.Position.Y - kv.Value.Y1)
                        .First(kv => kv.Value.X1 < kv.Value.X2).Key
                    : 0);

                await user.SendPacket(user.GetSetFieldPacket());

                if (user.Party != null)
                {
                    if (!user.IsInstantiated)
                    {
                        using var p = new OutPacket(SendPacketOperations.PartyResult);
                        p.EncodeByte((byte) PartyResultType.LoadParty_Done);
                        p.EncodeInt(user.Party.ID);

                        await user.Party.OnUpdateUserMigration(
                            user.Character.ID,
                            user.Service.State.ChannelID,
                            Template.ID
                        );

                        user.Party.EncodeData(user.Service.State.ChannelID, p);

                        await user.SendPacket(p);
                    }

                    await user.Party.UpdateUserMigration(
                        user.Character.ID,
                        user.Service.State.ChannelID,
                        user.Field.Template.ID
                    );
                }

                if (user.Guild != null && !user.IsInstantiated)
                {
                    var inactive = user.Guild.Members
                                       .FirstOrDefault(m => m.CharacterID == user.ID)
                                       ?.Inactive ?? false;

                    await user.Guild.OnUpdateNotifyLoginOrLogout(
                        user.Character.ID,
                        true
                    );

                    using var p = new OutPacket(SendPacketOperations.GuildResult);
                    p.EncodeByte((byte) GuildResultType.LoadGuild_Done);
                    p.EncodeBool(true);
                    user.Guild.EncodeData(p);
                    await user.SendPacket(p);

                    await user.Guild.UpdateNotifyLoginOrLogout(
                        user.Character.ID,
                        true
                    );
                    if (inactive)
                        await user.Guild.UpdateChangeLevelOrJob(
                            user.Character.ID,
                            user.Character.Level,
                            user.Character.Job
                        );
                }

                if (!user.IsInstantiated) user.IsInstantiated = true;
            }

            await pool.Enter(obj);
            await obj.UpdateFieldSplit(getEnterPacket);
        }

        public async Task Leave(IFieldObj obj, Func<IPacket> getLeavePacket)
        {
            var pool = GetPool(obj.Type);

            obj.Field = null;

            if (obj is IDisposable disposable)
                disposable.Dispose();
            if (obj is IFieldUser user)
                Array.Fill(user.Watching, null);
            if (obj is IFieldGeneratorObj generated && generated.Generator != null)
                await generated.Generator.Reset(generated);

            await pool.Leave(obj);
            await obj.UpdateFieldSplit(getLeavePacket: getLeavePacket);
        }

        public Task BroadcastPacket(IPacket packet)
            => Task.WhenAll(
                GetObjects<IFieldUser>()
                    .Select(u => u.SendPacket(packet))
            );

        public Task BroadcastPacket(IFieldObj source, IPacket packet)
            => source.FieldSplit.BroadcastPacket(source, packet);

        public Task BroadcastPacket(IFieldObj source, ISocialParty party, IPacket packet)
            => Task.WhenAll(source.FieldSplit
                .GetWatchers()
                .Where(u => u?.Party.ID == party.ID)
                .Select(u => u.SendPacket(packet)));

        public Task BroadcastPacket(IFieldObj source, ISocialGuild guild, IPacket packet)
            => Task.WhenAll(source.FieldSplit
                .GetWatchers()
                .Where(u => u?.Guild.ID == guild.ID)
                .Select(u => u.SendPacket(packet)));

        public async Task TryTick()
        {
            if (!GetObjects<IFieldUser>().Any())
                return;

            var now = DateTime.UtcNow;

            if ((now - LastGenObjTime).TotalSeconds >= 7)
            {
                LastGenObjTime = DateTime.UtcNow;

                var availableGenerators = _generators
                    .Where(g => g.Available(this))
                    .ToImmutableList();
                var mobGenerators = availableGenerators
                    .OfType<FieldGeneratorMob>()
                    .ToImmutableList();
                var otherGenerators = availableGenerators
                    .Except(mobGenerators)
                    .ToImmutableList();

                var userCount = GetObjects<FieldUser>().Count();
                var mobCount = GetObjects<FieldMob>().Count();
                var mobCapacity = Template.MobCapacityMin;

                if (userCount > Template.MobCapacityMin / 2)
                    mobCapacity += (Template.MobCapacityMax - Template.MobCapacityMin) *
                                   (2 * userCount - Template.MobCapacityMin) /
                                   (3 * Template.MobCapacityMin);

                mobCapacity = Math.Min(mobCapacity, Template.MobCapacityMax);

                var mobGenCount = mobCapacity - mobCount;

                await Task.WhenAll(
                    mobGenerators
                        .Shuffle()
                        .Take(mobGenCount)
                        .Select(g => g.Generate(this))
                );
                await Task.WhenAll(otherGenerators.Select(g => g.Generate(this)));
            }
        }
    }
}