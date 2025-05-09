using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Amib.Threading;
using Intersect.Collections;
using Intersect.Config;
using Intersect.Core;
using Intersect.Enums;
using Intersect.Framework.Core.GameObjects.Animations;
using Intersect.Framework.Core.GameObjects.Crafting;
using Intersect.Framework.Core.GameObjects.Events;
using Intersect.Framework.Core.GameObjects.Items;
using Intersect.Framework.Core.GameObjects.Mapping.Tilesets;
using Intersect.Framework.Core.GameObjects.Maps;
using Intersect.Framework.Core.GameObjects.Maps.MapList;
using Intersect.Framework.Core.GameObjects.NPCs;
using Intersect.Framework.Core.GameObjects.PlayerClass;
using Intersect.Framework.Core.GameObjects.Resources;
using Intersect.Framework.Core.GameObjects.Variables;
using Intersect.Framework.Reflection;
using Intersect.GameObjects;
using Intersect.Models;
using Intersect.Server.Core;
using Intersect.Server.Database.GameData;
using Intersect.Server.Database.Logging;
using Intersect.Server.Database.PlayerData;
using Intersect.Server.Database.PlayerData.Players;
using Intersect.Server.Database.PlayerData.Security;
using Intersect.Server.Entities;
using Intersect.Server.General;
using Intersect.Server.Localization;
using Intersect.Server.Maps;
using Intersect.Server.Networking;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Intersect.Server.Database;


public static partial class DbInterface
{

    /// <summary>
    /// This is our thread pool for handling game-loop database interactions.
    /// Min/Max Number of Threads & Idle Timeouts are set via server config.
    /// </summary>
    public static SmartThreadPool Pool = new(
            new STPStartInfo()
            {
                ThreadPoolName = "DatabasePool",
                IdleTimeout = Options.Instance.Processing.DatabaseThreadIdleTimeout,
                MinWorkerThreads = Options.Instance.Processing.MinDatabaseThreads,
                MaxWorkerThreads = Options.Instance.Processing.MaxDatabaseThreads
            }
        );

    private static string GameDbFilename => Path.Combine(ServerContext.ResourceDirectory, "gamedata.db");

    private static string LoggingDbFilename => Path.Combine(ServerContext.ResourceDirectory, "logging.db");

    private static string PlayersDbFilename => Path.Combine(ServerContext.ResourceDirectory, "playerdata.db");

    public static Dictionary<string, ServerVariableDescriptor> ServerVariableEventTextLookup = new();

    public static Dictionary<string, PlayerVariableDescriptor> PlayerVariableEventTextLookup = new();

    public static Dictionary<string, GuildVariableDescriptor> GuildVariableEventTextLookup = new();

    public static Dictionary<string, UserVariableDescriptor> UserVariableEventTextLookup = new();

    public static ConcurrentDictionary<Guid, ServerVariableDescriptor> UpdatedServerVariables = new();

    private static List<MapGrid> mapGrids = new();

    private static readonly HashSet<Guid> IdsToRegenerate = [
      new Guid("00000000-0000-0000-0000-000000000001"),
      new Guid("00000000-0000-0000-0000-000000000002"),
    ];

    public static GameContext CreateGameContext(
        bool readOnly = true,
        bool explicitLoad = false,
        bool lazyLoading = false,
        bool autoDetectChanges = false,
        QueryTrackingBehavior? queryTrackingBehavior = default
    ) => GameContext.Create(new DatabaseContextOptions
    {
        AutoDetectChanges = autoDetectChanges,
        ConnectionStringBuilder = Options.Instance.GameDatabase.Type.CreateConnectionStringBuilder(
            Options.Instance.GameDatabase,
            GameDbFilename
        ),
        DatabaseType = Options.Instance.GameDatabase.Type,
        ExplicitLoad = explicitLoad,
        KillServerOnConcurrencyException = Options.Instance.GameDatabase.KillServerOnConcurrencyException,
        LazyLoading = lazyLoading,
        LoggerFactory = CreateLoggerFactory<GameContext>(Options.Instance.GameDatabase),
        QueryTrackingBehavior = queryTrackingBehavior,
        ReadOnly = readOnly,
    });

    internal static LoggingContext CreateLoggingContext(
        bool readOnly = true,
        bool explicitLoad = false,
        bool lazyLoading = false,
        bool autoDetectChanges = false,
        QueryTrackingBehavior? queryTrackingBehavior = default
    ) => LoggingContext.Create(new DatabaseContextOptions
    {
        AutoDetectChanges = autoDetectChanges,
        ConnectionStringBuilder = Options.Instance.LoggingDatabase.Type.CreateConnectionStringBuilder(
            Options.Instance.LoggingDatabase,
            LoggingDbFilename
        ),
        DatabaseType = Options.Instance.LoggingDatabase.Type,
        ExplicitLoad = explicitLoad,
        KillServerOnConcurrencyException = Options.Instance.LoggingDatabase.KillServerOnConcurrencyException,
        LazyLoading = lazyLoading,
        LoggerFactory = CreateLoggerFactory<LoggingContext>(Options.Instance.LoggingDatabase),
        QueryTrackingBehavior = queryTrackingBehavior,
        ReadOnly = readOnly,
    });

    /// <summary>
    /// Creates a game context to query. Best practice is to scope this within a using statement.
    /// </summary>
    /// <param name="readOnly">Defines whether or not the context should initialize with change tracking. If readonly is true then SaveChanges will not work.</param>
    /// <returns></returns>
    public static PlayerContext CreatePlayerContext(
        bool readOnly = true,
        bool explicitLoad = false,
        bool lazyLoading = false,
        bool autoDetectChanges = false,
        QueryTrackingBehavior? queryTrackingBehavior = default
    ) => PlayerContext.Create(new DatabaseContextOptions
    {
        AutoDetectChanges = autoDetectChanges,
        ConnectionStringBuilder = Options.Instance.PlayerDatabase.Type.CreateConnectionStringBuilder(
            Options.Instance.PlayerDatabase,
            PlayersDbFilename
        ),
        DatabaseType = Options.Instance.PlayerDatabase.Type,
        ExplicitLoad = explicitLoad,
        KillServerOnConcurrencyException = Options.Instance.PlayerDatabase.KillServerOnConcurrencyException,
        LazyLoading = lazyLoading,
        LoggerFactory = CreateLoggerFactory<PlayerContext>(Options.Instance.PlayerDatabase),
        QueryTrackingBehavior = queryTrackingBehavior,
        ReadOnly = readOnly,
    });

    //Check Directories
    public static void CheckDirectories()
    {
        if (Directory.Exists(ServerContext.ResourceDirectory))
        {
            return;
        }

        if (ServerContext.IsDefaultResourceDirectory)
        {
            Directory.CreateDirectory(ServerContext.ResourceDirectory);
        }
        else
        {
            throw new DirectoryNotFoundException(
                Path.Combine(Environment.CurrentDirectory, ServerContext.ResourceDirectory)
            );
        }
    }

    //As of now Database writes only occur on player saving & when editors make game changes
    //Database writes are actually pretty rare. And even player saves are offloaded as tasks so
    //if delayed it won't matter much.
    //TODO: Options for saving frequency and number of backups to keep.
    public static void BackupDatabase()
    {
    }

    public static DbConnectionStringBuilder CreateConnectionStringBuilder(
        DatabaseOptions databaseOptions,
        string filename
    )
    {
        switch (databaseOptions.Type)
        {
            case DatabaseType.SQLite:
                return new SqliteConnectionStringBuilder($"Data Source={filename}");

            case DatabaseType.MySQL:
                return new MySqlConnectionStringBuilder
                {
                    Server = databaseOptions.Server,
                    Port = databaseOptions.Port,
                    Database = databaseOptions.Database,
                    UserID = databaseOptions.Username,
                    Password = databaseOptions.Password
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(databaseOptions.Type));
        }
    }

    private static readonly MethodInfo _methodInfoProcessMigrations =
        typeof(DbInterface)
            .GetMethod(nameof(ProcessMigrations), BindingFlags.NonPublic | BindingFlags.Static);

    private static void ProcessMigrations<TContext>(TContext context)
        where TContext : IntersectDbContext<TContext>
    {
        if (!context.HasPendingMigrations)
        {
            ApplicationContext.Context.Value?.Logger.LogDebug($"No pending migrations for {context.GetType().GetName(qualified: true)}, skipping...");
            return;
        }

        ApplicationContext.Context.Value?.Logger.LogDebug($"Pending schema migrations for {typeof(TContext).Name}:\n\t{string.Join("\n\t", context.PendingSchemaMigrations)}");
        ApplicationContext.Context.Value?.Logger.LogDebug($"Pending data migrations for {typeof(TContext).Name}:\n\t{string.Join("\n\t", context.PendingDataMigrationNames)}");

        var migrationScheduler = new MigrationScheduler<TContext>(context);
        ApplicationContext.Context.Value?.Logger.LogDebug("Scheduling pending migrations...");
        migrationScheduler.SchedulePendingMigrations();

        ApplicationContext.Context.Value?.Logger.LogDebug("Applying scheduled migrations...");
        migrationScheduler.ApplyScheduledMigrations();

        var remainingPendingSchemaMigrations = context.PendingSchemaMigrations.ToList();
        var processedSchemaMigrations =
            context.PendingSchemaMigrations.Where(migration => !remainingPendingSchemaMigrations.Contains(migration));

        context.OnSchemaMigrationsProcessed(processedSchemaMigrations.ToArray());
    }

    internal static ILoggerFactory CreateLoggerFactory<TDBContext>(DatabaseOptions databaseOptions)
        where TDBContext : IntersectDbContext<TDBContext>
    {
        var contextName = typeof(TDBContext).Name;
        var configuration = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Is(LevelConvert.ToSerilogLevel(databaseOptions.LogLevel))
            .WriteTo.Console(restrictedToMinimumLevel: Debugger.IsAttached ? LogEventLevel.Warning : LogEventLevel.Error)
            .WriteTo.File(path: $"logs/db-{contextName}.log").WriteTo.File(
                path: $"logs/db-errors-{contextName}.log",
                restrictedToMinimumLevel: LogEventLevel.Error,
                rollOnFileSizeLimit: true,
                retainedFileTimeLimit: TimeSpan.FromDays(30)
            );

        return new SerilogLoggerFactory(configuration.CreateLogger());
    }

    private static bool EnsureUpdated(IServerContext serverContext)
    {
        var gameDatabaseOptions = Options.Instance.GameDatabase;
        ApplicationContext.Context.Value?.Logger.LogInformation($"Creating game context using {gameDatabaseOptions.Type}...");
        using var gameContext = GameContext.Create(new DatabaseContextOptions
        {
            ConnectionStringBuilder = gameDatabaseOptions.Type.CreateConnectionStringBuilder(
                gameDatabaseOptions,
                GameDbFilename
            ),
            DatabaseType = gameDatabaseOptions.Type,
            EnableDetailedErrors = true,
            EnableSensitiveDataLogging = true,
            LoggerFactory = CreateLoggerFactory<GameContext>(gameDatabaseOptions),
        });

        var playerDatabaseOptions = Options.Instance.PlayerDatabase;
        ApplicationContext.Context.Value?.Logger.LogInformation($"Creating player context using {playerDatabaseOptions.Type}...");
        using var playerContext = PlayerContext.Create(new DatabaseContextOptions
        {
            ConnectionStringBuilder = playerDatabaseOptions.Type.CreateConnectionStringBuilder(
                playerDatabaseOptions,
                PlayersDbFilename
            ),
            DatabaseType = playerDatabaseOptions.Type,
            EnableDetailedErrors = true,
            EnableSensitiveDataLogging = true,
            LoggerFactory = CreateLoggerFactory<PlayerContext>(playerDatabaseOptions),
        });

        var loggingDatabaseOptions = Options.Instance.LoggingDatabase;
        ApplicationContext.Context.Value?.Logger.LogInformation($"Creating logging context using {loggingDatabaseOptions.Type}...");
        using var loggingContext = LoggingContext.Create(new DatabaseContextOptions
        {
            ConnectionStringBuilder = loggingDatabaseOptions.Type.CreateConnectionStringBuilder(
                loggingDatabaseOptions,
                LoggingDbFilename
            ),
            DatabaseType = loggingDatabaseOptions.Type,
            EnableDetailedErrors = true,
            EnableSensitiveDataLogging = true,
            LoggerFactory = CreateLoggerFactory<LoggingContext>(loggingDatabaseOptions),
        });

        // We don't want anyone running the old migration tool accidentally
        try
        {
            if (File.Exists("Intersect Migration Tool.exe"))
            {
                File.Delete("Intersect Migration Tool.exe");
            }

            if (File.Exists("Intersect Migration Tool.pdb"))
            {
                File.Delete("Intersect Migration Tool.pdb");
            }

            if (File.Exists("Intersect Migration Tool.mdb"))
            {
                File.Delete("Intersect Migration Tool.mdb");
            }
        }
        catch
        {
            // ignored
        }

        var gameContextPendingMigrations = gameContext.PendingSchemaMigrations;
        var playerContextPendingMigrations = playerContext.PendingSchemaMigrations;
        var loggingContextPendingMigrations = loggingContext.PendingSchemaMigrations;

        var showMigrationWarning = (
            gameContextPendingMigrations.Any() && !gameContextPendingMigrations.Contains("20180905042857_Initial")
        ) || (
            playerContextPendingMigrations.Any() &&
            !playerContextPendingMigrations.Contains("20180927161502_InitialPlayerDb")
        ) || (
            loggingContextPendingMigrations.Any() &&
            !loggingContextPendingMigrations.Contains("20191118024649_RequestLogs")
        );

        if (showMigrationWarning)
        {
            if (serverContext.StartupOptions.MigrateAutomatically)
            {
                Console.WriteLine(Strings.Database.MigratingAutomatically);
                ApplicationContext.Context.Value?.Logger.LogInformation(
                    "Skipping user prompt for database migration..."
                );
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(Strings.Database.UpgradeRequired);
                Console.WriteLine(
                    Strings.Database.UpgradeBackup.ToString(Strings.Database.UpgradeReady, Strings.Database.UpgradeExit)
                );

                Console.WriteLine();
                while (true)
                {
                    Console.Write("> ");
                    var input = Console.ReadLine().Trim();
                    if (input == Strings.Database.UpgradeReady.ToString().Trim())
                    {
                        break;
                    }

                    if (
                        !string.Equals(
                            input,
                            Strings.Database.UpgradeExit.ToString().Trim(),
                            StringComparison.CurrentCultureIgnoreCase
                        )
                    )
                    {
                        continue;
                    }

                    Environment.Exit(1);

                    return false;
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                "Please wait! Migrations can take several minutes, and even longer if you are using MySQL databases!"
            );
        }
        else
        {
            Console.WriteLine("No migrations pending that require user acceptance, skipping prompt...");
        }

        var contexts = new List<DbContext> { gameContext, playerContext, loggingContext };
        foreach (var context in contexts)
        {
            var contextType = context.GetType().FindGenericTypeParameters(typeof(IntersectDbContext<>)).First();
            _methodInfoProcessMigrations.MakeGenericMethod(contextType).Invoke(null, new object[] { context });
        }

        return true;
    }

    // Database setup, version checking
    internal static bool InitDatabase(IServerContext serverContext)
    {
        Console.WriteLine("Initializing database...");

        if (!EnsureUpdated(serverContext))
        {
            Console.Error.WriteLine("Database not updated.");
            return false;
        }

        Console.WriteLine("Loading game data...");

        LoadAllGameObjects();

        ValidateMapEvents();
        ValidateResourceStates();

        LoadTime();
        OnClassesLoaded();
        OnMapsLoaded();
        CacheServerVariableEventTextLookups();
        CachePlayerVariableEventTextLookups();
        CacheGuildVariableEventTextLookups();
        CacheUserVariableEventTextLookups();

        CheckPlayerDatabaseCaseInsensitiveCollisions();

        return true;
    }

    private static void CheckPlayerDatabaseCaseInsensitiveCollisions()
    {
        using var playerContext = CreatePlayerContext();
        var conflictingUsersByName =
            (from u1 in playerContext.Users
                join u2 in playerContext.Users on u1.Name equals u2.Name
                where u1.Id != u2.Id
                select u1).Distinct()
            .ToArray();
        if (conflictingUsersByName.Length > 0)
        {
            ApplicationContext.CurrentContext.Logger.LogError(
                "There are {Count} users with conflicting names (they only differ by case):\nThis needs to be resolved but cannot be handled automatically!\n{Users}",
                conflictingUsersByName.Length,
                string.Join('\n', conflictingUsersByName.Select(u => $"\t{u.Id}"))
            );
        }

        var conflictingUsersByEmail =
            (from u1 in playerContext.Users
                join u2 in playerContext.Users on u1.Email equals u2.Email
                where u1.Id != u2.Id
                select u1).Distinct()
            .ToArray();
        if (conflictingUsersByEmail.Length > 0)
        {
            ApplicationContext.CurrentContext.Logger.LogError(
                "There are {Count} users with conflicting emails (they only differ by case):\nThis needs to be resolved but cannot be handled automatically!\n{Users}",
                conflictingUsersByEmail.Length,
                string.Join('\n', conflictingUsersByName.Select(u => $"\t{u.Id}"))
            );
        }

        var conflictingPlayersByName =
            (from p1 in playerContext.Players
                join p2 in playerContext.Players on p1.Name equals p2.Name
                where p1.Id != p2.Id
                select p1).Distinct()
            .ToArray();
        if (conflictingPlayersByName.Length > 0)
        {
            ApplicationContext.CurrentContext.Logger.LogError(
                "There are {Count} players with conflicting names (they only differ by case):\nThis needs to be resolved but cannot be handled automatically!\n{Players}",
                conflictingPlayersByName.Length,
                string.Join('\n', conflictingPlayersByName.Select(p => $"\t{p.Id}"))
            );
        }

        var conflictingGuildsByName =
            (from g1 in playerContext.Guilds
                join g2 in playerContext.Guilds on g1.Name equals g2.Name
                where g1.Id != g2.Id
                select g1).Distinct()
            .ToArray();
        if (conflictingGuildsByName.Length > 0)
        {
            ApplicationContext.CurrentContext.Logger.LogError(
                "There are {Count} guilds with conflicting names (they only differ by case):\nThis needs to be resolved but cannot be handled automatically!\n{Guilds}",
                conflictingGuildsByName.Length,
                string.Join('\n', conflictingGuildsByName.Select(g => $"\t{g.Id}"))
            );
        }
    }

    public static void SetPlayerPower(string username, UserRights power)
    {
        var user = User.Find(username);
        if (user != null)
        {
            user.Power = power;
            user.Save();
        }
        else
        {
            Console.WriteLine(Strings.Account.AccountDoesNotExist);
        }
    }

    public static bool SetPlayerPower(User user, UserRights power)
    {
        if (user != null)
        {
            user.Power = power;
            user.Save();

            return true;
        }
        else
        {
            Console.WriteLine(Strings.Account.AccountDoesNotExist);

            return false;
        }
    }

    //User Info
    public static bool AccountExists(string accountname)
    {
        return User.Find(accountname) != null;
    }

    public static string UsernameFromEmail(string email)
    {
        var user = User.FindByEmail(email);
        if (user != null)
        {
            return user.Name;
        }
        return null;
    }

    public static Player GetUserCharacter(User user, Guid playerId, bool explicitLoad = false)
    {
        if (user == default)
        {
            return default;
        }

        foreach (var player in user.Players)
        {
            if (player.Id != playerId)
            {
                continue;
            }

            if (!explicitLoad)
            {
                return player;
            }

            try
            {
                using var playerContext = CreatePlayerContext(readOnly: true, explicitLoad: false);
                _ = player.LoadRelationships(playerContext);
            }
            catch (Exception exception)
            {
                Debugger.Break();
                ApplicationContext.Context.Value?.Logger.LogError(
                    exception,
                    "Failed to load relationships for user {UserId}'s player {PlayerId}",
                    user.Id,
                    playerId
                );
                throw new Exception($"Error during explicit load of player {BitConverter.ToString(playerId.ToByteArray()).Replace("-", string.Empty)}", exception);
            }

            return player;
        }

        return null;
    }

    public static bool TryRegister(
        string username,
        string email,
        string password,
        [NotNullWhen(true)] out User? user
    )
    {
        try
        {
            var rawSaltData = RandomNumberGenerator.GetBytes(20);
            var rawSalt = Convert.ToBase64String(rawSaltData);
            var encodedSaltData = Encoding.UTF8.GetBytes(rawSalt);
            var saltData = SHA256.HashData(encodedSaltData);
            var salt = BitConverter.ToString(saltData).Replace("-", string.Empty);

            user = new User
            {
                Name = username,
                Email = email,
                Salt = salt,
                Password = User.SaltPasswordHash(password, salt),
                Power = UserRights.None,
            };

            if (User.Count() == 0)
            {
                user.Power = UserRights.Admin;
            }

            user.Save(create: true);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(
                exception,
                "Error while registering '{Username}'",
                username
            );
            user = default;
            return false;
        }
    }

    public static void CreateAccount(
        Client? client,
        string username,
        string password,
        string email,
        bool grantFirstUserAdmin = true
    )
    {
        var salt = User.GenerateSalt();
        var saltedPasswordHash = User.SaltPasswordHash(password, salt);

        var user = new User
        {
            Name = username,
            Email = email,
            Salt = salt,
            Password = saltedPasswordHash,
            Power = UserRights.None,
        };

        if (grantFirstUserAdmin && User.Count() == 0)
        {
            user.Power = UserRights.Admin;
        }

        user.Save(create: true);

        client?.SetUser(user);
    }

    public static void UpdatePassword(User user, string password)
    {
        var salt = User.GenerateSalt();
        var saltedPasswordHash = User.SaltPasswordHash(password, salt);
        user.Salt = salt;
        user.Password = saltedPasswordHash;
        user.Save();
    }

    public static bool BagEmpty(Bag bag)
    {
        for (var i = 0; i < bag.Slots.Count; i++)
        {
            if (bag.Slots[i] != null)
            {
                var item = ItemDescriptor.Get(bag.Slots[i].ItemId);
                if (item != null)
                {
                    return false;
                }
            }
        }

        return true;
    }

    //Game Object Saving/Loading
    private static void LoadAllGameObjects()
    {
        foreach (var value in Enum.GetValues(typeof(GameObjectType)))
        {
            Debug.Assert(value != null, "value != null");
            var type = (GameObjectType)value;
            if (type == GameObjectType.Time)
            {
                continue;
            }

            LoadGameObjects(type);
        }
    }

    private static void ClearGameObjects(GameObjectType type)
    {
        switch (type)
        {
            case GameObjectType.Animation:
                AnimationDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Class:
                ClassDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Item:
                ItemDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Npc:
                NPCDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Projectile:
                ProjectileDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Quest:
                QuestDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Resource:
                ResourceDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Shop:
                ShopDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Spell:
                SpellDescriptor.Lookup.Clear();

                break;
            case GameObjectType.CraftTables:
                CraftingTableDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Crafts:
                CraftingRecipeDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Map:
                MapDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Event:
                EventDescriptor.Lookup.Clear();

                break;
            case GameObjectType.PlayerVariable:
                PlayerVariableDescriptor.Lookup.Clear();

                break;
            case GameObjectType.ServerVariable:
                ServerVariableDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Tileset:
                TilesetDescriptor.Lookup.Clear();

                break;
            case GameObjectType.Time:
                break;
            case GameObjectType.GuildVariable:
                GuildVariableDescriptor.Lookup.Clear();

                break;
            case GameObjectType.UserVariable:
                UserVariableDescriptor.Lookup.Clear();

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    private static void LoadGameObjects(GameObjectType gameObjectType)
    {
        ClearGameObjects(gameObjectType);
        try
        {
            using (var context = CreateGameContext(readOnly: true))
            {
                switch (gameObjectType)
                {
                    case GameObjectType.Animation:
                        foreach (var anim in context.Animations) // TODO: fix "The data is NULL at ordinal 2"
                        {
                            AnimationDescriptor.Lookup.Set(anim.Id, anim);
                        }

                        break;
                    case GameObjectType.Class:
                        foreach (var cls in context.Classes)
                        {
                            ClassDescriptor.Lookup.Set(cls.Id, cls);
                        }

                        break;
                    case GameObjectType.Item:

                        var loadedItems = context.Items
                            .Include(i => i.EquipmentProperties);

                        foreach (var itm in loadedItems)
                        {
                            ItemDescriptor.Lookup.Set(itm.Id, itm);
                        }

                        break;
                    case GameObjectType.Npc:
                        foreach (var npc in context.Npcs)
                        {
                            NPCDescriptor.Lookup.Set(npc.Id, npc);
                        }

                        break;
                    case GameObjectType.Projectile:
                        foreach (var proj in context.Projectiles)
                        {
                            ProjectileDescriptor.Lookup.Set(proj.Id, proj);
                        }

                        break;
                    case GameObjectType.Quest:
                        foreach (var qst in context.Quests)
                        {
                            QuestDescriptor.Lookup.Set(qst.Id, qst);
                        }

                        break;
                    case GameObjectType.Resource:
                        foreach (var res in context.Resources)
                        {
                            ResourceDescriptor.Lookup.Set(res.Id, res);
                        }

                        break;
                    case GameObjectType.Shop:
                        foreach (var shp in context.Shops)
                        {
                            ShopDescriptor.Lookup.Set(shp.Id, shp);
                        }

                        break;
                    case GameObjectType.Spell:
                        foreach (var spl in context.Spells)
                        {
                            SpellDescriptor.Lookup.Set(spl.Id, spl);
                        }

                        break;
                    case GameObjectType.CraftTables:
                        foreach (var craft in context.CraftingTables)
                        {
                            CraftingTableDescriptor.Lookup.Set(craft.Id, craft);
                        }

                        break;
                    case GameObjectType.Crafts:
                        foreach (var craft in context.Crafts)
                        {
                            CraftingRecipeDescriptor.Lookup.Set(craft.Id, craft);
                        }

                        break;
                    case GameObjectType.Map:
                        foreach (var map in context.Maps)
                        {
                            MapController.Lookup.Set(map.Id, map);
                            if (Options.Instance.Map.Layers.DestroyOrphanedLayers)
                            {
                                map.DestroyOrphanedLayers();
                            }
                        }

                        break;
                    case GameObjectType.Event:
                        foreach (var evt in context.Events)
                        {
                            EventDescriptor.Lookup.Set(evt.Id, evt);
                        }

                        break;
                    case GameObjectType.PlayerVariable:
                        foreach (var psw in context.PlayerVariables)
                        {
                            PlayerVariableDescriptor.Lookup.Set(psw.Id, psw);
                        }

                        break;
                    case GameObjectType.ServerVariable:
                        foreach (var psw in context.ServerVariables)
                        {
                            ServerVariableDescriptor.Lookup.Set(psw.Id, psw);
                        }

                        break;
                    case GameObjectType.Tileset:
                        foreach (var psw in context.Tilesets)
                        {
                            TilesetDescriptor.Lookup.Set(psw.Id, psw);
                        }

                        break;
                    case GameObjectType.Time:
                        break;
                    case GameObjectType.GuildVariable:
                        foreach (var psw in context.GuildVariables)
                        {
                            GuildVariableDescriptor.Lookup.Set(psw.Id, psw);
                        }

                        break;
                    case GameObjectType.UserVariable:
                        foreach (var psw in context.UserVariables)
                        {
                            UserVariableDescriptor.Lookup.Set(psw.Id, psw);
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(gameObjectType), gameObjectType, null);
                }
            }
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(
                exception,
                "Error while loading game objects of type {GameObjectType}",
                gameObjectType
            );
            throw;
        }
    }

    private static void ValidateMapEvents()
    {
        var missingEvents = 0;
        var correctedEvents = 0;

        foreach (var (mapId, databaseObject) in MapController.Lookup)
        {
            if (databaseObject is not MapDescriptor mapDescriptor)
            {
                ApplicationContext.CurrentContext.Logger.LogError(
                    "Found an invalid database object in the MapDescriptor lookup ({InvalidObjectType}, {InvalidObjectId}, '{InvalidObjectName}'",
                    databaseObject.GetType().GetName(qualified: true),
                    databaseObject.Id,
                    databaseObject.Name
                );
                continue;
            }

            var actualMapId = mapDescriptor.Id;
            if (mapId != actualMapId)
            {
                ApplicationContext.CurrentContext.Logger.LogError(
                    "Map with ID {ActualMapId} was recorded in the lookup under the ID {ExpectedMapId}, this needs to be investigated",
                    actualMapId,
                    mapId
                );
            }

            foreach (var eventId in mapDescriptor.EventIds)
            {
                if (!EventDescriptor.TryGet(eventId, out var eventDescriptor))
                {
                    ApplicationContext.CurrentContext.Logger.LogWarning(
                        "Map {MapId} references missing event {EventId}, unexpected behavior may occur",
                        mapId,
                        eventId
                    );
                    ++missingEvents;
                    continue;
                }

                // Ignore common events
                if (eventDescriptor is not { CommonEvent: false })
                {
                    continue;
                }

                // The map ID is correct, no validation necessary
                var referencedMapId = eventDescriptor.MapId;
                if (referencedMapId == mapId)
                {
                    continue;
                }

                // If the event is 1) not common and 2) is not using this map's ID, fix it. It was copied wrong in the editor
                string referencedMapName = $"deleted map {referencedMapId}";
                if (MapController.TryGet(referencedMapId, out var referencedMapDescriptor))
                {
                    referencedMapName = referencedMapDescriptor.Name;
                }

                if (string.IsNullOrWhiteSpace(referencedMapName))
                {
                    referencedMapName = "(unnamed map)";
                }

                eventDescriptor.MapId = mapId;
                ++correctedEvents;

                var eventName = eventDescriptor.Name?.Trim();
                if (string.IsNullOrWhiteSpace(eventName))
                {
                    eventName = "(unnamed event)";
                }

                var mapName = mapDescriptor.Name;
                if (string.IsNullOrWhiteSpace(mapName))
                {
                    mapName = "(unnamed map)";
                }


                ApplicationContext.CurrentContext.Logger.LogWarning(
                    "Event '{EventName}' ({EventId}) on the map '{MapName}'({MapId}) was pointing to '{ReferencedMapName}' ({ReferencedMapId}) and while this has been corrected, the correction will not be saved until the event is resaved",
                    eventName,
                    eventId,
                    mapName,
                    mapId,
                    referencedMapName,
                    referencedMapId
                );
            }
        }

        ApplicationContext.CurrentContext.Logger.LogWarning(
            "Finished validating map events on all maps, there were {MissingEvents} missing events and {CorrectedEvents} corrected events",
            missingEvents,
            correctedEvents
        );
    }

    private static void ValidateResourceStates()
    {
        using var context = CreateGameContext(readOnly: false);
        foreach (var (resourceId, resource) in ResourceDescriptor.Lookup)
        {
            if (resource is not ResourceDescriptor descriptor)
            {
                ApplicationContext.CurrentContext.Logger.LogError(
                    "Found an invalid database object in the ResourceDescriptor lookup ({InvalidObjectType}, {InvalidObjectId}, '{InvalidObjectName}')",
                    resource.GetType().GetName(qualified: true),
                    resourceId,
                    resource.Name
                );
                continue;
            }

            var statesToUpdate = descriptor.States.Where(pair => IdsToRegenerate.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
            if (statesToUpdate.Length > 0)
            {
                foreach (var state in statesToUpdate)
                {
                    // invalid id, lets fix it
                    var invalidId = state.Id;
                    state.Id = Guid.NewGuid();
                    descriptor.States.Remove(invalidId);
                    descriptor.States.Add(state.Id, state);
                }

                context.Resources.Update(descriptor);
            }
        }

        context.ChangeTracker.DetectChanges();
        context.SaveChanges();
    }

    public static IDatabaseObject AddGameObject(GameObjectType gameObjectType)
    {
        return AddGameObject(gameObjectType, Guid.Empty);
    }

    public static IDatabaseObject AddGameObject(GameObjectType gameObjectType, Guid predefinedid)
    {
        if (predefinedid == Guid.Empty)
        {
            predefinedid = Guid.NewGuid();
        }

        IDatabaseObject dbObj = null;
        switch (gameObjectType)
        {
            case GameObjectType.Animation:
                dbObj = new AnimationDescriptor(predefinedid);

                break;
            case GameObjectType.Class:
                dbObj = new ClassDescriptor(predefinedid);

                break;
            case GameObjectType.Item:
                dbObj = new ItemDescriptor(predefinedid);

                break;
            case GameObjectType.Npc:
                dbObj = new NPCDescriptor(predefinedid);

                break;
            case GameObjectType.Projectile:
                dbObj = new ProjectileDescriptor(predefinedid);

                break;
            case GameObjectType.Resource:
                dbObj = new ResourceDescriptor(predefinedid);

                break;
            case GameObjectType.Shop:
                dbObj = new ShopDescriptor(predefinedid);

                break;
            case GameObjectType.Spell:
                dbObj = new SpellDescriptor(predefinedid);

                break;
            case GameObjectType.CraftTables:
                dbObj = new CraftingTableDescriptor(predefinedid);

                break;
            case GameObjectType.Crafts:
                dbObj = new CraftingRecipeDescriptor(predefinedid);

                break;
            case GameObjectType.Map:
                dbObj = new MapController(predefinedid);

                break;
            case GameObjectType.Event:
                dbObj = new EventDescriptor(predefinedid);

                break;
            case GameObjectType.PlayerVariable:
                dbObj = new PlayerVariableDescriptor(predefinedid);

                break;
            case GameObjectType.ServerVariable:
                dbObj = new ServerVariableDescriptor(predefinedid);

                break;
            case GameObjectType.Tileset:
                dbObj = new TilesetDescriptor(predefinedid);

                break;
            case GameObjectType.Time:
                break;

            case GameObjectType.Quest:
                dbObj = new QuestDescriptor(predefinedid);
                ((QuestDescriptor)dbObj).StartEvent = (EventDescriptor)AddGameObject(GameObjectType.Event);
                ((QuestDescriptor)dbObj).EndEvent = (EventDescriptor)AddGameObject(GameObjectType.Event);
                ((QuestDescriptor)dbObj).StartEvent.CommonEvent = false;
                ((QuestDescriptor)dbObj).EndEvent.CommonEvent = false;

                break;

            case GameObjectType.GuildVariable:
                dbObj = new GuildVariableDescriptor(predefinedid);

                break;

            case GameObjectType.UserVariable:
                dbObj = new UserVariableDescriptor(predefinedid);

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(gameObjectType), gameObjectType, null);
        }

        return dbObj == null ? null : AddGameObject(gameObjectType, dbObj);
    }

    public static IDatabaseObject AddGameObject(GameObjectType gameObjectType, IDatabaseObject dbObj)
    {
        try
        {
            using (var context = CreateGameContext(readOnly: false))
            {

                switch (gameObjectType)
                {
                    case GameObjectType.Animation:
                        context.Animations.Add((AnimationDescriptor)dbObj);
                        AnimationDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Class:
                        context.Classes.Add((ClassDescriptor)dbObj);
                        ClassDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Item:
                        context.Items.Add((ItemDescriptor)dbObj);
                        ItemDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;
                    case GameObjectType.Npc:
                        context.Npcs.Add((NPCDescriptor)dbObj);
                        NPCDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Projectile:
                        context.Projectiles.Add((ProjectileDescriptor)dbObj);
                        ProjectileDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Quest:
                        context.Quests.Add((QuestDescriptor)dbObj);
                        QuestDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Resource:
                        context.Resources.Add((ResourceDescriptor)dbObj);
                        ResourceDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Shop:
                        context.Shops.Add((ShopDescriptor)dbObj);
                        ShopDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Spell:
                        context.Spells.Add((SpellDescriptor)dbObj);
                        SpellDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.CraftTables:
                        context.CraftingTables.Add((CraftingTableDescriptor)dbObj);
                        CraftingTableDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Crafts:
                        context.Crafts.Add((CraftingRecipeDescriptor)dbObj);
                        CraftingRecipeDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Map:
                        context.Maps.Add((MapController)dbObj);
                        MapController.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Event:
                        context.Events.Add((EventDescriptor)dbObj);
                        EventDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.PlayerVariable:
                        context.PlayerVariables.Add((PlayerVariableDescriptor)dbObj);
                        PlayerVariableDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.ServerVariable:
                        context.ServerVariables.Add((ServerVariableDescriptor)dbObj);
                        ServerVariableDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Tileset:
                        context.Tilesets.Add((TilesetDescriptor)dbObj);
                        TilesetDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.Time:
                        break;

                    case GameObjectType.GuildVariable:
                        context.GuildVariables.Add((GuildVariableDescriptor)dbObj);
                        GuildVariableDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    case GameObjectType.UserVariable:
                        context.UserVariables.Add((UserVariableDescriptor)dbObj);
                        UserVariableDescriptor.Lookup.Set(dbObj.Id, dbObj);

                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(gameObjectType), gameObjectType, null);
                }

                context.ChangeTracker.DetectChanges();
                context.Entry(dbObj).State = EntityState.Added;
                context.SaveChanges();
            }

            return dbObj;
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(
                exception,
                "Error adding a {GameObjectType}",
                gameObjectType
            );
            throw;
        }
    }

    public static void DeleteGameObject(IDatabaseObject gameObject)
    {
        try
        {
            using (var context = CreateGameContext(readOnly: false))
            {
                switch (gameObject.Type)
                {
                    case GameObjectType.Animation:
                        context.Animations.Remove((AnimationDescriptor)gameObject);

                        break;
                    case GameObjectType.Class:
                        context.Classes.Remove((ClassDescriptor)gameObject);

                        break;
                    case GameObjectType.Item:
                        context.Items.Remove((ItemDescriptor)gameObject);

                        break;
                    case GameObjectType.Npc:
                        context.Npcs.Remove((NPCDescriptor)gameObject);

                        break;
                    case GameObjectType.Projectile:
                        context.Projectiles.Remove((ProjectileDescriptor)gameObject);

                        break;
                    case GameObjectType.Quest:

                        if (((QuestDescriptor)gameObject).StartEvent != null)
                        {
                            context.Events.Remove(((QuestDescriptor)gameObject).StartEvent);
                            context.Entry(((QuestDescriptor)gameObject).StartEvent).State = EntityState.Deleted;
                            EventDescriptor.Lookup.Delete(((QuestDescriptor)gameObject).StartEvent);
                        }

                        if (((QuestDescriptor)gameObject).EndEvent != null)
                        {
                            context.Events.Remove(((QuestDescriptor)gameObject).EndEvent);
                            context.Entry(((QuestDescriptor)gameObject).EndEvent).State = EntityState.Deleted;
                            EventDescriptor.Lookup.Delete(((QuestDescriptor)gameObject).EndEvent);
                        }

                        foreach (var tsk in ((QuestDescriptor)gameObject).Tasks)
                        {
                            if (tsk.CompletionEvent != null)
                            {
                                context.Events.Remove(tsk.CompletionEvent);
                                context.Entry(tsk.CompletionEvent).State = EntityState.Deleted;
                                EventDescriptor.Lookup.Delete(tsk.CompletionEvent);
                            }
                        }

                        context.Quests.Remove((QuestDescriptor)gameObject);

                        break;
                    case GameObjectType.Resource:
                        context.Resources.Remove((ResourceDescriptor)gameObject);

                        break;
                    case GameObjectType.Shop:
                        context.Shops.Remove((ShopDescriptor)gameObject);

                        break;
                    case GameObjectType.Spell:
                        context.Spells.Remove((SpellDescriptor)gameObject);

                        break;
                    case GameObjectType.CraftTables:
                        context.CraftingTables.Remove((CraftingTableDescriptor)gameObject);

                        break;
                    case GameObjectType.Crafts:
                        context.Crafts.Remove((CraftingRecipeDescriptor)gameObject);

                        break;
                    case GameObjectType.Map:
                        //Delete all map events first
                        foreach (var evtId in ((MapController)gameObject).EventIds)
                        {
                            var evt = EventDescriptor.Get(evtId);
                            if (evt != null)
                            {
                                DeleteGameObject(evt);
                            }
                        }
                        context.Maps.Remove((MapController)gameObject);
                        MapController.Lookup.Delete(gameObject);

                        break;
                    case GameObjectType.Event:
                        context.Events.Remove((EventDescriptor)gameObject);

                        break;
                    case GameObjectType.PlayerVariable:
                        context.PlayerVariables.Remove((PlayerVariableDescriptor)gameObject);

                        break;
                    case GameObjectType.ServerVariable:
                        context.ServerVariables.Remove((ServerVariableDescriptor)gameObject);

                        break;
                    case GameObjectType.Tileset:
                        context.Tilesets.Remove((TilesetDescriptor)gameObject);

                        break;
                    case GameObjectType.Time:
                        break;
                    case GameObjectType.GuildVariable:
                        context.GuildVariables.Remove((GuildVariableDescriptor)gameObject);

                        break;
                    case GameObjectType.UserVariable:
                        context.UserVariables.Remove((UserVariableDescriptor)gameObject);

                        break;
                }

                if (gameObject.Type.GetLookup().Values.Contains(gameObject))
                {
                    if (!gameObject.Type.GetLookup().Delete(gameObject))
                    {
                        throw new Exception();
                    }
                }

                context.ChangeTracker.DetectChanges();
                context.Entry(gameObject).State = EntityState.Deleted;
                context.SaveChanges();
            }
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(
                exception,
                "Error deleting {GameObjectType} {GameObjectId} '{GameObjectName}'",
                gameObject.Type,
                gameObject.Id,
                gameObject.Name
            );
            throw;
        }
    }

    public static void SaveGameObject(IDatabaseObject gameObject)
    {
        try
        {
            using (var context = CreateGameContext(readOnly: false))
            {

                switch (gameObject.Type)
                {
                    case GameObjectType.Animation:
                        context.Animations.Update((AnimationDescriptor)gameObject);

                        break;
                    case GameObjectType.Class:
                        context.Classes.Update((ClassDescriptor)gameObject);

                        break;
                    case GameObjectType.Item:
                    {
                        if (gameObject is not ItemDescriptor itemDescriptor)
                        {
                            throw new InvalidOperationException();
                        }

                        itemDescriptor.ValidateStatRanges();

                        if (itemDescriptor.EquipmentProperties?.DescriptorId == Guid.Empty)
                        {
                            context.Items_EquipmentProperties.Add(itemDescriptor.EquipmentProperties);
                        }
                        else
                        {
                            EquipmentProperties? deletedEquipmentProperties =
                                context.Items_EquipmentProperties.FirstOrDefault(
                                    ep => ep.DescriptorId == itemDescriptor.Id
                                );
                            if (deletedEquipmentProperties != default)
                            {
                                context.Items_EquipmentProperties.Remove(deletedEquipmentProperties);
                            }
                        }

                        context.Items.Update(itemDescriptor);

                        break;
                    }
                    case GameObjectType.Npc:
                        context.Npcs.Update((NPCDescriptor)gameObject);

                        break;
                    case GameObjectType.Projectile:
                        context.Projectiles.Update((ProjectileDescriptor)gameObject);

                        break;
                    case GameObjectType.Quest:

                        if (((QuestDescriptor)gameObject).StartEvent != null)
                        {
                            context.Events.Update(((QuestDescriptor)gameObject).StartEvent);
                        }

                        if (((QuestDescriptor)gameObject).EndEvent != null)
                        {
                            context.Events.Update(((QuestDescriptor)gameObject).EndEvent);
                        }

                        foreach (var tsk in ((QuestDescriptor)gameObject).Tasks)
                        {
                            if (tsk.CompletionEvent != null)
                            {
                                context.Events.Update(tsk.CompletionEvent);
                            }
                        }

                        context.Quests.Update((QuestDescriptor)gameObject);

                        break;
                    case GameObjectType.Resource:
                        context.Resources.Update((ResourceDescriptor)gameObject);

                        break;
                    case GameObjectType.Shop:
                        context.Shops.Update((ShopDescriptor)gameObject);

                        break;
                    case GameObjectType.Spell:
                        context.Spells.Update((SpellDescriptor)gameObject);

                        break;
                    case GameObjectType.CraftTables:
                        context.CraftingTables.Update((CraftingTableDescriptor)gameObject);

                        break;
                    case GameObjectType.Crafts:
                        context.Crafts.Update((CraftingRecipeDescriptor)gameObject);

                        break;
                    case GameObjectType.Map:
                        context.Maps.Update((MapController)gameObject);

                        break;
                    case GameObjectType.Event:
                        context.Events.Update((EventDescriptor)gameObject);

                        break;
                    case GameObjectType.PlayerVariable:
                        context.PlayerVariables.Update((PlayerVariableDescriptor)gameObject);

                        break;
                    case GameObjectType.ServerVariable:
                        context.ServerVariables.Update((ServerVariableDescriptor)gameObject);

                        break;
                    case GameObjectType.Tileset:
                        context.Tilesets.Update((TilesetDescriptor)gameObject);

                        break;
                    case GameObjectType.Time:
                        break;
                    case GameObjectType.GuildVariable:
                        context.GuildVariables.Update((GuildVariableDescriptor)gameObject);

                        break;
                    case GameObjectType.UserVariable:
                        context.UserVariables.Update((UserVariableDescriptor)gameObject);

                        break;
                }

                context.ChangeTracker.DetectChanges();
                context.SaveChanges();
            }
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(
                exception,
                "Error saving {GameObjectType} {GameObjectId} '{GameObjectName}'",
                gameObject.Type,
                gameObject.Id,
                gameObject.Name
            );
            throw;
        }
    }

    //Post Loading Functions
    private static void OnMapsLoaded()
    {
        if (MapDescriptor.Lookup.Count == 0)
        {
            Console.WriteLine(Strings.Database.NoMaps);
            AddGameObject(GameObjectType.Map);
        }

        GenerateMapGrids();
        LoadMapFolders();
        CheckAllMapConnections();

        foreach (var map in MapController.Lookup)
        {
            ((MapController)map.Value).Initialize();
        }
    }

    private static void OnClassesLoaded()
    {
        if (ClassDescriptor.Lookup.Count == 0)
        {
            Console.WriteLine(Strings.Database.NoClasses);
            var cls = (ClassDescriptor)AddGameObject(GameObjectType.Class);
            cls.Name = Strings.Database.Default;
            var defaultMale = new ClassSprite()
            {
                Sprite = "Base_Male.png",
                Gender = Gender.Male
            };

            var defaultFemale = new ClassSprite()
            {
                Sprite = "Base_Female.png",
                Gender = Gender.Female
            };

            cls.Sprites.Add(defaultMale);
            cls.Sprites.Add(defaultFemale);
            for (var i = 0; i < Enum.GetValues<Vital>().Length; i++)
            {
                cls.BaseVital[i] = 20;
            }

            for (var i = 0; i < Enum.GetValues<Stat>().Length; i++)
            {
                cls.BaseStat[i] = 20;
            }
            SaveGameObject(cls);
        }
    }

    public static void CachePlayerVariableEventTextLookups()
    {
        var lookup = new Dictionary<string, PlayerVariableDescriptor>();
        var addedIds = new HashSet<string>();
        foreach (PlayerVariableDescriptor variable in PlayerVariableDescriptor.Lookup.Values)
        {
            if (!string.IsNullOrWhiteSpace(variable.TextId) && !addedIds.Contains(variable.TextId))
            {
                lookup.Add(Strings.Events.PlayerVariable + "{" + variable.TextId + "}", variable);
                lookup.Add(Strings.Events.PlayerSwitch + "{" + variable.TextId + "}", variable);
                addedIds.Add(variable.TextId);
            }
        }
        PlayerVariableEventTextLookup = lookup;
    }

    public static void CacheServerVariableEventTextLookups()
    {
        var lookup = new Dictionary<string, ServerVariableDescriptor>();
        var addedIds = new HashSet<string>();
        foreach (ServerVariableDescriptor variable in ServerVariableDescriptor.Lookup.Values)
        {
            if (!string.IsNullOrWhiteSpace(variable.TextId) && !addedIds.Contains(variable.TextId))
            {
                lookup.Add(Strings.Events.GlobalVariable + "{" + variable.TextId + "}", variable);
                lookup.Add(Strings.Events.GlobalSwitch + "{" + variable.TextId + "}", variable);
                addedIds.Add(variable.TextId);
            }
        }
        ServerVariableEventTextLookup = lookup;
    }

    public static void CacheGuildVariableEventTextLookups()
    {
        var lookup = new Dictionary<string, GuildVariableDescriptor>();
        var addedIds = new HashSet<string>();
        foreach (GuildVariableDescriptor variable in GuildVariableDescriptor.Lookup.Values)
        {
            if (!string.IsNullOrWhiteSpace(variable.TextId) && !addedIds.Contains(variable.TextId))
            {
                lookup.Add(Strings.Events.GuildVariable + "{" + variable.TextId + "}", variable);
                addedIds.Add(variable.TextId);
            }
        }
        GuildVariableEventTextLookup = lookup;
    }

    public static void CacheUserVariableEventTextLookups()
    {
        var lookup = new Dictionary<string, UserVariableDescriptor>();
        var addedIds = new HashSet<string>();
        foreach (UserVariableDescriptor variable in UserVariableDescriptor.Lookup.Values)
        {
            if (!string.IsNullOrWhiteSpace(variable.TextId) && !addedIds.Contains(variable.TextId))
            {
                lookup.Add(Strings.Events.UserVariable + "{" + variable.TextId + "}", variable);
                addedIds.Add(variable.TextId);
            }
        }
        UserVariableEventTextLookup = lookup;
    }

    //Extra Map Helper Functions
    public static void CheckAllMapConnections()
    {
        var changed = false;
        foreach (MapController map in MapController.Lookup.Values)
        {
            CheckMapConnections(map, MapController.Lookup);
        }
    }

    public static bool CheckMapConnections(MapController map, DatabaseObjectLookup maps)
    {
        var updated = false;
        if (!maps.Keys.Contains(map.Up) && map.Up != Guid.Empty)
        {
            map.Up = Guid.Empty;
            updated = true;
        }

        if (!maps.Keys.Contains(map.Down) && map.Down != Guid.Empty)
        {
            map.Down = Guid.Empty;
            updated = true;
        }

        if (!maps.Keys.Contains(map.Left) && map.Left != Guid.Empty)
        {
            map.Left = Guid.Empty;
            updated = true;
        }

        if (!maps.Keys.Contains(map.Right) && map.Right != Guid.Empty)
        {
            map.Right = Guid.Empty;
            updated = true;
        }

        if (updated)
        {
            SaveGameObject(map);
            PacketSender.SendMapToEditors(map.Id);
            return true;
        }

        return false;
    }

    public static void GenerateMapGrids()
    {
        lock (mapGrids)
        {
            mapGrids.Clear();
            foreach (var map in MapController.Lookup.Values)
            {
                if (mapGrids.Count < 1)
                {
                    mapGrids.Add(new MapGrid(map.Id, 0));
                    continue;
                }

                for (var y = 0; y < mapGrids.Count; y++)
                {
                    if (!mapGrids[y].Contains(map.Id))
                    {
                        if (y != mapGrids.Count - 1)
                        {
                            continue;
                        }

                        mapGrids.Add(new MapGrid(map.Id, mapGrids.Count));
                    }

                    break;
                }
            }

            foreach (MapController map in MapController.Lookup.Values)
            {
                lock (map.GetMapLock())
                {
                    var gridIndex = map.MapGrid;
                    var grid = mapGrids[gridIndex];
                    var surroundingMapIds = new List<Guid>();
                    var surroundingMaps = new List<MapController>();
                    for (var x = map.MapGridX - 1; x <= map.MapGridX + 1; x++)
                    {
                        for (var y = map.MapGridY - 1; y <= map.MapGridY + 1; y++)
                        {
                            if (x == map.MapGridX && y == map.MapGridY)
                            {
                                continue;
                            }

                            if (x < grid.XMin || x >= grid.XMax || y < grid.YMin || y >= grid.YMax)
                            {
                                continue;
                            }

                            if (grid.MapIdGrid[x, y] == Guid.Empty)
                            {
                                continue;
                            }

                            var idFromGrid = grid.MapIdGrid[x, y];
                            surroundingMapIds.Add(idFromGrid);
                            if (MapController.TryGet(idFromGrid, out var mapOnGrid))
                            {
                                surroundingMaps.Add(mapOnGrid);
                            }
                        }
                    }
                    map.SurroundingMapIds = surroundingMapIds.ToArray();
                    map.SurroundingMaps = surroundingMaps.ToArray();
                }
            }

            for (var gridIndex = 0; gridIndex < mapGrids.Count; gridIndex++)
            {
                PacketSender.SendMapGridToAll(gridIndex);
            }
        }
    }

    public static MapGrid GetGrid(int index)
    {
        lock (mapGrids)
        {
            return mapGrids[index];
        }
    }

    public static bool GridsContain(Guid id)
    {
        lock (mapGrids)
        {
            return mapGrids.Any(mapGrid => mapGrid.Contains(id));
        }
    }

    //Map Folders
    private static void LoadMapFolders()
    {
        try
        {
            using (var context = CreateGameContext(readOnly: false))
            {
                var mapFolders = context.MapFolders.OrderBy(f => f.Id).FirstOrDefault();
                if (mapFolders == null)
                {
                    context.MapFolders.Add(MapList.List);
                    context.ChangeTracker.DetectChanges();
                    context.SaveChanges();
                }
                else
                {
                    MapList.List = mapFolders;
                }
            }
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(exception, "Error loading map folders");
            throw;
        }

        foreach (var map in MapDescriptor.Lookup)
        {
            if (MapList.List.FindMap(map.Value.Id) == null)
            {
                MapList.List.AddMap(map.Value.Id, map.Value.TimeCreated, MapDescriptor.Lookup);
            }
        }

        MapList.List.PostLoad(MapDescriptor.Lookup, true, true);
        PacketSender.SendMapListToAll();
    }

    public static void SaveMapList()
    {
        try
        {
            using (var context = CreateGameContext(readOnly: false))
            {
                context.MapFolders.Update(MapList.List);
                context.ChangeTracker.DetectChanges();
                context.SaveChanges();
            }
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(exception, "Error saving map list");
            throw;
        }
    }

    //Time
    private static void LoadTime()
    {
        try
        {
            using (var context = CreateGameContext(readOnly: false))
            {
                var time = context.Time.OrderBy(t => t.Id).FirstOrDefault();
                if (time == null)
                {
                    context.Time.Add(DaylightCycleDescriptor.Instance);
                    context.ChangeTracker.DetectChanges();
                    context.SaveChanges();
                }
                else
                {
                    DaylightCycleDescriptor.Instance = time;
                }
            }
            Time.Init();
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(exception, "Error loading time objects");
            throw;
        }
    }

    public static void SaveTime()
    {
        try
        {
            using (var context = CreateGameContext(readOnly: false))
            {
                context.Time.Update(DaylightCycleDescriptor.Instance);
                context.ChangeTracker.DetectChanges();
                context.SaveChanges();
            }
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(exception, "Error saving time objects");
            throw;
        }
    }

    public static void SaveUpdatedServerVariables()
    {
        if (UpdatedServerVariables.Count > 0)
        {
            using (var context = CreateGameContext(readOnly: false))
            {
                foreach (var variable in UpdatedServerVariables)
                {
                    var serverVar = variable.Value;
                    if (serverVar != null)
                    {
                        context.ServerVariables.Update(variable.Value);
                    }
                    UpdatedServerVariables.TryRemove(variable.Key, out ServerVariableDescriptor obj);
                }
                context.SaveChanges();
            }
        }
    }

    public static void HandleMigrationCommand()
    {
        var databases = new List<(Type, DatabaseOptions, string)>
        {
            (typeof(GameContext), Options.Instance.GameDatabase, Strings.Migration.GameDatabaseName),
            (typeof(PlayerContext), Options.Instance.PlayerDatabase, Strings.Migration.PlayerDatabaseName),
            (typeof(LoggingContext), Options.Instance.LoggingDatabase, Strings.Migration.LoggingDatabaseName)
        };

        Console.WriteLine();
        Console.WriteLine(Strings.Migration.SelectContext);
        Console.WriteLine();

        for (var databaseIndex = 0; databaseIndex < databases.Count; databaseIndex++)
        {
            var (contextType, options, databaseName) = databases[databaseIndex];
            var selectionNumber = databaseIndex + 1;
            var databaseTypeName = options.Type.GetName();
            Console.WriteLine(Strings.Migration.SelectDatabase.ToString(
                selectionNumber,
                databaseName,
                databaseTypeName,
                contextType == typeof(GameContext) ? Strings.Migration.SqliteRecommended : string.Empty
            ));
        }

        Console.WriteLine();
        Console.WriteLine(Strings.Migration.Cancel);

        // TODO: Remove > when moving to ReadKeyWait when console magic is ready
        Console.Write("> ");
        var input = Console.ReadLine();
        Console.WriteLine();

        if (!int.TryParse(input, out var selectedDatabaseIndex))
        {
            Console.WriteLine(Strings.Migration.MigrationCanceled);
            return;
        }

        if (selectedDatabaseIndex < 1 || selectedDatabaseIndex > databases.Count)
        {
            Console.WriteLine(Strings.Migration.MigrationCanceled);
            return;
        }

        var (selectedContextType, selectedOptions, selectedDatabaseName) = databases[selectedDatabaseIndex - 1];

        var databaseTypes = new List<DatabaseType> { DatabaseType.Sqlite, DatabaseType.MySql };

        Console.WriteLine();
        Console.WriteLine(Strings.Migration.SelectProvider.ToString(selectedDatabaseName));
        var databaseTypeIndex = 1;
        foreach (var databaseType in databaseTypes)
        {
            Console.WriteLine(
                Strings.Migration.SelectDatabaseType.ToString(databaseTypeIndex, databaseType.GetName()));
            ++databaseTypeIndex;
        }

        Console.WriteLine();
        Console.WriteLine(Strings.Migration.Cancel);

        // TODO: Remove > when moving to ReadKeyWait when console magic is ready
        Console.Write("> ");
        input = Console.ReadLine();
        Console.WriteLine();

        if (!int.TryParse(input, out var selectedDatabaseTypeIndex))
        {
            Console.WriteLine(Strings.Migration.MigrationCanceled);
            return;
        }

        if (selectedDatabaseTypeIndex < 1 || selectedDatabaseTypeIndex > databaseTypes.Count)
        {
            Console.WriteLine(Strings.Migration.MigrationCanceled);
            return;
        }

        var selectedDatabaseType = databaseTypes[selectedDatabaseTypeIndex - 1];
        if (selectedDatabaseType == selectedOptions.Type)
        {
            Console.WriteLine();
            Console.WriteLine(
                Strings.Migration.AlreadyUsingProvider.ToString(selectedDatabaseName,
                    selectedDatabaseType.GetName()));
            Console.WriteLine(Strings.Migration.MigrationCanceled);
            return;
        }

        try
        {
            Task task;
            if (selectedContextType == typeof(GameContext))
            {
                task = Migrate<GameContext>(selectedOptions, selectedDatabaseType);
            }
            else if (selectedContextType == typeof(PlayerContext))
            {
                task = Migrate<PlayerContext>(selectedOptions, selectedDatabaseType);
            }
            else if (selectedContextType == typeof(LoggingContext))
            {
                task = Migrate<LoggingContext>(selectedOptions, selectedDatabaseType);
            }
            else
            {
                throw new InvalidOperationException();
            }

            task.Wait();
        }
        catch (Exception exception)
        {
            ApplicationContext.Context.Value?.Logger.LogError(
                exception,
                "Error processing migration for {SelectedContextType}",
                selectedContextType
            );
            throw;
        }
    }

    public static async Task Migrate<TContext>(DatabaseOptions fromDatabaseOptions, DatabaseType toDatabaseType)
        where TContext : IntersectDbContext<TContext>
    {
        string sqliteFileName;
        if (typeof(TContext) == typeof(GameContext))
        {
            sqliteFileName = GameDbFilename;
        }
        else if (typeof(TContext) == typeof(LoggingContext))
        {
            sqliteFileName = LoggingDbFilename;
        }
        else if (typeof(TContext) == typeof(PlayerContext))
        {
            sqliteFileName = PlayersDbFilename;
        }
        else
        {
            throw new InvalidOperationException();
        }

        var fromContextOptions = new DatabaseContextOptions
        {
            AutoDetectChanges = false,
            ConnectionStringBuilder =
                fromDatabaseOptions.Type.CreateConnectionStringBuilder(fromDatabaseOptions, sqliteFileName),
            DatabaseType = fromDatabaseOptions.Type,
            ExplicitLoad = false,
            LazyLoading = false,
            LoggerFactory = CreateLoggerFactory<TContext>(fromDatabaseOptions),
            QueryTrackingBehavior = default,
            ReadOnly = false,
        };

        DatabaseOptions toDatabaseOptions;
        DatabaseContextOptions toContextOptions;
        switch (toDatabaseType)
        {
            case DatabaseType.MySql:
                {
                    while (true)
                    {
                        Console.WriteLine(Strings.Migration.EnterConnectionStringParameters);

                        Console.Write(Strings.Migration.PromptHost.ToString(Strings.Migration.DefaultHost));
                        var host = Console.ReadLine()?.Trim();
                        if (string.IsNullOrWhiteSpace(host))
                        {
                            host = Strings.Migration.DefaultHost;
                        }

                        Console.Write(Strings.Migration.PromptPort.ToString(Strings.Migration.DefaultPortMySql));
                        var portString = Console.ReadLine()?.Trim();
                        if (string.IsNullOrWhiteSpace(portString))
                        {
                            portString = Strings.Migration.DefaultPortMySql;
                        }
                        var port = ushort.Parse(portString);

                        var contextName = typeof(TContext).Name.Replace("Context", "").ToLowerInvariant();
                        var version = typeof(DbInterface).Assembly.GetVersionName();
                        var defaultDatabase = Strings.Migration.DefaultDatabase.ToString(version, contextName);
                        Console.Write(Strings.Migration.PromptDatabase.ToString(defaultDatabase));
                        var database = Console.ReadLine()?.Trim();
                        if (string.IsNullOrWhiteSpace(database))
                        {
                            database = defaultDatabase;
                        }

                        Console.Write(Strings.Migration.PromptUsername.ToString(Strings.Migration.DefaultUsername));
                        var username = Console.ReadLine().Trim();
                        if (string.IsNullOrWhiteSpace(username))
                        {
                            username = Strings.Migration.DefaultUsername;
                        }

                        Console.Write(Strings.Migration.PromptPassword);
                        var password = GetPassword();

                        Console.WriteLine();
                        Console.WriteLine(Strings.Migration.MySqlConnecting);

                        toDatabaseOptions = new()
                        {
                            Type = toDatabaseType,
                            Server = host,
                            Port = port,
                            Database = database,
                            Username = username,
                            Password = password,
                            LogLevel = fromDatabaseOptions.LogLevel,
                        };
                        toContextOptions = new()
                        {
                            ConnectionStringBuilder = toDatabaseType.CreateConnectionStringBuilder(
                                toDatabaseOptions,
                                default
                            ),
                            DatabaseType = toDatabaseType,
                            LoggerFactory = CreateLoggerFactory<TContext>(toDatabaseOptions),
                        };

                        try
                        {
                            await using var testContext = IntersectDbContext<TContext>.Create(toContextOptions);
                            break;
                        }
                        catch (Exception exception)
                        {
                            ApplicationContext.Context.Value?.Logger.LogError(Strings.Migration.MySqlConnectionError.ToString(exception));
                            Console.WriteLine();
                            Console.WriteLine(Strings.Migration.MySqlTryAgain);
                            var input = Console.ReadLine();
                            var key = input.Length > 0 ? input[0] : ' ';
                            Console.WriteLine();

                            var shouldTryAgain = string.Equals(
                                Strings.Migration.TryAgainCharacter,
                                key.ToString(),
                                StringComparison.Ordinal
                            );

                            if (shouldTryAgain)
                            {
                                continue;
                            }

                            ApplicationContext.Context.Value?.Logger.LogInformation(Strings.Migration.MigrationCanceled);
                            return;
                        }
                    }

                    break;
                }

            case DatabaseType.Sqlite:
                {
                    string dbFileName;
                    if (typeof(TContext).Extends<GameContext>())
                    {
                        dbFileName = GameDbFilename;
                    }
                    else if (typeof(TContext).Extends<PlayerContext>())
                    {
                        dbFileName = PlayersDbFilename;
                    }
                    else if (typeof(TContext).Extends<LoggingContext>())
                    {
                        dbFileName = LoggingDbFilename;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unsupported context type: {typeof(TContext).FullName}");
                    }

                    // Check if target SQLite file exists
                    if (File.Exists(dbFileName))
                    {
                        // If it does, check if it is OK to overwrite
                        Console.WriteLine();
                        ApplicationContext.Context.Value?.Logger.LogError(Strings.Migration.DatabaseFileAlreadyExists.ToString(dbFileName));
                        var input = Console.ReadLine();
                        var key = input.Length > 0 ? input[0] : ' ';
                        Console.WriteLine();
                        if (key.ToString() != Strings.Migration.ConfirmCharacter)
                        {
                            ApplicationContext.Context.Value?.Logger.LogInformation(Strings.Migration.MigrationCanceled);
                            return;
                        }

                        File.Delete(dbFileName);
                    }

                    toDatabaseOptions = new()
                    {
                        LogLevel = fromDatabaseOptions.LogLevel,
                        Type = toDatabaseType,
                    };
                    toContextOptions = new()
                    {
                        ConnectionStringBuilder = toDatabaseType.CreateConnectionStringBuilder(
                            toDatabaseOptions,
                            dbFileName
                        ),
                        DatabaseType = toDatabaseType,
                        LoggerFactory = CreateLoggerFactory<TContext>(toDatabaseOptions),
                    };

                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(toDatabaseType), toDatabaseType, null);
        }

        // Shut down server, start migration.
        ApplicationContext.Context.Value?.Logger.LogInformation(Strings.Migration.StoppingServer);

        //This variable will end the server loop and save any pending changes
        ServerContext.Instance.DisposeWithoutExiting = true;
        ServerContext.Instance.RequestShutdown();

        while (ServerContext.Instance.IsRunning)
        {
            Thread.Sleep(100);
        }

        ApplicationContext.Context.Value?.Logger.LogInformation(Strings.Migration.StartingMigration);
        var migrationService = new DatabaseTypeMigrationService();
        if (await migrationService.TryMigrate<TContext>(fromContextOptions, toContextOptions))
        {
            if (typeof(TContext).Extends<GameContext>())
            {
                Options.Instance.GameDatabase = toDatabaseOptions;
            }
            else if (typeof(TContext).Extends<PlayerContext>())
            {
                Options.Instance.PlayerDatabase = toDatabaseOptions;
            }
            else if (typeof(TContext).Extends<LoggingContext>())
            {
                Options.Instance.LoggingDatabase = toDatabaseOptions;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported context type: {typeof(TContext).FullName}");
            }

            Options.SaveToDisk();

            ApplicationContext.Context.Value?.Logger.LogInformation(Strings.Migration.MigrationComplete);
            Bootstrapper.Context.WaitForConsole();
            ServerContext.Exit(0);
        }
        else
        {
            ApplicationContext.Context.Value?.Logger.LogError($"Error migrating context type: {typeof(TContext).FullName}");
            ServerContext.Exit(1);
        }
    }

    private static void MigrateDbSet<T>(DbSet<T> oldDbSet, DbSet<T> newDbSet) where T : class
    {
        foreach (var itm in oldDbSet)
        {
            newDbSet.Add(itm);
        }
    }

    //Code taken from Stackoverflow on 9/20/2018
    //Answer by Dai and Damian Leszczyński - Vash
    //https://stackoverflow.com/questions/3404421/password-masking-console-application
    public static string GetPassword()
    {
        var pwd = string.Empty;
        while (true)
        {
            var i = Console.ReadKey(true);
            if (i.Key == ConsoleKey.Enter)
            {
                break;
            }
            else if (i.Key == ConsoleKey.Backspace)
            {
                if (pwd.Length > 1)
                {
                    pwd = pwd.Remove(pwd.Length - 2, 1);
                    Console.Write("\b \b");
                }
            }
            else if (i.KeyChar != '\u0000'
            ) // KeyChar == '\u0000' if the key pressed does not correspond to a printable character, e.g. F1, Pause-Break, etc
            {
                pwd = pwd + i.KeyChar;
                Console.Write("*");
            }
        }

        return pwd;
    }
}
