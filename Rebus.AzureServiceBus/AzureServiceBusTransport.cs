﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Subscriptions;
using Rebus.Threading;
using Rebus.Transport;

#pragma warning disable 1998

namespace Rebus.AzureServiceBus
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses Azure Service Bus queues to send/receive messages.
    /// </summary>
    public class AzureServiceBusTransport : ITransport, IInitializable, IDisposable, ISubscriptionStorage
    {
        const string OutgoingMessagesKey = "azure-service-bus-transport";

        static readonly TimeSpan[] RetryWaitTimes =
        {
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        };

        readonly ConcurrentDictionary<string, TopicDescription> _topics = new ConcurrentDictionary<string, TopicDescription>(StringComparer.InvariantCultureIgnoreCase);
        readonly ConcurrentDictionary<string, TopicClient> _topicClients = new ConcurrentDictionary<string, TopicClient>(StringComparer.InvariantCultureIgnoreCase);
        readonly ConcurrentDictionary<string, QueueClient> _queueClients = new ConcurrentDictionary<string, QueueClient>(StringComparer.InvariantCultureIgnoreCase);
        readonly NamespaceManager _namespaceManager;
        readonly string _connectionString;
        readonly IRebusLoggerFactory _rebusLoggerFactory;
        readonly string _inputQueueAddress;
        readonly ILog _log;

        readonly TimeSpan _peekLockDuration = TimeSpan.FromMinutes(5);
        readonly AsyncBottleneck _bottleneck = new AsyncBottleneck(10);
        readonly Ignorant _ignorant = new Ignorant();

        readonly ConcurrentQueue<BrokeredMessage> _prefetchQueue = new ConcurrentQueue<BrokeredMessage>();

        bool _prefetchingEnabled;
        int _numberOfMessagesToPrefetch;
        bool _disposed;

        /// <summary>
        /// Constructs the transport, connecting to the service bus pointed to by the connection string.
        /// </summary>
        public AzureServiceBusTransport(string connectionString, string inputQueueAddress, IRebusLoggerFactory rebusLoggerFactory)
        {
            if (connectionString == null) throw new ArgumentNullException("connectionString");

            _namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            _connectionString = connectionString;
            _rebusLoggerFactory = rebusLoggerFactory;
            _log = rebusLoggerFactory.GetCurrentClassLogger();

            if (inputQueueAddress != null)
            {
                _inputQueueAddress = inputQueueAddress.ToLowerInvariant();
            }
        }

        public void Initialize()
        {
            if (_inputQueueAddress != null)
            {
                _log.Info("Initializing Azure Service Bus transport with queue '{0}'", _inputQueueAddress);
                CreateQueue(_inputQueueAddress);
                return;
            }

            _log.Info("Initializing one-way Azure Service Bus transport");
        }

        /// <summary>
        /// Purges the input queue by deleting it and creating it again
        /// </summary>
        public void PurgeInputQueue()
        {
            _log.Info("Purging queue '{0}'", _inputQueueAddress);
            _namespaceManager.DeleteQueue(_inputQueueAddress);

            CreateQueue(_inputQueueAddress);
        }

        /// <summary>
        /// Configures the transport to prefetch the specified number of messages into an in-mem queue for processing, disabling automatic peek lock renewal
        /// </summary>
        public void PrefetchMessages(int numberOfMessagesToPrefetch)
        {
            _prefetchingEnabled = true;
            _numberOfMessagesToPrefetch = numberOfMessagesToPrefetch;
        }

        /// <summary>
        /// Enables automatic peek lock renewal - only recommended if you truly need to handle messages for a very long time
        /// </summary>
        public bool AutomaticallyRenewPeekLock { get; set; }

        public void CreateQueue(string address)
        {
            if (_namespaceManager.QueueExists(address)) return;

            var queueDescription = new QueueDescription(address)
            {
                MaxSizeInMegabytes = 1024,
                MaxDeliveryCount = 100,
                LockDuration = _peekLockDuration,
                EnablePartitioning = PartitioningEnabled,
                UserMetadata = string.Format("Created by Rebus {0:yyyy-MM-dd} - {0:HH:mm:ss}", DateTime.Now)
            };

            try
            {
                _log.Info("Queue '{0}' does not exist - will create it now", address);
                _namespaceManager.CreateQueue(queueDescription);
                _log.Info("Created!");
            }
            catch (MessagingEntityAlreadyExistsException)
            {
                // fair enough...
                _log.Info("MessagingEntityAlreadyExistsException - carrying on");
            }
        }

        public bool PartitioningEnabled { get; set; }

        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            GetOutgoingMessages(context)
                .GetOrAdd(destinationAddress, _ => new ConcurrentQueue<TransportMessage>())
                .Enqueue(message);
        }

        static BrokeredMessage CreateBrokeredMessage(TransportMessage message)
        {
            var headers = message.Headers.Clone();
            var brokeredMessage = new BrokeredMessage(new MemoryStream(message.Body), true);

            string timeToBeReceivedStr;
            if (headers.TryGetValue(Headers.TimeToBeReceived, out timeToBeReceivedStr))
            {
                timeToBeReceivedStr = headers[Headers.TimeToBeReceived];
                var timeToBeReceived = TimeSpan.Parse(timeToBeReceivedStr);
                brokeredMessage.TimeToLive = timeToBeReceived;
            }

            string deferUntilTime;
            if (headers.TryGetValue(Headers.DeferredUntil, out deferUntilTime))
            {
                var deferUntilDateTimeOffset = deferUntilTime.ToDateTimeOffset();
                brokeredMessage.ScheduledEnqueueTimeUtc = deferUntilDateTimeOffset.UtcDateTime;
                headers.Remove(Headers.DeferredUntil);
            }

            string contentType;
            if (headers.TryGetValue(Headers.ContentType, out contentType))
            {
                brokeredMessage.ContentType = contentType;
            }

            string correlationId;
            if (headers.TryGetValue(Headers.CorrelationId, out correlationId))
            {
                brokeredMessage.CorrelationId = correlationId;
            }

            brokeredMessage.Label = message.GetMessageLabel();

            foreach (var kvp in headers)
            {
                brokeredMessage.Properties[kvp.Key] = PossiblyLimitLength(kvp.Value);
            }

            return brokeredMessage;
        }

        static string PossiblyLimitLength(string str)
        {
            const int maxLengthPrettySafe = 16300;

            if (str.Length < maxLengthPrettySafe) return str;

            return string.Format("{0} (... cut out because length exceeded {1} characters ...) {2}",
                str.Substring(0, 8000), 
                maxLengthPrettySafe,
                str.Substring(str.Length - 8000));
        }

        /// <summary>
        /// Should return a new <see cref="Retrier"/>, fully configured to correctly "accept" the right exceptions
        /// </summary>
        static Retrier GetRetrier()
        {
            return new Retrier(RetryWaitTimes)
                .On<MessagingException>(e => e.IsTransient)
                .On<MessagingCommunicationException>(e => e.IsTransient)
                .On<ServerBusyException>(e => e.IsTransient);
        }

        public async Task<TransportMessage> Receive(ITransactionContext context)
        {
            if (_inputQueueAddress == null)
            {
                throw new InvalidOperationException("This Azure Service Bus transport does not have an input queue, hence it is not possible to reveive anything");
            }

            using (await _bottleneck.Enter())
            {
                var brokeredMessage = await ReceiveBrokeredMessage();

                if (brokeredMessage == null) return null;

                var headers = brokeredMessage.Properties
                    .Where(kvp => kvp.Value is string)
                    .ToDictionary(kvp => kvp.Key, kvp => (string)kvp.Value);

                var messageId = headers.GetValueOrNull(Headers.MessageId);
                var leaseDuration = (brokeredMessage.LockedUntilUtc - DateTime.UtcNow);
                var lockRenewalInterval = TimeSpan.FromMinutes(0.8 * leaseDuration.TotalMinutes);

                var renewalTask = GetRenewalTaskOrFakeDisposable(messageId, brokeredMessage, lockRenewalInterval);

                context.OnAborted(() =>
                {
                    renewalTask.Dispose();

                    try
                    {
                        brokeredMessage.Abandon();
                    }
                    catch (Exception exception)
                    {
                        // if it fails, it'll be back on the queue anyway....
                        _log.Warn("Could not abandon message: {0}", exception);
                    }
                });

                context.OnCommitted(async () =>
                {
                    renewalTask.Dispose();
                });

                context.OnCompleted(async () =>
                {
                    await brokeredMessage.CompleteAsync();
                });

                context.OnDisposed(() =>
                {
                    renewalTask.Dispose();

                    brokeredMessage.Dispose();
                });

                using (var memoryStream = new MemoryStream())
                {
                    await brokeredMessage.GetBody<Stream>().CopyToAsync(memoryStream);
                    return new TransportMessage(headers, memoryStream.ToArray());
                }
            }
        }

        ConcurrentDictionary<string, ConcurrentQueue<TransportMessage>> GetOutgoingMessages(ITransactionContext context)
        {
            return context.GetOrAdd(OutgoingMessagesKey, () =>
            {
                var destinations = new ConcurrentDictionary<string, ConcurrentQueue<TransportMessage>>();

                context.OnCommitted(async () =>
                {
                    // send outgoing messages
                    foreach (var destinationAndMessages in destinations)
                    {
                        var destinationAddress = destinationAndMessages.Key;
                        var messages = destinationAndMessages.Value;

                        var sendTasks = messages
                            .Select(async message =>
                            {
                                await GetRetrier().Execute(async () =>
                                {
                                    using (var brokeredMessageToSend = CreateBrokeredMessage(message))
                                    {
                                        try
                                        {
                                            await Send(destinationAddress, brokeredMessageToSend);
                                        }
                                        catch (MessagingEntityNotFoundException exception)
                                        {
                                            throw new MessagingEntityNotFoundException(string.Format("Could not send to '{0}'!", destinationAddress), exception);
                                        }
                                    }
                                });
                            })
                            .ToArray();

                        await Task.WhenAll(sendTasks);
                    }
                });

                return destinations;
            });
        }

        async Task Send(string destinationAddress, BrokeredMessage brokeredMessageToSend)
        {
            if (destinationAddress.StartsWith("subscription/"))
            {
                var topic = destinationAddress.Substring(destinationAddress.IndexOf('/') + 1);

                await GetTopicClient(topic).SendAsync(brokeredMessageToSend);
            }
            else
            {
                await GetQueueClient(destinationAddress).SendAsync(brokeredMessageToSend);
            }
        }

        TopicClient GetTopicClient(string topic)
        {
            return _topicClients.GetOrAdd(topic, t =>
            {
                _log.Debug("Initializing new topic client for {0}", topic);

                var topicDescription = EnsureTopicExists(topic);

                var fromConnectionString = TopicClient.CreateFromConnectionString(_connectionString, topicDescription.Path);

                return fromConnectionString;
            });
        }


        IDisposable GetRenewalTaskOrFakeDisposable(string messageId, BrokeredMessage brokeredMessage, TimeSpan lockRenewalInterval)
        {
            if (!AutomaticallyRenewPeekLock)
            {
                return new FakeDisposable();
            }

            if (_prefetchingEnabled)
            {
                return new FakeDisposable();
            }

            var renewalTask = new AsyncTask(string.Format("RenewPeekLock-{0}", messageId),
                async () =>
                {
                    _log.Info("Renewing peek lock for message with ID {0}", messageId);

                    await GetRetrier().Execute(brokeredMessage.RenewLockAsync);
                },
                _rebusLoggerFactory,
                prettyInsignificant: true)
            {
                Interval = lockRenewalInterval
            };

            renewalTask.Start();

            return renewalTask;
        }

        class FakeDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        async Task<BrokeredMessage> ReceiveBrokeredMessage()
        {
            var queueAddress = _inputQueueAddress;

            if (_prefetchingEnabled)
            {
                BrokeredMessage nextMessage;

                if (_prefetchQueue.TryDequeue(out nextMessage))
                {
                    return nextMessage;
                }

                var client = GetQueueClient(queueAddress);

                var brokeredMessages = (await client.ReceiveBatchAsync(_numberOfMessagesToPrefetch, TimeSpan.FromSeconds(1))).ToList();

                _ignorant.Reset();

                if (!brokeredMessages.Any()) return null;

                foreach (var receivedMessage in brokeredMessages)
                {
                    _prefetchQueue.Enqueue(receivedMessage);
                }

                _prefetchQueue.TryDequeue(out nextMessage);

                return nextMessage; //< just accept null at this point if there was nothing
            }

            try
            {
                var brokeredMessage = await GetQueueClient(queueAddress).ReceiveAsync(TimeSpan.FromSeconds(1));

                _ignorant.Reset();

                return brokeredMessage;
            }
            catch (Exception exception)
            {
                if (_ignorant.IsToBeIgnored(exception)) return null;

                QueueClient possiblyFaultyQueueClient;

                if (_queueClients.TryRemove(queueAddress, out possiblyFaultyQueueClient))
                {
                    CloseQueueClient(possiblyFaultyQueueClient);
                }

                throw;
            }
        }

        static void CloseQueueClient(QueueClient queueClientToClose)
        {
            try
            {
                queueClientToClose.Close();
            }
            catch (Exception)
            {
                // ignored because we don't care!
            }
        }

        QueueClient GetQueueClient(string queueAddress)
        {
            var queueClient = _queueClients.GetOrAdd(queueAddress, address =>
            {
                _log.Debug("Initializing new queue client for {0}", address);

                var newQueueClient = QueueClient.CreateFromConnectionString(_connectionString, address, ReceiveMode.PeekLock);

                return newQueueClient;
            });

            return queueClient;
        }

        public string Address
        {
            get { return _inputQueueAddress; }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                DisposePrefetchedMessages();

                _queueClients.Values.ForEach(CloseQueueClient);
            }
            finally
            {
                _disposed = true;
            }
        }

        void DisposePrefetchedMessages()
        {
            BrokeredMessage brokeredMessage;
            while (_prefetchQueue.TryDequeue(out brokeredMessage))
            {
                using (brokeredMessage)
                {
                    try
                    {
                        brokeredMessage.Abandon();
                    }
                    catch (Exception exception)
                    {
                        _log.Warn("Could not abandon brokered message with ID {0}: {1}", brokeredMessage.MessageId, exception);
                    }
                }
            }
        }

        public async Task<string[]> GetSubscriberAddresses(string topic)
        {
            var normalizedTopic = topic.ToValidAzureServiceBusEntityName();

            return new[] { string.Format("subscription/{0}", normalizedTopic) };
        }

        /// <summary>
        /// Registers this endpoint as a subscriber by creating a subscription for the given topic, setting up
        /// auto-forwarding from that subscription to this endpoint's input queue
        /// </summary>
        public async Task RegisterSubscriber(string topic, string subscriberAddress)
        {
            VerifyIsOwnInputQueueAddress(subscriberAddress);

            var normalizedTopic = topic.ToValidAzureServiceBusEntityName();
            var topicDescription = EnsureTopicExists(normalizedTopic);
            var inputQueueClient = GetQueueClient(_inputQueueAddress);

            var inputQueuePath = inputQueueClient.Path;
            var topicPath = topicDescription.Path;
            var subscriptionName = GetSubscriptionName();

            var subscriptionAlreadyExisted = false;

            try
            {
                var subscription = await _namespaceManager.CreateSubscriptionAsync(topicPath, subscriptionName);

                subscription.ForwardTo = inputQueuePath;

                await _namespaceManager.UpdateSubscriptionAsync(subscription);
            }
            catch (MessagingEntityAlreadyExistsException)
            {
                subscriptionAlreadyExisted = true;
            }

            if (subscriptionAlreadyExisted)
            {
                var subscription = await _namespaceManager.GetSubscriptionAsync(topicPath, subscriptionName);

                subscription.ForwardTo = inputQueuePath;

                await _namespaceManager.UpdateSubscriptionAsync(subscription);
            }
        }

        /// <summary>
        /// Unregisters this endpoint as a subscriber by deleting the subscription for the given topic
        /// </summary>
        public async Task UnregisterSubscriber(string topic, string subscriberAddress)
        {
            VerifyIsOwnInputQueueAddress(subscriberAddress);

            var normalizedTopic = topic.ToValidAzureServiceBusEntityName();
            var topicDescription = EnsureTopicExists(normalizedTopic);
            var topicPath = topicDescription.Path;
            var subscriptionName = GetSubscriptionName();

            try
            {
                await _namespaceManager.DeleteSubscriptionAsync(topicPath, subscriptionName);
            }
            catch (MessagingEntityNotFoundException) { }
        }

        string GetSubscriptionName()
        {
            return _inputQueueAddress.ToValidAzureServiceBusEntityName();
        }

        void VerifyIsOwnInputQueueAddress(string subscriberAddress)
        {
            if (subscriberAddress == _inputQueueAddress) return;

            throw new ArgumentException(
                string.Format(
                    "Cannot register subscriptions endpoint with input queue '{0}' in endpoint with input queue '{1}'! The Azure Service Bus transport functions as a centralized subscription storage, which means that all subscribers are capable of managing their own subscriptions",
                    subscriberAddress, _inputQueueAddress));
        }

        TopicDescription EnsureTopicExists(string normalizedTopic)
        {
            return _topics.GetOrAdd(normalizedTopic, t =>
            {
                try
                {
                    return _namespaceManager.CreateTopic(normalizedTopic);
                }
                catch (MessagingEntityAlreadyExistsException)
                {
                    return _namespaceManager.GetTopic(normalizedTopic);
                }
                catch (Exception exception)
                {
                    throw new ArgumentException(string.Format("Could not create topic '{0}'", normalizedTopic), exception);
                }
            });
        }

        /// <summary>
        /// Always returns true because Azure Service Bus topics and subscriptions are global
        /// </summary>
        public bool IsCentralized
        {
            get { return true; }
        }
    }
}
