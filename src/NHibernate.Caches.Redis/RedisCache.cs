﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using NHibernate.Cache;
using NHibernate.Util;
using System.Net.Sockets;
using StackExchange.Redis;

namespace NHibernate.Caches.Redis
{
    public class RedisCache : ICache
    {
        private const string CacheNamePrefix = "NHibernate-Cache:";

        private static readonly IInternalLogger log = LoggerProvider.LoggerFor(typeof(RedisCache));

        private readonly Dictionary<object, string> acquiredLocks = new Dictionary<object, string>();
        private readonly ConnectionMultiplexer connectionMultiplexer;
        private readonly RedisCacheProviderOptions options;
        private readonly TimeSpan expiry;
        private readonly TimeSpan lockTimeout = TimeSpan.FromSeconds(30);

        private const int DefaultExpiry = 300 /*5 minutes*/;

        public string RegionName { get; private set; }
        internal RedisNamespace CacheNamespace { get; private set; }
        public int Timeout { get { return Timestamper.OneMs * 60000; } }

        public RedisCache(string regionName, ConnectionMultiplexer connectionMultiplexer, RedisCacheProviderOptions options)
            : this(regionName, new Dictionary<string, string>(), null, connectionMultiplexer, options)
        {

        }

        public RedisCache(string regionName, IDictionary<string, string> properties, RedisCacheElement element, ConnectionMultiplexer connectionMultiplexer, RedisCacheProviderOptions options)
        {
            this.connectionMultiplexer = connectionMultiplexer.ThrowIfNull("connectionMultiplexer");
            this.options = options.ThrowIfNull("options").Clone();

            RegionName = regionName.ThrowIfNull("regionName");

            expiry = element != null
                ? element.Expiration
                : TimeSpan.FromSeconds(PropertiesHelper.GetInt32(Cfg.Environment.CacheDefaultExpiration, properties, DefaultExpiry));

            log.DebugFormat("using expiration : {0} seconds", expiry.TotalSeconds);

            var regionPrefix = PropertiesHelper.GetString(Cfg.Environment.CacheRegionPrefix, properties, null);
            log.DebugFormat("using region prefix : {0}", regionPrefix);

            var namespacePrefix = CacheNamePrefix + RegionName;
            if (!String.IsNullOrWhiteSpace(regionPrefix))
            {
                namespacePrefix = regionPrefix + ":" + namespacePrefix;
            }

            CacheNamespace = new RedisNamespace(namespacePrefix);
            SyncGeneration();
        }

        public long NextTimestamp()
        {
            return Timestamper.Next();
        }

        protected void SyncGeneration()
        {
            try
            {
                if (CacheNamespace.GetGeneration() == -1)
                {
                    var latestGeneration = FetchGeneration();
                    CacheNamespace.SetGeneration(latestGeneration);
                }
            }
            catch (Exception e)
            {
                log.ErrorFormat("could not sync generation");

                var evtArg = new RedisCacheExceptionEventArgs(e);
                OnException(evtArg);
                if (evtArg.Throw) { throw; }
            }
        }

        private long FetchGeneration()
        {
            var db = connectionMultiplexer.GetDatabase();

            var generationKey = CacheNamespace.GetGenerationKey();
            var attemptedGeneration = db.StringGet(generationKey);

            if (!attemptedGeneration.HasValue)
            {
                var generation = db.StringIncrement(generationKey);
                log.DebugFormat("creating new generation : {0}", generation);
                return generation;
            }

            log.DebugFormat("using existing generation : {0}", attemptedGeneration);
            return Convert.ToInt64(attemptedGeneration);
        }

        public virtual void Put(object key, object value)
        {
            key.ThrowIfNull("key");
            value.ThrowIfNull("value");

            log.DebugFormat("put in cache : {0}", key);

            try
            {
                var data = Serialize(value);

                ExecuteEnsureGeneration(transaction =>
                {
                    var cacheKey = CacheNamespace.GlobalCacheKey(key);

                    transaction.StringSetAsync(cacheKey, data, expiry);
                    var globalKeysKey = CacheNamespace.GetGlobalKeysKey();

                    transaction.SetAddAsync(globalKeysKey, cacheKey);
                });
            }
            catch (Exception e)
            {
                log.ErrorFormat("could not put in cache : {0}", key);

                var evtArg = new RedisCacheExceptionEventArgs(e);
                OnException(evtArg);
                if (evtArg.Throw) { throw; }
            }
        }

        public virtual object Get(object key)
        {
            key.ThrowIfNull();

            log.DebugFormat("get from cache : {0}", key);

            try
            {
                Task<RedisValue> dataResult = null;

                ExecuteEnsureGeneration(transaction =>
                {
                    var cacheKey = CacheNamespace.GlobalCacheKey(key);
                    dataResult = transaction.StringGetAsync(cacheKey);
                });

                var data = dataResult.Result;

                return Deserialize(data);

            }
            catch (Exception e)
            {
                log.ErrorFormat("coult not get from cache : {0}", key);

                var evtArg = new RedisCacheExceptionEventArgs(e);
                OnException(evtArg);
                if (evtArg.Throw) { throw; }

                return null;
            }
        }

        public virtual void Remove(object key)
        {
            key.ThrowIfNull();

            log.DebugFormat("remove from cache : {0}", key);

            try
            {
                ExecuteEnsureGeneration(transaction =>
                {
                    var cacheKey = CacheNamespace.GlobalCacheKey(key);

                    transaction.KeyDeleteAsync(cacheKey, CommandFlags.FireAndForget);
                });
            }
            catch (Exception e)
            {
                log.ErrorFormat("could not remove from cache : {0}", key);

                var evtArg = new RedisCacheExceptionEventArgs(e);
                OnException(evtArg);
                if (evtArg.Throw) { throw; }
            }
        }

        public virtual void Clear()
        {
            var generationKey = CacheNamespace.GetGenerationKey();
            var globalKeysKey = CacheNamespace.GetGlobalKeysKey();

            log.DebugFormat("clear cache : {0}", generationKey);

            try
            {
                var db = connectionMultiplexer.GetDatabase();
                var transaction = db.CreateTransaction();

                var generationIncrement = transaction.StringIncrementAsync(generationKey);

                transaction.KeyDeleteAsync(globalKeysKey, CommandFlags.FireAndForget);

                transaction.Execute();

                CacheNamespace.SetGeneration(generationIncrement.Result);
            }
            catch (Exception e)
            {
                log.ErrorFormat("could not clear cache : {0}", generationKey);

                var evtArg = new RedisCacheExceptionEventArgs(e);
                OnException(evtArg);
                if (evtArg.Throw) { throw; }
            }
        }

        public virtual void Destroy()
        {
            // No-op since Redis is distributed.
            log.DebugFormat("destroying cache : {0}", CacheNamespace.GetGenerationKey());
        }

        public virtual void Lock(object key)
        {
            log.DebugFormat("acquiring cache lock : {0}", key);

            try
            {
                var globalKey = CacheNamespace.GlobalKey(key, RedisNamespace.NumTagsForLockKey);

                var db = connectionMultiplexer.GetDatabase();

                ExecExtensions.RetryUntilTrue(() =>
                {
                    var wasSet = db.StringSet(globalKey, "lock " + DateTime.UtcNow.ToUnixTime(), when: When.NotExists);

                    if (wasSet)
                        acquiredLocks[key] = globalKey;

                    return wasSet;
                }, lockTimeout);
            }
            catch (Exception e)
            {
                log.ErrorFormat("could not acquire cache lock : ", key);

                var evtArg = new RedisCacheExceptionEventArgs(e);
                OnException(evtArg);
                if (evtArg.Throw) { throw; }
            }
        }

        public virtual void Unlock(object key)
        {
            string globalKey;
            if (!acquiredLocks.TryGetValue(key, out globalKey)) { return; }

            log.DebugFormat("releasing cache lock : {0}", key);

            try
            {
                var db = connectionMultiplexer.GetDatabase();

                db.KeyDelete(globalKey);
            }
            catch (Exception e)
            {
                log.ErrorFormat("could not release cache lock : {0}", key);

                var evtArg = new RedisCacheExceptionEventArgs(e);
                OnException(evtArg);
                if (evtArg.Throw) { throw; }
            }
        }

        private void ExecuteEnsureGeneration(Action<StackExchange.Redis.ITransaction> action)
        {
            var db = connectionMultiplexer.GetDatabase();

            var executed = false;

            while (!executed)
            {
                var generation = db.StringGet(CacheNamespace.GetGenerationKey());
                var serverGeneration = Convert.ToInt64(generation);

                CacheNamespace.SetGeneration(serverGeneration);

                var transaction = db.CreateTransaction();

                // The generation on the server may have been removed.
                if (serverGeneration < CacheNamespace.GetGeneration())
                {
                    db.StringSetAsync(
                        key: CacheNamespace.GetGenerationKey(),
                        value: CacheNamespace.GetGeneration(), 
                        flags: CommandFlags.FireAndForget
                    );
                }

                transaction.AddCondition(Condition.StringEqual(CacheNamespace.GetGenerationKey(), CacheNamespace.GetGeneration()));

                action(transaction);

                executed = transaction.Execute();
            }
        }

        private RedisValue Serialize(object value)
        {
            if (options.Serializer == null)
            {
                throw new InvalidOperationException("A serializer was not configured on the RedisCacheProviderOptions.");
            }
            return options.Serializer.Serialize(value);
        }

        private object Deserialize(RedisValue value)
        {
            if (options.Serializer == null)
            {
                throw new InvalidOperationException("A serializer was not configured on the RedisCacheProviderOptions.");
            }
            return options.Serializer.Deserialize(value);
        }

        private void OnException(RedisCacheExceptionEventArgs e)
        {
            if (options.OnException == null)
            {
                var isSocketException = e.Exception is RedisConnectionException || e.Exception is SocketException || e.Exception.InnerException is SocketException;

                if (!isSocketException)
                {
                    e.Throw = true;
                }
            }
            else
            {
                options.OnException(e);
            }
        }
    }
}