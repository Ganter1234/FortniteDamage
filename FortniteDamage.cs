using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Dapper;
using System.Collections.Concurrent;
using System.Globalization;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FortniteDamage;
public class FortniteDamage : BasePlugin, IPluginConfig<FortniteDamageConfig>
{
    public override string ModuleName => "Fortnite Damage";
    public override string ModuleAuthor => "Ganter1234";
    public override string ModuleVersion => "1.0";
    public override string ModuleDescription => "Fortnite hit particles";
    public FortniteDamageConfig Config { get; set; } = new();
    private readonly ConcurrentDictionary<ulong, bool> _playerSettings = new();
    private readonly Dictionary<int, PendingShot> _pendingShots = new();
    private readonly List<DamageParticle> _particles = new();
    private readonly List<string> _commands = new();
    private string _connectionString = string.Empty;
    private const string ParticlePath = "particles/kolka/fortnite_dmg_v2/kolka_damage_";

    public override void Load(bool hotReload)
    {
        LoadSettings();
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            ClearEffects();
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnServerPrecacheResources>(OnServerPrecacheResources);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
    }

    public void OnConfigParsed(FortniteDamageConfig config)
    {
        if (!float.IsFinite(config.Distance) || config.Distance < 0)
            throw new ArgumentException("[FortniteDamage] Distance must be a finite, non-negative number.");

        foreach (var command in _commands)
            RemoveCommand(command, CommandToggle);
        _commands.Clear();

        foreach (var command in config.Commands)
        {
            if (_commands.Contains(command, StringComparer.OrdinalIgnoreCase)) continue;
            _commands.Add(command);
            AddCommand(command, "Toggle Fortnite damage display", CommandToggle);
        }

        Config = config;
    }

    public void OnServerPrecacheResources(ResourceManifest manifest)
    {
        for (int digit = 0; digit <= 9; digit++)
        {
            foreach (var side in new[] { "fl", "fr" })
            {
                foreach (var crit in new[] { "", "_crit" })
                {
                    var path = $"{ParticlePath}{digit}_{side}{crit}";
                    manifest.AddResource($"{path}.vpcf");
                    manifest.AddResource($"{path}_child.vpcf");
                }
            }
        }
    }

    private bool CanShowDamage(CCSPlayerController player)
    {
        return player.IsValid && !player.IsBot && !player.IsHLTV
            && (!_playerSettings.TryGetValue(player.SteamID, out var enabled) || enabled);
    }

    public void CommandToggle(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;

        if (player.AuthorizedSteamID == null)
        {
            player.PrintToChat(StringExtensions.ReplaceColorTags(Localizer.ForPlayer(player, "FH_NotAuthorized")));
            return;
        }

        bool enabled = _playerSettings.TryGetValue(player.SteamID, out var current) && !current;
        _playerSettings[player.SteamID] = enabled;
        var steamId = player.SteamID;
        _ = Task.Run(async () =>
        {
            try
            {
                await using (var connection = new SqliteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    await connection.ExecuteAsync(@"
                        INSERT INTO `fortnite_damage` (`steamid`, `enable`) VALUES (@Auth, @Enable)
                        ON CONFLICT (`steamid`) DO UPDATE SET `enable` = excluded.`enable`;", new
                    {
                        Auth = steamId.ToString(CultureInfo.InvariantCulture),
                        Enable = enabled
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[FortniteDamage] Failed to save player settings.");
            }
        });

        if (!enabled) ClearPlayerEffects(player);
        player.PrintToChat(StringExtensions.ReplaceColorTags(Localizer.ForPlayer(player, enabled ? "FH_Enable" : "FH_Disable")));
    }

    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (attacker == null || victim == null || !victim.IsValid || attacker == victim
            || @event.DmgHealth <= 0 || !CanShowDamage(attacker)) return HookResult.Continue;

        var pawn = victim.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return HookResult.Continue;

        var position = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
        bool ducked = pawn.MovementServices?.As<CCSPlayer_MovementServices>().Ducked ?? false;
        bool crit = @event.Hitgroup == 1;

        if (@event.Weapon is "xm1014" or "nova" or "mag7" or "sawedoff")
        {
            int slot = attacker.Slot;
            if (!_pendingShots.TryGetValue(slot, out var shot))
            {
                shot = new PendingShot(attacker);
                _pendingShots.Add(slot, shot);
                shot.Timer = AddTimer(0.1f, () => FlushShot(slot, shot), TimerFlags.STOP_ON_MAPCHANGE);
            }

            uint target = pawn.EntityHandle.Raw;
            if (shot.Hits.TryGetValue(target, out var hit))
            {
                hit.Damage += @event.DmgHealth;
                hit.Crit |= crit;
                hit.Position = position;
                hit.Ducked = ducked;
            }
            else
                shot.Hits.Add(target, new PendingHit(@event.DmgHealth, crit, position, ducked));
        }
        else
            ShowDamage(attacker, @event.DmgHealth, crit, position, ducked);

        return HookResult.Continue;
    }

    private void FlushShot(int slot, PendingShot shot)
    {
        _pendingShots.Remove(slot);
        if (!CanShowDamage(shot.Attacker)) return;

        foreach (var hit in shot.Hits.Values)
            ShowDamage(shot.Attacker, hit.Damage, hit.Crit, hit.Position, hit.Ducked);
    }

    private void ShowDamage(CCSPlayerController attacker, int damage, bool crit, Vector position, bool ducked)
    {
        var pawn = attacker.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null || damage <= 0) return;

        var angles = new QAngle(pawn.EyeAngles.X, pawn.EyeAngles.Y, pawn.EyeAngles.Z);
        float yaw = (pawn.AbsRotation?.Y ?? angles.Y) * MathF.PI / 180.0f;
        float forwardX = MathF.Cos(yaw), forwardY = MathF.Sin(yaw);
        float rightX = forwardY, rightY = -forwardX;
        float dx = pawn.AbsOrigin.X - position.X, dy = pawn.AbsOrigin.Y - position.Y, dz = pawn.AbsOrigin.Z - position.Z;
        float spacing = MathF.Max(1.0f, MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 700.0f) * 6.0f;
        string digits = damage.ToString(CultureInfo.InvariantCulture);
        int left = (digits.Length + 1) / 2;
        float x = position.X + rightX * spacing * left * (Random.Shared.NextSingle() * 1.5f - 0.5f);
        float y = position.Y + rightY * spacing * left * (Random.Shared.NextSingle() * 1.5f - 0.5f);
        float z = position.Z + (crit ? (ducked ? 45 : 60) : (ducked ? 25 : 35))
            + Random.Shared.NextSingle() * (crit ? 10 : 20);

        foreach (char digit in digits)
        {
            var origin = new Vector(x - forwardX * Config.Distance - rightX * spacing * left,
                y - forwardY * Config.Distance - rightY * spacing * left, z);
            string effect = $"{ParticlePath}{digit}_{(left-- > 0 ? "fl" : "fr")}{(crit ? "_crit" : "")}";
            var particle = CreateParticle(attacker, $"{effect}.vpcf", origin, angles);
            if (particle == null) continue;

            AddTimer(0.5f, () =>
            {
                // Игрок мог отключить эффект или выйти до второй стадии анимации.
                if (!_particles.Contains(particle)) return;
                RemoveParticle(particle);
                if (!CanShowDamage(attacker)) return;
                var child = CreateParticle(attacker, $"{effect}_child.vpcf", origin, angles);
                if (child != null) AddTimer(2.5f, () => RemoveParticle(child), TimerFlags.STOP_ON_MAPCHANGE);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    private DamageParticle? CreateParticle(CCSPlayerController owner, string effect, Vector position, QAngle angles)
    {
        var entity = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (entity == null || !entity.IsValid) return null;

        var particle = new DamageParticle(entity, owner);
        _particles.Add(particle);
        entity.EffectName = effect;
        entity.StartActive = true;
        entity.Teleport(position, angles);
        entity.DispatchSpawn();
        return particle;
    }

    private void RemoveParticle(DamageParticle particle)
    {
        _particles.Remove(particle);
        if (particle.Entity.IsValid) particle.Entity.Remove();
    }

    public void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_particles.Count == 0) return;

        foreach (var (info, player) in infoList)
        {
            if (player == null || !player.IsValid) continue;
            var target = !player.PawnIsAlive ? player.Pawn.Value?.ObserverServices?.ObserverTarget.Value : null;

            foreach (var particle in _particles)
            {
                if (!particle.Entity.IsValid) continue;
                bool isOwner = player.EntityHandle.Raw == particle.Owner.EntityHandle.Raw;
                bool observingOwner = particle.Owner.IsValid && target != null && target.IsValid
                    && target.EntityHandle.Raw == particle.Owner.PlayerPawn.Raw;
                if (!isOwner && !observingOwner) info.TransmitEntities.Remove(particle.Entity);
            }
        }
    }

    private void ClearPlayerEffects(CCSPlayerController player)
    {
        if (_pendingShots.Remove(player.Slot, out var shot)) shot.Timer?.Kill();
        foreach (var particle in _particles.Where(x => x.Owner.EntityHandle.Raw == player.EntityHandle.Raw).ToArray())
            RemoveParticle(particle);
    }

    public void OnClientDisconnect(int slot)
    {
        if (_pendingShots.Remove(slot, out var shot)) shot.Timer?.Kill();
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player != null && player.IsValid) ClearPlayerEffects(player);
    }

    private void ClearEffects()
    {
        foreach (var shot in _pendingShots.Values) shot.Timer?.Kill();
        _pendingShots.Clear();
        foreach (var particle in _particles.ToArray()) RemoveParticle(particle);
    }

    public void OnMapEnd()
    {
        ClearEffects();
    }

    private void LoadSettings()
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Join(ModuleDirectory, "database.db")
        }.ToString();

        Task.Run(async () =>
        {
            try
            {
                await using (var connection = new SqliteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    await connection.ExecuteAsync(@"
                        CREATE TABLE IF NOT EXISTS `fortnite_damage` (
                            `steamid` TEXT NOT NULL PRIMARY KEY,
                            `enable` INT NOT NULL DEFAULT 1
                        );");

                    var settings = await connection.QueryAsync<(string SteamId, bool Enabled)>("SELECT `steamid`, `enable` FROM `fortnite_damage`;");
                    foreach (var (steamId, enabled) in settings)
                        _playerSettings.TryAdd(ulong.Parse(steamId, CultureInfo.InvariantCulture), enabled);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[FortniteDamage] Failed to load player settings.");
            }
        });
    }

    private sealed record DamageParticle(CParticleSystem Entity, CCSPlayerController Owner);

    private sealed class PendingShot(CCSPlayerController attacker)
    {
        public CCSPlayerController Attacker { get; } = attacker;
        public Dictionary<uint, PendingHit> Hits { get; } = new();
        public Timer? Timer { get; set; }
    }

    private sealed class PendingHit(int damage, bool crit, Vector position, bool ducked)
    {
        public int Damage { get; set; } = damage;
        public bool Crit { get; set; } = crit;
        public Vector Position { get; set; } = position;
        public bool Ducked { get; set; } = ducked;
    }
}
