﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Common;
using EventStore.ClientAPI.Exceptions;
using EventStore.ClientAPI.Projections;
using Newtonsoft.Json;
using NServiceBus;
using NServiceBus.Extensibility;
using NServiceBus.Logging;
using NServiceBus.MessageInterfaces;
using NServiceBus.Pipeline;
using NServiceBus.Transport;
using NServiceBus.Unicast;
using MessageContext = NServiceBus.Transport.MessageContext;

namespace Aggregates.Internal
{
    internal class EventSubscriber : IEventSubscriber
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(EventSubscriber));
        private static readonly TransportTransaction transportTranaction = new TransportTransaction();
        private static readonly ContextBag contextBag = new ContextBag();
        
        private string _endpoint;
        private int _readsize;

        private SemaphoreSlim _concurrencyLimit;

        private readonly MessageHandlerRegistry _registry;
        private readonly IEventStoreConnection _connection;
        private readonly JsonSerializerSettings _settings;
        
        private EventStorePersistentSubscriptionBase _subscription;

        private bool _disposed;

        public bool ProcessingLive { get; set; }
        public Action<string, Exception> Dropped { get; set; }

        public EventSubscriber(MessageHandlerRegistry registry,
            IEventStoreConnection connection, IMessageMapper mapper)
        {
            _registry = registry;
            _connection = connection;
            _settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                Binder = new EventSerializationBinder(mapper),
                ContractResolver = new EventContractResolver(mapper)
            };

        }

        public async Task Setup(string endpoint, int readsize)
        {
            _endpoint = endpoint;
            _readsize = readsize;


            if (!_connection.Settings.GossipSeeds.Any())
                throw new ArgumentException(
                    "Eventstore connection settings does not contain gossip seeds (even if single host call SetGossipSeedEndPoints and SetClusterGossipPort)");

            var manager = new ProjectionsManager(_connection.Settings.Log,
                new IPEndPoint(_connection.Settings.GossipSeeds[0].EndPoint.Address, _connection.Settings.ExternalGossipPort), TimeSpan.FromSeconds(5));

            var discoveredEvents = _registry.GetMessageTypes().Where(x => typeof(IEvent).IsAssignableFrom(x)).ToList();

            var stream = $"{_endpoint}.{Assembly.GetEntryAssembly().GetName().Version}";

            // Link all events we are subscribing to to a stream
            var functions =
                discoveredEvents
                    .Select(eventType => $"'{eventType.AssemblyQualifiedName}': function(s,e) {{ linkTo('{stream}', e); }}")
                    .Aggregate((cur, next) => $"{cur},\n{next}");

            var definition = $"fromAll().when({{\n{functions}\n}})";

            try
            {
                var existing = await manager.GetQueryAsync(stream).ConfigureAwait(false);

                if (existing != definition)
                {
                    Logger.Fatal(
                        $"Projection [{stream}] already exists and is a different version!  If you've upgraded your code don't forget to bump your app's version!");
                    throw new EndpointVersionException(
                        $"Projection [{stream}] already exists and is a different version!  If you've upgraded your code don't forget to bump your app's version!");
                }
            }
            catch (ProjectionCommandFailedException)
            {
                try
                {
                    // Projection doesn't exist 
                    await
                        manager.CreateContinuousAsync(stream, definition, _connection.Settings.DefaultUserCredentials)
                            .ConfigureAwait(false);
                }
                catch (ProjectionCommandConflictException)
                {
                }
            }
        }

        public async Task Subscribe(CancellationToken cancelToken)
        {
            var stream = $"{_endpoint}.{Assembly.GetEntryAssembly().GetName().Version}";

            Logger.Write(LogLevel.Info, () => $"Endpoint [{_endpoint}] connecting to subscription group [{stream}]");

            try
            {
                var settings = PersistentSubscriptionSettings.Create()
                    .WithReadBatchOf(_readsize)
                    .WithLiveBufferSizeOf(_readsize * _readsize)
                    .CheckPointAfter(TimeSpan.FromSeconds(15))
                    .ResolveLinkTos()
                    .StartFromCurrent()
                    .WithNamedConsumerStrategy(SystemConsumerStrategies.Pinned)
                    .Build();
                await
                    _connection.CreatePersistentSubscriptionAsync(stream, stream, settings,
                        _connection.Settings.DefaultUserCredentials).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            _subscription = await _connection.ConnectToPersistentSubscriptionAsync(stream, stream, (subscription, e) =>
            {

                if (Bus.OnMessage == null || Bus.OnError == null)
                {
                    Logger.Warn($"Could not find NSBs onMessage handler yet - if this persists there is a problem.");
                    subscription.Stop(TimeSpan.FromSeconds(30));
                }

                if (_concurrencyLimit == null)
                    _concurrencyLimit = new SemaphoreSlim(Bus.PushSettings.MaxConcurrency);

                var @event = e.Event;
                if (!@event.IsJson)
                {
                    subscription.Acknowledge(e);
                    return;
                }
                Logger.Write(LogLevel.Debug, () => $"Event appeared stream [{@event.EventStreamId}] number {@event.EventNumber}");


                var descriptor = @event.Metadata.Deserialize(_settings);

                if (cancelToken.IsCancellationRequested) return;

                _concurrencyLimit.Wait(cancelToken);

                Task.Run(async () =>
                {
                    var headers = new Dictionary<string, string>(descriptor.Headers)
                    {
                        [Headers.EnclosedMessageTypes] = @event.EventType,
                        [Headers.MessageId] = @event.EventId.ToString()
                    };

                    using (var tokenSource = new CancellationTokenSource())
                    {
                        var processed = false;
                        var errorHandled = false;
                        var numberOfDeliveryAttempts = 0;

                        while (!processed && !errorHandled)
                        {
                            try
                            {
                                var messageContext = new MessageContext(@event.EventId.ToString(), headers,
                                    @event.Data ?? new byte[0], transportTranaction, tokenSource, contextBag);
                                await Bus.OnMessage(messageContext).ConfigureAwait(false);
                                processed = true;
                            }
                            catch (Exception ex)
                            {
                                ++numberOfDeliveryAttempts;
                                var errorContext = new ErrorContext(ex, headers, @event.EventId.ToString(),
                                    @event.Data ?? new byte[0], transportTranaction, numberOfDeliveryAttempts);
                                errorHandled = await Bus.OnError(errorContext).ConfigureAwait(false) ==
                                               ErrorHandleResult.Handled;
                            }
                        }

                        //if (processed || errorHandled)
                        subscription.Acknowledge(e);
                        _concurrencyLimit.Release();
                    }
                }, cancelToken);

            }, subscriptionDropped: (_, reason, e) =>
            {
                Logger.Write(LogLevel.Warn, () => $"Subscription dropped for reason: {reason}.  Exception: {e?.Message ?? "UNKNOWN"}");
                ProcessingLive = false;
                Dropped?.Invoke(reason.ToString(), e);
            }, autoAck: false).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _concurrencyLimit.Dispose();
            _subscription.Stop(TimeSpan.FromSeconds(5));
        }
    }
}