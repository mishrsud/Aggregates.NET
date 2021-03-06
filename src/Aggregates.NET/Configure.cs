﻿using Aggregates.Contracts;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using Aggregates.Internal;
using Aggregates.Messages;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Aggregates
{
    public class Configuration
    {
        public static Configure Settings { get; internal set; }

        public static async Task Build(Action<Configure> settings)
        {
            var config = new Configure();
            settings(config);

            if (config.Container == null)
                throw new ArgumentException("Must designate a container implementation");

            Settings = config;

            try
            {
                await config.RegistrationTasks.WhenAllAsync(x => x(config)).ConfigureAwait(false);
                await config.SetupTasks.WhenAllAsync(x => x(config)).ConfigureAwait(false);
            }
            catch
            {
                Settings = null;
                throw;
            }
        }
    }


    public class Configure
    {

        // Log settings
        public TimeSpan? SlowAlertThreshold { get; private set; }
        public bool ExtraStats { get; private set; }

        // Data settings
        public StreamIdGenerator Generator { get; private set; }
        public int ReadSize { get; private set; }
        public Compression Compression { get; private set; }

        // Messaging settings
        public string Endpoint { get; private set; }
        public string UniqueAddress { get; private set; }
        public int Retries { get; private set; }
        public int ParallelMessages { get; private set; }
        public int ParallelEvents { get; private set; }
        public int MaxConflictResolves { get; private set; }

        // Delayed cache settings
        public int FlushSize { get; private set; }
        public TimeSpan FlushInterval { get; private set; }
        public TimeSpan DelayedExpiration { get; private set; }
        public int MaxDelayed { get; private set; }

        public bool Passive { get; private set; }

        internal List<Func<Configure, Task>> RegistrationTasks;
        internal List<Func<Configure, Task>> SetupTasks;
        internal List<Func<Configure, Task>> StartupTasks;
        internal List<Func<Configure, Task>> ShutdownTasks;
        internal IContainer Container;

        public static Configure Start()
        {
            return new Configure();
        }

        public Configure()
        {
            RegistrationTasks = new List<Func<Configure, Task>>();
            SetupTasks = new List<Func<Configure, Task>>();
            StartupTasks = new List<Func<Configure, Task>>();
            ShutdownTasks = new List<Func<Configure, Task>>();

            Endpoint = "demo";
            // Set sane defaults
            Generator = new StreamIdGenerator((type, streamType, bucket, stream, parents) => $"{streamType}-{bucket}-[{parents.BuildParentsString()}]-{type.FullName.Replace(".", "")}-{stream}");
            ReadSize = 100;
            Compression = Compression.None;
            UniqueAddress = Guid.NewGuid().ToString("N");
            Retries = 10;
            ParallelMessages = 10;
            ParallelEvents = 10;
            MaxConflictResolves = 3;
            FlushSize = 500;
            FlushInterval = TimeSpan.FromMinutes(1);
            DelayedExpiration = TimeSpan.FromMinutes(5);
            MaxDelayed = 5000;

            RegistrationTasks.Add((c) =>
            {
                var container = c.Container;

                container.Register<IDelayedChannel, DelayedChannel>(Lifestyle.UnitOfWork);
                container.Register<IDomainUnitOfWork, UnitOfWork>(Lifestyle.UnitOfWork);

                container.Register<IRepositoryFactory, RepositoryFactory>(Lifestyle.PerInstance);
                container.Register<IProcessor, Processor>(Lifestyle.PerInstance);
                container.Register<IStoreSnapshots>((factory) => new StoreSnapshots(factory.Resolve<IMetrics>(), factory.Resolve<IStoreEvents>(), factory.Resolve<ISnapshotReader>(), c.Generator), Lifestyle.PerInstance);
                container.Register<IStorePocos>((factory) => new StorePocos(factory.Resolve<IStoreEvents>(), factory.Resolve<ICache>(), factory.Resolve<IMessageSerializer>(), true, c.Generator), Lifestyle.PerInstance);
                container.Register<IOobWriter>((factory) => new OobWriter(factory.Resolve<IMessageDispatcher>(), factory.Resolve<IStoreEvents>(), c.Generator), Lifestyle.PerInstance);
                container.Register<ISnapshotReader, SnapshotReader>(Lifestyle.PerInstance);

                container.Register<ICache, IntelligentCache>(Lifestyle.Singleton);
                container.Register<IMetrics, NullMetrics>(Lifestyle.Singleton);
                container.Register<IDelayedCache>((factory) => new DelayedCache(factory.Resolve<IMetrics>(), factory.Resolve<IStoreEvents>(), c.FlushInterval, c.Endpoint, c.MaxDelayed, c.FlushSize, c.DelayedExpiration, c.Generator), Lifestyle.Singleton);

                container.Register<IEventSubscriber>((factory) => new EventSubscriber(factory.Resolve<IMetrics>(), factory.Resolve<IMessaging>(), factory.Resolve<IEventStoreConsumer>(), c.ParallelEvents), Lifestyle.Singleton, "eventsubscriber");
                container.Register<IEventSubscriber>((factory) => new DelayedSubscriber(factory.Resolve<IMetrics>(), factory.Resolve<IEventStoreConsumer>(), factory.Resolve<IMessageDispatcher>(), c.Retries), Lifestyle.Singleton, "delayedsubscriber");
                container.Register<IEventSubscriber>((factory) => (IEventSubscriber)factory.Resolve<ISnapshotReader>(), Lifestyle.Singleton, "snapshotreader");

                container.Register<Func<Exception, string, Error>>((factory) =>
                {
                    var eventFactory = factory.Resolve<IEventFactory>();
                    return (exception, message) =>
                    {
                        return eventFactory.Create<Error>(e =>
                        {
                            e.Message = $"{message} - {exception.GetType().Name}: {exception.Message}";
                            e.Trace = exception.AsString();
                        });
                    };
                }, Lifestyle.Singleton);

                container.Register<Func<Accept>>((factory) =>
                {
                    var eventFactory = factory.Resolve<IEventFactory>();
                    return () => eventFactory.Create<Accept>(x => { });
                }, Lifestyle.Singleton);

                container.Register<Func<string, Reject>>((factory) =>
                {
                    var eventFactory = factory.Resolve<IEventFactory>();
                    return message => { return eventFactory.Create<Reject>(e => { e.Message = message; }); };
                }, Lifestyle.Singleton);
                container.Register<Func<BusinessException, Reject>>((factory) =>
                {
                    var eventFactory = factory.Resolve<IEventFactory>();
                    return exception =>
                    {
                        return eventFactory.Create<Reject>(e =>
                        {
                            e.Exception = exception;
                            e.Message = $"{exception.GetType().Name} - {exception.Message}";
                        });
                    };
                }, Lifestyle.Singleton);

                return Task.CompletedTask;
            });

        }
        public Configure SetEndpointName(string endpoint)
        {
            Endpoint = endpoint;
            return this;
        }
        public Configure SetSlowAlertThreshold(TimeSpan? threshold)
        {
            SlowAlertThreshold = threshold;
            return this;
        }
        public Configure SetExtraStats(bool extra)
        {
            ExtraStats = extra;
            return this;
        }
        public Configure SetStreamIdGenerator(StreamIdGenerator generator)
        {
            Generator = generator;
            return this;
        }
        public Configure SetReadSize(int readsize)
        {
            ReadSize = readsize;
            return this;
        }
        public Configure SetCompression(Compression compression)
        {
            Compression = compression;
            return this;
        }
        public Configure SetUniqueAddress(string address)
        {
            UniqueAddress = address;
            return this;
        }
        public Configure SetRetries(int retries)
        {
            Retries = retries;
            return this;
        }
        public Configure SetParallelMessages(int parallel)
        {
            ParallelMessages = parallel;
            return this;
        }
        public Configure SetParallelEvents(int parallel)
        {
            ParallelEvents = parallel;
            return this;
        }
        public Configure SetMaxConflictResolves(int attempts)
        {
            MaxConflictResolves = attempts;
            return this;
        }
        public Configure SetFlushSize(int size)
        {
            FlushSize = size;
            return this;
        }
        public Configure SetFlushInterval(TimeSpan interval)
        {
            FlushInterval = interval;
            return this;
        }
        public Configure SetDelayedExpiration(TimeSpan expiration)
        {
            DelayedExpiration = expiration;
            return this;
        }
        public Configure SetMaxDelayed(int max)
        {
            MaxDelayed = max;
            return this;
        }
        /// <summary>
        /// Passive means the endpoint doesn't need a unit of work, it won't process events or commands
        /// </summary>
        /// <returns></returns>
        public Configure SetPassive()
        {
            Passive = true;
            return this;
        }

        public Configure AddMetrics<TImplementation>() where TImplementation : class, IMetrics
        {
            RegistrationTasks.Add((c) =>
            {
                c.Container.Register<IMetrics, TImplementation>(Lifestyle.Singleton);
                return Task.CompletedTask;
            });
            return this;
        }
    }
}
