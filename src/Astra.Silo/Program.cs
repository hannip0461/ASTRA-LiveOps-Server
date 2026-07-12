using Astra.Domain;
using Astra.Infrastructure;
using Astra.Infrastructure.Postgres;
using Astra.Infrastructure.Orleans;
using Astra.Infrastructure.Telemetry;
using Microsoft.Extensions.Options;
using Astra.Silo;
using Orleans.Hosting;
using Orleans.Serialization;
using StackExchange.Redis;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var storeProvider = context.Configuration.GetValue("Astra:StoreProvider", "InMemory");
        services.AddAstraOpenTelemetry(
            context.Configuration,
            "Astra.Silo",
            includePostgres: StringComparer.OrdinalIgnoreCase.Equals(storeProvider, "PostgreSQL"));
        services.Configure<ExceptionSerializationOptions>(
            options => options.SupportedNamespacePrefixes.Add("Astra.Domain"));
        services.AddSingleton<IActiveContentCache, InMemoryActiveContentCache>();
        if (StringComparer.OrdinalIgnoreCase.Equals(storeProvider, "PostgreSQL"))
        {
            var connectionString = context.Configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required when Astra:StoreProvider=PostgreSQL.");
            services.AddSingleton(_ => PostgresDataSourceFactory.Create(
                context.Configuration,
                connectionString,
                "Astra.Silo",
                defaultMaximumPoolSize: 24));
            services.AddSingleton<IPlayerAccountStore, PostgresPlayerAccountStore>();
            services.AddSingleton<IContentSnapshotStore, PostgresContentSnapshotStore>();
            services.AddSingleton<PostgresMailStore>();
            var redisConnectionString = context.Configuration.GetConnectionString("Redis");
            if (!string.IsNullOrWhiteSpace(redisConnectionString))
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
                redisOptions.AbortOnConnectFail = false;
                services.AddSingleton<IConnectionMultiplexer>(
                    _ => ConnectionMultiplexer.Connect(redisOptions));
            }

            services.AddSingleton<IMailStore>(serviceProvider =>
            {
                var inner = serviceProvider.GetRequiredService<PostgresMailStore>();
                var redis = serviceProvider.GetService<IConnectionMultiplexer>();
                if (redis is null)
                {
                    return inner;
                }

                var options = serviceProvider.GetRequiredService<IOptions<RedisMailCacheOptions>>();
                return new RedisCachedMailStore(inner, redis, options);
            });
            services.AddSingleton<PostgresSchemaInitializer>();

            if (context.Configuration.GetValue("Astra:ApplyDatabaseSchema", false))
            {
                services.AddHostedService<PostgresSchemaHostedService>();
            }

            services.AddOptions<PostgresContentCacheOptions>()
                .Bind(context.Configuration.GetSection("Astra:ContentCache"))
                .Validate(
                    options => options.ReconciliationInterval > TimeSpan.Zero,
                    "Astra:ContentCache:ReconciliationInterval must be positive.")
                .Validate(
                    options => options.ReconnectDelay > TimeSpan.Zero,
                    "Astra:ContentCache:ReconnectDelay must be positive.")
                .ValidateOnStart();
            services.AddHostedService<PostgresContentCacheSynchronizer>();
        }
        else
        {
            services.AddSingleton<IPlayerAccountStore, InMemoryPlayerAccountStore>();
            services.AddSingleton<IContentSnapshotStore, InMemoryContentSnapshotStore>();
            services.AddSingleton<IMailStore, InMemoryMailStore>();
        }

        services.AddSingleton<IGachaRandomSource, CryptographicGachaRandomSource>();
        services.AddSingleton<PlayerAccountCommandProcessor>();
        services.Configure<RedisMailCacheOptions>(
            context.Configuration.GetSection("Astra:RedisMailCache"));
        services.AddSingleton<GachaCommandFactory>();
        services.AddSingleton<ContentValidationService>();
    })
    .UseOrleans((context, silo) =>
        silo.UseAstraClustering(context.Configuration))
    .Build();

host.Run();
